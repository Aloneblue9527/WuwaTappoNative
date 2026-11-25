using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WuwatappoNative.Core;

namespace WuwatappoNative.Pet;

/// <summary>
/// 主程序侧的手柄扩展子进程客户端。
/// 即使驱动在热插拔时让子进程崩溃，主程序也只会短暂失去手柄状态并自动重启子进程。
/// </summary>
internal sealed class ControllerBridgeClient : IDisposable
{
    readonly object _stateGate = new();
    readonly object _processGate = new();
    Process _process;
    int _generation;
    bool _wanted;
    bool _disposed;
    bool _connected;
    ControllerPadState _state;

    public bool TryRead(out ControllerPadState state)
    {
        lock (_stateGate)
        {
            state = _state;
            return _connected;
        }
    }

    public void Start()
    {
        lock (_processGate)
        {
            if (_disposed || _wanted) return;
            _wanted = true;
        }
        Spawn();
    }

    public void Stop()
    {
        Process process = null;
        lock (_processGate)
        {
            _wanted = false;
            ++_generation;
            process = _process;
            _process = null;
        }
        Kill(process);
        lock (_stateGate)
        {
            _connected = false;
            _state = default;
        }
    }

    void Spawn()
    {
        Process process;
        string pipeName;
        int generation;
        lock (_processGate)
        {
            if (_disposed || !_wanted || _process != null) return;
            pipeName = "WuwatappoNative.ControllerBridge." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N");
            generation = ++_generation;
            try
            {
                process = Process.Start(BuildStartInfo(pipeName));
            }
            catch
            {
                ScheduleRetry();
                return;
            }
            if (process == null)
            {
                ScheduleRetry();
                return;
            }
            _process = process;
            process.EnableRaisingEvents = true;
            process.Exited += (s, e) => OnProcessExited(process);
        }
        var reader = new Thread(() => ReadLoop(pipeName, process, generation))
        {
            IsBackground = true,
            Name = "ControllerBridgeReader",
        };
        reader.Start();
    }

    void OnProcessExited(Process process)
    {
        lock (_processGate)
        {
            if (!ReferenceEquals(_process, process)) return;
            _process = null;
        }
        lock (_stateGate)
        {
            _connected = false;
            _state = default;
        }
        process.Dispose();
        if (_wanted)
        {
            Log.Write("手柄协议扩展子进程已退出，1 秒后重启");
            ScheduleRetry();
        }
    }

    void ScheduleRetry()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(1000).ConfigureAwait(false);
            lock (_processGate)
            {
                if (_disposed || !_wanted) return;
            }
            Spawn();
        });
    }

    void ReadLoop(string pipeName, Process process, int generation)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.In, PipeOptions.None);
            client.Connect(8000);
            using var reader = new BinaryReader(client);
            while (true)
            {
                bool connected = reader.ReadBoolean();
                var state = ControllerPadState.ReadFrom(reader);
                lock (_stateGate)
                {
                    if (generation != _generation) return;
                    _connected = connected;
                    _state = state;
                }
            }
        }
        catch (Exception)
        {
            // 子进程崩溃、退出或管道断开：清空状态，Exited 事件负责延迟重启。
            // 如果只是连接超时/卡住，主动结束子进程，交给 Exited 重新拉起。
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch { }
        }
        finally
        {
            lock (_stateGate)
            {
                if (generation == _generation)
                {
                    _connected = false;
                    _state = default;
                }
            }
            try
            {
                if (process.HasExited) process.Dispose();
            }
            catch
            {
                // Exited 事件可能已经先一步释放了进程对象。
            }
        }
    }

    static ProcessStartInfo BuildStartInfo(string pipeName)
    {
        string executable = Environment.ProcessPath;
        string entry = Assembly.GetEntryAssembly()?.Location;
        string args;
        if (!string.IsNullOrWhiteSpace(executable) &&
            string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(entry))
        {
            args = "\"" + entry + "\" --controller-bridge \"" + pipeName + "\"";
        }
        else
        {
            executable = string.IsNullOrWhiteSpace(executable) ? Application.ExecutablePath : executable;
            args = "--controller-bridge \"" + pipeName + "\"";
        }

        return new ProcessStartInfo
        {
            FileName = executable,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
    }

    static void Kill(Process process)
    {
        if (process == null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch { }
        finally
        {
            process.Dispose();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }
}
