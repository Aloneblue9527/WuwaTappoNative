using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using WuwatappoNative.Core;
using WuwatappoNative.Net;

namespace WuwatappoNative.Assets;

public sealed class AssetFrame
{
    public string Url;
    public double OffsetX, OffsetY;
}

public sealed class AssetEntry
{
    public long Id;
    public string Type = "";
    public string Url = "";
    public double OffsetX, OffsetY;
    public readonly Dictionary<string, AssetFrame> Frames = new(StringComparer.OrdinalIgnoreCase);
    public string Name = "";
    public string SeriesName = "";
    public string Rarity = "common";
    public long GameRoleId;
    public long HeightLightId;
    public bool IsAtlas;

    public AssetFrame Frame(string key) => Frames.TryGetValue(key, out var f) ? f : null;
}

/// <summary>
/// 外观目录。真实链路：activity-config → assets_host → /app/json-host/appearance_asset_host.json → appearance_asset.json。
/// 六个区：character_skin / accessory / emote 及对应 *_atlas。名称来自 api/common/atlas。
/// </summary>
public sealed class Catalog
{
    public const string GlobalAssetsHost = "https://wutheringwaves-wuwatappo.kurogames-global.com";
    public string AssetsHost { get; private set; } = "";
    public long AssetsJsonTs { get; private set; }
    public string Locale { get; private set; } = "zh";
    string _dictKey = "";
    public readonly Dictionary<string, Dictionary<long, AssetEntry>> ByType = new();
    public readonly Dictionary<long, string> Names = new();
    public readonly Dictionary<long, string> Series = new();
    public readonly Dictionary<long, string> Rarities = new();
    public readonly Dictionary<string, long> AtlasIdByName = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, long> IdByAssetName = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>正片 id → 素材文件名（来自图鉴 height_light_id 反查），用于显示名称。</summary>
    public readonly Dictionary<long, string> AssetNameById = new();
    public readonly Dictionary<string, string> DictCharacter = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, string> DictSeries = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, string> DictAccessory = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, string> DictEmote = new(StringComparer.OrdinalIgnoreCase);
    public bool DictReady { get; private set; }
    public event Action Loaded;
    readonly System.Threading.SemaphoreSlim _loadGate = new(1, 1);

    public int Count(string type) => ByType.TryGetValue(type, out var m) ? m.Count : 0;
    public IEnumerable<AssetEntry> All(string type) => ByType.TryGetValue(type, out var m) ? m.Values : Enumerable.Empty<AssetEntry>();
    public AssetEntry Find(string type, long id) => ByType.TryGetValue(type, out var m) && m.TryGetValue(id, out var e) ? e : null;

    static string NormalizeLocale(string locale)
    {
        string v = (locale ?? "").Trim().ToLowerInvariant();
        return v == "en" || v == "ja" ? v : "zh";
    }

    static string DictionaryKey(string locale)
        => locale == "en" ? "en" : locale == "ja" ? "jp" : "zh";

    static string CacheSuffix(string locale)
        => locale == "en" ? "en" : locale == "ja" ? "ja" : "";

    static string AcceptLanguageFor(string locale)
        => locale == "en" ? "en-US,en;q=0.9" : locale == "ja" ? "ja-JP,ja;q=0.9,en;q=0.8" : "zh-CN,zh;q=0.9,en;q=0.8";

    static string CatalogCachePath(string locale)
    {
        string suffix = CacheSuffix(locale);
        return string.IsNullOrEmpty(suffix) ? Paths.CatalogCache : Path.Combine(Paths.CacheDir, "appearance_asset_" + suffix + ".json");
    }

    static string CatalogMetaPath(string locale)
    {
        string suffix = CacheSuffix(locale);
        return string.IsNullOrEmpty(suffix) ? Paths.CatalogMeta : Path.Combine(Paths.CacheDir, "appearance_meta_" + suffix + ".json");
    }

