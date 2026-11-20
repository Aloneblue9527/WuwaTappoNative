namespace PerfMonitor;

public static class ConsoleTable
{
    private static int _rowsSinceHeader;

    public static void PrintHeader()
    {
        Console.WriteLine();
        Console.WriteLine("时间         CPU%   单核%   WS(MB)  Priv(MB)   GDI   USER   句柄   线程  线程Δms  最热线程");
        Console.WriteLine("----------  ------  ------  -------  --------  -----  -----  -----  -----  --------  ------------------------------");
        _rowsSinceHeader = 0;
    }

    public static void PrintRow(Sample s)
    {
        if (_rowsSinceHeader >= 25)
        {
            PrintHeader();
        }
        _rowsSinceHeader++;

        string top = s.TopThread == null
            ? "-"
            : $"{s.TopThread.Id} {Truncate(s.TopThread.Name, 26)}";

        Console.WriteLine(
            $"{s.Timestamp:HH:mm:ss}  " +
            $"{s.CpuNormalizedPct,6:F2}  " +
            $"{s.CpuSingleCorePct,6:F1}  " +
            $"{s.WorkingSetBytes / 1024.0 / 1024.0,7:F1}  " +
            $"{s.PrivateBytes / 1024.0 / 1024.0,8:F1}  " +
            $"{s.GdiObjects,5}  " +
            $"{s.UserObjects,5}  " +
            $"{s.HandleCount,5}  " +
            $"{s.ThreadCount,5}  " +
            $"{s.ThreadCpuDeltaMs,8:F1}  " +
            $"{top}");
    }

    public static void PrintSpikeStarted(Spike spike)
    {
        Console.WriteLine();
        Console.WriteLine($"[尖峰 #{spike.Index}] {spike.StartTime:HH:mm:ss} 开始：CPU={spike.PeakCpuPct:F2}% 基线={spike.BaselinePct:F2}% 阈值={spike.ThresholdPct:F2}%");
    }

    public static void PrintSpikeEnded(Spike spike)
    {
        string thread = spike.PeakThread == null
            ? "-"
            : $"{spike.PeakThread.Id} {Truncate(spike.PeakThread.Name, 30)} ({spike.PeakThread.CpuPctSingleCore:F1}% 单核)";
        Console.WriteLine($"[尖峰 #{spike.Index}] 结束：持续 {spike.DurationSec:F1}s 峰值 {spike.PeakCpuPct:F2}%（单核 {spike.PeakSingleCorePct:F1}%），峰值线程 {thread}");
        Console.WriteLine();
    }

    public static void PrintTopThreads(List<Sample> samples, int topN)
    {
        var rows = samples
            .SelectMany(s => s.Threads)
            .GroupBy(t => t.Id)
            .Select(g =>
            {
                var name = g.Select(t => t.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "";
                return new
                {
                    Id = g.Key,
                    Name = name,
                    TotalDeltaMs = g.Sum(t => t.DeltaMs),
                    MaxCpuPct = g.Max(t => t.CpuPctSingleCore),
                    CumulativeMs = g.Max(t => t.CumulativeMs),
                };
            })
            .OrderByDescending(t => t.TotalDeltaMs)
            .Take(topN)
            .ToList();

        Console.WriteLine();
        Console.WriteLine("=== 累计 CPU Top 线程 ===");
        Console.WriteLine("线程ID    累计Δms   单核峰值%   累计CPU ms  线程描述");
        foreach (var t in rows)
        {
            Console.WriteLine($"{t.Id,-10} {t.TotalDeltaMs,9:F1} {t.MaxCpuPct,10:F1} {t.CumulativeMs,11:F0}  {Truncate(t.Name, 40)}");
        }
    }

    public static void PrintSummary(Stats stats, List<Spike> spikes, ReportPaths paths, ProcessInfo process)
    {
        Console.WriteLine();
        Console.WriteLine("=== 采样摘要 ===");
        Console.WriteLine($"进程: {process.Name} (PID {process.Pid})，逻辑核: {stats.LogicalCores}，样本: {stats.SampleCount}，时长: {stats.DurationSec:F1}s");
        Console.WriteLine($"CPU 归一化%: 中位数 {stats.CpuMedianPct:F2} / p95 {stats.CpuP95Pct:F2} / 最大 {stats.CpuMaxPct:F2}");
        Console.WriteLine($"CPU 单核%:   中位数 {stats.CoreMedianPct:F1} / p95 {stats.CoreP95Pct:F1} / 最大 {stats.CoreMaxPct:F1}");
        Console.WriteLine($"WorkingSet: 中位数 {stats.WsMedianMB:F1} MB / p95 {stats.WsP95MB:F1} MB / 最大 {stats.WsMaxMB:F1} MB / 首尾差 {stats.WsGrowthMB:+0.0;-0.0;0.0} MB");
        Console.WriteLine($"Private:    中位数 {stats.PrivateMedianMB:F1} MB / p95 {stats.PrivateP95MB:F1} MB / 最大 {stats.PrivateMaxMB:F1} MB / 首尾差 {stats.PrivateGrowthMB:+0.0;-0.0;0.0} MB");
        Console.WriteLine($"GDI 最大 {stats.GdiMax}，USER 最大 {stats.UserMax}，句柄最大 {stats.HandleMax}，线程最大 {stats.ThreadMax}");
        Console.WriteLine($"尖峰数量: {spikes.Count}");
        if (spikes.Count > 0)
        {
            Console.WriteLine("序号  开始      持续(s)  峰值CPU%  单核%   基线%   峰值线程");
            foreach (var s in spikes)
            {
                string thread = s.PeakThread == null ? "-" : $"{s.PeakThread.Id} {Truncate(s.PeakThread.Name, 30)}";
                string gap = s.GapSincePreviousEndSec.HasValue ? $"  距上尖峰 {s.GapSincePreviousEndSec.Value:F1}s" : "";
                Console.WriteLine($"#{s.Index,-3} {s.StartTime:HH:mm:ss}  {s.DurationSec,7:F1}  {s.PeakCpuPct,8:F2}  {s.PeakSingleCorePct,6:F1}  {s.BaselinePct,6:F2}  {thread}{gap}");
            }
        }
        Console.WriteLine();
        Console.WriteLine("报告文件：");
        Console.WriteLine("  CSV       " + paths.Csv);
        Console.WriteLine("  线程 CSV  " + paths.ThreadsCsv);
        Console.WriteLine("  JSON      " + paths.Json);
        Console.WriteLine("  日志关联  " + paths.LogText);
    }

    private static string Truncate(string text, int max)
        => string.IsNullOrEmpty(text) ? "" : text.Length <= max ? text : text[..max] + "…";
}
