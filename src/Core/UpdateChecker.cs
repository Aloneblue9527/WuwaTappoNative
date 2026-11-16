using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace WuwatappoNative.Core;

public sealed class ReleaseAsset
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("digest")] public string Digest { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
}

public sealed class ReleaseEntry
{
    [JsonPropertyName("tag")] public string Tag { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("displayVersion")] public string DisplayVersion { get; set; } = "";
    [JsonPropertyName("body")] public string Body { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("publishedAt")] public string PublishedAt { get; set; } = "";
    [JsonPropertyName("installer")] public ReleaseAsset Installer { get; set; }
}

public sealed class ReleaseInfo
{
    [JsonPropertyName("tag")] public string Tag { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("displayVersion")] public string DisplayVersion { get; set; } = "";
    [JsonPropertyName("body")] public string Body { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("publishedAt")] public string PublishedAt { get; set; } = "";
    [JsonPropertyName("installer")] public ReleaseAsset Installer { get; set; }
    /// <summary>从新到旧的更新日志；当前版本与最新版之间跨多个版本时用于拼接展示。</summary>
    [JsonPropertyName("entries")] public ReleaseEntry[] Entries { get; set; } = Array.Empty<ReleaseEntry>();
}

/// <summary>
/// 更新比较用的版本号：主版本.次版本.补丁 + 可选测试序号。
/// 同一主版本下正式版大于测试版；测试版之间按 beta 序号比较。
/// </summary>
internal readonly struct UpdateVersion : IComparable<UpdateVersion>
{
    public Version Base { get; }
    public int Beta { get; }
    public bool IsBeta => Beta > 0;
    public string Text => IsBeta ? $"{Base.ToString(3)}-beta.{Beta}" : Base.ToString(3);

    public UpdateVersion(Version baseVersion, int beta)
    {
        Base = baseVersion;
        Beta = beta;
    }

    public int CompareTo(UpdateVersion other)
    {
        int byBase = Base.CompareTo(other.Base);
        if (byBase != 0) return byBase;
        if (Beta == other.Beta) return 0;
        if (Beta == 0) return 1;
        if (other.Beta == 0) return -1;
        return Beta.CompareTo(other.Beta);
    }

    public static bool TryParse(string raw, out UpdateVersion version)
    {
        version = default;
        raw = (raw ?? "").Trim();
        if (raw.StartsWith("v", StringComparison.OrdinalIgnoreCase)) raw = raw.Substring(1);
        int metadata = raw.IndexOf('+');
        if (metadata >= 0) raw = raw.Substring(0, metadata);
        var match = Regex.Match(raw,
            @"^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?:\.\d+)?(?:-beta\.(?<beta>\d+))?$",
            RegexOptions.IgnoreCase);
        if (!match.Success) return false;
        if (!int.TryParse(match.Groups["major"].Value, out int major) ||
            !int.TryParse(match.Groups["minor"].Value, out int minor) ||
            !int.TryParse(match.Groups["patch"].Value, out int patch))
            return false;
        int beta = 0;
        if (match.Groups["beta"].Success &&
            !int.TryParse(match.Groups["beta"].Value, out beta))
            return false;
        version = new UpdateVersion(new Version(major, minor, patch), beta);
        return true;
    }

    public static bool TryParseBeta(string raw, out UpdateVersion version)
    {
        version = default;
        var match = Regex.Match(raw ?? "",
            @"(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)-beta\.(?<beta>\d+)",
            RegexOptions.IgnoreCase);
        if (!match.Success) return false;
        if (!int.TryParse(match.Groups["major"].Value, out int major) ||
            !int.TryParse(match.Groups["minor"].Value, out int minor) ||
            !int.TryParse(match.Groups["patch"].Value, out int patch) ||
            !int.TryParse(match.Groups["beta"].Value, out int beta))
            return false;
        version = new UpdateVersion(new Version(major, minor, patch), beta);
        return true;
    }
}

/// <summary>GitHub Release 检查。只读取公开 Release，不上传任何本机数据。</summary>
public static class UpdateChecker
{
    const string ReleasesApi = "https://api.github.com/repos/DanielToyama/WuwaTappoNative/releases?per_page=30";
    const string ReleasesPage = "https://github.com/DanielToyama/WuwaTappoNative/releases/latest";
    const int MaxEntries = 20;
    static ReleaseInfo _cached;
    static DateTimeOffset _cachedUntil;

    public static Task<ReleaseInfo> CheckAsync(Version current, bool force = false, string httpProxy = null)
        => CheckAsync(current?.ToString() ?? "0.0.0", force, httpProxy);

