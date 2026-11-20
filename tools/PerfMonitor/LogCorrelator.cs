using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PerfMonitor;

/// <summary>
/// 读取 %LOCALAPPDATA%\WuwatappoNative\logs\app-*.log，把尖峰前后若干秒的日志行对齐到报告。
/// 只读、按行流式读取；对 token/cookie/password 等敏感字段做脱敏。
/// </summary>
public static class LogCorrelator
{
    private static readonly Regex LinePrefix = new(
        @"^(?<time>\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3}) \[(?<level>[A-Z]+)\s*\](?:\[(?<cat>[^\]]+)\])?",
        RegexOptions.Compiled);

    private static readonly Regex Sensitive = new(
        @"(?i)(?<key>token|access_token|refresh_token|authorization|cookie|set-cookie|password|passwd|secret|session_id|sessionid)\s*[:=]\s*(?<value>[^\s,;""']+|""[^""]*"")",
        RegexOptions.Compiled);

    private static readonly Regex SensitiveJson = new(
        @"""(?<key>token|access_token|refresh_token|authorization|cookie|password|passwd|secret|session_id|sessionid)""\s*:\s*""(?<value>[^""]*)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BearerToken = new(
        @"(?i)\bBearer\s+[A-Za-z0-9\-._~+/]+=*",
        RegexOptions.Compiled);

    private static readonly Regex JwtToken = new(
        @"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b",
        RegexOptions.Compiled);

    private static readonly Regex CookieOrAuthHeader = new(
        @"(?i)\b(?<key>cookie|set-cookie|authorization)\s*[:=]\s*[^\r\n]+",
        RegexOptions.Compiled);

    public static LogCorrelationResult Correlate(Options options, List<Sample> samples, List<Spike> spikes)
    {
        var result = new LogCorrelationResult
        {
            LogDir = options.LogDir ?? "",
            WindowStart = samples.Count > 0 ? samples[0].Timestamp.AddMinutes(-1) : DateTime.Now,
            WindowEnd = samples.Count > 0 ? samples[^1].Timestamp.AddMinutes(1) : DateTime.Now,
        };

        if (string.IsNullOrWhiteSpace(options.LogDir) || !Directory.Exists(options.LogDir))
        {
            result.Files.Add("(日志目录不存在)");
            return result;
        }

        var files = Directory.EnumerateFiles(options.LogDir, "app-*.log", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .Where(f => f.LastWriteTime >= result.WindowStart.AddDays(-1))
            .OrderByDescending(f => f.LastWriteTime)
            .Take(4)
            .ToList();

        // 分卷 app-yyyy-MM-dd.1.log 也纳入，但只读最近修改的少量文件。
        if (files.Count == 0)
        {
            files = Directory.EnumerateFiles(options.LogDir, "app-*.log*", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .Where(f => f.LastWriteTime >= result.WindowStart.AddDays(-1))
                .OrderByDescending(f => f.LastWriteTime)
                .Take(4)
                .ToList();
        }

        if (files.Count == 0)
        {
            result.Files.Add("(最近时间段没有 app-*.log)");
            return result;
        }

        result.Files.AddRange(files.Select(f => f.FullName));

        var all = new List<LogLine>();
        foreach (var file in files)
        {
            ReadFile(file.FullName, result, all);
        }

        result.LinesInWindow = all.Count;
        foreach (var line in all)
        {
            var m = LinePrefix.Match(line.Text);
            if (!m.Success) continue;
            string level = m.Groups["level"].Value.Trim();
            string category = m.Groups["cat"].Success ? m.Groups["cat"].Value.Trim() : "(none)";
            result.LevelCounts[level] = result.LevelCounts.GetValueOrDefault(level) + 1;
            result.CategoryCounts[category] = result.CategoryCounts.GetValueOrDefault(category) + 1;
        }

        foreach (var spike in spikes)
        {
            var from = spike.StartTime.AddSeconds(-options.LogWindowBeforeSec);
            var to = spike.EndTime.AddSeconds(options.LogWindowAfterSec);
            var lines = all
                .Where(l => l.Time >= from && l.Time <= to)
                .OrderBy(l => l.Time)
                .Take(200)
                .ToList();
            result.SpikeLogs.Add(new SpikeLog
            {
                SpikeIndex = spike.Index,
                StartTime = spike.StartTime,
                EndTime = spike.EndTime,
                Lines = lines,
            });
        }

        var diag = DiagCorrelator.Correlate(options, samples, spikes);
        result.DiagLinesScanned = diag.Scanned;
        result.DiagLinesInWindow = diag.InWindow;
        result.DiagZOrderEvents = diag.ZOrder;
        result.DiagMemoryEvents = diag.Memory;
        result.SpikeDiagEvents = diag.SpikeEvents;

        return result;
    }

    private static void ReadFile(string path, LogCorrelationResult result, List<LogLine> target)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            int lineNo = 0;
            while (reader.ReadLine() is { } line)
            {
                lineNo++;
                result.LinesScanned++;
                if (line.Length < 24) continue;
                if (!DateTime.TryParseExact(line.AsSpan(0, 23), "yyyy-MM-dd HH:mm:ss.fff",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                    continue;
                if (time < result.WindowStart || time > result.WindowEnd) continue;

                target.Add(new LogLine
                {
                    File = Path.GetFileName(path),
                    LineNumber = lineNo,
                    Time = time,
                    Text = Sanitize(line),
                });
            }
        }
        catch
        {
            // 日志正被写入时读取失败不应影响采样报告。
        }
    }

    /// <summary>把 token=... / "token":"..." 等替换为 &lt;redacted&gt;；保留其它排查信息。</summary>
    public static string Sanitize(string line)
    {
        if (string.IsNullOrEmpty(line)) return "";
        // 先处理最常见的“整段凭据”形态，避免值里带空格时只脱敏掉第一个单词。
        string text = CookieOrAuthHeader.Replace(line, m => $"{m.Groups["key"].Value}=<redacted>");
        text = BearerToken.Replace(text, "Bearer <redacted>");
        text = JwtToken.Replace(text, "<redacted-jwt>");
        text = SensitiveJson.Replace(text, m => $"\"{m.Groups["key"].Value}\":\"<redacted>\"");
        text = Sensitive.Replace(text, m => $"{m.Groups["key"].Value}=<redacted>");
        return text;
    }
}
