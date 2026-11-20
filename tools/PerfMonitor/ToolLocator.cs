using System.Diagnostics;

namespace PerfMonitor;

public static class ToolLocator
{
    public static List<ToolInfo> Detect()
    {
        return new List<ToolInfo>
        {
            new() { Name = "dotnet-counters", Path = FindExecutable("dotnet-counters") },
            new() { Name = "dotnet-trace", Path = FindExecutable("dotnet-trace") },
            new() { Name = "PerfView", Path = FindPerfView() },
        };
    }

    public static void Print(List<ToolInfo> tools)
    {
        Console.WriteLine("可选诊断工具（仅检测，不会自动安装）：");
        foreach (var tool in tools)
        {
            Console.WriteLine(tool.Found
                ? $"  [有] {tool.Name,-16} {tool.Path}"
                : $"  [无] {tool.Name,-16} （使用内置 Process/Thread API）");
        }
        Console.WriteLine();
    }

    private static string? FindExecutable(string name)
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? new[] { name + ".exe", name + ".cmd", name + ".bat", name }
            : new[] { name };

        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathVar))
        {
            foreach (string dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (string candidate in candidates)
                {
                    try
                    {
                        string full = Path.Combine(dir.Trim(), candidate);
                        if (File.Exists(full)) return full;
                    }
                    catch { }
                }
            }
        }

        if (OperatingSystem.IsWindows())
        {
            string userTools = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools");
            foreach (string candidate in candidates)
            {
                string full = Path.Combine(userTools, candidate);
                if (File.Exists(full)) return full;
            }
        }

        return null;
    }

    private static string? FindPerfView()
    {
        string? onPath = FindExecutable("PerfView");
        if (onPath != null) return onPath;
        if (!OperatingSystem.IsWindows()) return null;

        string[] common =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PerfView", "PerfView.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "PerfView.exe"),
        };
        return common.FirstOrDefault(File.Exists);
    }
}

/// <summary>
/// 可选的 dotnet-counters 采集器：只有显式 --with-counters 且检测到工具时才启动。
/// 采样结束时只结束这个辅助进程，不触碰目标进程。
/// </summary>
public sealed class CountersCollector : IDisposable
{
    private readonly Process? _process;
    private readonly System.Text.StringBuilder _output = new();
    private readonly object _gate = new();
    private bool _stopped;

    public CountersResult Result { get; }

    private CountersCollector(Process? process, CountersResult result)
    {
        _process = process;
        Result = result;
    }

    public static CountersCollector? TryStart(string? exePath, int pid, string outDir, string baseName)
    {
        if (string.IsNullOrEmpty(exePath)) return null;

        Directory.CreateDirectory(outDir);
        string outputPath = baseName + "-counters.csv";
        var result = new CountersResult
        {
            Requested = true,
            OutputPath = outputPath,
        };

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("collect");
            psi.ArgumentList.Add("--process-id");
            psi.ArgumentList.Add(pid.ToString());
            psi.ArgumentList.Add("--format");
            psi.ArgumentList.Add("csv");
            psi.ArgumentList.Add("--output");
            psi.ArgumentList.Add(outputPath);
            psi.ArgumentList.Add("--refresh-interval");
            psi.ArgumentList.Add("1");

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            if (!process.Start())
            {
                result.Error = "dotnet-counters 启动返回 false";
                process.Dispose();
                return new CountersCollector(null, result);
            }

            var collector = new CountersCollector(process, result);
            process.OutputDataReceived += (_, e) => collector.Append(e.Data);
            process.ErrorDataReceived += (_, e) => collector.Append(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            result.Started = true;
            Console.WriteLine("已启动 dotnet-counters 采集：" + outputPath);
            return collector;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            return new CountersCollector(null, result);
        }
    }

    private void Append(string? line)
    {
        if (string.IsNullOrEmpty(line)) return;
        lock (_gate)
        {
            if (_output.Length > 8192) _output.Remove(0, _output.Length - 4096);
            _output.AppendLine(line);
        }
    }

    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        if (_process == null) return;
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(3000);
            }
            Result.ExitCode = _process.HasExited ? _process.ExitCode : null;
        }
        catch (Exception ex)
        {
            Result.Error = ex.Message;
        }
        finally
        {
            lock (_gate) Result.Tail = _output.ToString();
            try { _process.Dispose(); } catch { }
        }
    }

    public void Dispose() => Stop();
}
