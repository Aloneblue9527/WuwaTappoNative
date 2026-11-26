using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace WuwatappoNative.Pet;

/// <summary>
/// WinMM DirectInput 手柄读取桥。只在本进程内调用 joyGetPosEx，不替换系统 DLL、
/// 不写注册表；把常见手柄映射成 XInput 兼容按键，交给现有手柄计数逻辑。
/// </summary>
internal static class WinMmPadBridge
{
    public const int SlotCount = 16;
    public const uint PovCentered = 65535;

    const uint JoyReturnAll = 0x00FF;
    const long FullScanIntervalMs = 2000;
    static readonly uint JoyInfoSize = (uint)Marshal.SizeOf<JoyInfoEx>();
    static readonly object Gate = new();
    static readonly List<(uint Index, ushort SonyProduct, bool CapsRead)> ConnectedSlots = new();
    static long _nextFullScan;
    const ushort DPadUp = 0x0001;
    const ushort DPadDown = 0x0002;
    const ushort DPadLeft = 0x0004;
    const ushort DPadRight = 0x0008;
    const ushort Start = 0x0010;
    const ushort Back = 0x0020;
    const ushort LeftThumb = 0x0040;
    const ushort RightThumb = 0x0080;
    const ushort LeftShoulder = 0x0100;
    const ushort RightShoulder = 0x0200;
    const ushort A = 0x1000;
    const ushort B = 0x2000;
    const ushort X = 0x4000;
    const ushort Y = 0x8000;

    [StructLayout(LayoutKind.Sequential)]
    public struct JoyInfoEx
    {
        public uint dwSize;
        public uint dwFlags;
        public uint dwXpos;
        public uint dwYpos;
        public uint dwZpos;
        public uint dwRpos;
        public uint dwUpos;
        public uint dwVpos;
        public uint dwButtons;
        public uint dwButtonNumber;
        public uint dwPOV;
        public uint dwReserved1;
        public uint dwReserved2;
    }

    /// <summary>扫描 0..15 号 WinMM 设备并合并所有已连接手柄；没有任何设备时返回 false。</summary>
    public static bool TryRead(DualSenseRawInput dualSense, out ControllerPadState merged)
    {
        merged = default;
        bool any = false;
        lock (Gate)
        {
            try
            {
                long now = Environment.TickCount64;
                if (now >= _nextFullScan)
                {
                    // 全量扫描用来发现新设备；随后只轮询已知槽位，避免每帧调用 16 次 WinMM。
                    ConnectedSlots.Clear();
                    for (uint slot = 0; slot < SlotCount; slot++)
                    {
                        if (!TryReadSlot(slot, out JoyInfoEx info)) continue;
                        var (sonyProduct, capsRead) = GetSonyProduct(slot);
                        ConnectedSlots.Add((slot, sonyProduct, capsRead));
                        if (ShouldSkipWinMm(capsRead, sonyProduct, dualSense.HasUsbState(sonyProduct), dualSense.HasAnyUsbState()))
                            continue;
                        merged = merged.Merge(Convert(info, sonyProduct));
                        any = true;
                    }
                    _nextFullScan = now + FullScanIntervalMs;
                }
                else
                {
                    for (int i = ConnectedSlots.Count - 1; i >= 0; i--)
                    {
                        var (slot, sonyProduct, capsRead) = ConnectedSlots[i];
                        if (!TryReadSlot(slot, out JoyInfoEx info))
                        {
                            ConnectedSlots.RemoveAt(i);
                            continue;
                        }
                        if (ShouldSkipWinMm(capsRead, sonyProduct, dualSense.HasUsbState(sonyProduct), dualSense.HasAnyUsbState()))
                            continue;
                        merged = merged.Merge(Convert(info, sonyProduct));
                        any = true;
                    }
                }
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
        }
        return any;
    }

    static bool TryReadSlot(uint slot, out JoyInfoEx info)
    {
        info = new JoyInfoEx { dwSize = JoyInfoSize, dwFlags = JoyReturnAll };
        return joyGetPosEx(slot, ref info) == 0;
    }

    /// <summary>把常见 DirectInput 手柄布局转换成 XInput 按钮位；摇杆不参与计数。</summary>
    public static ControllerPadState Convert(JoyInfoEx joy, ushort sonyProduct = 0)
    {
        // 其他 WinMM 手柄继续用按钮 6/7 作为数字扳机。
        byte leftTrigger = HasButton(joy.dwButtons, 6) ? (byte)255 : (byte)0;
        byte rightTrigger = HasButton(joy.dwButtons, 7) ? (byte)255 : (byte)0;
        return new ControllerPadState(ConvertButtons(joy.dwButtons, joy.dwPOV), leftTrigger, rightTrigger,
            Kind: sonyProduct is 0x0CE6 or 0x0DF2 ? ControllerKind.DualSense : ControllerKind.Generic);
    }

    internal static ushort ConvertButtons(uint input, uint pov)
    {
        ushort buttons = 0;
        if (HasButton(input, 0)) buttons |= X;
        if (HasButton(input, 1)) buttons |= A;
        if (HasButton(input, 2)) buttons |= B;
        if (HasButton(input, 3)) buttons |= Y;
        if (HasButton(input, 4)) buttons |= LeftShoulder;
        if (HasButton(input, 5)) buttons |= RightShoulder;
        if (HasButton(input, 8)) buttons |= Back;
        if (HasButton(input, 9)) buttons |= Start;
        if (HasButton(input, 10)) buttons |= LeftThumb;
        if (HasButton(input, 11)) buttons |= RightThumb;

        if (pov < 36000)
        {
            if (pov >= 31500 || pov <= 4500) buttons |= DPadUp;
            if (pov >= 4500 && pov <= 13500) buttons |= DPadRight;
            if (pov >= 13500 && pov <= 22500) buttons |= DPadDown;
            if (pov >= 22500 && pov <= 31500) buttons |= DPadLeft;
        }

        return buttons;
    }

    static bool HasButton(uint buttons, int index) => (buttons & (1u << index)) != 0;

    /// <summary>
    /// 单手柄场景的产品识别回退：caps 读取失败时，只要 Raw Input 已有状态，就保守跳过 WinMM，
    /// 避免无法确认型号的 WinMM 数字扳机覆盖 USB DualSense 的模拟值。
    /// </summary>
    internal static bool ShouldSkipWinMm(bool capsRead, ushort sonyProduct, bool hasUsbForProduct, bool hasAnyUsbState)
    {
        if (capsRead) return sonyProduct != 0 && hasUsbForProduct;
        return hasAnyUsbState;
    }

    static (ushort Product, bool CapsRead) GetSonyProduct(uint slot)
    {
        if (joyGetDevCapsW((UIntPtr)slot, out var caps, (uint)Marshal.SizeOf<JoyCaps>()) != 0)
            return (0, false);
        return (DualSenseRawInput.GetProductId(caps.VendorId, caps.ProductId), true);
    }

    // JOYCAPSW 的完整缓冲区大小，本函数只需要开头的厂商和产品字段。
    [StructLayout(LayoutKind.Explicit, Size = 728)]
    struct JoyCaps
    {
        [FieldOffset(0)] public ushort VendorId;
        [FieldOffset(2)] public ushort ProductId;
    }

    [DllImport("winmm.dll")]
    static extern uint joyGetPosEx(uint uJoyID, ref JoyInfoEx pji);

    [DllImport("winmm.dll", ExactSpelling = true)]
    static extern uint joyGetDevCapsW(UIntPtr slot, out JoyCaps caps, uint size);
}
