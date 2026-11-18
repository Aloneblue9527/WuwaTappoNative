using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace WuwatappoNative.Core;

/// <summary>
/// 应用日志。三套按天滚动的文件都放在 logs 目录：
/// app-yyyy-MM-dd.log（普通日志）、diag-yyyy-MM-dd.log（诊断快照）、synth-yyyy-MM-dd.jsonl（合成快照）。
/// 保留最近 7 个自然日；单文件超过软上限后生成 .1/.2 分卷。所有写入失败都静默，不能影响主流程。
/// </summary>
public static class Log
{
    const int RetainDays = 7;
    const long AppLimitBytes = 16L * 1024 * 1024;
    const long DiagLimitBytes = 16L * 1024 * 1024;
    const long SynthLimitBytes = 32L * 1024 * 1024;

    static readonly object Gate = new();
    static readonly LogTarget AppTarget = new("app", ".log", AppLimitBytes);
    static readonly LogTarget DiagTarget = new("diag", ".log", DiagLimitBytes);
    static readonly LogTarget SynthTarget = new("synth", ".jsonl", SynthLimitBytes);
    static bool _verbose;
    static bool _initialized;

    public static void Init(string[] args = null)
    {
        lock (Gate)
        {
            _verbose = args != null && Array.IndexOf(args, "--verbose") >= 0;
            try { Paths.EnsureDirs(); } catch { }
            if (_initialized) return;
            _initialized = true;

            string migrated = null;
            try { migrated = MigrateLegacyLog(Paths.Root, Paths.LogsDir, DateTime.UtcNow); } catch { }
            try { CleanupOldLogsCore(Paths.LogsDir, DateTime.UtcNow, ActivePaths()); } catch { }

            WriteLine(AppTarget, "INFO ", null,
                "---- start " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ----");
            if (!string.IsNullOrEmpty(migrated))
                WriteLine(AppTarget, "INFO ", "log", "旧日志已迁移: " + migrated);
        }
    }

    /// <summary>兼容旧调用：按 Info 记录。旧代码里的 DBG 前缀只在 --verbose 下输出。</summary>
    public static void Write(string message)
    {
        if (!_verbose && message != null && message.StartsWith("DBG ", StringComparison.Ordinal)) return;
        Info(message);
    }

    public static void Debug(string message, string category = null)
    {
        if (!_verbose) return;
        WriteLine(AppTarget, "DEBUG", category, message);
    }

    public static void Info(string message, string category = null)
        => WriteLine(AppTarget, "INFO ", category, message);

    public static void Warn(string message, string category = null)
        => WriteLine(AppTarget, "WARN ", category, message);

    public static void Error(string message, string category = null)
        => WriteLine(AppTarget, "ERROR", category, message);

    /// <summary>记录完整异常（含 inner exception 与堆栈）。</summary>
    public static void Exception(string context, Exception ex)
    {
        string text = string.IsNullOrEmpty(context) ? "异常" : context;
        if (ex != null) text += ": " + ex;
        WriteLine(AppTarget, "ERROR", "exception", text);
    }

    /// <summary>诊断快照写入 diag-yyyy-MM-dd.log；用于 z-order 等需要现场数据的排查。</summary>
    public static void Diagnostic(string category, string message)
        => WriteLine(DiagTarget, "DIAG ", category, message);

    public static void DiagnosticException(string category, string context, Exception ex)
    {
        string text = string.IsNullOrEmpty(context) ? "诊断异常" : context;
        if (ex != null) text += ": " + ex;
        WriteLine(DiagTarget, "DIAG ", category, text);
    }

    /// <summary>合成快照：一行一个合法 JSON，不添加时间前缀。由 SynthesisLog 调用。</summary>
    internal static void WriteSynthesisLine(string json)
    {
        if (json == null) return;
        try
        {
            lock (Gate)
            {
                SynthTarget.WriteLineRaw(json + Environment.NewLine, DateTime.Now);
            }
        }
        catch { }
    }

    /// <summary>
    /// 清理 logs 目录内超过 7 个自然日的日志。按 LastWriteTimeUtc 判断，绝不递归子目录，
    /// 且排除当前正在写的文件。供启动、跨天切换和测试调用。
    /// </summary>
    internal static int CleanupOldLogs(string logsDir, DateTime nowUtc)
    {
        lock (Gate)
        {
            try { return CleanupOldLogsCore(logsDir, nowUtc, ActivePaths()); }
            catch { return 0; }
        }
    }

    /// <summary>把旧版根目录 log.txt 迁移到 logs\app-yyyy-MM-dd.log；超过保留期直接删除。</summary>
    internal static string MigrateLegacyLog(string rootDir, string logsDir, DateTime nowUtc)
    {
        try
        {
            string legacy = Path.Combine(rootDir ?? "", "log.txt");
            if (string.IsNullOrEmpty(rootDir) || !File.Exists(legacy)) return null;

            var info = new FileInfo(legacy);
            if (info.LastWriteTimeUtc.Date < nowUtc.Date.AddDays(-(RetainDays - 1)))
            {
                File.Delete(legacy);
                return null;
            }

            Directory.CreateDirectory(logsDir);
            string target = Path.Combine(logsDir, "app-" + info.LastWriteTime.ToString("yyyy-MM-dd") + ".log");
            if (File.Exists(target))
            {
                // 目标日期已有日志：追加而不是覆盖，迁移失败时保留旧文件。
                File.AppendAllText(target, File.ReadAllText(legacy), new UTF8Encoding(false));
                File.Delete(legacy);
            }
            else
            {
                try { File.Move(legacy, target); }
                catch
                {
                    File.Copy(legacy, target, true);
                    File.Delete(legacy);
                }
            }
            return target;
        }
        catch { return null; }
    }

    static int CleanupOldLogsCore(string logsDir, DateTime nowUtc, IReadOnlyCollection<string> excluded)
    {
        if (string.IsNullOrWhiteSpace(logsDir) || !Directory.Exists(logsDir)) return 0;
        DateTime cutoff = nowUtc.Date.AddDays(-(RetainDays - 1));
        var excludedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (excluded != null)
        {
            foreach (var path in excluded)
            {
                if (!string.IsNullOrEmpty(path)) excludedSet.Add(Path.GetFullPath(path));
            }
        }

        int deleted = 0;
        string[] patterns = { "app-*.log", "synth-*.jsonl", "diag-*.log" };
        foreach (string pattern in patterns)
        {
            foreach (string file in Directory.EnumerateFiles(logsDir, pattern, SearchOption.TopDirectoryOnly))
            {
                try
                {
                    string full = Path.GetFullPath(file);
                    if (excludedSet.Contains(full)) continue;
                    if (File.GetLastWriteTimeUtc(full).Date >= cutoff) continue;
                    File.Delete(full);
                    deleted++;
                }
                catch { }
            }
        }
        return deleted;
    }

    static string[] ActivePaths()
    {
        return new[] { AppTarget.Path, DiagTarget.Path, SynthTarget.Path }
            .Where(p => !string.IsNullOrEmpty(p))
            .ToArray();
    }

    static void WriteLine(LogTarget target, string level, string category, string message)
    {
        try
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level + "]"
                + (string.IsNullOrEmpty(category) ? "" : "[" + category + "]")
                + " " + (message ?? "");
            lock (Gate)
            {
                target.WriteLine(line, DateTime.Now);
            }
        }
        catch { }
    }

