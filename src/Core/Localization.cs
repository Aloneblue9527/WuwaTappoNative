using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace WuwatappoNative.Core;

/// <summary>
/// 原生界面（托盘/右键菜单/键盘/桌宠底栏）的轻量本地化。
/// 字典放在 wwwroot/locales/native-en.json 与 native-ja.json，只包含原生 key；
/// 读取失败、文件缺失或 key 不存在时一律回退调用处传入的中文，保证默认 zh 行为不变。
/// </summary>
public static class Localization
{
    static readonly object Gate = new();
    static Dictionary<string, string> _en;
    static Dictionary<string, string> _ja;
    static string _current = "zh";

    /// <summary>当前语言：zh / en / ja。</summary>
    public static string Current
    {
        get { lock (Gate) return _current; }
    }

    public static string Normalize(string locale)
    {
        string value = (locale ?? "").Trim().ToLowerInvariant();
        if (value.StartsWith("en", StringComparison.Ordinal)) return "en";
        if (value.StartsWith("ja", StringComparison.Ordinal) || value.StartsWith("jp", StringComparison.Ordinal)) return "ja";
        return "zh";
    }

    /// <summary>
    /// 新用户/旧配置没有 Locale 时的默认语言：跟随 Windows UI 语言。
    /// 支持中文、日语、英语；其他系统语言回退英语，不再默认中文。
    /// </summary>
    public static string SystemDefault()
    {
        var culture = System.Globalization.CultureInfo.CurrentUICulture;
        if (string.IsNullOrEmpty(culture.TwoLetterISOLanguageName) ||
            string.Equals(culture.TwoLetterISOLanguageName, "iv", StringComparison.OrdinalIgnoreCase))
            culture = System.Globalization.CultureInfo.InstalledUICulture;
        return culture.TwoLetterISOLanguageName.ToLowerInvariant() switch
        {
            "zh" => "zh",
            "ja" => "ja",
            "en" => "en",
            _ => "en",
        };
    }

    /// <summary>切换语言；非 zh 时按需加载字典，失败时保持中文回退。</summary>
    public static void SetLocale(string locale)
    {
        lock (Gate)
        {
            _current = Normalize(locale);
            EnsureLoadedLocked();
        }
    }

    /// <summary>取翻译；locale 为 zh 或字典缺失时返回 fallback。</summary>
    public static string T(string key, string fallback)
    {
        Dictionary<string, string> dict;
        lock (Gate)
        {
            if (_current == "zh") return fallback;
            EnsureLoadedLocked();
            dict = _current == "ja" ? _ja : _en;
        }
        return dict != null && dict.TryGetValue(key, out string text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : fallback;
    }

    /// <summary>带 {0} 参数的翻译；格式化失败时返回原文。</summary>
    public static string F(string key, string fallback, params object[] args)
    {
        string format = T(key, fallback);
        if (args == null || args.Length == 0) return format;
        try { return string.Format(format, args); }
        catch { return format; }
    }

    static void EnsureLoadedLocked()
    {
        if (_en == null) _en = Load("en");
        if (_ja == null) _ja = Load("ja");
    }

    static Dictionary<string, string> Load(string locale)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            string path = Path.Combine(Paths.WwwRoot, "locales", "native-" + locale + ".json");
            if (!File.Exists(path)) return result;
            if (JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) is not JsonObject root) return result;
            foreach (var pair in root)
            {
                if (pair.Value is JsonValue value && value.TryGetValue<string>(out string text) && !string.IsNullOrWhiteSpace(text))
                    result[pair.Key] = text;
            }
        }
        catch
        {
            // 字典损坏/不可读时保持空表，所有调用回退中文。
            result.Clear();
        }
        return result;
    }
}
