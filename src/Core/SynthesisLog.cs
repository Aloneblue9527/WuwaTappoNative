using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using WuwatappoNative.Core;

namespace WuwatappoNative.Core;

/// <summary>
/// 合成快照：logs\synth-yyyy-MM-dd.jsonl，一行一个操作。只记录物品 id、名称、类型、稀有度和数量，
/// 不记录 token/Cookie/Authorization 等凭据。任何序列化或写入失败都不能影响合成流程。
/// </summary>
public static class SynthesisLog
{
    public const int SchemaVersion = 1;
    static long _sequence;

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public static string NewOperationId(string prefix = "synth")
        => (prefix ?? "synth") + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")
            + "-" + Interlocked.Increment(ref _sequence).ToString("D4");

    public static AutoSynthesisSnapshot StartAuto(bool manual, Settings cfg)
    {
        return new AutoSynthesisSnapshot
        {
            SchemaVersion = SchemaVersion,
            OperationId = NewOperationId("auto"),
            Time = DateTimeOffset.Now,
            Trigger = manual ? "manual" : "auto",
            Mode = "normal-tier-up",
            Settings = new AutoSynthesisSettingsSnapshot
            {
                AutoSynthUp = cfg?.AutoSynthUp ?? false,
                AnyItemSynthesisEnabled = cfg?.AnyItemSynthesisEnabled ?? false,
            },
            Bag = new AutoSynthesisBagSnapshot(),
            Rounds = new List<AutoSynthesisRoundSnapshot>(),
            Result = new AutoSynthesisResultSnapshot(),
        };
    }

    public static ManualSynthesisSnapshot StartManual(bool sameLevel, IEnumerable<long> assetIds)
    {
        var ids = assetIds?.ToList() ?? new List<long>();
        return new ManualSynthesisSnapshot
        {
            SchemaVersion = SchemaVersion,
            OperationId = NewOperationId("manual"),
            Time = DateTimeOffset.Now,
            Mode = sameLevel ? "same-level" : "normal",
            SameLevel = sameLevel,
            RequestAssetIds = ids,
            Assets = new List<ManualSynthesisAssetSnapshot>(),
            Result = new ManualSynthesisResultSnapshot(),
        };
    }

    public static void WriteAuto(AutoSynthesisSnapshot snapshot)
    {
        if (snapshot == null) return;
        try
        {
            snapshot.SchemaVersion = SchemaVersion;
            snapshot.Result ??= new AutoSynthesisResultSnapshot();
            Log.WriteSynthesisLine(Serialize(snapshot));
        }
        catch { }
    }

    public static void WriteManual(ManualSynthesisSnapshot snapshot)
    {
        if (snapshot == null) return;
        try
        {
            snapshot.SchemaVersion = SchemaVersion;
            snapshot.Result ??= new ManualSynthesisResultSnapshot();
            Log.WriteSynthesisLine(Serialize(snapshot));
        }
        catch { }
    }

    /// <summary>序列化为单行 JSON；测试也通过它验证字段与隐私约束。</summary>
    internal static string Serialize(object snapshot)
        => JsonSerializer.Serialize(snapshot, snapshot?.GetType() ?? typeof(object), JsonOptions);
}

public sealed class AutoSynthesisSnapshot
{
    public int SchemaVersion { get; set; }
    public string OperationId { get; set; } = "";
    public DateTimeOffset Time { get; set; }
    public string Trigger { get; set; } = "auto";
    public string Mode { get; set; } = "normal-tier-up";
    public AutoSynthesisSettingsSnapshot Settings { get; set; } = new();
    public AutoSynthesisBagSnapshot Bag { get; set; } = new();
    public List<AutoSynthesisRoundSnapshot> Rounds { get; set; } = new();
    public AutoSynthesisResultSnapshot Result { get; set; } = new();
}

public sealed class AutoSynthesisSettingsSnapshot
{
    public bool AutoSynthUp { get; set; }
    public bool AnyItemSynthesisEnabled { get; set; }
}

public sealed class AutoSynthesisBagSnapshot
{
    public int Total { get; set; }
    public bool Truncated { get; set; }
    public List<AutoSynthesisItemSnapshot> Items { get; set; } = new();
}

public sealed class AutoSynthesisItemSnapshot
{
    public long AssetId { get; set; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Rarity { get; set; } = "";
    public long Quantity { get; set; }
    public int Usable { get; set; }
}

public sealed class AutoSynthesisRoundSnapshot
{
    public int Round { get; set; }
    public string GroupKey { get; set; } = "";
    public List<long> ConsumedAssetIds { get; set; } = new();
    public List<AutoSynthesisMaterialSnapshot> Materials { get; set; } = new();
    public int RetryCount { get; set; }
    public long RequestElapsedMs { get; set; }
    public long ResponseAssetId { get; set; }
    public string ResponseAssetType { get; set; } = "";
    public string ResponseRarity { get; set; } = "";
    public string ResponseName { get; set; } = "";
    public string Error { get; set; } = "";
}

public sealed class AutoSynthesisMaterialSnapshot
{
    public long AssetId { get; set; }
    public int Quantity { get; set; }
}

public sealed class AutoSynthesisResultSnapshot
{
    public bool Success { get; set; }
    public bool Busy { get; set; }
    public int Processed { get; set; }
    public int Count { get; set; }
    public List<long> AssetIds { get; set; } = new();
    public string Message { get; set; } = "";
    public long ElapsedMs { get; set; }
    public bool Cancelled { get; set; }
    public string Error { get; set; } = "";
}

public sealed class ManualSynthesisSnapshot
{
    public int SchemaVersion { get; set; }
    public string OperationId { get; set; } = "";
    public DateTimeOffset Time { get; set; }
    public string Mode { get; set; } = "normal";
    public bool SameLevel { get; set; }
    public List<long> RequestAssetIds { get; set; } = new();
    public List<ManualSynthesisAssetSnapshot> Assets { get; set; } = new();
    public ManualSynthesisResultSnapshot Result { get; set; } = new();
}

public sealed class ManualSynthesisAssetSnapshot
{
    public long AssetId { get; set; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Rarity { get; set; } = "";
}

public sealed class ManualSynthesisResultSnapshot
{
    public bool Success { get; set; }
    public long ResponseAssetId { get; set; }
    public string ResponseAssetType { get; set; } = "";
    public string ResponseRarity { get; set; } = "";
    public string ResponseName { get; set; } = "";
    public long ElapsedMs { get; set; }
    public string Error { get; set; } = "";
    public ApiExceptionSnapshot ApiException { get; set; }
}

/// <summary>ApiException 的可记录字段；包含 request_id/path，不包含请求头或凭据。</summary>
public sealed class ApiExceptionSnapshot
{
    public int Code { get; set; }
    public string RequestId { get; set; } = "";
    public string Path { get; set; } = "";
}
