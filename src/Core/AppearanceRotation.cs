using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace WuwatappoNative.Core;

/// <summary>
/// 外观轮换（角色皮肤 + 配件）的纯逻辑。列表只保存本地素材 id：
/// 不校验拥有状态、不访问背包/服务器，符合“本地外观可任意搭配”的既有行为。
/// </summary>
public static class AppearanceRotation
{
    public const int DefaultIntervalMinutes = 30;
    public const int MinIntervalMinutes = 1;
    public const int MaxIntervalMinutes = 1440;

    /// <summary>去重并丢弃无效 id，保持用户添加顺序。</summary>
    public static long[] NormalizeIds(IEnumerable<long> ids)
    {
        if (ids == null) return Array.Empty<long>();
        var seen = new HashSet<long>();
        var result = new List<long>();
        foreach (long id in ids)
        {
            if (id <= 0 || !seen.Add(id)) continue;
            result.Add(id);
        }
        return result.ToArray();
    }

    /// <summary>0/负数视为未配置（默认 30），其余夹到 1–1440 分钟。</summary>
    public static int NormalizeInterval(int minutes)
    {
        if (minutes <= 0) return DefaultIntervalMinutes;
        return Math.Clamp(minutes, MinIntervalMinutes, MaxIntervalMinutes);
    }

    /// <summary>
    /// 当前 id 在列表中的下一项；当前 id 不在列表里时从第一项开始。
    /// 空列表返回 0，表示该类不轮换。
    /// </summary>
    public static long NextId(IReadOnlyList<long> ids, long currentId)
    {
        if (ids == null || ids.Count == 0) return 0;
        int index = -1;
        for (int i = 0; i < ids.Count; i++)
        {
            if (ids[i] != currentId) continue;
            index = i;
            break;
        }
        return ids[index < 0 ? 0 : (index + 1) % ids.Count];
    }
}

/// <summary>
/// 后台外观轮换定时器。由 App 持有，跨面板关闭/重开仍然运行；
/// 只修改 Settings、保存 config 并通知 UI 刷新，不触碰房间成员或服务器接口。
/// </summary>
public sealed class AppearanceRotationService : IDisposable
{
    readonly Settings _cfg;
    readonly System.Windows.Forms.Timer _timer;

    /// <summary>完成一次轮换后触发（UI 线程）。参数为新的角色皮肤 id / 配件 id。</summary>
    public event Action<long, long> Applied;

    public AppearanceRotationService(Settings cfg)
    {
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        _timer = new System.Windows.Forms.Timer { Interval = 10_000 };
        _timer.Tick += (s, e) => SafeRotate();
    }

    public bool TimerRunning => _timer.Enabled;

    /// <summary>启动轮换定时器；面板关闭/重渲染不会影响它。</summary>
    public void Start() => UpdateTimer();

    public void RefreshTimer() => UpdateTimer();

    public void SetRotation(bool enabled, int intervalMinutes)
    {
        bool wasEnabled = _cfg.RotationEnabled;
        _cfg.RotationEnabled = enabled;
        _cfg.RotationIntervalMinutes = AppearanceRotation.NormalizeInterval(intervalMinutes);
        if (enabled && !wasEnabled)
        {
            // 刚开启时从当前时刻开始计时，避免用户一按开关就立刻跳走。
            _cfg.RotationLastAppliedUtcMs = NowMs();
        }
        ConfigStore.Save(_cfg);
        UpdateTimer();
    }

    /// <summary>加入/移出对应轮换列表；返回 true 表示加入，false 表示移出。</summary>
    public bool Toggle(string type, long id)
    {
        if (!TryGetList(type, out var list) || id <= 0) return false;
        var next = list.ToList();
        bool added;
        if (next.RemoveAll(x => x == id) > 0)
        {
            added = false;
        }
        else
        {
            next.Add(id);
            added = true;
        }
        Assign(type, AppearanceRotation.NormalizeIds(next));
        ConfigStore.Save(_cfg);
        UpdateTimer();
        return added;
    }