    public static async Task<ReleaseInfo> CheckAsync(string currentVersion, bool force = false, string httpProxy = null)
    {
        if (!force && DateTimeOffset.UtcNow < _cachedUntil) return _cached;
        try
        {
            if (!UpdateVersion.TryParse(currentVersion, out var current))
            {
                Log.Write("检查更新失败：无法解析当前版本 " + currentVersion);
                _cachedUntil = DateTimeOffset.UtcNow.AddMinutes(10);
                return _cached = null;
            }
            using var http = CreateHttpClient(httpProxy, TimeSpan.FromSeconds(15));
            http.DefaultRequestHeaders.UserAgent.ParseAdd("WuwatappoNative/" + current.Text);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            string json = await http.GetStringAsync(ReleasesApi).ConfigureAwait(false);
            if (JsonNode.Parse(json) is not JsonArray releases)
            {
                _cachedUntil = DateTimeOffset.UtcNow.AddMinutes(10);
                return _cached = null;
            }

            // 正式版只收正式版；测试版同时收正式版和后续测试版。
            bool betaChannel = current.IsBeta;
            var newer = new List<(ReleaseEntry Entry, UpdateVersion Version)>();
            foreach (var node in releases)
            {
                if (node is not JsonObject root) continue;
                if (ReadBool(root["draft"])) continue;
                bool prerelease = ReadBool(root["prerelease"]);
                if (prerelease && !betaChannel) continue;
                string tag = ReadString(root["tag_name"]);
                string name = ReadString(root["name"]);
                var assets = ReadAssets(root["assets"]);
                if (!TryParseReleaseVersion(tag, name, assets, prerelease, out var version)) continue;
                if (version.CompareTo(current) <= 0) continue;
                newer.Add((new ReleaseEntry
                {
                    Tag = tag,
                    Name = name,
                    DisplayVersion = version.Text,
                    Body = ReadString(root["body"]),
                    Url = ReadString(root["html_url"]),
                    PublishedAt = ReadString(root["published_at"]),
                    Installer = SelectInstaller(assets),
                }, version));
            }
            if (newer.Count == 0)
            {
                _cachedUntil = DateTimeOffset.UtcNow.AddMinutes(30);
                return _cached = null;
            }

            // GitHub 返回顺序不保证；同一正式版优先于同号测试版，测试版按序号从新到旧。
            newer.Sort((a, b) => b.Version.CompareTo(a.Version));
            if (newer.Count > MaxEntries) newer.RemoveRange(MaxEntries, newer.Count - MaxEntries);
            var latest = newer[0].Entry;
            var info = new ReleaseInfo
            {
                Tag = latest.Tag,
                Name = latest.Name,
                DisplayVersion = latest.DisplayVersion,
                Body = latest.Body,
                Url = string.IsNullOrEmpty(latest.Url) ? ReleasesPage : latest.Url,
                PublishedAt = latest.PublishedAt,
                Installer = latest.Installer,
                Entries = newer.Select(x => x.Entry).ToArray(),
            };
            _cachedUntil = DateTimeOffset.UtcNow.AddMinutes(30);
            return _cached = info;
        }
        catch (Exception ex)
        {
            Log.Write("检查更新失败 " + ex.Message);
            _cachedUntil = DateTimeOffset.UtcNow.AddMinutes(10);
            return _cached = null;
        }
    }

    static bool TryParseReleaseVersion(string tag, string name, List<ReleaseAsset> assets, bool prerelease, out UpdateVersion version)
    {
        version = default;
        if (!prerelease)
            return UpdateVersion.TryParse(tag, out version) && !version.IsBeta;
        // 测试版使用滚动 tag（如 v0.3.1-beta），真实测试序号在 Release 标题或安装包名里。
        if (UpdateVersion.TryParseBeta(name, out version)) return true;
        foreach (var asset in assets)
        {
            if (UpdateVersion.TryParseBeta(asset.Name, out version)) return true;
        }
        return false;
    }

    static HttpClient CreateHttpClient(string httpProxy, TimeSpan timeout)
    {
        var handler = new HttpClientHandler();
        if (!string.IsNullOrWhiteSpace(httpProxy) &&
            Uri.TryCreate(httpProxy.Trim(), UriKind.Absolute, out var proxyUri) &&
            (proxyUri.Scheme == Uri.UriSchemeHttp || proxyUri.Scheme == Uri.UriSchemeHttps))
        {
            handler.Proxy = new WebProxy(proxyUri);
            handler.UseProxy = true;
        }
        return new HttpClient(handler) { Timeout = timeout };
    }

    static List<ReleaseAsset> ReadAssets(JsonNode node)
    {
        var assets = new List<ReleaseAsset>();
        if (node is not JsonArray arr) return assets;
        foreach (var item in arr)
        {
            if (item is not JsonObject asset) continue;
            string name = ReadString(asset["name"]);
            string url = ReadString(asset["browser_download_url"]);
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) continue;
            assets.Add(new ReleaseAsset
            {
                Name = name,
                Url = url,
                Digest = ReadString(asset["digest"]),
                Size = ReadLong(asset["size"]),
            });
        }
        return assets;
    }

    static ReleaseAsset SelectInstaller(List<ReleaseAsset> assets)
    {
        return assets.FirstOrDefault(a => a.Name.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase))
            ?? assets.FirstOrDefault(a => a.Name.IndexOf("Setup", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                          a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
    }

    static string ReadString(JsonNode node)
    {
        try { return node?.GetValue<string>() ?? ""; } catch { return ""; }
    }

    static long ReadLong(JsonNode node)
    {
        try { return node?.GetValue<long>() ?? 0; } catch { return 0; }
    }

    static bool ReadBool(JsonNode node)
    {
        try { return node?.GetValue<bool>() ?? false; } catch { return false; }
    }
}
