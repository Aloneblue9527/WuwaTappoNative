using System;
using System.IO;
using System.Text.Json.Nodes;

namespace WuwatappoNative.Core;

/// <summary>单个账号/区服的点击账本：累计 gross 与尚未上报的本地点击数。</summary>
public sealed class ClickLedger
{
    public string Cuid { get; set; } = "";
    public string Region { get; set; } = "cn";
    public long Cumulative { get; set; }
    public long Pending { get; set; }
    public long UpdatedAtMs { get; set; }
}

/// <summary>
/// 点击账本持久化。官方 1.0.5 会保存 localClickCount/cumulativeClickCount，并在登录后按
/// “缓存累计 − 服务器累计”恢复 pending；Native 使用独立 JSON，避免重启丢失 +10000。
/// </summary>
public static class ClickLedgerStore
{
    static readonly object Gate = new();

    public static ClickLedger Load(string cuid, string region)
    {
        cuid = (cuid ?? "").Trim();
        region = NormalizeRegion(region);
        if (string.IsNullOrEmpty(cuid)) return new ClickLedger { Region = region };
        lock (Gate)
        {
            try
            {
                var root = ReadRoot();
                if (root?["accounts"] is JsonObject accounts &&
                    accounts[Key(region, cuid)] is JsonObject item)
                {
                    return new ClickLedger
                    {
                        Cuid = cuid,
                        Region = region,
                        Cumulative = ReadLong(item["cumulative"]),
                        Pending = ReadLong(item["pending"]),
                        UpdatedAtMs = ReadLong(item["updatedAtMs"]),
                    };
                }
            }
            catch (Exception ex) { Log.Write("点击账本读取失败：" + ex.Message); }
            return new ClickLedger { Cuid = cuid, Region = region };
        }
    }

    public static void Save(ClickLedger ledger)
    {
        if (ledger == null) return;
        string cuid = (ledger.Cuid ?? "").Trim();
        string region = NormalizeRegion(ledger.Region);
        if (string.IsNullOrEmpty(cuid)) return;
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Paths.Root);
                var root = ReadRoot() ?? new JsonObject { ["schema"] = 1 };
                if (root["accounts"] is not JsonObject accounts)
                {
                    accounts = new JsonObject();
                    root["accounts"] = accounts;
                }
                accounts[Key(region, cuid)] = new JsonObject
                {
                    ["cuid"] = cuid,
                    ["region"] = region,
                    ["cumulative"] = Math.Max(0, ledger.Cumulative),
                    ["pending"] = Math.Max(0, ledger.Pending),
                    ["updatedAtMs"] = ledger.UpdatedAtMs > 0
                        ? ledger.UpdatedAtMs
                        : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                };
                string tmp = Paths.ClickLedgerFile + ".tmp";
                File.WriteAllText(tmp, root.ToJsonString(new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                }));
                File.Move(tmp, Paths.ClickLedgerFile, true);
            }
            catch (Exception ex) { Log.Write("点击账本写入失败：" + ex.Message); }
        }
    }

    static JsonObject ReadRoot()
    {
        if (!File.Exists(Paths.ClickLedgerFile)) return null;
        return JsonNode.Parse(File.ReadAllText(Paths.ClickLedgerFile)) as JsonObject;
    }

    static string NormalizeRegion(string region)
        => string.Equals((region ?? "").Trim(), "global", StringComparison.OrdinalIgnoreCase) ? "global" : "cn";

    static string Key(string region, string cuid) => region + ":" + cuid;

    static long ReadLong(JsonNode node)
    {
        try
        {
            if (node is JsonValue value)
            {
                if (value.TryGetValue<long>(out long l)) return l;
                if (value.TryGetValue<int>(out int i)) return i;
                if (value.TryGetValue<string>(out string s) && long.TryParse(s, out long parsed)) return parsed;
            }
        }
        catch { }
        return 0;
    }
}
