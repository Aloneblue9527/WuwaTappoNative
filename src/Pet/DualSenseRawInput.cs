using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WuwatappoNative.Pet;

/// <summary>在手柄子进程中读取原生 USB DualSense 输入报告。</summary>
internal sealed class DualSenseRawInput : NativeWindow, IDisposable
{
    const ushort SonyVendorId = 0x054C;
    const ushort DualSenseProductId = 0x0CE6;
    const ushort DualSenseEdgeProductId = 0x0DF2;
    const int UsbReportSize = 64;
    const int WmInputDeviceChange = 0x00FE;
    const int WmInput = 0x00FF;
    const int DeviceRemoval = 2;
    const uint InputSink = 0x0100;
    const uint DeviceNotify = 0x2000;
    const uint InputData = 0x10000003;
    const uint DeviceInfo = 0x2000000B;
    const uint HidType = 2;
    const long ProductProbeRetryMs = 2000;

    static readonly int HeaderSize = Marshal.SizeOf<RawInputHeader>();
    readonly byte[] _buffer = new byte[4096];
    readonly Dictionary<IntPtr, (ushort ProductId, long NextProductProbeMs, ControllerPadState? State)> _devices = new();

    public DualSenseRawInput()
    {
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); // HWND_MESSAGE
        var gamepad = new RawInputDevice { UsagePage = 1, Usage = 5, Flags = InputSink | DeviceNotify, Target = Handle };
        if (!RegisterRawInputDevices(ref gamepad, 1, (uint)Marshal.SizeOf<RawInputDevice>()))
            DestroyHandle(); // 注册失败时由原有 WinMM 路径处理。
    }

    public ControllerPadState? ReadState()
    {
        ControllerPadState? merged = null;
        foreach (var device in _devices.Values)
        {
            if (device.State is not { } state) continue;
            merged = merged?.Merge(state) ?? state;
        }
        return merged;
    }

    public bool HasUsbState(ushort productId)
    {
        if (productId == 0) return false;
        foreach (var device in _devices.Values)
        {
            if (device.ProductId == productId && device.State.HasValue) return true;
        }
        return false;
    }

    /// <summary>产品识别失败时用于单手柄场景的保守回退：只要 Raw Input 有状态，就避免 WinMM 数字扳机覆盖。</summary>
    public bool HasAnyUsbState()
    {
        foreach (var device in _devices.Values)
            if (device.State.HasValue) return true;
        return false;
    }

    internal static bool ShouldProbeProduct(ushort productId, long nowMs, long nextProbeMs) =>
        productId == 0 && nowMs >= nextProbeMs;

    public static ushort GetProductId(uint vendorId, uint productId) =>
        vendorId == SonyVendorId && productId is DualSenseProductId or DualSenseEdgeProductId
            ? (ushort)productId : (ushort)0;

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmInput)
            ReadInput(message.LParam);
        else if (message.Msg == WmInputDeviceChange && message.WParam == new IntPtr(DeviceRemoval))
            _devices.Remove(message.LParam);

        base.WndProc(ref message);
    }

    void ReadInput(IntPtr input)
    {
        uint capacity = (uint)_buffer.Length;
        uint bytes = GetRawInputData(input, InputData, _buffer, ref capacity, (uint)HeaderSize);
        int reportOffset = HeaderSize + 2 * sizeof(uint); // RAWHID.dwSizeHid 和 dwCount
        if (bytes < reportOffset || bytes > _buffer.Length) return;

        var header = MemoryMarshal.Read<RawInputHeader>(_buffer);
        if (header.Type != HidType || header.Device == IntPtr.Zero) return;

        uint size = MemoryMarshal.Read<uint>(_buffer.AsSpan(HeaderSize));
        uint count = MemoryMarshal.Read<uint>(_buffer.AsSpan(HeaderSize + sizeof(uint)));
        if (size != UsbReportSize || count == 0 || count > (bytes - reportOffset) / size) return;

        if (!_devices.TryGetValue(header.Device, out var device))
            device = (0, 0, null);

        long now = Environment.TickCount64;
        if (ShouldProbeProduct(device.ProductId, now, device.NextProductProbeMs))
        {
            ushort productId = ReadProductId(header.Device);
            device.ProductId = productId;
            device.NextProductProbeMs = productId == 0 ? now + ProductProbeRetryMs : 0;
        }
        if (device.ProductId == 0)
        {
            // 产品信息读取失败或设备不受支持；保留探测时间，后续报告继续重试。
            _devices[header.Device] = device;
            return;
        }

        // 管道传递状态快照，取该批次最后一份输入报告。
        int offset = reportOffset + (int)(count - 1) * UsbReportSize;
        if (TryDecodeUsbReport(_buffer.AsSpan(offset, UsbReportSize), out var state))
            device.State = state;
        _devices[header.Device] = device;
    }

    static ushort ReadProductId(IntPtr handle)
    {
        var info = new RawHidInfo { Size = (uint)Marshal.SizeOf<RawHidInfo>() };
        uint size = info.Size;
        return GetRawInputDeviceInfoW(handle, DeviceInfo, ref info, ref size) != uint.MaxValue && info.Type == HidType
            ? GetProductId(info.VendorId, info.ProductId) : (ushort)0;
    }

    internal static bool TryDecodeUsbReport(ReadOnlySpan<byte> report, out ControllerPadState state)
    {
        state = default;
        if (report.Length != UsbReportSize || report[0] != 0x01) return false;

        // 报告 0x01：4 个摇杆轴、2 个扳机轴、序号，随后是方向与按键位。
        byte hat = (byte)(report[8] & 0x0F);
        uint buttons = (uint)(report[8] >> 4) | ((uint)report[9] << 4);
        uint pov = hat < 8 ? (uint)hat * 4500 : WinMmPadBridge.PovCentered;
        state = new ControllerPadState(
            WinMmPadBridge.ConvertButtons(buttons, pov), report[5], report[6],
            ToStick(report[1]), ToStick((byte)(255 - report[2])),
            ToStick(report[3]), ToStick((byte)(255 - report[4])));
        return true;
    }

    // 将 127/128 都视作中位，两侧按比例覆盖 XInput 的有符号 16 位轴范围。
    static short ToStick(byte sample) => sample < 128
        ? (short)((sample - 127) * 32768 / 127)
        : (short)((sample - 128) * 32767 / 127);

    public void Dispose()
    {
        if (Handle != IntPtr.Zero) DestroyHandle();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RawInputDevice
    {
        public ushort UsagePage, Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RawInputHeader
    {
        public uint Type, Size;
        public IntPtr Device, Param;
    }

    // RID_DEVICE_INFO 的联合体按其最大成员（键盘）分配。
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    struct RawHidInfo
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public uint Type;
        [FieldOffset(8)] public uint VendorId;
        [FieldOffset(12)] public uint ProductId;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool RegisterRawInputDevices(ref RawInputDevice device, uint count, uint size);

    [DllImport("user32.dll", ExactSpelling = true)]
    static extern uint GetRawInputData(IntPtr input, uint command, [Out] byte[] data, ref uint size, uint headerSize);

    [DllImport("user32.dll", ExactSpelling = true)]
    static extern uint GetRawInputDeviceInfoW(IntPtr device, uint command, ref RawHidInfo info, ref uint size);
}
