using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WuwatappoNative.Core;

/// <summary>
/// 前台全屏检测：窗口矩形完整覆盖其所在显示器时视为全屏。
/// 只做只读 Win32 查询，不注入、不挂接目标进程。
/// </summary>
internal static class FullscreenDetector
{
    const int GwlExStyle = -20;
    const long WsExToolWindow = 0x00000080L;
    const uint MonitorDefaultToNearest = 2;
    const int DwmwaCloaked = 14;
    const int CoverTolerance = 4;

    static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "TaskSwitcherWnd",
        "ForegroundStaging",
        "XamlExplorerHostIslandWindow",
        "ApplicationManager_DesktopShellWindow",
    };

    [StructLayout(LayoutKind.Sequential)]
    struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    /// <summary>当前前台窗口是否全屏；processName 仅用于日志，不参与判断。</summary>
    public static bool IsForegroundFullscreen(out string processName)
    {
        processName = "";
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == (uint)Environment.ProcessId) return false;
            if (ShellClasses.Contains(ClassName(hwnd))) return false;
            if (IsCloaked(hwnd)) return false;

            long exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            if ((exStyle & WsExToolWindow) != 0) return false;

            if (!GetWindowRect(hwnd, out Rect window)) return false;
            IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero) return false;
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info)) return false;
            if (!Covers(window, info.Monitor)) return false;

            try
            {
                using var process = Process.GetProcessById((int)pid);
                processName = process.ProcessName;
            }
            catch { }
            return true;
        }
        catch { return false; }
    }

    internal static bool Covers(int left, int top, int right, int bottom,
        int monitorLeft, int monitorTop, int monitorRight, int monitorBottom)
        => left <= monitorLeft + CoverTolerance
            && top <= monitorTop + CoverTolerance
            && right >= monitorRight - CoverTolerance
            && bottom >= monitorBottom - CoverTolerance;

    static bool Covers(Rect window, Rect monitor)
        => Covers(window.Left, window.Top, window.Right, window.Bottom,
            monitor.Left, monitor.Top, monitor.Right, monitor.Bottom);

    static string ClassName(IntPtr hwnd)
    {
        try
        {
            var sb = new StringBuilder(256);
            GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }
        catch { return ""; }
    }

    static bool IsCloaked(IntPtr hwnd)
    {
        try
        {
            return DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
        }
        catch { return false; }
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);
    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
