namespace PerfMonitor;

/// <summary>一次进程级采样。</summary>
public sealed class Sample
{
    public int Index { get; set; }
    public DateTime Timestamp { get; set; }
    public double ElapsedSec { get; set; }

    /// <summary>按逻辑核归一化的进程 CPU%（0..100）。</summary>
    public double CpuNormalizedPct { get; set; }

    /// <summary>单核百分比（可能超过 100）。</summary>
    public double CpuSingleCorePct { get; set; }

    public long WorkingSetBytes { get; set; }
    public long PrivateBytes { get; set; }
    public uint GdiObjects { get; set; }
    public uint UserObjects { get; set; }
    public int HandleCount { get; set; }
    public int ThreadCount { get; set; }

    /// <summary>本采样周期内所有线程 CPU 时间增量之和（毫秒）。</summary>
    public double ThreadCpuDeltaMs { get; set; }

    public List<ThreadSample> Threads { get; } = new();

    /// <summary>由尖峰分析器回填：该样本之前的历史中位数。</summary>
    public double BaselinePct { get; set; }

    /// <summary>由尖峰分析器回填：该样本的尖峰阈值。</summary>
    public double SpikeThresholdPct { get; set; }

    public ThreadSample? TopThread
        => Threads.Count == 0 ? null
            : Threads.OrderByDescending(t => t.DeltaMs)
                     .ThenByDescending(t => t.CumulativeMs)
                     .First();
}

/// <summary>一次线程级采样。</summary>
public sealed class ThreadSample
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public double DeltaMs { get; set; }
    public double CumulativeMs { get; set; }

    /// <summary>本周期线程 CPU%，单核口径。</summary>
    public double CpuPctSingleCore { get; set; }

    public string Priority { get; set; } = "";
    public string State { get; set; } = "";
    public string WaitReason { get; set; } = "";
}

/// <summary>进程基础信息（不含命令行，避免把敏感参数写进报告）。</summary>
public sealed record ProcessInfo(
    int Pid,
    string Name,
    DateTime? StartTime,
    string? ExecutablePath,
    int LogicalCores,
    double TotalCpuMsAtStart,
    long WorkingSetAtStart);

public sealed class Spike
{
    public int Index { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public double StartElapsedSec { get; set; }
    public double EndElapsedSec { get; set; }
    public double DurationSec { get; set; }
    public double PeakCpuPct { get; set; }
    public double PeakSingleCorePct { get; set; }
    public double BaselinePct { get; set; }
    public double ThresholdPct { get; set; }
    public int PeakSampleIndex { get; set; }
    public ThreadSample? PeakThread { get; set; }
    public List<ThreadSample> TopThreadsAtPeak { get; set; } = new();
    public double? GapSincePreviousEndSec { get; set; }
    public double? StartIntervalSec { get; set; }
    public bool Ongoing { get; set; }
}

public sealed class Stats
{
    public int SampleCount { get; set; }
    public double DurationSec { get; set; }
    public int LogicalCores { get; set; }

    public double CpuMedianPct { get; set; }
    public double CpuP95Pct { get; set; }
    public double CpuMaxPct { get; set; }
    public double CpuMeanPct { get; set; }

    public double CoreMedianPct { get; set; }
    public double CoreP95Pct { get; set; }
    public double CoreMaxPct { get; set; }

    public double WsMedianMB { get; set; }
    public double WsP95MB { get; set; }
    public double WsMaxMB { get; set; }
    public double WsGrowthMB { get; set; }

    public double PrivateMedianMB { get; set; }
    public double PrivateP95MB { get; set; }
    public double PrivateMaxMB { get; set; }
    public double PrivateGrowthMB { get; set; }

    public uint GdiMedian { get; set; }
    public uint GdiMax { get; set; }
    public uint UserMedian { get; set; }
    public uint UserMax { get; set; }
    public int HandleMedian { get; set; }
    public int HandleMax { get; set; }
    public int ThreadMedian { get; set; }
    public int ThreadMax { get; set; }
    public double ThreadCpuDeltaMedianMs { get; set; }
    public double ThreadCpuDeltaMaxMs { get; set; }
}

public sealed class LogLine
{
    public string File { get; set; } = "";
    public int LineNumber { get; set; }
    public DateTime Time { get; set; }
    public string Text { get; set; } = "";
}

public sealed class SpikeLog
{
    public int SpikeIndex { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public List<LogLine> Lines { get; set; } = new();
}

public sealed class LogCorrelationResult
{
    public string LogDir { get; set; } = "";
    public List<string> Files { get; set; } = new();
    public DateTime WindowStart { get; set; }
    public DateTime WindowEnd { get; set; }
    public int LinesScanned { get; set; }
    public int LinesInWindow { get; set; }
    public Dictionary<string, int> LevelCounts { get; set; } = new();
    public Dictionary<string, int> CategoryCounts { get; set; } = new();
    public List<SpikeLog> SpikeLogs { get; set; } = new();

    // diag-*.log 对齐结果（z-order / memory / 其它诊断类别）
    public int DiagLinesScanned { get; set; }
    public int DiagLinesInWindow { get; set; }
    public List<DiagEvent> DiagZOrderEvents { get; set; } = new();
    public List<DiagEvent> DiagMemoryEvents { get; set; } = new();
    public List<SpikeDiag> SpikeDiagEvents { get; set; } = new();
}

public sealed class DiagEvent
{
    public DateTime Time { get; set; }
    public string Category { get; set; } = "";
    public string Summary { get; set; } = "";
    public int LineBytes { get; set; }
}

public sealed class SpikeDiag
{
    public int SpikeIndex { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public List<DiagEvent> Events { get; set; } = new();
}

public sealed class ToolInfo
{
    public string Name { get; set; } = "";
    public string? Path { get; set; }
    public bool Found => !string.IsNullOrEmpty(Path);
}

public sealed class CountersResult
{
    public bool Requested { get; set; }
    public bool Started { get; set; }
    public string? OutputPath { get; set; }
    public string? Error { get; set; }
    public int? ExitCode { get; set; }
    public string? Tail { get; set; }
}
