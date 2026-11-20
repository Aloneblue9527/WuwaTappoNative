using System.Diagnostics;
using System.Text;

namespace PerfMonitor;

internal static class Program
{
    private static volatile bool _cancel;

    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("参数错误：" + ex.Message);
            Console.Error.WriteLine("用 --help 查看用法。");
            return 2;
        }

        if (options.ShowHelp)
        {
            Options.PrintUsage();
            return 0;
        }

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _cancel = true;
            Console.WriteLine();
            Console.WriteLine("收到 Ctrl+C，正在收尾并写报告…");
        };

        try
        {
            using var process = ProcessResolver.Resolve(options);
            var processInfo = ProcessResolver.ReadInfo(process, Math.Max(1, Environment.ProcessorCount));

            Console.WriteLine("PerfMonitor 1.0 — WuwatappoNative 性能采样");
            Console.WriteLine($"进程: {processInfo.Name} (PID {processInfo.Pid})，逻辑核: {processInfo.LogicalCores}");
            Console.WriteLine($"启动时间: {(processInfo.StartTime.HasValue ? processInfo.StartTime.Value.ToString("yyyy-MM-dd HH:mm:ss") : "未知")}");
            Console.WriteLine($"采样: {options.DurationSec:F0}s × {options.IntervalSec:F2}s，Top {options.TopThreads} 线程");
            Console.WriteLine($"报告目录: {options.OutDir}");
            Console.WriteLine();

            var tools = ToolLocator.Detect();
            ToolLocator.Print(tools);

            string baseName = ReportWriter.CreateBaseName(options.OutDir, processInfo.Name);
            CountersCollector? counters = null;
            if (options.WithCounters)
            {
                var counterTool = tools.FirstOrDefault(t => t.Name == "dotnet-counters");
                if (counterTool?.Path == null)
                {
                    Console.WriteLine("--with-counters 已指定，但系统未安装 dotnet-counters；继续使用 Process/Thread API。");
                }
                else
                {
                    counters = CountersCollector.TryStart(counterTool.Path, processInfo.Pid, options.OutDir, baseName);
                }
            }

            using (counters)
            {
                var sampler = new ProcessSampler(process);
                var samples = new List<Sample>();
                var clock = Stopwatch.StartNew();
                double nextAt = options.IntervalSec;

                ConsoleTable.PrintHeader();
                while (!_cancel && clock.Elapsed.TotalSeconds < options.DurationSec)
                {
                    while (!_cancel)
                    {
                        double wait = nextAt - clock.Elapsed.TotalSeconds;
                        if (wait <= 0) break;
                        Thread.Sleep(TimeSpan.FromMilliseconds(Math.Min(wait * 1000.0, 200.0)));
                    }
                    if (_cancel) break;

                    var sample = sampler.TakeSample(samples.Count);
                    samples.Add(sample);
                    ConsoleTable.PrintRow(sample);

                    nextAt += options.IntervalSec;

                    bool exited = false;
                    try { exited = process.HasExited; } catch { }
                    if (exited)
                    {
                        Console.WriteLine("目标进程已退出，提前结束采样。");
                        break;
                    }
                }

                if (counters != null) counters.Stop();

                if (samples.Count == 0)
                {
                    Console.Error.WriteLine("没有采到任何样本（目标进程可能已退出）。");
                    return 3;
                }

                var stats = StatsCalculator.Compute(samples, options, processInfo.LogicalCores);
                var spikes = SpikeAnalyzer.Detect(samples, options);
                LogCorrelationResult? log = options.NoLog ? null : LogCorrelator.Correlate(options, samples, spikes);

                var paths = ReportWriter.Write(
                    options, processInfo, samples, spikes, stats, log, tools,
                    counters?.Result, baseName);

                ConsoleTable.PrintTopThreads(samples, Math.Max(10, options.TopThreads));
                ConsoleTable.PrintSummary(stats, spikes, paths, processInfo);
                if (log != null)
                {
                    Console.WriteLine($"diag: 扫描 {log.DiagLinesScanned} 行，zorder 事件 {log.DiagZOrderEvents.Count}，memory 事件 {log.DiagMemoryEvents.Count}");
                }
                if (counters?.Result.Started == true)
                {
                    Console.WriteLine("dotnet-counters 输出: " + counters.Result.OutputPath);
                }
                return 0;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("错误：" + ex.Message);
            return 2;
        }
    }
}
