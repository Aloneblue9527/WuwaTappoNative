using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PerfMonitor;

/// <summary>
/// 读取 diag-*.log，把 z-order / memory / render 等诊断事件对齐到采样窗口和尖峰。
/// z-order 原始行约 28KB，这里只输出去掉 JSON 主体后的短摘要，避免报告膨胀。
/// </summary>
public static class DiagCorrelator
{
    private static readonly Regex LinePrefix = new(
        @"^(?<time>\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3}) \[DIAG\s*\]\[(?<cat>[^\]]+)\]",
        RegexOptions.Compiled);

    private static readonly Regex MemoryFields = new(
        @"workingSetBytes=(?<ws>\d+) privateBytes=(?<priv>\d+) gdi=(?<gdi>\d+) user=(?<user>\d+) visiblePets=(?<pets>\d+)",
        RegexOptions.Compiled);

    public static (int Scanned, int InWindow, List<DiagEvent> ZOrder, List<DiagEvent> Memory, List<SpikeDiag> SpikeEvents)
        Correlate(Options options, List<Sample> samples, List<Spike> spikes)
    {
        var zorder = new List<DiagEvent>();
        var memory = new List<DiagEvent>();
        var spikeEvents = new List<SpikeDiag>();
        if (string.IsNullOrWhiteSpace(options.LogDir) || !Directory.Exists(options.LogDir) || samples.Count == 0)
            return (0, 0, zorder, memory, spikeEvents);

        var windowStart = samples[0].Timestamp.AddMinutes(-1);
        var windowEnd = samples[^1].Timestamp.AddMinutes(1);

        var files = Directory.EnumerateFiles(options.LogDir, "diag-*.log", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .Where(f => f.LastWriteTime >= windowStart.AddDays(-1))
            .OrderByDescending(f => f.LastWriteTime)
            .Take(2)
            .ToList();
        if (files.Count == 0) return (0, 0, zorder, memory, spikeEvents);

        var all = new List<DiagEvent>();
        int scanned = 0;
        foreach (var file in files)
        {
            try
            {
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
                while (reader.ReadLine() is { } line)
                {
                    scanned++;
                    if (line.Length < 32) continue;
                    var m = LinePrefix.Match(line);
                    if (!m.Success) continue;
                    if (!DateTime.TryParseExact(line.AsSpan(0, 23), "yyyy-MM-dd HH:mm:ss.fff",
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                        continue;
                    if (time < windowStart || time > windowEnd) continue;

                    string category = m.Groups["cat"].Value.Trim();
                    string message = line.Length > m.Length ? line[m.Length..].TrimStart() : "";
                    var ev = new DiagEvent
                    {
                        Time = time,
                        Category = category,
                        LineBytes = line.Length,
                        Summary = Summarize(category, message, line.Length),
                    };
                    all.Add(ev);
                    if (category.Equals("zorder", StringComparison.OrdinalIgnoreCase)) zorder.Add(ev);
                    else if (category.Equals("memory", StringComparison.OrdinalIgnoreCase)) memory.Add(ev);
                }
            }
            catch
            {
                // 诊断日志正被写入时读取失败不影响主报告。
            }
        }

        foreach (var spike in spikes)
        {
            var from = spike.StartTime.AddSeconds(-options.LogWindowBeforeSec);
            var to = spike.EndTime.AddSeconds(options.LogWindowAfterSec);
            var events = all
                .Where(e => e.Time >= from && e.Time <= to)
                .OrderBy(e => e.Time)
                .Take(200)
                .ToList();
            spikeEvents.Add(new SpikeDiag
            {
                SpikeIndex = spike.Index,
                StartTime = spike.StartTime,
                EndTime = spike.EndTime,
                Events = events,
            });
        }

        zorder.Sort((a, b) => a.Time.CompareTo(b.Time));
        memory.Sort((a, b) => a.Time.CompareTo(b.Time));
        return (scanned, all.Count, zorder, memory, spikeEvents);
    }

    private static string Summarize(string category, string message, int lineChars)
    {
        if (category.Equals("zorder", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;
                string reason = GetString(root, "reason");
                string detail = GetString(root, "detail");
                int windows = GetArrayLength(root, "topLevel");
                int pets = GetArrayLength(root, "pets");
                return $"zorder reason={reason} detail={detail} windows={windows} pets={pets} chars={lineChars}";
            }
            catch
            {
                return Truncate("zorder " + message, 240);
            }
        }

        if (category.Equals("memory", StringComparison.OrdinalIgnoreCase))
        {
            var m = MemoryFields.Match(message);
            if (m.Success)
            {
                double ws = long.Parse(m.Groups["ws"].Value, CultureInfo.InvariantCulture) / 1024.0 / 1024.0;
                double priv = long.Parse(m.Groups["priv"].Value, CultureInfo.InvariantCulture) / 1024.0 / 1024.0;
                return $"memory ws={ws:F1}MB priv={priv:F1}MB gdi={m.Groups["gdi"].Value} user={m.Groups["user"].Value} pets={m.Groups["pets"].Value}";
            }
        }

        return Truncate(category + " " + message, 240);
    }

    private static string GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static int GetArrayLength(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.GetArrayLength()
            : 0;

    private static string Truncate(string text, int max)
        => string.IsNullOrEmpty(text) || text.Length <= max ? text : text[..max] + "…";
}
