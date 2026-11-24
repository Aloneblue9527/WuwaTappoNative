using System;
using System.Collections.Generic;

namespace WuwatappoNative.Core;

/// <summary>单个稀有度的宝箱通知开关与物品分类过滤。</summary>
public sealed class ChestNotifyRule
{
    public bool Enabled { get; set; }
    public bool Character { get; set; } = true;
    public bool Accessory { get; set; } = true;
    public bool Emote { get; set; } = true;
}

/// <summary>
/// 宝箱领取通知的纯规则：稀有度范围 × 物品分类矩阵，以及完全本地化的通知文案。
/// 默认只通知稀有/史诗，三类物品都保留，避免普通/优良材料刷屏。
/// </summary>
public static class ChestNotificationRules
{
    public static readonly string[] Rarities = { "common", "uncommon", "rare", "epic" };
    public static readonly string[] Categories = { "character_skin", "accessory", "emote" };

    const string CommonEmoji = "🟢";
    const string UncommonEmoji = "🔵";
    const string RareEmoji = "🟣";
    const string EpicEmoji = "🟡";

    public static Dictionary<string, ChestNotifyRule> CreateDefaultRules()
    {
        return new Dictionary<string, ChestNotifyRule>(StringComparer.OrdinalIgnoreCase)
        {
            ["common"] = new ChestNotifyRule { Enabled = false },
            ["uncommon"] = new ChestNotifyRule { Enabled = false },
            ["rare"] = new ChestNotifyRule { Enabled = true },
            ["epic"] = new ChestNotifyRule { Enabled = true },
        };
    }

    /// <summary>补齐缺失稀有度并把未知 key 丢弃；null/损坏配置直接回到默认矩阵。</summary>
    public static Dictionary<string, ChestNotifyRule> Normalize(Dictionary<string, ChestNotifyRule> rules)
    {
        var defaults = CreateDefaultRules();
        if (rules == null) return defaults;
        var result = new Dictionary<string, ChestNotifyRule>(StringComparer.OrdinalIgnoreCase);
        foreach (string rarity in Rarities)
        {
            if (rules.TryGetValue(rarity, out var rule) && rule != null)
                result[rarity] = rule;
            else
                result[rarity] = defaults[rarity];
        }
        return result;
    }

    public static string NormalizeRarity(string rarity)
    {
        string value = (rarity ?? "").Trim().ToLowerInvariant();
        return value switch
        {
            "common" => "common",
            "uncommon" => "uncommon",
            "rare" => "rare",
            "epic" => "epic",
            _ => "",
        };
    }

    public static string NormalizeCategory(string type)
    {
        string value = (type ?? "").Trim().ToLowerInvariant();
        return value switch
        {
            "character_skin" or "character" or "role" or "skin" => "character_skin",
            "accessory" or "part" => "accessory",
            "emote" => "emote",
            _ => "",
        };
    }

    /// <summary>
    /// 主开关 + 稀有度范围 + 该稀有度的分类过滤同时满足才通知。
    /// 未知稀有度/分类一律不通知，避免服务端新增类型时误报。
    /// </summary>
    public static bool ShouldNotify(Settings cfg, string rarity, string category)
    {
        if (cfg == null || !cfg.NotifyChestClaimed) return false;
        string r = NormalizeRarity(rarity);
        string c = NormalizeCategory(category);
        if (r.Length == 0 || c.Length == 0) return false;
        var rules = Normalize(cfg.ChestNotifyRules);
        if (!rules.TryGetValue(r, out var rule) || rule == null || !rule.Enabled) return false;
        return c switch
        {
            "character_skin" => rule.Character,
            "accessory" => rule.Accessory,
            "emote" => rule.Emote,
            _ => false,
        };
    }

    public static string EmojiFor(string rarity) => NormalizeRarity(rarity) switch
    {
        "common" => CommonEmoji,
        "uncommon" => UncommonEmoji,
        "rare" => RareEmoji,
        "epic" => EpicEmoji,
        _ => "",
    };

    public static string RarityLabel(string rarity) => NormalizeRarity(rarity) switch
    {
        "common" => Localization.T("app.rarity.common", "普通"),
        "uncommon" => Localization.T("app.rarity.uncommon", "优良"),
        "rare" => Localization.T("app.rarity.rare", "稀有"),
        "epic" => Localization.T("app.rarity.epic", "史诗"),
        _ => "",
    };

    /// <summary>
    /// 中文精确格式：获得了🟣稀有物资：嘉贝莉娜・团团团团转（左侧宝箱）。
    /// 翻译只替换周边文字，emoji 和物品原名原样保留。
    /// </summary>
    public static string BuildClaimMessage(string rarity, string itemName, string sideLabel)
    {
        string emoji = EmojiFor(rarity);
        string rarityLabel = RarityLabel(rarity);
        return Localization.F(
            "app.chest.itemClaim",
            "获得了{0}{1}物资：{2}（{3}宝箱）",
            emoji,
            rarityLabel,
            itemName ?? "",
            sideLabel ?? "");
    }

    /// <summary>Boot/前端 DTO；字段名与 options 页矩阵逐项对应。</summary>
    public static Dictionary<string, object> ToDto(Dictionary<string, ChestNotifyRule> rules)
    {
        var normalized = Normalize(rules);
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (string rarity in Rarities)
        {
            var rule = normalized[rarity];
            result[rarity] = new
            {
                enabled = rule.Enabled,
                character = rule.Character,
                accessory = rule.Accessory,
                emote = rule.Emote,
            };
        }
        return result;
    }
}
