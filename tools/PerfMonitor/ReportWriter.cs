using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace PerfMonitor;

public sealed record ReportPaths(string BaseName, string Csv, string ThreadsCsv, string Json, string LogText);

public static class ReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    public static string CreateBaseName(string outDir, string processName)
    {
        Directory.CreateDirectory(outDir);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string safeName = SanitizeFileName(processName);
        return Path.Combine(outDir, $"perf-{safeName}-{stamp}");
    }

    public static ReportPaths Write(
        Options options,
        ProcessInfo process,
        List<Sample> samples,
        List<Spike> spikes,
        Stats stats,
        LogCorrelationResult? log,
        List<ToolInfo> tools,
        CountersResult? counters,
        string? baseName = null)
    {
        Directory.CreateDirectory(options.OutDir);
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = CreateBaseName(options.OutDir, process.Name);

        string csvPath = baseName + ".csv";
        string threadsPath = baseName + "-threads.csv";
        string jsonPath = baseName + ".json";
        string logPath = baseName + "-log.txt";

        WriteSamplesCsv(csvPath, samples);
        WriteThreadsCsv(threadsPath, samples, options.TopThreads);
        WriteJson(jsonPath, options, process, samples, spikes, stats, log, tools, counters);
        WriteLogText(logPath, samples, spikes, log);

        return new ReportPaths(baseName, csvPath, threadsPath, jsonPath, logPath);
    }

    private static void WriteSamplesCsv(string path, List<Sample> samples)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("timestamp,elapsed_sec,cpu_pct_normalized,cpu_pct_single_core,working_set_mb,private_mb,gdi_objects,user_objects,handle_count,thread_count,thread_cpu_delta_ms,top_thread_id,top_thread_name,top_thread_cpu_pct,top_thread_cumulative_ms");
        foreach (var s in samples)
        {
            var top = s.TopThread;
            writer.WriteLine(string.Join(",",
                Csv(s.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)),
                F2(s.ElapsedSec),
                F3(s.CpuNormalizedPct),
                F2(s.CpuSingleCorePct),
                F2(s.WorkingSetBytes / 1024.0 / 1024.0),
                F2(s.PrivateBytes / 1024.0 / 1024.0),
                s.GdiObjects.ToString(CultureInfo.InvariantCulture),
                s.UserObjects.ToString(CultureInfo.InvariantCulture),
                s.HandleCount.ToString(CultureInfo.InvariantCulture),
                s.ThreadCount.ToString(CultureInfo.InvariantCulture),
                F2(s.ThreadCpuDeltaMs),
                top?.Id.ToString(CultureInfo.InvariantCulture) ?? "",
                Csv(top?.Name ?? ""),
                top == null ? "" : F2(top.CpuPctSingleCore),
                top == null ? "" : F1(top.CumulativeMs)));
        }
    }

    private static void WriteThreadsCsv(string path, List<Sample> samples, int topN)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("timestamp,elapsed_sec,thread_id,thread_name,cpu_pct_single_core,delta_ms,cumulative_ms,priority,state,wait_reason");
        foreach (var s in samples)
        {
            var top = s.Threads
                .Where(t => t.DeltaMs > 0.001)
                .OrderByDescending(t => t.DeltaMs)
                .ThenByDescending(t => t.CumulativeMs)
                .Take(topN);
            foreach (var t in top)
            {
                writer.WriteLine(string.Join(",",
                    Csv(s.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)),
                    F2(s.ElapsedSec),
                    t.Id.ToString(CultureInfo.InvariantCulture),
                    Csv(t.Name),
                    F2(t.CpuPctSingleCore),
                    F2(t.DeltaMs),
                    F1(t.CumulativeMs),
                    Csv(t.Priority),
                    Csv(t.State),
                    Csv(t.WaitReason)));
            }
        }
    }

    private static void WriteJson(
        string path,
        Options options,
        ProcessInfo process,
        List<Sample> samples,
        List<Spike> spikes,
        Stats stats,
        LogCorrelationResult? log,
        List<ToolInfo> tools,
        CountersResult? counters)
    {
        var topThreads = samples
            .SelectMany(s => s.Threads)
            .GroupBy(t => t.Id)
            .Select(g =>
            {
                var name = g.Select(t => t.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "";
                return new
                {
                    threadId = g.Key,
                    name,
                    totalDeltaMs = Math.Round(g.Sum(t => t.DeltaMs), 2),
                    maxCpuPctSingleCore = Math.Round(g.Max(t => t.CpuPctSingleCore), 2),
                    cumulativeMs = Math.Round(g.Max(t => t.CumulativeMs), 1),
                    busySamples = g.Count(t => t.DeltaMs > 0.5),
                };
            })
            .OrderByDescending(t => t.totalDeltaMs)
            .Take(24)
            .ToList();

        var payload = new
        {
            schemaVersion = 1,
            generatedAt = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
            tool = "PerfMonitor 1.0",
            note = "CPU% 按逻辑核归一化；singleCore 为单核百分比。线程 CPU% 为单核口径。",
            options = new
            {
                pid = options.Pid,
                process = options.ProcessName,
                durationSec = options.DurationSec,
                intervalSec = options.IntervalSec,
                topThreads = options.TopThreads,
                spikeFactor = options.SpikeFactor,
                spikeMinDeltaPct = options.SpikeMinDeltaPct,
                baselineWindow = options.BaselineWindow,
                warmupSamples = options.WarmupSamples,
            },
            process = new
            {
                pid = process.Pid,
                name = process.Name,
                startTime = process.StartTime?.ToString("O", CultureInfo.InvariantCulture),
                executablePath = process.ExecutablePath,
                logicalCores = process.LogicalCores,
                totalCpuMsAtStart = Math.Round(process.TotalCpuMsAtStart, 1),
                workingSetAtStartMB = Math.Round(process.WorkingSetAtStart / 1024.0 / 1024.0, 2),
            },
            stats = new
            {
                sampleCount = stats.SampleCount,
                durationSec = Math.Round(stats.DurationSec, 2),
                cpuPctNormalized = new
                {
                    median = Math.Round(stats.CpuMedianPct, 3),
                    p95 = Math.Round(stats.CpuP95Pct, 3),
                    max = Math.Round(stats.CpuMaxPct, 3),
                    mean = Math.Round(stats.CpuMeanPct, 3),
                },
                cpuPctSingleCore = new
                {
                    median = Math.Round(stats.CoreMedianPct, 2),
                    p95 = Math.Round(stats.CoreP95Pct, 2),
                    max = Math.Round(stats.CoreMaxPct, 2),
                },
                workingSetMB = new
                {
                    median = Math.Round(stats.WsMedianMB, 2),
                    p95 = Math.Round(stats.WsP95MB, 2),
                    max = Math.Round(stats.WsMaxMB, 2),
                    growth = Math.Round(stats.WsGrowthMB, 2),
                },
                privateMB = new
                {
                    median = Math.Round(stats.PrivateMedianMB, 2),
                    p95 = Math.Round(stats.PrivateP95MB, 2),
                    max = Math.Round(stats.PrivateMaxMB, 2),
                    growth = Math.Round(stats.PrivateGrowthMB, 2),
                },
                gdi = new { median = stats.GdiMedian, max = stats.GdiMax },
                user = new { median = stats.UserMedian, max = stats.UserMax },
                handles = new { median = stats.HandleMedian, max = stats.HandleMax },
                threads = new { median = stats.ThreadMedian, max = stats.ThreadMax },
                threadCpuDeltaMs = new
                {
                    median = Math.Round(stats.ThreadCpuDeltaMedianMs, 2),
                    max = Math.Round(stats.ThreadCpuDeltaMaxMs, 2),
                },
            },
            spikes = spikes.Select(s => new
            {
                index = s.Index,
                startTime = s.StartTime.ToString("O", CultureInfo.InvariantCulture),
                endTime = s.EndTime.ToString("O", CultureInfo.InvariantCulture),
                durationSec = Math.Round(s.DurationSec, 2),
                peakCpuPctNormalized = Math.Round(s.PeakCpuPct, 3),
                peakCpuPctSingleCore = Math.Round(s.PeakSingleCorePct, 2),
                baselinePctNormalized = Math.Round(s.BaselinePct, 3),
                thresholdPctNormalized = Math.Round(s.ThresholdPct, 3),
                ongoing = s.Ongoing,
                gapSincePreviousEndSec = s.GapSincePreviousEndSec.HasValue ? Math.Round(s.GapSincePreviousEndSec.Value, 2) : (double?)null,
                startIntervalSec = s.StartIntervalSec.HasValue ? Math.Round(s.StartIntervalSec.Value, 2) : (double?)null,
                peakThread = s.PeakThread == null ? null : new
                {
                    id = s.PeakThread.Id,
                    name = s.PeakThread.Name,
                    cpuPctSingleCore = Math.Round(s.PeakThread.CpuPctSingleCore, 2),
                    cumulativeMs = Math.Round(s.PeakThread.CumulativeMs, 1),
                },
                topThreadsAtPeak = s.TopThreadsAtPeak.Select(t => new
                {
                    id = t.Id,
                    name = t.Name,
                    cpuPctSingleCore = Math.Round(t.CpuPctSingleCore, 2),
                    deltaMs = Math.Round(t.DeltaMs, 2),
                    cumulativeMs = Math.Round(t.CumulativeMs, 1),
                    priority = t.Priority,
                    state = t.State,
                }),
            }),
            samples = samples.Select(s => new
            {
                index = s.Index,
                timestamp = s.Timestamp.ToString("O", CultureInfo.InvariantCulture),
                elapsedSec = Math.Round(s.ElapsedSec, 3),
                cpuPctNormalized = Math.Round(s.CpuNormalizedPct, 3),
                cpuPctSingleCore = Math.Round(s.CpuSingleCorePct, 2),
                workingSetMB = Math.Round(s.WorkingSetBytes / 1024.0 / 1024.0, 2),
                privateMB = Math.Round(s.PrivateBytes / 1024.0 / 1024.0, 2),
                gdi = s.GdiObjects,
                user = s.UserObjects,
                handles = s.HandleCount,
                threads = s.ThreadCount,
                threadCpuDeltaMs = Math.Round(s.ThreadCpuDeltaMs, 2),
                topThread = s.TopThread == null ? null : new
                {
                    id = s.TopThread.Id,
                    name = s.TopThread.Name,
                    cpuPctSingleCore = Math.Round(s.TopThread.CpuPctSingleCore, 2),
                },
            }),
            topThreadsOverall = topThreads,
            log = log == null ? null : new
            {
                logDir = log.LogDir,
                files = log.Files,
                windowStart = log.WindowStart.ToString("O", CultureInfo.InvariantCulture),
                windowEnd = log.WindowEnd.ToString("O", CultureInfo.InvariantCulture),
                linesScanned = log.LinesScanned,
                linesInWindow = log.LinesInWindow,
                levelCounts = log.LevelCounts,
                categoryCounts = log.CategoryCounts,
                spikeLogs = log.SpikeLogs.Select(sl => new
                {
                    spikeIndex = sl.SpikeIndex,
                    startTime = sl.StartTime.ToString("O", CultureInfo.InvariantCulture),
                    endTime = sl.EndTime.ToString("O", CultureInfo.InvariantCulture),
                    lines = sl.Lines.Select(l => new
                    {
                        time = l.Time.ToString("O", CultureInfo.InvariantCulture),
                        file = l.File,
                        line = l.LineNumber,
                        text = l.Text,
                    }),
                }),
                diagLinesScanned = log.DiagLinesScanned,
                diagLinesInWindow = log.DiagLinesInWindow,
                diagZOrderEvents = log.DiagZOrderEvents.Select(e => new
                {
                    time = e.Time.ToString("O", CultureInfo.InvariantCulture),
                    category = e.Category,
                    summary = e.Summary,
                }),
                diagMemoryEvents = log.DiagMemoryEvents.Select(e => new
                {
                    time = e.Time.ToString("O", CultureInfo.InvariantCulture),
                    category = e.Category,
                    summary = e.Summary,
                }),
                spikeDiagEvents = log.SpikeDiagEvents.Select(sd => new
                {
                    spikeIndex = sd.SpikeIndex,
                    startTime = sd.StartTime.ToString("O", CultureInfo.InvariantCulture),
                    endTime = sd.EndTime.ToString("O", CultureInfo.InvariantCulture),
                    events = sd.Events.Select(e => new
                    {
                        time = e.Time.ToString("O", CultureInfo.InvariantCulture),
                        category = e.Category,
                        summary = e.Summary,
                    }),
                }),
            },
            optionalTools = tools.Select(t => new { name = t.Name, found = t.Found, path = t.Path }),
            counters = counters == null ? null : new
            {
                requested = counters.Requested,
                started = counters.Started,
                outputPath = counters.OutputPath,
                error = counters.Error,
                exitCode = counters.ExitCode,
                tail = counters.Tail,
            },
        };

        File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonOptions), new UTF8Encoding(false));
    }

    private static void WriteLogText(
        string path,
        List<Sample> samples,
        List<Spike> spikes,
        LogCorrelationResult? log)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# PerfMonitor 日志关联");
        if (samples.Count > 0)
        {
            sb.AppendLine($"# 采样窗口: {samples[0].Timestamp:yyyy-MM-dd HH:mm:ss} ~ {samples[^1].Timestamp:yyyy-MM-dd HH:mm:ss}");
        }
        if (log == null)
        {
            sb.AppendLine("# 已禁用日志关联（--no-log）");
        }
        else
        {
            sb.AppendLine($"# 日志目录: {log.LogDir}");
            sb.AppendLine($"# 日志文件: {string.Join(", ", log.Files)}");
            sb.AppendLine($"# 扫描行数: {log.LinesScanned}，窗口内行数: {log.LinesInWindow}");
            sb.AppendLine($"# diag 扫描行数: {log.DiagLinesScanned}，窗口内事件: {log.DiagLinesInWindow}，zorder={log.DiagZOrderEvents.Count}，memory={log.DiagMemoryEvents.Count}");
            if (log.LevelCounts.Count > 0)
            {
                sb.AppendLine("# 级别统计: " + string.Join(", ", log.LevelCounts.OrderByDescending(p => p.Value).Select(p => $"{p.Key}={p.Value}")));
            }
            if (log.CategoryCounts.Count > 0)
            {
                sb.AppendLine("# 分类 Top: " + string.Join(", ", log.CategoryCounts.OrderByDescending(p => p.Value).Take(12).Select(p => $"{p.Key}={p.Value}")));
            }
            sb.AppendLine("# 说明: 仅列出尖峰前后窗口内的 app-*.log 行；diag 只保留 zorder/memory 摘要；敏感字段已脱敏。");
            sb.AppendLine();

            if (spikes.Count == 0)
            {
                sb.AppendLine("(未检测到 CPU 尖峰)");
            }
            else
            {
                foreach (var spikeLog in log.SpikeLogs)
                {
                    sb.AppendLine($"[尖峰 #{spikeLog.SpikeIndex}] {spikeLog.StartTime:yyyy-MM-dd HH:mm:ss.fff} ~ {spikeLog.EndTime:HH:mm:ss.fff}，共 {spikeLog.Lines.Count} 行");
                    if (spikeLog.Lines.Count == 0)
                    {
                        sb.AppendLine("  (该时间段没有 app 日志行)");
                    }
                    else
                    {
                        foreach (var line in spikeLog.Lines)
                        {
                            sb.AppendLine($"  {line.File}:{line.LineNumber}  {line.Text}");
                        }
                    }
                    sb.AppendLine();
                }
            }

            // diag-*.log：采样窗口内的 z-order 时间线
            sb.AppendLine("## diag z-order 时间线（采样窗口内）");
            if (log.DiagZOrderEvents.Count == 0)
            {
                sb.AppendLine("(采样窗口内没有 zorder 诊断事件)");
            }
            else
            {
                foreach (var ev in log.DiagZOrderEvents)
                    sb.AppendLine($"  {ev.Time:HH:mm:ss.fff}  {ev.Summary}");
            }
            sb.AppendLine();

            // 每个尖峰附近的 diag 事件
            sb.AppendLine("## 每个尖峰附近的 diag 事件");
            foreach (var sd in log.SpikeDiagEvents)
            {
                sb.AppendLine($"[尖峰 #{sd.SpikeIndex}] {sd.StartTime:HH:mm:ss.fff} ~ {sd.EndTime:HH:mm:ss.fff}，diag 事件 {sd.Events.Count} 条");
                if (sd.Events.Count == 0)
                {
                    sb.AppendLine("  (该时间段没有 diag 事件)");
                }
                else
                {
                    foreach (var ev in sd.Events)
                        sb.AppendLine($"  {ev.Time:HH:mm:ss.fff}  {ev.Summary}");
                }
                sb.AppendLine();
            }
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static string F1(double v) => v.ToString("F1", CultureInfo.InvariantCulture);
    private static string F2(double v) => v.ToString("F2", CultureInfo.InvariantCulture);
    private static string F3(double v) => v.ToString("F3", CultureInfo.InvariantCulture);

    private static string Csv(string text)
    {
        text ??= "";
        return text.Contains(',') || text.Contains('"') || text.Contains('\n') || text.Contains('\r')
            ? "\"" + text.Replace("\"", "\"\"") + "\""
            : text;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = (name ?? "process").Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}
