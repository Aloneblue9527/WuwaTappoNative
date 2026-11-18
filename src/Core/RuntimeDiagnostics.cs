using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WuwatappoNative.Core;

/// <summary>
/// 进程级内存/GDI/USER 句柄观测。周期采样只读计数器，不触发 GC，也不参与缓存淘汰。
/// 普通周期记录写入 diag 日志的 memory 分类；超过 1GB 或发生渲染 OOM 时才升级为 Warn。
/// </summary>
internal static class RuntimeDiagnostics
{
    const long WarnBytes = 1024L * 1024 * 1024;

    /// <summary>渲染 OOM 后窗口暂停重试的秒数。</summary>
    public const int RenderRetrySeconds = 10;

    [DllImport("user32.dll")]
    static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    public readonly record struct Snapshot(long WorkingSetBytes, long PrivateBytes, uint GdiObjects, uint UserObjects);

    public static Snapshot Sample()
    {
        long workingSet = 0, privateBytes = 0;
        try
        {
            using var process = Process.GetCurrentProcess();
            workingSet = process.WorkingSet64;
            privateBytes = process.PrivateMemorySize64;
        }
        catch { }

        uint gdi = 0, user = 0;
        try
        {
            IntPtr handle = GetCurrentProcess();
            gdi = GetGuiResources(handle, 0);
            user = GetGuiResources(handle, 1);
        }
        catch { }

        return new Snapshot(workingSet, privateBytes, gdi, user);
    }

    /// <summary>周期采样：普通情况写 diag，超过 1GB 时写 Warn。visiblePets 由 UI 线程统计。</summary>
    public static void LogPeriodic(int visiblePets)
    {
        try
        {
            var sample = Sample();
            string line = Format(sample) + " visiblePets=" + Math.Max(0, visiblePets);
            if (sample.WorkingSetBytes >= WarnBytes || sample.PrivateBytes >= WarnBytes)
                Log.Warn(line + " thresholdBytes=" + WarnBytes, "memory");
            else
                Log.Diagnostic("memory", line);
        }
        catch { }
    }

    /// <summary>渲染 OOM：Warn 记录现场（含成员 id/内存/GDI），完整堆栈另写 diag。</summary>
    public static void LogRenderOom(string source, long memberId, Exception ex)
    {
        try
        {
            var sample = Sample();
            string line = "render-oom source=" + (source ?? "unknown")
                + " member=" + memberId
                + " " + Format(sample)
                + " nextRetrySeconds=" + RenderRetrySeconds
                + " exception=" + (ex == null ? "" : ex.GetType().Name + ": " + ex.Message);
            Log.Warn(line, "render");
            Log.DiagnosticException("render", line, ex);
        }
        catch { }
    }

    static string Format(Snapshot sample)
        => "workingSetBytes=" + sample.WorkingSetBytes
            + " privateBytes=" + sample.PrivateBytes
            + " gdi=" + sample.GdiObjects
            + " user=" + sample.UserObjects;
}
