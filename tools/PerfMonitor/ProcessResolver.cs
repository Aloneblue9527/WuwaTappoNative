using System.Diagnostics;

namespace PerfMonitor;

public static class ProcessResolver
{
    /// <summary>按 --pid 或 --process 附加现有进程，不启动、不结束目标进程。</summary>
    public static Process Resolve(Options options)
    {
        if (options.Pid > 0)
        {
            try
            {
                return Process.GetProcessById(options.Pid);
            }
            catch (ArgumentException)
            {
                throw new InvalidOperationException($"找不到 PID={options.Pid} 的进程（可能已退出）");
            }
        }

        Process[] matches;
        try
        {
            matches = Process.GetProcessesByName(options.ProcessName);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("按进程名枚举失败：" + ex.Message, ex);
        }

        if (matches.Length == 0)
        {
            throw new InvalidOperationException(
                $"没有找到进程 {options.ProcessName}。请先启动 WuwatappoNative，或用 --pid 指定其他进程。");
        }

        if (matches.Length == 1) return matches[0];

        Console.WriteLine($"发现 {matches.Length} 个同名进程：");
        foreach (var p in matches)
        {
            string title = "";
            long ws = 0;
            try { title = p.MainWindowTitle; } catch { }
            try { ws = p.WorkingSet64; } catch { }
            Console.WriteLine($"  PID={p.Id,-8} WS={ws / 1024.0 / 1024.0,8:F1} MB  {title}");
        }

        var chosen = matches
            .OrderByDescending(p => SafeMainWindowHandle(p) != IntPtr.Zero)
            .ThenByDescending(p => SafeWorkingSet(p))
            .First();
        Console.WriteLine($"自动选择 PID={chosen.Id}；如需指定请用 --pid。");
        foreach (var p in matches)
            if (!ReferenceEquals(p, chosen)) p.Dispose();
        return chosen;
    }

    public static ProcessInfo ReadInfo(Process process, int logicalCores)
    {
        string? path = null;
        DateTime? start = null;
        long ws = 0;
        double cpu = 0;
        try { path = process.MainModule?.FileName; } catch { }
        try { start = process.StartTime; } catch { }
        try { ws = process.WorkingSet64; } catch { }
        try { cpu = process.TotalProcessorTime.TotalMilliseconds; } catch { }
        return new ProcessInfo(process.Id, SafeName(process), start, path, logicalCores, cpu, ws);
    }

    private static string SafeName(Process p)
    {
        try { return p.ProcessName; } catch { return "?"; }
    }

    private static IntPtr SafeMainWindowHandle(Process p)
    {
        try { return p.MainWindowHandle; } catch { return IntPtr.Zero; }
    }

    private static long SafeWorkingSet(Process p)
    {
        try { return p.WorkingSet64; } catch { return 0; }
    }
}
