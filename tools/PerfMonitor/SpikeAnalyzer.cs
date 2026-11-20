namespace PerfMonitor;

public static class SpikeAnalyzer
{
    /// <summary>
    /// 以“最近 N 个样本的中位数”为基线：连续 2 个样本超过 max(基线×倍数, 基线+最小增量) 记为尖峰；
    /// 尖峰内取峰值样本，持续到最后一个仍高于阈值的样本。基线接近 0 时用最小增量兜底，避免把噪声当尖峰。
    /// </summary>
    public static List<Spike> Detect(List<Sample> samples, Options options)
    {
        var spikes = new List<Spike>();
        if (samples.Count == 0) return spikes;

        for (int i = 0; i < samples.Count; i++)
        {
            var history = new List<double>(Math.Min(options.BaselineWindow, i));
            for (int j = Math.Max(0, i - options.BaselineWindow); j < i; j++)
            {
                if (j >= options.WarmupSamples) history.Add(samples[j].CpuNormalizedPct);
            }

            double baseline = history.Count >= 3 ? Percentile(history, 0.5) : 0;
            samples[i].BaselinePct = baseline;
            samples[i].SpikeThresholdPct = history.Count >= 3
                ? Math.Max(baseline * options.SpikeFactor, baseline + options.SpikeMinDeltaPct)
                : double.PositiveInfinity;
        }

        int index = options.WarmupSamples;
        while (index < samples.Count - 1)
        {
            var s = samples[index];
            if (double.IsInfinity(s.SpikeThresholdPct) || s.CpuNormalizedPct < s.SpikeThresholdPct)
            {
                index++;
                continue;
            }

            // 连续两个采样都高于阈值才算尖峰。
            var next = samples[index + 1];
            if (double.IsInfinity(next.SpikeThresholdPct) || next.CpuNormalizedPct < next.SpikeThresholdPct)
            {
                index++;
                continue;
            }

            int start = index;
            int peakIndex = index;
            int lastAbove = index + 1;
            double endThreshold = Math.Max(samples[start].BaselinePct * 1.25, samples[start].BaselinePct + 0.25);

            for (int j = index + 1; j < samples.Count; j++)
            {
                if (samples[j].CpuNormalizedPct > samples[peakIndex].CpuNormalizedPct)
                    peakIndex = j;

                if (!double.IsInfinity(samples[j].SpikeThresholdPct)
                    && samples[j].CpuNormalizedPct >= samples[j].SpikeThresholdPct)
                {
                    lastAbove = j;
                    continue;
                }

                // 连续两个采样回落到结束阈值以下才认为尖峰结束；避免单点抖动把尖峰切碎。
                if (samples[j].CpuNormalizedPct < endThreshold
                    && (j + 1 >= samples.Count || samples[j + 1].CpuNormalizedPct < endThreshold))
                {
                    break;
                }
            }

            var peakSample = samples[peakIndex];
            var topThreads = peakSample.Threads
                .OrderByDescending(t => t.DeltaMs)
                .ThenByDescending(t => t.CumulativeMs)
                .Take(Math.Max(1, options.TopThreads))
                .ToList();

            var spike = new Spike
            {
                Index = spikes.Count + 1,
                StartTime = samples[start].Timestamp,
                EndTime = samples[lastAbove].Timestamp,
                StartElapsedSec = samples[start].ElapsedSec,
                EndElapsedSec = samples[lastAbove].ElapsedSec,
                DurationSec = Math.Max(0, samples[lastAbove].ElapsedSec - samples[start].ElapsedSec + options.IntervalSec),
                PeakCpuPct = peakSample.CpuNormalizedPct,
                PeakSingleCorePct = peakSample.CpuSingleCorePct,
                BaselinePct = samples[start].BaselinePct,
                ThresholdPct = samples[start].SpikeThresholdPct,
                PeakSampleIndex = peakIndex,
                PeakThread = topThreads.FirstOrDefault(),
                TopThreadsAtPeak = topThreads,
                Ongoing = lastAbove >= samples.Count - 1,
            };

            if (spikes.Count > 0)
            {
                var previous = spikes[^1];
                spike.GapSincePreviousEndSec = Math.Max(0, spike.StartElapsedSec - previous.EndElapsedSec);
                spike.StartIntervalSec = Math.Max(0, spike.StartElapsedSec - previous.StartElapsedSec);
            }

            spikes.Add(spike);
            index = Math.Max(start + 2, lastAbove + 1);
        }

        return spikes;
    }