    /// <summary>按日期打开 StreamWriter；AutoFlush 保证崩溃前的日志尽量落盘。</summary>
    sealed class LogTarget
    {
        readonly string _prefix;
        readonly string _extension;
        readonly long _limitBytes;
        string _date;
        int _part;
        StreamWriter _writer;
        string _path;

        public LogTarget(string prefix, string extension, long limitBytes)
        {
            _prefix = prefix;
            _extension = extension;
            _limitBytes = limitBytes;
        }

        public string Path => _path;

        public void WriteLine(string line, DateTime now)
        {
            EnsureWriter(now);
            if (_writer == null) return;
            _writer.WriteLine(line);
        }

        /// <summary>合成 JSONL 使用原始行，不能加日志前缀，否则不再是合法 JSON。</summary>
        public void WriteLineRaw(string text, DateTime now)
        {
            EnsureWriter(now);
            if (_writer == null) return;
            _writer.Write(text);
        }

        void EnsureWriter(DateTime now)
        {
            string date = now.ToString("yyyy-MM-dd");
            if (_writer != null && string.Equals(_date, date, StringComparison.Ordinal))
            {
                if (_writer.BaseStream.Length < _limitBytes) return;
                Open(date, _part + 1);
                return;
            }

            Open(date, 0);
            // 跨天切换文件时顺手清理；放 Open 之后，新文件已在保护列表中。
            try { Log.CleanupOldLogsCore(Paths.LogsDir, DateTime.UtcNow, Log.ActivePaths()); } catch { }
        }

        void Open(string date, int part)
        {
            Close();
            string path = BuildPath(date, part);
            // 同名分卷已满时跳到下一个分卷，避免继续往超大文件追加。
            for (int guard = 0; guard < 100 && File.Exists(path); guard++)
            {
                try
                {
                    if (new FileInfo(path).Length < _limitBytes) break;
                }
                catch { break; }
                part++;
                path = BuildPath(date, part);
            }

            try
            {
                Directory.CreateDirectory(Paths.LogsDir);
                var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                _date = date;
                _part = part;
                _path = path;
            }
            catch
            {
                _writer = null;
                _path = path;
            }
        }

        string BuildPath(string date, int part)
        {
            string name = _prefix + "-" + date + (part <= 0 ? "" : "." + part) + _extension;
            return System.IO.Path.Combine(Paths.LogsDir, name);
        }

        void Close()
        {
            try { _writer?.Dispose(); } catch { }
            _writer = null;
        }
    }
}
