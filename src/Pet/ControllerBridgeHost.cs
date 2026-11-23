using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace WuwatappoNative.Pet;

/// <summary>
/// 独立进程里的 WinMM 手柄轮询器。WinMM 在部分驱动热插拔时可能直接让进程崩溃，
/// 因此把 joyGetPosEx 放到子进程；主程序只读取命名管道，驱动崩溃不会带走主程序。
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
            while (true)
            {
                bool connected = WinMmPadBridge.TryRead(out var state);
                writer.Write((byte)(connected ? 1 : 0));
                writer.Write(state.Buttons);
                writer.Write(state.LeftTrigger);
                writer.Write(state.RightTrigger);
                writer.Flush();
                Thread.Sleep(16);
            }
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
