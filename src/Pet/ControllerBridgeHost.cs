using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows.Forms;

namespace WuwatappoNative.Pet;

/// <summary>
/// 手柄扩展在子进程内接收 USB DualSense 报告并轮询其他 WinMM 手柄。
/// 主程序通过命名管道读取快照，避免 WinMM 驱动热插拔异常波及界面。
/// </summary>
internal static class ControllerBridgeHost
{
    public static void Run(string pipeName)
    {
        try
        {
            using var server = new NamedPipeServerStream(
                pipeName,
                PipeDirection.Out,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.None);
            server.WaitForConnection();
            using var writer = new BinaryWriter(server, Encoding.UTF8, leaveOpen: false);
            using var dualSense = new DualSenseRawInput();
            using var pollTimer = new System.Windows.Forms.Timer { Interval = 16 };
            pollTimer.Tick += (_, _) =>
            {
                try
                {
                    bool connected = WinMmPadBridge.TryRead(dualSense, out var state);
                    if (dualSense.ReadState() is { } usbState)
                    {
                        state = state.Merge(usbState);
                        connected = true;
                    }
                    writer.Write(connected);
                    state.WriteTo(writer);
                    writer.Flush();
                }
                catch (IOException)
                {
                    // 主程序退出或断开，停止接收 Raw Input。
                    Application.ExitThread();
                }
            };
            pollTimer.Start();
            Application.Run();
        }
        catch (IOException)
        {
            // 主程序已退出或主动断开，直接结束子进程。
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
