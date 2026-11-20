using System.Globalization;

namespace PerfMonitor;

/// <summary>命令行参数。所有时间单位均为秒，CPU 百分比默认按逻辑核归一化。</summary>
public sealed class Options
{
    public int Pid { get; set; }
    public string ProcessName { get; set; } = "WuwatappoNative";
    public double DurationSec { get; set; } = 300;
    public double IntervalSec { get; set; } = 1.0;
    public int TopThreads { get; set; } = 8;
    public string OutDir { get; set; } = Path.Combine(Environment.CurrentDirectory, "out", "perf");
    public string? LogDir { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WuwatappoNative", "logs");
    public bool NoLog { get; set; }
    public bool WithCounters { get; set; }
    public double SpikeFactor { get; set; } = 2.0;
    public double SpikeMinDeltaPct { get; set; } = 0.75;
    public int BaselineWindow { get; set; } = 60;
    public int WarmupSamples { get; set; } = 5;
    public double LogWindowBeforeSec { get; set; } = 20;
    public double LogWindowAfterSec { get; set; } = 10;
    public bool ShowHelp { get; set; }

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next()
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"参数 {a} 缺少取值");
                return args[++i];
            }

            switch (a)
            {
                case "-h":
                case "--help":
                    o.ShowHelp = true;
                    break;
                case "--pid":
                    o.Pid = ParseInt(Next(), a);
                    break;
                case "--process":
                    o.ProcessName = Next().Trim();
                    if (o.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        o.ProcessName = o.ProcessName[..^4];
                    break;
                case "--duration":
                    o.DurationSec = ParseDouble(Next(), a);
                    break;
                case "--interval":
                    o.IntervalSec = ParseDouble(Next(), a);
                    break;
                case "--top":
                    o.TopThreads = ParseInt(Next(), a);
                    break;
                case "--out":
                    o.OutDir = Path.GetFullPath(Next());
                    break;
                case "--log-dir":
                    o.LogDir = Path.GetFullPath(Next());
                    break;
                case "--no-log":
                    o.NoLog = true;
                    break;
                case "--with-counters":
                    o.WithCounters = true;
                    break;
                case "--spike-factor":
                    o.SpikeFactor = ParseDouble(Next(), a);
                    break;
                case "--spike-min-delta":
                    o.SpikeMinDeltaPct = ParseDouble(Next(), a);
                    break;
                case "--baseline-window":
                    o.BaselineWindow = ParseInt(Next(), a);
                    break;
                case "--warmup":
                    o.WarmupSamples = ParseInt(Next(), a);
                    break;
                case "--log-before":
                    o.LogWindowBeforeSec = ParseDouble(Next(), a);
                    break;
                case "--log-after":
                    o.LogWindowAfterSec = ParseDouble(Next(), a);
                    break;
                default:
                    throw new ArgumentException("未知参数：" + a + "（用 --help 查看用法）");
            }
        }

        if (o.Pid <= 0 && string.IsNullOrWhiteSpace(o.ProcessName))
            throw new ArgumentException("必须指定 --pid 或 --process");
        if (o.IntervalSec < 0.2) throw new ArgumentException("--interval 不能小于 0.2 秒");
        if (o.DurationSec < 1) throw new ArgumentException("--duration 不能小于 1 秒");
        if (o.TopThreads < 1 || o.TopThreads > 64) throw new ArgumentException("--top 必须在 1..64 之间");
        if (o.SpikeFactor < 1.05) throw new ArgumentException("--spike-factor 不能小于 1.05");
        if (o.SpikeMinDeltaPct < 0) throw new ArgumentException("--spike-min-delta 不能为负数");
        if (o.BaselineWindow < 5) throw new ArgumentException("--baseline-window 不能小于 5");
        if (o.WarmupSamples < 0) throw new ArgumentException("--warmup 不能为负数");
        return o;
    }

    static int ParseInt(string text, string name)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? v : throw new ArgumentException($"{name} 需要整数，收到：{text}");

    static double ParseDouble(string text, string name)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? v : throw new ArgumentException($"{name} 需要数字，收到：{text}");

    public static void PrintUsage()
    {
        Console.WriteLine("""
PerfMonitor —— WuwatappoNative 进程性能采样工具（.NET 8，无 NuGet 依赖）

用法：
  dotnet run --project tools/PerfMonitor -c Release -- [选项]

附加目标：
  --pid <PID>             附加指定进程；优先级高于 --process
  --process <名称>        按进程名查找，默认 WuwatappoNative（可省略 .exe）

采样：
  --duration <秒>         总时长，默认 300
  --interval <秒>         采样间隔，默认 1.0（最小 0.2）
  --top <N>               每次采样输出前 N 个线程，默认 8

输出：
  --out <目录>            报告目录，默认 <当前目录>/out/perf
  --log-dir <目录>        app-*.log 所在目录，默认 %LOCALAPPDATA%\WuwatappoNative\logs
  --no-log                不做日志关联
  --with-counters         检测到 dotnet-counters 时顺带采集一份 counters CSV

尖峰判定：
  --spike-factor <倍数>   基线倍数，默认 2.0
  --spike-min-delta <pp>  相对基线的最小增量（归一化百分点），默认 0.75
  --baseline-window <N>   基线取最近 N 个样本的中位数，默认 60
  --warmup <N>            前 N 个样本只用于预热，默认 5

日志关联窗口：
  --log-before <秒>       尖峰开始前多少秒，默认 20
  --log-after <秒>        尖峰结束后多少秒，默认 10

说明：
  * 只读采样，不会结束、挂起或注入目标进程。
  * CPU% 同时输出“按逻辑核归一化”和“单核百分比”；线程 CPU% 为单核百分比。
  * 报告中不会写入 token / Cookie / 密码；日志行会做敏感字段脱敏。
""");
    }
}