    public static double Percentile(IReadOnlyCollection<double> values, double p)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToArray();
        int rank = (int)Math.Ceiling(p * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }
}

public static class StatsCalculator
{
    public static Stats Compute(List<Sample> samples, Options options, int logicalCores)
    {
        var stats = new Stats
        {
            SampleCount = samples.Count,
            DurationSec = samples.Count == 0 ? 0 : samples[^1].ElapsedSec,
            LogicalCores = logicalCores,
        };
        if (samples.Count == 0) return stats;

        var cpu = samples.Select(s => s.CpuNormalizedPct).ToList();
        var core = samples.Select(s => s.CpuSingleCorePct).ToList();
        var ws = samples.Select(s => s.WorkingSetBytes / 1024.0 / 1024.0).ToList();
        var priv = samples.Select(s => s.PrivateBytes / 1024.0 / 1024.0).ToList();
        var gdi = samples.Select(s => s.GdiObjects).ToList();
        var user = samples.Select(s => s.UserObjects).ToList();
        var handles = samples.Select(s => s.HandleCount).ToList();
        var threads = samples.Select(s => s.ThreadCount).ToList();
        var threadDelta = samples.Select(s => s.ThreadCpuDeltaMs).ToList();

        stats.CpuMedianPct = SpikeAnalyzer.Percentile(cpu, 0.5);
        stats.CpuP95Pct = SpikeAnalyzer.Percentile(cpu, 0.95);
        stats.CpuMaxPct = cpu.Max();
        stats.CpuMeanPct = cpu.Average();

        stats.CoreMedianPct = SpikeAnalyzer.Percentile(core, 0.5);
        stats.CoreP95Pct = SpikeAnalyzer.Percentile(core, 0.95);
        stats.CoreMaxPct = core.Max();

        stats.WsMedianMB = SpikeAnalyzer.Percentile(ws, 0.5);
        stats.WsP95MB = SpikeAnalyzer.Percentile(ws, 0.95);
        stats.WsMaxMB = ws.Max();
        stats.WsGrowthMB = ws[^1] - ws[0];

        stats.PrivateMedianMB = SpikeAnalyzer.Percentile(priv, 0.5);
        stats.PrivateP95MB = SpikeAnalyzer.Percentile(priv, 0.95);
        stats.PrivateMaxMB = priv.Max();
        stats.PrivateGrowthMB = priv[^1] - priv[0];

        stats.GdiMedian = (uint)SpikeAnalyzer.Percentile(gdi.Select(v => (double)v).ToList(), 0.5);
        stats.GdiMax = gdi.Max();
        stats.UserMedian = (uint)SpikeAnalyzer.Percentile(user.Select(v => (double)v).ToList(), 0.5);
        stats.UserMax = user.Max();
        stats.HandleMedian = (int)SpikeAnalyzer.Percentile(handles.Select(v => (double)v).ToList(), 0.5);
        stats.HandleMax = handles.Max();
        stats.ThreadMedian = (int)SpikeAnalyzer.Percentile(threads.Select(v => (double)v).ToList(), 0.5);
        stats.ThreadMax = threads.Max();
        stats.ThreadCpuDeltaMedianMs = SpikeAnalyzer.Percentile(threadDelta, 0.5);
        stats.ThreadCpuDeltaMaxMs = threadDelta.Max();

        return stats;
    }
}
