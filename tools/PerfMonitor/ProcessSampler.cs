using System.Diagnostics;

namespace PerfMonitor;

/// <summary>
/// 只读进程/线程采样器。读取 Process.TotalProcessorTime 与 ProcessThread.TotalProcessorTime 的差值，
/// 不依赖 ETW、dotnet-counters 或管理员权限（GDI/USER 计数在受保护进程上可能拿不到）。
/// </summary>
public sealed class ProcessSampler
{
    private readonly Process _process;
    private readonly int _logicalCores;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<int, double> _lastThreadCpuMs = new();
    private readonly Dictionary<int, string> _threadNames = new();
    private double _lastProcessCpuMs;
    private double _lastWallSec;

    public Process Process => _process;
    public int LogicalCores => _logicalCores;

    public ProcessSampler(Process process)
    {
        _process = process;
        _logicalCores = Math.Max(1, Environment.ProcessorCount);
        _lastProcessCpuMs = ReadTotalCpuMs(process);
        _lastWallSec = _clock.Elapsed.TotalSeconds;
        SnapshotThreadCpu(process, _lastThreadCpuMs);
    }

    public Sample TakeSample(int index)
    {
        var sample = new Sample
        {
            Index = index,
            Timestamp = DateTime.Now,
            ElapsedSec = _clock.Elapsed.TotalSeconds,
        };

        try
        {
            _process.Refresh();
        }
        catch
        {
            // 进程已退出时让上层通过 HasExited 判断。
        }

        double nowWall = _clock.Elapsed.TotalSeconds;
        double wallSec = Math.Max(0.001, nowWall - _lastWallSec);
        double wallMs = wallSec * 1000.0;

        try
        {
            double cpuMs = ReadTotalCpuMs(_process);
            double deltaMs = Math.Max(0, cpuMs - _lastProcessCpuMs);
            _lastProcessCpuMs = cpuMs;
            sample.CpuSingleCorePct = deltaMs / wallMs * 100.0;
            sample.CpuNormalizedPct = sample.CpuSingleCorePct / _logicalCores;
        }
        catch
        {
            sample.CpuSingleCorePct = 0;
            sample.CpuNormalizedPct = 0;
        }

        try { sample.WorkingSetBytes = _process.WorkingSet64; } catch { }
        try { sample.PrivateBytes = _process.PrivateMemorySize64; } catch { }
        try { sample.HandleCount = _process.HandleCount; } catch { }

        var gui = NativeMethods.GetGuiResourceCounts(_process.Id);
        if (gui.HasValue)
        {
            sample.GdiObjects = gui.Value.Gdi;
            sample.UserObjects = gui.Value.User;
        }

        var currentThreadCpu = new Dictionary<int, double>();
        try
        {
            var threads = _process.Threads;
            sample.ThreadCount = threads.Count;
            foreach (ProcessThread thread in threads)
            {
                int id;
                double cpuMs;
                try
                {
                    id = thread.Id;
                    cpuMs = thread.TotalProcessorTime.TotalMilliseconds;
                }
                catch
                {
                    // 目标以管理员权限运行时 ProcessThread.TotalProcessorTime 会被拒绝；
                    // 用 OpenThread(THREAD_QUERY_LIMITED_INFORMATION)+GetThreadTimes 兜底。
                    try { id = thread.Id; } catch { continue; }
                    double? fallback = NativeMethods.GetThreadTotalCpuMs(id);
                    if (!fallback.HasValue) continue;
                    cpuMs = fallback.Value;
                }

                currentThreadCpu[id] = cpuMs;
                double previous;
                double deltaMs = _lastThreadCpuMs.TryGetValue(id, out previous)
                    ? Math.Max(0, cpuMs - previous)
                    : 0;

                sample.ThreadCpuDeltaMs += deltaMs;

                string name = GetThreadName(id, index);
                string priority = "", state = "", waitReason = "";
                try { priority = thread.PriorityLevel.ToString(); } catch { }
                try { state = thread.ThreadState.ToString(); } catch { }
                try { waitReason = thread.WaitReason.ToString(); } catch { }

                sample.Threads.Add(new ThreadSample
                {
                    Id = id,
                    Name = name,
                    DeltaMs = deltaMs,
                    CumulativeMs = cpuMs,
                    CpuPctSingleCore = deltaMs / wallMs * 100.0,
                    Priority = priority,
                    State = state,
                    WaitReason = waitReason,
                });
            }
        }
        catch
        {
            // 进程退出或线程集合不可用：保留已采到的进程级数据。
        }

        _lastThreadCpuMs.Clear();
        foreach (var pair in currentThreadCpu) _lastThreadCpuMs[pair.Key] = pair.Value;
        _lastWallSec = nowWall;
        return sample;
    }

    private string GetThreadName(int threadId, int sampleIndex)
    {
        if (_threadNames.TryGetValue(threadId, out string? cached) && !string.IsNullOrEmpty(cached))
            return cached;

        // 空名字每 10 个采样重试一次；线程描述通常只在启动时设置一次。
        if (cached == null || sampleIndex % 10 == 0)
        {
            string name = NativeMethods.GetThreadDescriptionSafe(threadId);
            _threadNames[threadId] = name;
            return name;
        }
        return cached;
    }

    private static double ReadTotalCpuMs(Process process)
    {
        try { return process.TotalProcessorTime.TotalMilliseconds; }
        catch { return 0; }
    }

    private static void SnapshotThreadCpu(Process process, Dictionary<int, double> target)
    {
        try
        {
            foreach (ProcessThread thread in process.Threads)
            {
                try { target[thread.Id] = thread.TotalProcessorTime.TotalMilliseconds; }
                catch { }
            }
        }
        catch { }
    }
}