    /// <summary>判断 bridge 传入的类型是否有效；Toggle 的 false 也可能是“已移出”，不能拿来判错。</summary>
    public static bool IsValidType(string type)
        => string.Equals(type, "character_skin", StringComparison.Ordinal)
        || string.Equals(type, "accessory", StringComparison.Ordinal);

    /// <summary>只移除，不因 id 不存在而失败。</summary>
    public bool Remove(string type, long id)
    {
        if (!TryGetList(type, out var list) || id <= 0) return false;
        var next = list.Where(x => x != id).ToArray();
        if (next.Length == list.Length) return false;
        Assign(type, AppearanceRotation.NormalizeIds(next));
        ConfigStore.Save(_cfg);
        UpdateTimer();
        return true;
    }

    /// <summary>供定时器和测试调用：到点则角色/配件各自推进一项。</summary>
    public bool CheckRotation(DateTime utcNow)
    {
        if (!_cfg.RotationEnabled) return false;
        var characters = AppearanceRotation.NormalizeIds(_cfg.RotationCharacterIds);
        var accessories = AppearanceRotation.NormalizeIds(_cfg.RotationAccessoryIds);
        if (characters.Length == 0 && accessories.Length == 0) return false;

        long now = ToMs(utcNow);
        long interval = AppearanceRotation.NormalizeInterval(_cfg.RotationIntervalMinutes) * 60_000L;
        long last = _cfg.RotationLastAppliedUtcMs;
        if (last > 0 && now - last < interval) return false;
        if (last == 0)
        {
            // 兼容旧/手工编辑的配置：先记下起点，不立刻换装。
            _cfg.RotationLastAppliedUtcMs = now;
            ConfigStore.Save(_cfg);
            return false;
        }

        long nextCharacter = _cfg.CharacterAssetId;
        long nextAccessory = _cfg.AccessoryAssetId;
        if (characters.Length > 0)
            nextCharacter = AppearanceRotation.NextId(characters, _cfg.CharacterAssetId);
        if (accessories.Length > 0)
            nextAccessory = AppearanceRotation.NextId(accessories, _cfg.AccessoryAssetId);

        _cfg.CharacterAssetId = nextCharacter;
        _cfg.AccessoryAssetId = nextAccessory;
        // 轮换属于本地外观：标记后会阻止 user/me 再把服务器外观覆盖回来。
        _cfg.LocalAppearance = true;
        _cfg.RotationLastAppliedUtcMs = now;
        ConfigStore.Save(_cfg);
        Log.Write($"外观轮换 skin={nextCharacter} accessory={nextAccessory}（仅本地）");
        try { Applied?.Invoke(nextCharacter, nextAccessory); }
        catch (Exception ex) { Log.Write("外观轮换刷新界面失败 " + ex.Message); }
        return true;
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }

    void SafeRotate()
    {
        try { CheckRotation(DateTime.UtcNow); }
        catch (Exception ex) { Log.Write("外观轮换失败 " + ex.Message); }
    }

    void UpdateTimer()
    {
        bool hasItems = AppearanceRotation.NormalizeIds(_cfg.RotationCharacterIds).Length > 0
            || AppearanceRotation.NormalizeIds(_cfg.RotationAccessoryIds).Length > 0;
        if (_cfg.RotationEnabled && hasItems)
        {
            if (!_timer.Enabled) _timer.Start();
        }
        else if (_timer.Enabled)
        {
            _timer.Stop();
        }
    }

    bool TryGetList(string type, out long[] list)
    {
        if (type == "character_skin")
        {
            list = AppearanceRotation.NormalizeIds(_cfg.RotationCharacterIds);
            return true;
        }
        if (type == "accessory")
        {
            list = AppearanceRotation.NormalizeIds(_cfg.RotationAccessoryIds);
            return true;
        }
        list = Array.Empty<long>();
        return false;
    }

    void Assign(string type, long[] ids)
    {
        if (type == "character_skin") _cfg.RotationCharacterIds = ids;
        else if (type == "accessory") _cfg.RotationAccessoryIds = ids;
    }

    static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    static long ToMs(DateTime utcNow) =>
        new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
}
