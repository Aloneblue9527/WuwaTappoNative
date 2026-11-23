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
    static readonly List<uint> ConnectedSlots = new();
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

    public readonly record struct PadState(ushort Buttons, byte LeftTrigger, byte RightTrigger);

    /// <summary>扫描 0..15 号 WinMM 设备并合并所有已连接手柄；没有任何设备时返回 false。</summary>
    public static bool TryRead(out PadState merged)
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
                        ConnectedSlots.Add(slot);
                        Merge(ref merged, Convert(info));
                        any = true;
                    }
                    _nextFullScan = now + FullScanIntervalMs;
                }
                else
                {
                    for (int i = ConnectedSlots.Count - 1; i >= 0; i--)
                    {
                        uint slot = ConnectedSlots[i];
                        if (!TryReadSlot(slot, out JoyInfoEx info))
                        {
                            ConnectedSlots.RemoveAt(i);
                            continue;
                        }
                        Merge(ref merged, Convert(info));
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

    static void Merge(ref PadState merged, PadState state)
    {
        merged = new PadState(
            (ushort)(merged.Buttons | state.Buttons),
            state.LeftTrigger > merged.LeftTrigger ? state.LeftTrigger : merged.LeftTrigger,
            state.RightTrigger > merged.RightTrigger ? state.RightTrigger : merged.RightTrigger);
    }

    static bool TryReadSlot(uint slot, out JoyInfoEx info)
    {
        info = new JoyInfoEx { dwSize = JoyInfoSize, dwFlags = JoyReturnAll };
        return joyGetPosEx(slot, ref info) == 0;
    }

    /// <summary>把常见 DirectInput 手柄布局转换成 XInput 按钮位；摇杆不参与计数。</summary>
    public static PadState Convert(JoyInfoEx joy)
    {
        ushort buttons = 0;
        if (HasButton(joy, 0)) buttons |= X;
        if (HasButton(joy, 1)) buttons |= A;
        if (HasButton(joy, 2)) buttons |= B;
        if (HasButton(joy, 3)) buttons |= Y;
        if (HasButton(joy, 4)) buttons |= LeftShoulder;
        if (HasButton(joy, 5)) buttons |= RightShoulder;
        if (HasButton(joy, 8)) buttons |= Back;
        if (HasButton(joy, 9)) buttons |= Start;
        if (HasButton(joy, 10)) buttons |= LeftThumb;
        if (HasButton(joy, 11)) buttons |= RightThumb;

        // DirectInput 手柄通常把 L2/R2 放在按钮 6/7；XInput 用独立触发轴表示。
        byte leftTrigger = HasButton(joy, 6) ? (byte)255 : (byte)0;
        byte rightTrigger = HasButton(joy, 7) ? (byte)255 : (byte)0;

        if (joy.dwPOV != PovCentered && joy.dwPOV < 36000)
        {
            uint angle = joy.dwPOV;
            if (angle >= 31500 || angle <= 4500) buttons |= DPadUp;
            if (angle >= 4500 && angle <= 13500) buttons |= DPadRight;
            if (angle >= 13500 && angle <= 22500) buttons |= DPadDown;
            if (angle >= 22500 && angle <= 31500) buttons |= DPadLeft;
        }

        return new PadState(buttons, leftTrigger, rightTrigger);
    }

    static bool HasButton(JoyInfoEx joy, int index) => (joy.dwButtons & (1u << index)) != 0;

    [DllImport("winmm.dll")]
    static extern uint joyGetPosEx(uint uJoyID, ref JoyInfoEx pji);
}
