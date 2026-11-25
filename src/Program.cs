using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using WuwatappoNative.Core;
using WuwatappoNative.Net;
using WuwatappoNative.Pet;

namespace WuwatappoNative;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // WinMM 在少数驱动热插拔时会直接触发访问冲突；手柄扩展把 WinMM 轮询放到
        // 同一个 exe 的子进程，主程序只读命名管道，子进程崩溃不会带走 UI/账号。
        if (args.Length >= 2 && string.Equals(args[0], "--controller-bridge", StringComparison.Ordinal))
        {
            ControllerBridgeHost.Run(args[1]);
            return;
        }
        Paths.EnsureDirs();
        Log.Init(args);
        OfficialTokenReader.CleanupStaleDumps();
        // 单实例必须在提权/创建窗口之前判断；第二个实例只负责请求已有实例显示面板。
        if (!SingleInstance.Acquire())
        {
            SingleInstance.SignalExisting();
            Log.Write("已有实例运行，已请求显示面板");
            SingleInstance.Dispose();
            return;
        }
        // 管理员模式是显式设置。Raw Input 无法越过 UIPI：当前台程序权限更高时，
        // 普通权限实例收不到按键，只能以管理员权限重新启动自身。
        var startupCfg = ConfigStore.Load();
        if (startupCfg.RunAsAdmin && !Elevation.IsElevated &&
            Array.IndexOf(args, "--no-elevate") < 0)
        {
            SingleInstance.ReleaseForRelaunch();
            if (Elevation.TryRestartElevated()) return;
            // UAC 被取消：重新拿回锁，继续以普通权限运行。
            if (!SingleInstance.Acquire())
            {
                SingleInstance.SignalExisting();
                SingleInstance.Dispose();
                return;
            }
        }
        CleanupObsoleteUpdatePackages();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        LogStartupEnvironment(startupCfg);
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            if (e.ExceptionObject is Exception ex) Log.Exception("AppDomain.UnhandledException", ex);
            else Log.Error("AppDomain.UnhandledException: " + e.ExceptionObject, "exception");
        };
        Application.ThreadException += (s, e) => Log.Exception("Application.ThreadException", e.Exception);

        var app = new App();
        SingleInstance.SetActivationHandler(() => app.RequestPanelFromOtherInstance());
        try
        {
            Application.Run(app);
        }
        catch (Exception ex)
        {
            Log.Write("FATAL " + ex);
            Log.Exception("FATAL", ex);
            throw;
        }
        finally
        {
            SingleInstance.Dispose();
        }
    }

    /// <summary>
    /// 启动时清掉 updates 里全部早于当前版本的安装包；失败只记日志，不影响启动。
    /// </summary>
    static void CleanupObsoleteUpdatePackages()
    {
        try
        {
            var assembly = typeof(Program).Assembly;
            string current = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(current))
                current = assembly.GetName().Version?.ToString() ?? "";
            int deleted = UpdateCacheCleaner.CleanupObsoleteInstallers(Paths.UpdatesDir, current);
            if (deleted > 0) Log.Write($"已清理 {deleted} 个旧版安装包");
        }
        catch (Exception ex)
        {
            Log.Write("清理旧版安装包失败：" + ex.Message);
        }
    }

    /// <summary>启动环境：版本、系统、.NET、权限、屏幕/DPI 和关键设置；不记录任何凭据。</summary>
    static void LogStartupEnvironment(Settings cfg)
    {
        try
        {
            var asm = typeof(Program).Assembly;
            var sb = new StringBuilder();
            sb.Append("启动环境")
                .Append(" version=").Append(asm.GetName().Version?.ToString() ?? "")
                .Append(" informational=").Append(asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "")
                .Append(" os=").Append(Environment.OSVersion.VersionString)
                .Append(" build=").Append(Environment.OSVersion.Version.Build)
                .Append(" dotnet=").Append(Environment.Version)
                .Append(" x64=").Append(Environment.Is64BitProcess)
                .Append(" elevated=").Append(Elevation.IsElevated);

            var screens = Screen.AllScreens;
            sb.Append(" screens=").Append(screens.Length);
            for (int i = 0; i < screens.Length; i++)
            {
                var screen = screens[i];
                sb.Append(" screen[").Append(i).Append("]")
                    .Append(" device=").Append(screen.DeviceName)
                    .Append(" primary=").Append(screen.Primary)
                    .Append(" bounds=").Append(FormatRect(screen.Bounds))
                    .Append(" work=").Append(FormatRect(screen.WorkingArea))
                    .Append(" dpi=").Append(GetMonitorDpi(screen));
            }

            if (cfg != null)
            {
                sb.Append(" topmost=").Append(cfg.Topmost)
                    .Append(" clickThrough=").Append(cfg.ClickThrough)
                    .Append(" scale=").Append(cfg.Scale)
                    .Append(" serverRegion=").Append(cfg.ServerRegion)
                    .Append(" updateChannel=").Append(cfg.UpdateChannel)
                    .Append(" autoSynthUp=").Append(cfg.AutoSynthUp);
            }
            Log.Info(sb.ToString(), "startup");
        }
        catch (Exception ex)
        {
            Log.Exception("记录启动环境失败", ex);
        }
    }

    static string FormatRect(System.Drawing.Rectangle rect)
        => rect.Left + "," + rect.Top + "," + rect.Width + "x" + rect.Height;

    static uint GetMonitorDpi(Screen screen)
    {
        try
        {
            var point = new POINT
            {
                X = screen.Bounds.Left + screen.Bounds.Width / 2,
                Y = screen.Bounds.Top + screen.Bounds.Height / 2,
            };
            IntPtr monitor = MonitorFromPoint(point, 2 /* MONITOR_DEFAULTTONEAREST */);
            if (monitor == IntPtr.Zero) return 0;
            return GetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out uint dpiX, out _) == 0 ? dpiX : 0;
        }
        catch { return 0; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("Shcore.dll")]
    static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}
