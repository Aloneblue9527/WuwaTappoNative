using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
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
    static ReleaseInfo _cachedRaw;
    static string _cachedLocale = "";
    static DateTimeOffset _cachedUntil;
    static bool _cachedPrerelease;

    public static Task<ReleaseInfo> CheckAsync(Version current, bool force = false, string httpProxy = null)
        => CheckAsync(current?.ToString() ?? "0.0.0", force, httpProxy);

    internal static bool IsBetaVersion(string raw)
        => UpdateVersion.TryParse(raw, out var version) && version.IsBeta;

    /// <summary>
    /// 检查更新。includePrerelease 为 true 时同时接收正式版和测试版；
    /// 为 false 时只接收正式版。为空时按当前构建类型自动选择。
    /// locale 决定 Release 正文使用哪种语言；原始多语言正文会缓存，切换语言无需重新联网。
    /// </summary>
    public static async Task<ReleaseInfo> CheckAsync(
        string currentVersion,
        bool force = false,
        string httpProxy = null,
        bool? includePrerelease = null,
        string locale = null)
    {
        if (!UpdateVersion.TryParse(currentVersion, out var current))
        {
            Log.Write("检查更新失败：无法解析当前版本 " + currentVersion);
            _cachedUntil = DateTimeOffset.UtcNow.AddMinutes(10);
            _cachedRaw = null;
            return _cached = null;
        }
        string normalizedLocale = Localization.Normalize(locale);
        bool betaChannel = includePrerelease ?? current.IsBeta;
        if (!force && _cachedPrerelease == betaChannel && DateTimeOffset.UtcNow < _cachedUntil)
        {
            if (_cachedRaw == null) return _cached = null;
            if (!string.Equals(_cachedLocale, normalizedLocale, StringComparison.Ordinal))
            {
                _cachedLocale = normalizedLocale;
                _cached = LocalizeReleaseInfo(_cachedRaw, normalizedLocale);
            }
            return _cached;
        }
        _cachedPrerelease = betaChannel;
        try
        {
            using var http = CreateHttpClient(httpProxy, TimeSpan.FromSeconds(15));
            http.DefaultRequestHeaders.UserAgent.ParseAdd("WuwatappoNative/" + current.Text);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            string json = await http.GetStringAsync(ReleasesApi).ConfigureAwait(false);
            if (JsonNode.Parse(json) is not JsonArray releases)
            {
                _cachedUntil = DateTimeOffset.UtcNow.AddMinutes(10);
                _cachedRaw = null;
                return _cached = null;
            }

            // 用户选择的通道决定是否接收 prerelease；不再是“当前是测试版才收测试版”。
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
                _cachedRaw = null;
                return _cached = null;
            }

            // GitHub 返回顺序不保证；同一正式版优先于同号测试版，测试版按序号从新到旧。
            newer.Sort((a, b) => b.Version.CompareTo(a.Version));
            if (newer.Count > MaxEntries) newer.RemoveRange(MaxEntries, newer.Count - MaxEntries);
            var latest = newer[0].Entry;
            var rawInfo = new ReleaseInfo
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
            _cachedRaw = rawInfo;
            _cachedLocale = normalizedLocale;
            _cachedUntil = DateTimeOffset.UtcNow.AddMinutes(30);
            return _cached = LocalizeReleaseInfo(rawInfo, normalizedLocale);
        }
        catch (Exception ex)
        {
            Log.Write("检查更新失败 " + ex.Message);
            _cachedUntil = DateTimeOffset.UtcNow.AddMinutes(10);
            _cachedRaw = null;
            return _cached = null;
        }
    }

    /// <summary>按当前界面语言复制一份发布信息；原始的多语言日志仍留在缓存里，切换语言时无需重新联网。</summary>
    static ReleaseInfo LocalizeReleaseInfo(ReleaseInfo source, string locale)
    {
        if (source == null) return null;
        var entries = source.Entries ?? Array.Empty<ReleaseEntry>();
        return new ReleaseInfo
        {
            Tag = source.Tag,
            Name = source.Name,
            DisplayVersion = source.DisplayVersion,
            Body = ReleaseNotesLocalizer.Select(source.Body, locale),
            Url = source.Url,
            PublishedAt = source.PublishedAt,
            Installer = source.Installer,
            Entries = entries.Select(entry => new ReleaseEntry
            {
                Tag = entry.Tag,
                Name = entry.Name,
                DisplayVersion = entry.DisplayVersion,
                Body = ReleaseNotesLocalizer.Select(entry.Body, locale),
                Url = entry.Url,
                PublishedAt = entry.PublishedAt,
                Installer = entry.Installer,
            }).ToArray(),
        };
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

/// <summary>
/// 从 Release 正文中选取当前语言段落。维护者在 release-notes/*.md 里用
/// &lt;!-- lang:zh --&gt; ... &lt;!-- /lang:zh --&gt; 包裹各语言版本；
/// 没有标记的旧 Release 视为中文说明，只对中文界面返回；缺失当前语言时返回空，
/// 由前端显示本地化的“暂无当前语言说明”占位，避免把其他语言内容展示给用户。
/// </summary>
internal static class ReleaseNotesLocalizer
{
    static readonly Regex OpenMarker = new(
        @"^\s*<!--\s*lang\s*:\s*(zh|en|ja)\s*-->\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex CloseMarker = new(
        @"^\s*<!--\s*/\s*lang\s*:\s*(zh|en|ja)\s*-->\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Select(string markdown, string locale)
    {
        string text = (markdown ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (text.Length == 0) return "";

        var sections = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        string current = null;
        bool hasMarkers = false;
        foreach (string line in text.Split('\n'))
        {
            Match open = OpenMarker.Match(line);
            if (open.Success)
            {
                hasMarkers = true;
                current = open.Groups[1].Value.ToLowerInvariant();
                if (!sections.ContainsKey(current))
                    sections[current] = new StringBuilder();
                continue;
            }

            Match close = CloseMarker.Match(line);
            if (close.Success)
            {
                hasMarkers = true;
                current = null;
                continue;
            }

            if (current != null) sections[current].AppendLine(line);
        }

        // 旧格式没有语言标记：历史 Release 只有中文，不能把它展示给英/日界面。
        string wanted = Localization.Normalize(locale);
        if (!hasMarkers) return wanted == "zh" ? text : "";

        if (sections.TryGetValue(wanted, out StringBuilder section) &&
            !string.IsNullOrWhiteSpace(section.ToString()))
            return section.ToString().Trim();

        // 当前语言没有对应段落时返回空，前端会显示本地化占位，不混入其他语言。
        return "";
    }
}