    static string DictionaryCachePath(string key)
        => Path.Combine(Paths.CacheDir, "dict_" + key + ".json");

    public string CdnUrl(string relative)
    {
        if (string.IsNullOrEmpty(relative)) return "";
        var host = (AssetsHost ?? "").TrimEnd('/');
        return host + "/" + relative.TrimStart('/');
    }

    public string AtlasPath(string assetType, string assetName)
    {
        string folder = assetType switch
        {
            "character_skin" => "character_atlas",
            "accessory" => "accessory_atlas",
            "emote" => "emote_atlas",
            "character_skin_atlas" => "character_atlas",
            "accessory_atlas" => "accessory_atlas",
            "emote_atlas" => "emote_atlas",
            _ => assetType
        };
        if (string.IsNullOrEmpty(assetName)) return "";
        return folder + "/" + assetName;
    }

    public async Task<bool> LoadAsync(ApiClient api, Action<string> progress = null, string locale = null)
    {
        await _loadGate.WaitAsync();
        try { return await LoadAsyncCore(api, progress, locale); }
        finally { _loadGate.Release(); }
    }

    async Task<bool> LoadAsyncCore(ApiClient api, Action<string> progress, string locale)
    {
        string targetLocale = NormalizeLocale(locale);
        Locale = targetLocale;
        bool useGlobal = targetLocale != "zh";
        bool fromCdn = false;
        ApiClient catalogApi = api;
        ApiClient globalApi = null;
        try
        {
            if (useGlobal)
            {
                // 非中文界面固定读取国际服公开 JSON，才能拿到英文/日文图鉴数据。
                globalApi = new ApiClient(api.Cid, ApiClient.GlobalBaseUrl, AcceptLanguageFor(targetLocale));
                catalogApi = globalApi;
                AssetsHost = GlobalAssetsHost;
                AssetsJsonTs = 0;
            }
            else
            {
                progress?.Invoke("读取活动配置…");
                var cfg = await api.GetAsync("api/common/activity-config");
                if (cfg is JsonObject o)
                {
                    AssetsHost = ApiClient.ReadString(o["assets_host"]).TrimEnd('/');
                    AssetsJsonTs = ApiClient.ReadLong(o["assets_json_ts"]);
                }
                if (string.IsNullOrEmpty(AssetsHost)) throw new InvalidOperationException("activity-config 未返回 assets_host");
            }

            progress?.Invoke(useGlobal ? "读取国际服素材目录地址…" : "读取素材目录地址…");
            var hostFile = await GetJsonAsync($"{AssetsHost}/app/json-host/appearance_asset_host.json?v={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
            string catalogUrl = ApiClient.ReadString(hostFile?["url"]);
            if (string.IsNullOrEmpty(catalogUrl)) throw new InvalidOperationException("appearance_asset_host.json 缺少 url");

            progress?.Invoke(useGlobal ? "下载国际服外观目录…" : "下载外观目录…");
            var root = await GetJsonAsync(catalogUrl);
            Parse(root);
            try
            {
                Directory.CreateDirectory(Paths.CacheDir);
                File.WriteAllText(CatalogCachePath(targetLocale), root.ToJsonString());
                File.WriteAllText(CatalogMetaPath(targetLocale), new JsonObject
                {
                    ["host"] = AssetsHost,
                    ["ts"] = AssetsJsonTs,
                    ["locale"] = targetLocale
                }.ToJsonString());
            }
            catch (Exception ex) { Log.Write("目录缓存写入失败 " + ex.Message); }
            fromCdn = true;
            Log.Write($"目录加载完成 locale={targetLocale} host={AssetsHost} skins={Count("character_skin")} accessories={Count("accessory")} emotes={Count("emote")}");
        }
        catch (Exception ex)
        {
            Log.Write("CDN 目录失败，使用离线兜底：" + ex.Message);
            progress?.Invoke("CDN 不可用，使用离线目录");
            LoadOffline(targetLocale);
        }

        try
        {
            progress?.Invoke(targetLocale == "zh" ? "读取中文名称字典…" : "读取本地化名称字典…");
            await LoadDictionariesAsync(targetLocale);
        }
        catch (Exception ex)
        {
            Log.Write("名称字典加载失败 " + ex.Message);
            LoadCachedDictionary(targetLocale);
        }

        try
        {
            progress?.Invoke("读取图鉴名称…");
            bool namesLoaded = await LoadNamesAsync(catalogApi);
            if (!namesLoaded && useGlobal)
            {
                try { await LoadNamesAsync(api); } catch (Exception fallback) { Log.Write("图鉴名称回退失败 " + fallback.Message); }
            }
        }
        catch (Exception ex)
        {
            Log.Write("图鉴名称加载失败 " + ex.Message);
            if (useGlobal)
            {
                try { await LoadNamesAsync(api); } catch (Exception fallback) { Log.Write("图鉴名称回退失败 " + fallback.Message); }
            }
        }
        finally
        {
            globalApi?.Dispose();
        }

        Loaded?.Invoke();
        return fromCdn;
    }

    public void LoadOffline(string locale = null)
    {
        string targetLocale = NormalizeLocale(locale ?? Locale);
        Locale = targetLocale;
        if (_dictKey != DictionaryKey(targetLocale) || !DictReady) LoadCachedDictionary(targetLocale);
        try
        {
            string path = File.Exists(CatalogCachePath(targetLocale))
                ? CatalogCachePath(targetLocale)
                : (targetLocale != "zh" && File.Exists(Paths.CatalogCache) ? Paths.CatalogCache : Paths.BundledCatalog);
            string metaPath = File.Exists(CatalogMetaPath(targetLocale))
                ? CatalogMetaPath(targetLocale)
                : (targetLocale == "zh" && File.Exists(Paths.CatalogMeta) ? Paths.CatalogMeta : null);
            if (!string.IsNullOrEmpty(metaPath))
            {
                var meta = JsonNode.Parse(File.ReadAllText(metaPath));
                AssetsHost = ApiClient.ReadString(meta?["host"]);
                AssetsJsonTs = ApiClient.ReadLong(meta?["ts"]);
            }
            else if (targetLocale != "zh")
            {
                // 没有本语言缓存时也不能把国际服目录的资源 URL 指到国服域。
                AssetsHost = GlobalAssetsHost;
                AssetsJsonTs = 0;
            }
            if (File.Exists(path))
            {
                Parse(JsonNode.Parse(File.ReadAllText(path)));
                Log.Write("离线目录已载入：" + path);
            }
        }
        catch (Exception ex) { Log.Write("离线目录载入失败 " + ex.Message); }
    }

    void Parse(JsonNode root)
    {
        ByType.Clear();
        if (root is not JsonObject obj) return;
        foreach (var key in new[] { "character_skin", "accessory", "emote", "character_skin_atlas", "accessory_atlas", "emote_atlas" })
        {
            var map = new Dictionary<long, AssetEntry>();
            if (obj[key] is JsonObject section)
            {
                foreach (var pair in section)
                {
                    if (!long.TryParse(pair.Key, out var id) || pair.Value is not JsonObject e) continue;
                    var entry = new AssetEntry
                    {
                        Id = id,
                        Type = key,
                        Url = ApiClient.ReadString(e["url"]),
                        OffsetX = ReadDouble(e["offset_x"]),
                        OffsetY = ReadDouble(e["offset_y"]),
                        GameRoleId = ApiClient.ReadLong(e["game_role_id"]),
                        HeightLightId = ApiClient.ReadLong(e["height_light_id"]),
                        IsAtlas = key.EndsWith("_atlas", StringComparison.Ordinal),
                    };
                    foreach (var frameKey in new[] { "body1", "body2", "body3", "head1", "head2" })
                    {
                        if (e[frameKey] is JsonObject f)
                            entry.Frames[frameKey] = new AssetFrame
                            {
                                Url = ApiClient.ReadString(f["url"]),
                                OffsetX = ReadDouble(f["offset_x"]),
                                OffsetY = ReadDouble(f["offset_y"]),
                            };
                    }
                    map[id] = entry;
                }
            }
            ByType[key] = map;
        }
    }

    async Task LoadDictionariesAsync(string locale)
    {
        if (string.IsNullOrEmpty(AssetsHost)) return;
        string key = DictionaryKey(NormalizeLocale(locale));
        var host = await GetJsonAsync($"{AssetsHost}/app/json-host/dict_host.json");
        string url = ApiClient.ReadString(host?["url"]?[key]).Trim();
        if (string.IsNullOrEmpty(url)) throw new InvalidOperationException("dict_host.json 缺少 " + key);
        var dict = await GetJsonAsync(url);
        try { File.WriteAllText(DictionaryCachePath(key), dict.ToJsonString()); } catch { }
        ApplyDictionary(dict);
        _dictKey = key;
        Log.Write($"名称字典 locale={key} character={DictCharacter.Count} series={DictSeries.Count} accessory={DictAccessory.Count} emote={DictEmote.Count}");
    }

    public bool LoadCachedDictionary(string locale = null)
    {
        string key = DictionaryKey(NormalizeLocale(locale ?? Locale));
        foreach (string candidate in new[] { key, "zh" })
        {
            try
            {
                var path = DictionaryCachePath(candidate);
                if (!File.Exists(path)) continue;
                ApplyDictionary(JsonNode.Parse(File.ReadAllText(path)));
                if (DictReady)
                {
                    _dictKey = candidate;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Write("字典缓存读取失败 " + candidate + " " + ex.Message);
            }
        }
        return false;
    }

    void ApplyDictionary(JsonNode dict)
    {
        DictCharacter.Clear(); DictSeries.Clear(); DictAccessory.Clear(); DictEmote.Clear();
        FillDict(dict?["character"], DictCharacter);
        FillDict(dict?["series"], DictSeries);
        FillDict(dict?["accessory"], DictAccessory);
        FillDict(dict?["emote"], DictEmote);
        DictReady = DictCharacter.Count > 0;
    }

    static void FillDict(JsonNode node, Dictionary<string, string> into)
    {
        if (node is not JsonObject o) return;
        foreach (var pair in o)
        {
            string value = ApiClient.ReadString(pair.Value);
            if (!string.IsNullOrEmpty(value)) into[pair.Key] = value;
        }
    }

    static string StripPng(string name) => (name ?? "").Trim().Replace(".png", "", StringComparison.OrdinalIgnoreCase);

    /// <summary>按原版规则拼显示名：角色 = 角色名 ・ 系列名；配件 = 配件名；表情 = 角色名 ・ 表情名。</summary>
    public string DisplayName(string assetType, string assetName)
    {
        string n = (assetName ?? "").Trim();
        if (string.IsNullOrEmpty(n)) return "";
        string type = (assetType ?? "").ToLowerInvariant();
        if (type.StartsWith("character_skin"))
        {
            var m = System.Text.RegularExpressions.Regex.Match(StripPng(n), @"^role_(\d+)_(\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success)
            {
                DictCharacter.TryGetValue(m.Groups[1].Value, out var ch);
                DictSeries.TryGetValue(m.Groups[2].Value, out var se);
                return JoinParts(ch, se);
            }
        }
        else if (type.StartsWith("accessory"))
        {
            var m = System.Text.RegularExpressions.Regex.Match(StripPng(n), @"^accessory_(\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            string key = m.Success ? m.Groups[1].Value : StripPng(n);
            if (DictAccessory.TryGetValue(key, out var acc)) return acc;
        }
        else if (type.StartsWith("emote"))
        {
            string baseName = StripPng(n);
            if (DictEmote.TryGetValue(baseName + ".png", out var full)) return full;
            var m = System.Text.RegularExpressions.Regex.Match(baseName, @"^emote_(\d+)_([a-z0-9]+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success)
            {
                DictCharacter.TryGetValue(m.Groups[1].Value, out var ch);
                DictEmote.TryGetValue(m.Groups[2].Value, out var em);
                if (string.IsNullOrEmpty(em)) DictEmote.TryGetValue(m.Groups[2].Value + ".png", out em);
                return JoinParts(ch, em);
            }
        }
        // 兜底：字典里按完整文件名查
        foreach (var map in new[] { DictCharacter, DictAccessory, DictEmote })
            if (map.TryGetValue(n, out var v) || map.TryGetValue(StripPng(n), out v)) return v;
        return "";
    }

    /// <summary>素材缩略图：先用 asset_name 找到图鉴 asset_id，再取 CDN 目录里的 atlas 图片。</summary>
    public string ThumbUrl(string assetType, string assetName)
    {
        string atlasType = assetType switch
        {
            "character_skin" => "character_skin_atlas",
            "accessory" => "accessory_atlas",
            "emote" => "emote_atlas",
            _ => assetType
        };
        if (!string.IsNullOrEmpty(assetName) && AtlasIdByName.TryGetValue(assetName, out var atlasId))
        {
            var entry = Find(atlasType, atlasId);
            if (entry != null && !string.IsNullOrEmpty(entry.Url)) return CdnUrl(entry.Url);
        }
        return CdnUrl(AtlasPath(assetType, assetName));
    }

    public string AtlasThumb(string atlasType, long atlasId)
    {
        var entry = Find(atlasType, atlasId);
        return entry != null && !string.IsNullOrEmpty(entry.Url) ? CdnUrl(entry.Url) : "";
    }

    /// <summary>联机 b/c 字段可能是数字 id，也可能是 role_xxx_yyy.png 这样的素材名。</summary>
    public long ResolveId(string assetType, string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return 0;
        key = key.Trim();
        if (long.TryParse(key, out var id)) return id;
        if (IdByAssetName.TryGetValue(key, out var resolved)) return resolved;
        if (IdByAssetName.TryGetValue(key + ".png", out resolved)) return resolved;
        if (IdByAssetName.TryGetValue(StripPng(key), out resolved)) return resolved;
        return 0;
    }

    public string SeriesName(string assetType, string assetName)
    {
        string type = (assetType ?? "").ToLowerInvariant();
        string n = StripPng(assetName);
        if (type.StartsWith("character_skin"))
        {
            var m = System.Text.RegularExpressions.Regex.Match(n, @"^role_(\d+)_(\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success && DictSeries.TryGetValue(m.Groups[2].Value, out var se)) return se;
        }
        return "";
    }

    static string JoinParts(string a, string b)
    {
        a = (a ?? "").Trim();
        b = (b ?? "").Trim();
        if (string.IsNullOrEmpty(a)) return b;
        if (string.IsNullOrEmpty(b)) return a;
        return a + "・" + b;
    }

    public async Task<JsonNode> FetchAtlasAsync(ApiClient fallbackApi, string locale = null)
    {
        string targetLocale = NormalizeLocale(locale ?? Locale);
        if (fallbackApi == null) return null;
        if (targetLocale == "zh") return await fallbackApi.GetAsync("api/common/atlas");
        try
        {
            using var globalApi = new ApiClient(fallbackApi.Cid, ApiClient.GlobalBaseUrl, AcceptLanguageFor(targetLocale));
            var data = await globalApi.GetAsync("api/common/atlas");
            if (data is JsonArray) return data;
            Log.Write("国际服图鉴列表返回非数组，回退当前区服");
            return await fallbackApi.GetAsync("api/common/atlas");
        }
        catch (Exception ex)
        {
            Log.Write("国际服图鉴列表失败，回退当前区服：" + ex.Message);
            return await fallbackApi.GetAsync("api/common/atlas");
        }
    }

    async Task<bool> LoadNamesAsync(ApiClient api)
    {
        var data = await api.GetAsync("api/common/atlas");
        if (data is not JsonArray arr) return false;
        var names = new Dictionary<long, string>();
        var series = new Dictionary<long, string>();
        var rarities = new Dictionary<long, string>();
        var atlasIdByName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var idByAssetName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var assetNameById = new Dictionary<long, string>();
        foreach (var item in arr)
        {
            if (item is not JsonObject o) continue;
            long id = ApiClient.ReadLong(o["asset_id"]);
            if (id <= 0) continue;
            string type = ApiClient.ReadString(o["asset_type"]);
            string assetName = ApiClient.ReadString(o["asset_name"]);
            string rarity = ApiClient.ReadString(o["rarity"]).ToLowerInvariant();
            string display = DisplayName(type, assetName);
            if (!string.IsNullOrEmpty(display)) names[id] = display;
            if (!string.IsNullOrEmpty(rarity)) rarities[id] = rarity;
            if (!string.IsNullOrEmpty(assetName))
            {
                atlasIdByName[assetName] = id;
                // 图鉴 atlas 条目的 height_light_id 指向正片 id，用于把 asset_name 反查成可渲染的皮肤/配件 id
                if (ByType.TryGetValue(type, out var atlasMap) && atlasMap.TryGetValue(id, out var atlasEntry) && atlasEntry.HeightLightId > 0)
                {
                    long baseId = atlasEntry.HeightLightId;
                    idByAssetName[assetName] = baseId;
                    assetNameById[baseId] = assetName;
                    // 名称/稀有度同时挂到正片 id 上，catalog.list 才能显示中文名而不是数字 id。
                    if (!string.IsNullOrEmpty(display)) names[baseId] = display;
                    if (!string.IsNullOrEmpty(rarity)) rarities[baseId] = rarity;
                    string seriesName = SeriesName(type, assetName);
                    if (!string.IsNullOrEmpty(seriesName)) series[baseId] = seriesName;
                }
            }
        }
        // 只在完整成功后替换，瞬时网络失败不会清空默认中文模式的名称。
        Names.Clear(); foreach (var pair in names) Names[pair.Key] = pair.Value;
        Series.Clear(); foreach (var pair in series) Series[pair.Key] = pair.Value;
        Rarities.Clear(); foreach (var pair in rarities) Rarities[pair.Key] = pair.Value;
        AtlasIdByName.Clear(); foreach (var pair in atlasIdByName) AtlasIdByName[pair.Key] = pair.Value;
        IdByAssetName.Clear(); foreach (var pair in idByAssetName) IdByAssetName[pair.Key] = pair.Value;
        AssetNameById.Clear(); foreach (var pair in assetNameById) AssetNameById[pair.Key] = pair.Value;
        Log.Write("图鉴名称 " + Names.Count);
        return true;
    }

    /// <summary>为条目补全显示名与稀有度（来自图鉴接口按 asset_id 索引）。</summary>
    public void ApplyNames(IEnumerable<(long id, string name, string rarity, string series)> rows)
    {
        foreach (var r in rows)
        {
            if (!string.IsNullOrEmpty(r.name)) Names[r.id] = r.name;
            if (!string.IsNullOrEmpty(r.series)) Series[r.id] = r.series;
        }
    }

    static double ReadDouble(JsonNode n)
    {
        if (n == null) return 0;
        try { return n.GetValue<double>(); } catch { return 0; }
    }

    static async Task<JsonNode> GetJsonAsync(string url)
    {
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var text = await http.GetStringAsync(url).ConfigureAwait(false);
        return JsonNode.Parse(text);
    }
}
