using System;
using System.Collections.Generic;
using System.Linq;

namespace WuwatappoNative.Core;

/// <summary>
/// 自定义伙伴的纯本地规则：名称显示、合成 id 分配和配置规范化。
/// 所有方法都不访问网络或 RoomManager，保证伙伴永远只是本机装饰。
/// </summary>
public static class CompanionPets
{
    /// <summary>合成 id 从 -1000 起向下分配；真实房间成员 id 为正数，二者不会重叠。</summary>
    public const long FirstSyntheticId = -1000;

    /// <summary>
    /// 显示名规则：包含中点（· 或日文 ・）时只显示中点前的部分。
    /// 例：菲比·Miratsu → 菲比。首尾空白会被清理；结果为空时退回完整名称。
    /// </summary>
    public static string DisplayName(string name)
    {
        string full = (name ?? "").Trim();
        int dot = full.IndexOfAny(new[] { '·', '・' });
        if (dot > 0)
        {
            string head = full[..dot].Trim();
            if (head.Length > 0) return head;
        }
        return full;
    }

    /// <summary>为新增伙伴分配下一个负数合成 id；已有负数 id 不会重复。</summary>
    public static long NextSyntheticId(Settings cfg)
    {
        long min = 0;
        bool any = false;
        foreach (var companion in cfg?.Companions ?? Enumerable.Empty<CompanionPet>())
        {
            if (companion == null || companion.SyntheticId >= 0) continue;
            min = any ? Math.Min(min, companion.SyntheticId) : companion.SyntheticId;
            any = true;
        }
        return any ? min - 1 : FirstSyntheticId;
    }

    /// <summary>配置加载/保存前的轻量规范化；任何损坏数据都安静回退，不影响真实联机数据。</summary>
    public static void Normalize(Settings cfg)
    {
        if (cfg == null) return;
        cfg.Companions ??= new List<CompanionPet>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var syntheticIds = new HashSet<long>();
        var cleaned = new List<CompanionPet>();
        long nextSynthetic = FirstSyntheticId;
        foreach (var companion in cfg.Companions)
        {
            if (companion == null) continue;
            string id = (companion.Id ?? "").Trim();
            if (id.Length == 0 || !ids.Add(id))
            {
                id = Guid.NewGuid().ToString("N");
                ids.Add(id);
            }
            companion.Id = id;
            companion.Name = (companion.Name ?? "").Trim();
            if (companion.Name.Length == 0) companion.Name = "伙伴";
            if (companion.Name.Length > 64) companion.Name = companion.Name[..64];
            companion.ClickIntervalSeconds = Math.Clamp(companion.ClickIntervalSeconds, 0, 3600);
            // 分配唯一负数 id；和已有负数 id 冲突时继续向下找。
            while (nextSynthetic == 0 || syntheticIds.Contains(nextSynthetic)) nextSynthetic--;
            long synthetic = companion.SyntheticId;
            if (synthetic >= 0 || syntheticIds.Contains(synthetic))
            {
                synthetic = nextSynthetic--;
                companion.SyntheticId = synthetic;
            }
            syntheticIds.Add(companion.SyntheticId);
            cleaned.Add(companion);
        }
        cfg.Companions = cleaned;
    }
}
