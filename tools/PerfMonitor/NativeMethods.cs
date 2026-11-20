using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PerfMonitor;

internal static class NativeMethods
{
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetThreadTimes(IntPtr hThread, out long creationTime, out long exitTime, out long kernelTime, out long userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenThread(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwThreadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern int GetThreadDescription(IntPtr hThread, out IntPtr ppszThreadDescription);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr LocalFree(IntPtr hMem);

    private const uint ThreadQueryLimitedInformation = 0x0800;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>
    /// 用 PROCESS_QUERY_LIMITED_INFORMATION 打开目标进程再读 GDI/USER。
    /// Process.Handle 需要 PROCESS_ALL_ACCESS，面对管理员权限目标会失败；limited handle 可以。
    /// </summary>
    internal static (uint Gdi, uint User)? GetGuiResourceCounts(int processId)
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
            if (handle == IntPtr.Zero) return null;
            uint gdi = GetGuiResources(handle, 0);
            uint user = GetGuiResources(handle, 1);
            if (gdi == 0 && user == 0) return null;
            return (gdi, user);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (handle != IntPtr.Zero) CloseHandle(handle);
        }
    }

    /// <summary>
    /// 用 THREAD_QUERY_LIMITED_INFORMATION 读取线程内核+用户时间。
    /// ProcessThread.TotalProcessorTime 对管理员权限目标会抛“拒绝访问”，这个 fallback 可以工作。
    /// </summary>
    internal static double? GetThreadTotalCpuMs(int threadId)
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = OpenThread(ThreadQueryLimitedInformation, false, (uint)threadId);
            if (handle == IntPtr.Zero) return null;
            if (!GetThreadTimes(handle, out _, out _, out long kernel, out long user)) return null;
            // FILETIME 单位 100ns，/10000 => 毫秒。
            return (kernel + user) / 10000.0;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (handle != IntPtr.Zero) CloseHandle(handle);
        }
    }

    /// <summary>读取 Windows 线程描述（SetThreadDescription）。失败或无权访问时返回空串。</summary>
    internal static string GetThreadDescriptionSafe(int threadId)
    {
        IntPtr handle = IntPtr.Zero;
        IntPtr text = IntPtr.Zero;
        try
        {
            handle = OpenThread(ThreadQueryLimitedInformation, false, (uint)threadId);
            if (handle == IntPtr.Zero) return "";
            int hr = GetThreadDescription(handle, out text);
            if (hr < 0 || text == IntPtr.Zero) return "";
            return Marshal.PtrToStringUni(text)?.Trim() ?? "";
        }
        catch
        {
            return "";
        }
        finally
        {
            if (text != IntPtr.Zero) LocalFree(text);
            if (handle != IntPtr.Zero) CloseHandle(handle);
        }
    }

    /// <summary>GDI/USER 对象数；取不到时返回 null。</summary>
    internal static (uint Gdi, uint User)? GetGuiResourceCounts(IntPtr processHandle)
    {
        try
        {
            uint gdi = GetGuiResources(processHandle, 0);
            uint user = GetGuiResources(processHandle, 1);
            if (gdi == 0 && user == 0 && Marshal.GetLastWin32Error() != 0) return null;
            return (gdi, user);
        }
        catch
        {
            return null;
        }
    }
}
