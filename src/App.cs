using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WuwatappoNative.Assets;
using WuwatappoNative.Core;
using WuwatappoNative.Net;
using WuwatappoNative.Pet;
using WuwatappoNative.Ui;

namespace WuwatappoNative;

public sealed class App : ApplicationContext
{
    public Settings Cfg { get; }
    public AuthService Auth { get; }
    public Catalog Catalog { get; } = new();
    public AssetStore Store { get; } = new();
    public RoomManager Room { get; }
    public PetWindow Pet { get; }
    public KeyboardWindow Keyboard { get; }
    /// <summary>主题预设与轮换状态；独立 JSON 持久化，轮换定时器跟随 App 生命周期。</summary>
    public ThemePresetManager ThemePresets { get; }
    public PanelWindow Panel { get; private set; }
    /// <summary>GitHub Pages 上发布的版本兼容状态；读取失败时为 null。</summary>
    public CompatibilityInfo Compatibility { get; private set; }
    /// <summary>统一联网门禁的当前原因；空字符串表示允许联网。</summary>
    public string OnlineBlockedReason => CompatibilityGate.BlockedReason(Compatibility, Cfg);

    /// <summary>只读公告服务；只在设置页渲染，不打扰其他页面。</summary>
    public AnnouncementService Announcements { get; }
    System.Threading.Timer _autoChestTimer;
    System.Threading.Timer _autoBoostTimer;
    System.Threading.Timer _clickRefreshTimer;
    int _autoBoostBusy;
    System.Threading.Timer _autoClickTimer;
    System.Threading.Timer _gameModeRuleTimer;
    System.Threading.Timer _compatibilityTimer;
    System.Threading.Timer _announcementTimer;
    readonly object _gameModeRuleGate = new();
    bool _gameModeRuleMatched;
    bool _gameModeRuleAuto;
    long _clickUserId;
    readonly object _autoEmoteGate = new();
    readonly Queue<long> _autoEmoteTimes = new();
    DateTime _nextLeftAttempt = DateTime.MinValue, _nextRightAttempt = DateTime.MinValue;
    int _chestBusy;
    int _autoChestTickBusy;
    int _autoSynthTicks;
    // 普通手动合成与自动升阶共用同一把锁，避免同时对同一批背包物资发请求。
    readonly SemaphoreSlim _synthesisGate = new(1, 1);
    // 只读 bags/list 的跨入口串行门：前端刷新、自动升阶和面板加载共用，避免开箱后并发打服务端。
    readonly SemaphoreSlim _bagGate = new(1, 1);
    int _disposed;
    readonly object _autoSynthCancelGate = new();
    CancellationTokenSource _autoSynthCts;
    // 自动升阶连续失败后的冷却：1 → 2 → 5 → 10 → 30 分钟封顶；手动 synth.auto 不受影响。
    readonly RetryBackoff _autoSynthBackoff = new(RetryBackoff.AutoSynthSchedule);
    System.Threading.Timer _memoryTimer;

    public App()
    {
        Cfg = ConfigStore.Load();
        // 先迁移/加载独立状态文件，再保存 config.json 以移除旧的状态字段。
        StateStore.Load(Cfg);
        // 旧版主题图片放在 Root\theme；新版统一放到 cache\theme，先搬目录再迁移 data URL。
        ThemeBackgroundStore.MigrateLegacyDirectory();
        // 旧版把背景图 data URL 直接存进 config；先落成文件并把配置改成文件名索引。
        ThemeBackgroundStore.MigrateLegacy(Cfg);
        ConfigStore.Save(Cfg);
        ThemePresets = new ThemePresetManager(Cfg);
        ThemePresets.CleanupOrphans();
        Announcements = new AnnouncementService(Cfg);
        // 配置与注册表对齐：用户手动删了 Run 项、或换过安装目录，都在启动时修正。
        Cfg.LaunchAtLogin = AutoStart.Sync(Cfg.LaunchAtLogin, Cfg.RunAsAdmin);
        AnimationTimer.FrameRate = Cfg.AnimationFps;
        Auth = new AuthService(Cfg);
        _clickUserId = Auth.UserId;
        Room = new RoomManager(Auth, Cfg, () => OnlineBlockedReason);
        // 辅助功能：可用点击低于 1000 时自动补 10000；定时检查而不是依赖某个点击事件。
        _autoBoostTimer = new System.Threading.Timer(_ => TryAutoBoostClicks(), null, 1000, 1000);
        Pet = new PetWindow(Cfg, Auth, Catalog, Store, Room);
        Keyboard = new KeyboardWindow();
        Keyboard.ApplyTheme(Cfg);
        Pet.KeyboardUI = Keyboard;
        // 预设轮换由 App 持有定时器：面板关闭/设置页重渲染都不会把它丢掉。
        ThemePresets.Applied += _ =>
        {
            // 预设应用/自动轮换走同一条 UI 线程链路，键盘三种模式与桌宠同时换色。
            try { Keyboard.ApplyTheme(Cfg); } catch { }
            try { Pet.BeginInvoke(new Action(() => Pet.ApplySettings())); } catch { }
            try { Panel?.PushBoot(); } catch { }
        };

        Pet.OpenPanelRequested += OpenPanel;
        Pet.OpenMainPanelRequested += OpenMainPanel;
        Pet.AutoSettingsChanged += ApplyAutoSettings;
        Pet.ChestOpenRequested += place => _ = OpenChestSafeAsync(place);
        Pet.KeyboardModeChanged += ApplyKeyboardMode;
        Pet.PetVisibilityChanged += visible =>
        {
            if (!visible) Keyboard.Hide();
            else if (Cfg.KeyboardMode != "hidden")
            {
                Keyboard.SetMode(KeyboardWindow.Mode.Hidden);
                ApplyKeyboardMode();
            }
        };
        Auth.Changed += () =>
        {
            if (!Auth.IsLoggedIn)
            {
                Room.ResetClickState();
                _clickUserId = 0;
                return;
            }
            // 切换账号不能继承上一账号的累计点击。
            if (_clickUserId != 0 && _clickUserId != Auth.UserId) Room.ResetClickState();
            _clickUserId = Auth.UserId;
            Room.SyncServerClicks(Auth.TotalClickCount, Auth.TotalClickConsume);
        };
        // 登录态变化必须立刻推给面板。否则面板仍保留旧 loggedIn/onlineAvailable，
        // 失效后点“重新登录”会继续按已登录页面渲染，看起来完全没有反应。
        // StateChanged 可能来自探针线程，先切回 UI 线程再读取 Room/Members 并推送。
        Auth.StateChanged += () =>
        {
            try
            {
                Pet.BeginInvoke(new Action(() =>
                {
                    PushAuth();
                    PushRoom();
                    Pet.RefreshHud();
                    if (Cfg.AutoSynthUp && Auth.CanUseOnline) CheckAutoSynthNow();
                }));
            }
            catch { }
        };
        // 点击数变化很频繁（每次按键一次）。面板可能没开，开了也不能每次都走一趟
        // WebView2 序列化 + postMessage；这里合并成 100ms 一次。
        Room.ClicksChanged += QueueClicksPush;
        Room.LocalClick += OnLocalClick;
        Room.ServiceUnavailableChanged += down =>
        {
            if (!Auth.CanUseOnline) return;
            try
            {
                Panel?.Push("toast", new
                {
                    text = down ? "联机服务暂不可用，正在自动重试…" : "联机服务已恢复",
                    kind = down ? "err" : "ok",
                });
            }
            catch { }
        };
        // 键盘显示直接吃 Raw Input 边沿：按下立刻亮，不再等 16ms 轮询，也不会漏掉快速点击。
        Pet.Input.KeyEdge += (vk, down) => Keyboard.OnKeyEdge(vk, down);
        Pet.GeometryChanged += LayoutKeyboard;
        Pet.RefreshRequested += () => _ = RefreshEverythingAsync();
        // 快捷键/托盘菜单改了窗口设置后，面板里的开关要跟着变，否则界面显示和实际状态不一致。
        Pet.SettingsChanged += () => { try { Panel?.PushBoot(); } catch { } };
        // 右键远程角色隐藏成员后，联机页的成员可见状态也要同步刷新。
        Pet.MemberVisibilityChanged += PushRoom;
        // 手柄按键和键盘按键走同一条链路：计数、广播、挥手。
        Keyboard.PadButtonDown += () => Pet.BeginInvoke(new Action(Pet.GamepadTap));
        Keyboard.PadButtonUp += () => Pet.BeginInvoke(new Action(Pet.GamepadRelease));
        Keyboard.PadButtonPressed += label => Pet.BeginInvoke(new Action(() => Pet.PushGamepadBubble(label)));
        Pet.FormClosed += (s, e) => { ThemePresets.Dispose(); ExitThread(); };

        Pet.Show();
        Pet.ApplyCaptureMode();
        ThemePresets.Start();
        if (!Cfg.AllowOverlap) Pet.ResolveOverlaps();
        ApplyKeyboardMode();
        Keyboard.ModeRequested += mode =>
        {
            Cfg.KeyboardMode = mode switch
            {
                KeyboardWindow.Mode.Letters => "letters",
                KeyboardWindow.Mode.Wuwa => "wuwa",
                KeyboardWindow.Mode.Rhythm => "rhythm",
                KeyboardWindow.Mode.Controller => "controller",
                _ => "hidden"
            };
            ConfigStore.Save(Cfg);
            ApplyKeyboardMode();
            Pet.RefreshHud();
        };
        Keyboard.CloseRequested += () =>
        {
            Cfg.KeyboardMode = "hidden";
            ConfigStore.Save(Cfg);
            ApplyKeyboardMode();
            Pet.RefreshHud();
        };
        Log.Write("桌宠窗口已创建");

        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--panel") >= 0 || !Cfg.DisclaimerShown)
            Pet.BeginInvoke(new Action(OpenPanel));

        _ = BootAsync();
        // 宝箱到点后最多等 5 秒就开始尝试，而不是等下一个 30 秒周期。
        _autoChestTimer = new System.Threading.Timer(_ => _ = AutoChestTickAsync(), null, 5000, 5000);
        // 网络恢复后最多 30 秒重新同步一次点击/宝箱等服务器状态。
        _clickRefreshTimer = new System.Threading.Timer(_ => _ = RefreshClicksAsync(), null, 30000, 30000);
        _gameModeRuleTimer = new System.Threading.Timer(_ => CheckGameModeRules(), null, 1500, 2000);
        // 启动后异步读取 GitHub Pages 上的兼容状态，之后每 30 分钟刷新；失败不阻塞启动。
        _compatibilityTimer = new System.Threading.Timer(_ => _ = RefreshCompatibilityAsync(), null, 2500, 30 * 60 * 1000);
        // 公告独立拉取；只在设置页显示，失败静默忽略。
        _announcementTimer = new System.Threading.Timer(_ => _ = RefreshAnnouncementsAsync(), null, 3500, 30 * 60 * 1000);
        // 进程内存/GDI 观测：启动 1 分钟后首次采样，之后每 5 分钟一次；只读计数器，不做 GC。
        _memoryTimer = new System.Threading.Timer(_ => LogMemorySnapshot(), null, 60_000, 300_000);
        ApplyAutoClickTimer();
        StartStressHarness();
    }

    /// <summary>周期记录 WorkingSet/Private/GDI/USER 和可见桌宠数；可见数在 UI 线程统计。</summary>
    void LogMemorySnapshot()
    {
        try
        {
            if (Pet == null || Pet.IsDisposed || !Pet.IsHandleCreated)
            {
                RuntimeDiagnostics.LogPeriodic(0);
                return;
            }
            Pet.BeginInvoke(new Action(() =>
            {
                try { RuntimeDiagnostics.LogPeriodic(Pet.VisiblePetCount); } catch { }
            }));
        }
        catch
        {
            try { RuntimeDiagnostics.LogPeriodic(0); } catch { }
        }
    }

    bool _stressMode;

    /// <summary>F5：重新拉取目录/外观/房间等数据，等价于原版的“刷新界面”。</summary>
    public async Task RefreshEverythingAsync()
    {
        try
        {
            await Catalog.LoadAsync(Auth.Api, msg => Log.Write("目录：" + msg));
            Pet.BeginInvoke(new Action(() => { Pet.ReloadAppearance(); Pet.RefreshMembers(); }));
            if (Auth.CanUseOnline)
            {
                await Auth.WhoAmIAsync();
                Room.SyncServerClicks(Auth.TotalClickCount, Auth.TotalClickConsume);
                Pet.BeginInvoke(new Action(Pet.RefreshHud));
                if (Panel != null && Panel.IsHandleCreated) Panel.BeginInvoke(new Action(PushAuth));
            }
            else if (Auth.State == AuthState.NetworkUnknown)
            {
                await Auth.RetryNetworkAsync();
            }
            PushRoom();
        }
        catch (Exception ex) { Log.Write("刷新失败 " + ex.Message); }
    }

    /// <summary>人数多场景的压测入口：--stress N [--stress-hz H]
    /// 只在本机伪造 N 个成员并按 H 次/秒模拟全员按键，不联网、不影响服务器房间。
    /// </summary>
    void StartStressHarness()
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "--stress");
        if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out int count) || count <= 0) return;
        int hz = 10;
        int j = Array.IndexOf(args, "--stress-hz");
        if (j >= 0 && j + 1 < args.Length && int.TryParse(args[j + 1], out int v) && v > 0) hz = v;
        _stressMode = true;
        Room.DebugSeedMembers(count, Cfg.CharacterAssetId, Cfg.AccessoryAssetId);
        var timer = new System.Windows.Forms.Timer { Interval = Math.Max(8, 1000 / hz) };
        timer.Tick += (s, e) => Room.DebugPulseMembers();
        timer.Start();
        Log.Write($"压测模式：成员={count} 频率={hz}Hz");
    }

    /// <summary>刷新 GitHub Pages 上的版本兼容状态；失败时保留上次结果，不影响本地功能。</summary>
    public async Task RefreshCompatibilityAsync(bool force = false)
    {
        var info = await CompatibilityChecker.CheckAsync(force);
        // 官方版本由客户端直接读取官方清单；即使 compatibility.json 暂时不可用，
        // 也能提示“官方已更新但 Native 尚未验证/已被官方强制更新”。
        var official = await OfficialVersionChecker.FetchAsync(Cfg.ServerRegion, force);
        // 维护者可在 compatibility.json 的 protocol.testedOfficial 中提升验证基线；
        // 远端基线只允许上调，低于本地编译基线时忽略。
        var officialCheck = OfficialVersionChecker.Evaluate(official, info?.Protocol?.TestedOfficial);
        if (info == null)
        {
            info = new CompatibilityInfo
            {
                Status = "unknown",
                Message = "暂时无法读取版本状态。",
                UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
            };
        }
        info.OfficialCheck = officialCheck;
        Compatibility = info;

        // 授权只对“当次官方版本 + forced 状态”有效：
        // 官方版本变化、变为 verified（基线已更新）或非强制更新时自动清除，避免旧授权残留。
        string officialVersion = (officialCheck.OfficialVersion ?? "").Trim();
        if (!string.IsNullOrEmpty(Cfg.ForcedOnlineAcceptedVersion)
            && officialVersion.Length > 0
            && !string.Equals(officialCheck.State, "unavailable", StringComparison.OrdinalIgnoreCase)
            && (!string.Equals(officialCheck.State, "forced", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Cfg.ForcedOnlineAcceptedVersion.Trim(), officialVersion, StringComparison.OrdinalIgnoreCase)))
        {
            Cfg.ForcedOnlineAcceptedVersion = "";
            ConfigStore.Save(Cfg);
            Log.Write("官方版本状态变化，已清除强制更新联网授权");
        }

        Log.Write($"版本状态：{info.Status} {info.Message}；官方客户端：{officialCheck.State} {officialCheck.OfficialVersion}");
        try { Panel?.Push("compatibility", info); } catch { }
    }

    /// <summary>
    /// 用户确认在官方强制更新后仍按旧协议联网。只接受当前官方版本，
    /// 官方下一次更新会使旧确认自动失效并重新提示。
    /// </summary>
    public async Task AcceptForcedOnlineAsync(string version)
    {
        var official = Compatibility?.OfficialCheck;
        if (official == null || !string.Equals(official.State, "forced", StringComparison.OrdinalIgnoreCase))
            throw new ApiException(-1, "当前不是官方强制更新状态，无需开启该选项。");
        string current = (official.OfficialVersion ?? "").Trim();
        if (current.Length == 0 || !string.Equals(current, (version ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
            throw new ApiException(-1, "官方版本状态已变化，请刷新版本状态后重试。");

        // 维护者标记 unsafe/maintenance 或 killSwitch 属于硬阻断，不能被“风险自负”绕过。
        string hardBlock = CompatibilityGate.BlockedReason(new CompatibilityInfo
        {
            Status = Compatibility.Status,
            Message = Compatibility.Message,
            KillSwitch = Compatibility.KillSwitch,
            OfficialCheck = new OfficialCheckStatus { State = "verified" },
        }, Cfg);
        if (!string.IsNullOrEmpty(hardBlock))
            throw new ApiException(-1, "当前存在不可绕过的联网限制：" + hardBlock);

        Cfg.ForcedOnlineAcceptedVersion = current;
        ConfigStore.Save(Cfg);
        Log.Write("用户选择在官方强制更新后继续联网：" + current);

        // 如果此前因门禁断开了大厅/房间，让后台重连恢复正常；不主动切换当前房间。
        try { await Room.EnsureLobbyAsync(); }
        catch (Exception ex) { Log.Write("强制更新后恢复联网失败：" + ex.Message); }
        try { Panel?.PushBoot(); } catch { }
    }

    /// <summary>撤销“强制更新后继续联网”的确认；下一次联网操作会重新被门禁拒绝。</summary>
    public void ClearForcedOnline()
    {
        if (string.IsNullOrEmpty(Cfg.ForcedOnlineAcceptedVersion)) return;
        Cfg.ForcedOnlineAcceptedVersion = "";
        ConfigStore.Save(Cfg);
        Log.Write("用户恢复官方强制更新后的联网限制");
        try { Panel?.PushBoot(); } catch { }
    }

    /// <summary>用户主动重新检测登录态；不会恢复 HTTP 或联机，只有“重新连接”才放行。</summary>
    public async Task<bool> RecheckAuthAsync()
    {
        bool credentialValid = await Auth.RecheckAsync();
        PushAuth();
        try { Pet.BeginInvoke(new Action(Pet.RefreshHud)); } catch { }
        PushRoom();
        return credentialValid;
    }

    /// <summary>
    /// 用户显式确认“重新连接”：先用 user/me 确认凭据，再恢复大厅/上次房间。
    /// 这是唯一会重新建立联机会话的入口；可能使另一客户端下线，调用前 UI 必须二次确认。
    /// </summary>
    public async Task ReconnectAfterAuthAsync()
    {
        if (!await Auth.ProbeAndResumeAsync())
            throw new ApiException(-1, Auth.StateMessage);
        try
        {
            using var recovery = Auth.BeginRecovery();
            if (!string.IsNullOrEmpty(Cfg.RoomNo))
                await Room.JoinAsync(Cfg.RoomNo);
            else
                await Room.EnsureLobbyAsync();
        }
        catch (ApiException ex) when (ex.Code == 90002)
        {
            await Auth.ReportSessionTakenOverAsync("重新连接时账号已在其他设备登录");
            throw;
        }
        PushAuth();
        try { Pet.BeginInvoke(new Action(Pet.RefreshHud)); } catch { }
        PushRoom();
    }

    /// <summary>拉取公告，把未读队列和完整公告牌都推给面板；请求失败时保留上次结果。</summary>
    public async Task RefreshAnnouncementsAsync(bool force = false)
    {
        if (!await Announcements.RefreshAsync(force)) return;
        try
        {
            Panel?.Push("announcements", Announcements.Visible);
            Panel?.Push("announcementHistory", Announcements.All);
        }
        catch { }
    }

    /// <summary>
    /// 原版会周期性刷新 user/me：宝箱消耗与点击数都来自服务器，只靠本地累加会逐渐对不上。
    /// 这里每 60s 拉一次，只同步运行时字段，不动本地外观。
    /// </summary>
    async Task RefreshClicksAsync()
    {
        try
        {
            if (!Auth.HasToken) return;
            if (Auth.State == AuthState.NetworkUnknown)
            {
                if (!await Auth.RetryNetworkAsync()) return;
            }
            if (!Auth.CanUseOnline) return;
            await Auth.MaybeRenewAsync();
            if (!await Auth.RefreshClicksAsync(Auth.CaptureSession())) return;
            Pet.BeginInvoke(new Action(() => { Pet.RefreshHud(); }));
            try { if (Panel != null && Panel.IsHandleCreated) Panel.BeginInvoke(new Action(PushAuth)); } catch { }
        }
        catch (Exception ex) { Log.Write("刷新点击数失败 " + ex.Message); }
    }

    void ApplySkinTestArg()
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "--set-skin");
        if (i < 0 || i + 1 >= args.Length) return;
        if (!long.TryParse(args[i + 1], out long skin)) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(2500);
            Cfg.CharacterAssetId = skin;
            Cfg.LocalAppearance = true;
            ConfigStore.Save(Cfg);
            Pet.BeginInvoke(new Action(() => Pet.ReloadAppearance()));
            await Room.AnnounceAppearanceAsync();
            Log.Write("维护命令：切换本地皮肤 " + skin);
        });
    }

    /// <summary>登录/启动后自动回到上次房间：优先服务器记录的 room_no，其次本地最近房间。</summary>
    public async Task ResumeLastRoomAsync()
    {
        // 服务器 room_no 是唯一真值；本机 OwnerRoomNo/LastRoom 不能覆盖服务器的“当前不在房间”。
        string room = Auth.ServerRoomNo;
        if (!string.IsNullOrEmpty(room)) Room.RememberRoom(room);
        if (string.IsNullOrEmpty(room))
        {
            Log.Write("服务器未记录当前房间，跳过自动回房");
            return;
        }
        try
        {
            await Room.JoinAsync(room);
            Log.Write("自动回到房间 " + room);
            PushRoom();
        }
        catch (Exception ex)
        {
            Log.Write("自动回房失败 " + room + " " + ex.Message);
        }
    }

    /// <summary>切换服务器区服（cn / global）：登出账号、换 BaseUrl、重载目录与外观。</summary>
    /// <param name="announce">是否在面板提示“已切换至…”。登录流程内部的自动切换应传 false。</param>
    public async Task SwitchServerRegionAsync(string region, bool announce = true)
    {
        region = string.Equals(region, "global", StringComparison.OrdinalIgnoreCase) ? "global" : "cn";
        if (Cfg.ServerRegion == region && Auth.Api.BaseUrl == ApiClient.ResolveBaseUrl(region))
            return;

        Log.Write($"正在切换服务器区服至：{region}");
        Cfg.ServerRegion = region;
        ConfigStore.Save(Cfg);

        // 不能在这里回到大厅：此时 Auth 仍然指向旧区服，会留下旧区服的 socket。
        try { await Room.LeaveAsync(reconnectLobby: false, ownerConfirmed: true); } catch { }
        try { await Auth.LogoutAsync(); } catch { }

        Auth.UpdateBaseUrl(ApiClient.ResolveBaseUrl(region));

        try
        {
            await Catalog.LoadAsync(Auth.Api, msg => Log.Write("目录：" + msg));
            Pet.BeginInvoke(new Action(() => { Pet.ReloadAppearance(); Pet.RefreshMembers(); }));
        }
        catch (Exception ex)
        {
            Log.Write("切换区服后加载目录失败：" + ex.Message);
        }

        try
        {
            if (Panel != null && Panel.IsHandleCreated)
            {
                Panel.BeginInvoke(new Action(() =>
                {
                    Panel.PushBoot();
                    if (announce)
                        Panel.Push("toast", new { text = $"已切换至{(region == "global" ? "国际服" : "国服")}，请重新登录", kind = "ok" });
                }));
            }
        }
        catch { }
        // 旧会话的背包/图鉴缓存在 WebView 里，仅 PushBoot 不会清空；切服后强制重载页面。
        try { Panel?.ReloadPage(); } catch { }
        _ = RefreshCompatibilityAsync(force: true);
    }

    /// <summary>自动敲击定时器：按设置频率产生一次本地敲击，由 RoomManager 批量上传。</summary>
    public void ApplyAutoSettings() => ApplyAutoClickTimer();

    void ApplyAutoClickTimer()
    {
        if (_autoClickTimer == null)
            _autoClickTimer = new System.Threading.Timer(_ => AutoClickTick(), null, Timeout.Infinite, Timeout.Infinite);
        int rate = Math.Clamp(Cfg.AutoClickPerSecond, 1, 10);
        if (Cfg.AutoClickEnabled)
        {
            int period = Math.Max(50, (int)Math.Round(1000.0 / rate));
            _autoClickTimer.Change(50, period);
        }
        else
        {
            _autoClickTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _ = Room.FlushClicksNowAsync();
        }
    }

    /// <summary>
    /// 每 2 秒扫描一次进程名；命中规则时自动开启鼠标穿透，进程全部退出后恢复。
    /// 只在匹配状态发生边沿变化时操作，避免反复覆盖用户手动切换的开关。
    /// </summary>
    void CheckGameModeRules()
    {
        try
        {
            string[] rules = Cfg.GameModeRules ?? Array.Empty<string>();
            bool matched = false;
            string matchedName = null;
            if (rules.Length > 0)
            {
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        if (GameModeRules.Matches(p.ProcessName, rules))
                        {
                            matched = true;
                            matchedName = p.ProcessName;
                            break;
                        }
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }

            // 用户手动关掉穿透后不再把这次开关算作规则接管，避免游戏退出时误关。
            if (matched && _gameModeRuleAuto && !Cfg.ClickThrough)
                _gameModeRuleAuto = false;

            bool becameMatched, becameUnmatched;
            lock (_gameModeRuleGate)
            {
                becameMatched = matched && !_gameModeRuleMatched;
                becameUnmatched = !matched && _gameModeRuleMatched;
                _gameModeRuleMatched = matched;
            }

            if (becameMatched)
            {
                Log.Write("游戏模式规则命中：" + (matchedName ?? "?"));
                Pet.BeginInvoke(new Action(() =>
                {
                    if (Cfg.ClickThrough) return;
                    _gameModeRuleAuto = true;
                    Pet.SetClickThrough(true);
                }));
            }
            else if (becameUnmatched && _gameModeRuleAuto)
            {
                Log.Write("游戏模式规则进程已退出，恢复鼠标交互");
                Pet.BeginInvoke(new Action(() =>
                {
                    if (!_gameModeRuleAuto) return;
                    _gameModeRuleAuto = false;
                    Pet.SetClickThrough(false);
                }));
            }
        }
        catch (Exception ex) { Log.Write("游戏模式规则检测失败 " + ex.Message); }
    }

    void AutoClickTick()
    {
        try
        {
            if (!Cfg.AutoClickEnabled || !Auth.CanUseOnline || !Room.IsConnected) return;
            Room.AddClicks(1);
            Room.NotifyLocalAction();
            if (Cfg.AutoClickAnimate)
            {
                try { Pet.BeginInvoke(new Action(Pet.VirtualTap)); } catch { }
            }
            // 点击上传由 RoomManager 每 5 秒批量执行，避免自动敲击时高频 cr 被服务端限流。
        }
        catch (Exception ex) { Log.Write("自动敲击失败 " + ex.Message); }
    }

    /// <summary>每次本地敲击（真实或自动）按每滚动一秒上限发送一个自动表情。</summary>
    void OnLocalClick()
    {
        try
        {
            if (!Cfg.AutoEmoteEnabled || !Auth.CanUseOnline || !Room.IsConnected) return;
            int max = Math.Clamp(Cfg.AutoEmoteMaxPerSecond, 1, 20);
            long now = Environment.TickCount64;
            lock (_autoEmoteGate)
            {
                while (_autoEmoteTimes.Count > 0 && now - _autoEmoteTimes.Peek() >= 1000)
                    _autoEmoteTimes.Dequeue();
                if (_autoEmoteTimes.Count >= max) return;
                _autoEmoteTimes.Enqueue(now);
            }
            long id = PickAutoEmoteId();
            if (id > 0) Pet.BeginInvoke(new Action(() => Pet.SendEmoteById(id)));
        }
        catch (Exception ex) { Log.Write("自动表情失败 " + ex.Message); }
    }

    long PickAutoEmoteId()
    {
        try
        {
            if (Cfg.AutoEmoteMode == "specific") return Cfg.AutoEmoteId;
            long[] ids;
            if (Cfg.AutoEmoteMode == "library")
                ids = Catalog.All("emote").Select(e => e.Id).Where(id => id > 0).ToArray();
            else
                ids = (Cfg.EmoteAssetIds ?? Array.Empty<long>()).Where(id => id > 0).ToArray();
            return ids.Length == 0 ? 0 : ids[Random.Shared.Next(ids.Length)];
        }
        catch { return 0; }
    }

    /// <summary>登录/启动/重新开启自动宝箱后立刻检查一次，不等 30 秒周期。</summary>
    public void CheckAutoChestNow() => _ = AutoChestTickAsync();

    async Task AutoChestTickAsync()
    {
        if (Interlocked.Exchange(ref _autoChestTickBusy, 1) == 1) return;
        try
        {
            if (!Auth.CanUseOnline) return;
            var session = Auth.CaptureSession();
            if (session == null) return;

            // 约每 60 秒检查一次自动升阶（若开启）
            if (++_autoSynthTicks % 12 == 0 && Cfg.AutoSynthUp)
            {
                _ = AutoSynthTickAsync(session);
            }

            if (!Cfg.AutoChest || Auth.UserInfo is not System.Text.Json.Nodes.JsonObject me) return;
            long now = session.ServerNowMs;
            long left = ApiClient.ReadLong(me["left_treasure_chest"]);
            long right = ApiClient.ReadLong(me["right_treasure_chest"]);

            // 宝箱到点（或即将到点）时云端很可能已刷新点击与消耗，先同步一次再决定是否领取。
            bool due = (left > 0 && now >= left - 5000) || (right > 0 && now >= right - 5000);
            if (due)
            {
                if (!await Auth.RefreshClicksAsync(session) || !Cfg.AutoChest) return;
                Pet.BeginInvoke(new Action(() => Pet.RefreshHud()));
                if (Auth.UserInfo is not System.Text.Json.Nodes.JsonObject fresh) return;
                me = fresh;
                now = session.ServerNowMs;
                left = ApiClient.ReadLong(me["left_treasure_chest"]);
                right = ApiClient.ReadLong(me["right_treasure_chest"]);
            }

            // 开箱前如果余额不足，先触发一次自动补贴，避免本轮开箱直接进入 60 秒退避。
            if (Cfg.AutoClickBoost && Room.AvailableClicks < 1000)
                TryAutoBoostClicks();

            await TryOpenChestAsync(session, "left", left, now, false);
            await TryOpenChestAsync(session, "right", right, now, true);
        }
        catch (Exception ex) { Log.Write("自动宝箱失败 " + ex.Message); }
        finally { Interlocked.Exchange(ref _autoChestTickBusy, 0); }
    }

    async Task TryOpenChestAsync(ApiClient session, string place, long unlockAt, long now, bool right)
    {
        var next = right ? _nextRightAttempt : _nextLeftAttempt;
        if (unlockAt <= 0 || now < unlockAt + 5000 || DateTime.UtcNow < next) return;
        Log.Write("自动宝箱尝试 " + place + " available=" + Room.AvailableClicks);
        try
        {
            await OpenChestAsync(place, true, session);
        }
        catch (Exception ex)
        {
            Log.Write("自动宝箱 " + place + " 未成功：" + ex.Message);
            bool insufficient = ex is ApiException api && (api.Message ?? "").Contains("可用点击数不足");
            Auth.TryUseSession(session, () =>
            {
                if (right) _nextRightAttempt = DateTime.UtcNow.AddSeconds(insufficient && Cfg.AutoClickBoost ? 2 : 60);
                else _nextLeftAttempt = DateTime.UtcNow.AddSeconds(insufficient && Cfg.AutoClickBoost ? 2 : 60);
            });
            if (insufficient && Cfg.AutoClickBoost) TryAutoBoostClicks();
        }
    }

    async Task OpenChestSafeAsync(string place)
    {
        try { await OpenChestAsync(place, false); }
        catch (Exception ex)
        {
            Log.Write("开宝箱失败 " + place + "：" + ex.Message);
            Pet.Notify("开宝箱失败：" + ex.Message);
            Toast("开宝箱失败：" + ex.Message, "err");
        }
    }

    /// <summary>开宝箱：手动点击桌宠锚点或自动领取都走这里。返回原始响应供面板弹奖励。</summary>
    public Task<System.Text.Json.Nodes.JsonNode> OpenChestAsync(string place, bool auto)
    {
        if (!Auth.CanUseOnline) throw new ApiException(-1, Auth.PauseMessage);
        var session = Auth.CaptureSession() ?? throw new ApiException(-1, "请先登录");
        return OpenChestAsync(place, auto, session);
    }

    async Task<System.Text.Json.Nodes.JsonNode> OpenChestAsync(string place, bool auto, ApiClient session)
    {
        string blocked = OnlineBlockedReason;
        if (!string.IsNullOrEmpty(blocked)) throw new ApiException(-1, blocked);
        if (!Auth.CanUseOnline) throw new ApiException(-1, Auth.PauseMessage);
        if (Interlocked.Exchange(ref _chestBusy, 1) == 1) throw new ApiException(-1, "正在领取宝箱，请稍候");
        try
        {
            System.Text.Json.Nodes.JsonNode resp;
            long consume;
            await Room.EnterClickUploadAsync();
            try
            {
                RoomManager.ChestClicks sent = default;
                Task<System.Text.Json.Nodes.JsonNode> request = null;
                if (!Auth.TryUseSession(session, () =>
                {
                    if (auto && !Cfg.AutoChest) return;
                    sent = Room.CaptureChestClicks();
                    const long ChestCost = 1000;
                    if (sent.Available < ChestCost)
                        throw new ApiException(-1, $"可用点击数不足（需要 {ChestCost}，当前 {sent.Available}）");
                    Log.Write("开宝箱 " + place + " auto=" + auto + " click_num=" + sent.Total + " available=" + sent.Available);
                    request = session.PostAsync("api/frontend/chests/open", new { place, click_num = sent.Total });
                }) || request == null) return null;

                try { resp = await request; }
                catch (OperationCanceledException) when (Auth.CaptureSession() != session) { return null; }
                consume = Math.Max(0, ApiClient.ReadLong(resp?["click_consume"]));
                if (!Auth.TryUseSession(session, () =>
                {
                    Room.CompleteChestClicks(sent, consume);
                    Auth.MarkChestOpened(place);
                })) return null;
            }
            finally { Room.ExitClickUpload(); }

            if (Auth.CaptureSession() != session) return null;
            PushAuth();
            try { Pet.BeginInvoke(new Action(Pet.RefreshHud)); } catch { }
            _ = RefreshAfterChestAsync(session);
            long id = ApiClient.ReadLong(resp?["asset_id"]);
            string type = ApiClient.ReadString(resp?["asset_type"]);
            Log.Write("开宝箱成功 " + place + " asset=" + id + " type=" + type + " consume=" + consume);
            if (id > 0)
            {
                // 面板关闭时 chest 事件会被丢弃；先由原生层记录，面板下次启动再补进「最近获得」。
                RecentChestStore.Add(id, (Cfg.ServerRegion == "global" ? "global" : "cn") + ":" + (Auth.Cuid ?? ""));
                if (string.IsNullOrEmpty(type)) type = GuessAssetType(id);
                Pet.BeginInvoke(new Action(() => Pet.ShowPrize(type, id)));
            }
            Panel?.Push("chest", resp);
            // 刻意不在开箱路径立即触发自动升阶：此刻前端会刷新 bag.list，后台也在跑 user/me，
            // 再叠加一次 bags/list 容易触发并发 403/10007。自动升阶交给 60 秒定时器即可。
            // 通知只是反馈：任何 UI 异常都不能让它把“已领取”当成失败，否则会重复重试并吃掉冷却。
            try
            {
                if (auto)
                {
                    string label = place == "left" ? "左" : "右";
                    Toast("自动领取了" + label + "侧宝箱");
                    if (Cfg.NotifyChestClaimed) Pet.Notify("自动领取了" + label + "侧宝箱");
                }
            }
            catch (Exception ex) { Log.Write("宝箱提示失败 " + ex.Message); }
            return resp;
        }
        finally { Interlocked.Exchange(ref _chestBusy, 0); }
    }

    /// <summary>开箱成功后后台校准服务器时间戳；失败只记日志，不影响已经到手的宝箱结果。</summary>
    async Task RefreshAfterChestAsync(ApiClient session)
    {
        try
        {
            if (!await Auth.WhoAmIAsync(session)) return;
            PushAuth();
            try { Pet.BeginInvoke(new Action(Pet.RefreshHud)); } catch { }
        }
        catch (Exception ex) { Log.Write("开宝箱后刷新账号失败 " + ex.Message); }
    }

    string GuessAssetType(long id)
    {
        if (Catalog.Find("character_skin", id) != null) return "character_skin";
        if (Catalog.Find("accessory", id) != null) return "accessory";
        if (Catalog.Find("emote", id) != null) return "emote";
        return "emote";
    }

    async Task BootAsync()
    {
        Catalog.LoadOffline();
        try
        {
            await Catalog.LoadAsync(Auth.Api, msg => Log.Write("目录：" + msg));
        }
        catch (Exception ex) { Log.Write("目录加载异常 " + ex); }

        try
        {
            bool ok = await Auth.TryRestoreAsync();
            Log.Write("恢复登录 " + ok);
            Log.Write($"服务器时间偏移：{Auth.ServerTimeOffsetMs}ms");
            if (ok)
            {
                Room.SyncServerClicks(Auth.TotalClickCount, Auth.TotalClickConsume);
                if (Array.IndexOf(Environment.GetCommandLineArgs(), "--leave-room") >= 0)
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(Cfg.RoomNo)) await Room.JoinAsync(Cfg.RoomNo);
                        await Room.LeaveAsync(ownerConfirmed: true);
                        Log.Write("维护命令：已退出房间");
                    }
                    catch (Exception ex) { Log.Write("维护命令退出房间失败 " + ex.Message); }
                    Pet.BeginInvoke(new Action(() => Pet.Close()));
                    return;
                }
                await Room.EnsureLobbyAsync();
                // 压测模式不进真实房间，否则 rm 快照会把伪造成员清掉。
                if (!_stressMode) await ResumeLastRoomAsync();
                ApplySkinTestArg();
                Pet.BeginInvoke(new Action(() => Pet.ApplySettings()));
                PushAuth();
                // 启动恢复登录后立刻检查一次，别让已经可领的宝箱干等 30 秒首个定时器。
                CheckAutoChestNow();
            }
            else
            {
                Log.Write("尚未登录：" + Auth.StateName + " " + Auth.StateMessage);
                Pet.BeginInvoke(new Action(() => Pet.ApplySettings()));
                PushAuth();
            }
        }
        catch (Exception ex) { Log.Write("启动登录失败 " + ex); }
    }

    void TryAutoBoostClicks()
    {
        if (!Cfg.AutoClickBoost || !Auth.CanUseOnline || Auth.UserInfo == null) return;
        if (Room.AvailableClicks >= 1000) return;
        if (Interlocked.Exchange(ref _autoBoostBusy, 1) == 1) return;
        try
        {
            if (Room.BoostLocalClicks(10000))
            {
                // 补贴可能发生在开箱失败退避之后：清掉旧退避并立即再检查一次。
                _nextLeftAttempt = DateTime.MinValue;
                _nextRightAttempt = DateTime.MinValue;
                Log.Write("自动补贴点击 +10000");
                if (Cfg.AutoChest) CheckAutoChestNow();
            }
        }
        catch (Exception ex) { Log.Write("自动补贴点击失败 " + ex.Message); }
        finally { Interlocked.Exchange(ref _autoBoostBusy, 0); }
    }

    public void OpenPanel()
    {
        if (Panel == null || Panel.IsDisposed)
        {
            Panel = new PanelWindow(this);
            Panel.FormClosed += (s, e) => Panel = null;
        }
        Panel.ShowAndFocus();
    }

    /// <summary>快捷键“打开主面板”：无论面板上次停在哪一页，都回到主页面并置前。</summary>
    public void OpenMainPanel()
    {
        OpenPanel();
        try { Panel?.BeginInvoke(new Action(() => Panel.NavigateTo("bag"))); } catch { }
    }

    /// <summary>另一个进程再次启动时，由单实例事件线程转回 UI 线程打开面板。</summary>
    public void RequestPanelFromOtherInstance()
    {
        try { Pet.BeginInvoke(new Action(OpenPanel)); } catch { }
    }

    /// <summary>提权重启时先让当前进程把设置响应送回面板，再退出，避免两个实例长时间并存。</summary>
    public void ScheduleRestartExit()
    {
        var timer = new System.Windows.Forms.Timer { Interval = 900 };
        timer.Tick += (s, e) =>
        {
            timer.Stop();
            timer.Dispose();
            try { Pet.Close(); } catch { }
        };
        timer.Start();
    }

    public void ApplyKeyboardMode()
    {
        Keyboard.SetRhythm(Cfg.RhythmMode, Cfg.RhythmKeys);
        var mode = Cfg.KeyboardMode switch
        {
            "letters" => KeyboardWindow.Mode.Letters,
            "wuwa" => KeyboardWindow.Mode.Wuwa,
            "rhythm" => KeyboardWindow.Mode.Rhythm,
            "controller" => KeyboardWindow.Mode.Controller,
            _ => KeyboardWindow.Mode.Hidden
        };
        if (Cfg.RhythmMode && mode == KeyboardWindow.Mode.Wuwa) mode = KeyboardWindow.Mode.Rhythm;
        if (!Cfg.RhythmMode && mode == KeyboardWindow.Mode.Rhythm) mode = KeyboardWindow.Mode.Wuwa;
        Keyboard.SetMode(mode);
        LayoutKeyboard();
    }

    /// <summary>键盘始终在角色下方；下方空间不足时优先把角色往上挪，仍放不下才贴屏幕底边。</summary>
    void LayoutKeyboard()
    {
        if (Keyboard.CurrentMode == KeyboardWindow.Mode.Hidden || !Pet.Visible) return;
        double scale = Cfg.Scale * Pet.DeviceDpi / 96.0;
        Keyboard.FollowPet(Pet.Location, Pet.Size, scale);
        if (Pet.IsDragging) return;
        int lift = Keyboard.ComputeLift(Pet.Bounds, scale);
        if (lift > 0)
        {
            Pet.Location = new Point(Pet.Left, Pet.Top - lift);
            Cfg.PetX = Pet.Left;
            Cfg.PetY = Pet.Top;
            ConfigStore.Save(Cfg);
            Keyboard.FollowPet(Pet.Location, Pet.Size, scale);
        }
    }

    int _clicksPending;
    System.Threading.Timer _clicksPushTimer;

    /// <summary>
    /// 点击数每次按键都会变。面板没开时直接丢弃；开着时按 100ms 节流推送，
    /// 避免高频输入把 WebView2 的消息队列塞满（表现出来就是界面“卡住不刷新”）。
    /// </summary>
    void QueueClicksPush()
    {
        if (Panel == null) return;
        Interlocked.Exchange(ref _clicksPending, 1);
        if (_clicksPushTimer == null)
        {
            _clicksPushTimer = new System.Threading.Timer(_ =>
            {
                if (Interlocked.Exchange(ref _clicksPending, 0) == 0) return;
                PushClicksNow();
            }, null, 100, Timeout.Infinite);
            return;
        }
        _clicksPushTimer.Change(100, Timeout.Infinite);
    }

    void PushClicksNow() => Panel?.Push("clicks", new
    {
        available = Room.AvailableClicks,
        total = Room.DisplayClicks,
        consume = Room.ServerConsume
    });

    public void PushAuth()
    {
        Panel?.Push("auth", new
        {
            loggedIn = Auth.IsLoggedIn,
            hasToken = Auth.HasToken,
            onlineAvailable = Auth.CanUseOnline,
            state = Auth.StateName,
            stateMessage = Auth.StateMessage,
            lastCheckedAt = Auth.LastCheckedAtMs,
            credentialValid = Auth.LastProbeCredentialValid,
            nickname = Auth.Nickname,
            cuid = Auth.Cuid,
            uid = Auth.PlayerId > 0 ? Auth.PlayerId : Auth.UserId,
            clicks = Room.AvailableClicks,
            clickTotal = Room.DisplayClicks,
            clickConsume = Room.ServerConsume,
            roomNo = Room.RoomNo,
            chestLeft = ApiClient.ReadLong(Auth.UserInfo?["left_treasure_chest"]),
            chestRight = ApiClient.ReadLong(Auth.UserInfo?["right_treasure_chest"]),
            serverOffsetMs = Auth.ServerTimeOffsetMs,
        });
    }

    public void PushRoom() => Panel?.Push("room", RoomState());
    public void PushCatalog() => Panel?.Push("catalog", new { skins = Catalog.Count("character_skin"), accessories = Catalog.Count("accessory"), emotes = Catalog.Count("emote") });

    public object RoomState()
    {
        var members = new System.Collections.Generic.List<object>();
        bool ownerRoomMatches = !string.IsNullOrEmpty(Cfg.OwnerRoomNo)
            && string.Equals(Cfg.OwnerRoomCuid, Auth.Cuid, StringComparison.Ordinal)
            && string.Equals(Cfg.OwnerRoomNo, Room.RoomNo, StringComparison.Ordinal);
        members.Add(new { id = Auth.UserId, nickname = Auth.Nickname, owner = Room.SelfIsOwner || ownerRoomMatches, online = true, self = true, visible = true, skin = Cfg.CharacterAssetId, part = Cfg.AccessoryAssetId });
        foreach (var m in Room.Members.Values)
            members.Add(new { id = m.Id, nickname = m.Nickname, owner = m.IsOwner, online = m.Online, self = false, visible = m.Visible, skin = m.RoleKey, part = m.PartKey });
        return new
        {
            state = Room.State,
            roomNo = Room.RoomNo,
            authPaused = !Auth.CanUseOnline,
            authState = Auth.StateName,
            authMessage = Auth.StateMessage,
            ownerRoomNo = ownerRoomMatches ? Cfg.OwnerRoomNo : "",
            selfIsOwner = Room.SelfIsOwner || ownerRoomMatches,
            connected = Room.IsConnected,
            ready = Room.RoomReady,
            lastError = Room.LastError,
            lastErrorAt = Room.LastErrorAtMs,
            serviceUnavailable = Room.ServiceUnavailable,
            members,
            hiddenAll = Cfg.HideOtherPets,
            history = Cfg.RoomHistory,
        };
    }

    public void Toast(string text, string kind = "info") => Panel?.Push("toast", new { text, kind });

    public void CheckAutoSynthNow()
    {
        if (!Cfg.AutoSynthUp) return;
        _ = AutoSynthTickAsync();
    }

    const string BagListPath = "api/frontend/bags/list";
    // 自动升阶只等一小会儿，避免整个任务被别的背包请求长时间拖住；面板命令可以多等一点。
    const int AutoBagGateWaitMs = 3000;
    const int PanelBagGateWaitMs = 8000;

    /// <summary>
    /// 面板 bag.list 入口：与自动升阶共用串行门和只读重试策略，避免前端刷新与后台任务同时请求。
    /// 失败时抛出带中文消息的 ApiException，保持原有命令错误通道。
    /// </summary>
    public async Task<JsonNode> LoadBagAsync(CancellationToken cancellationToken = default)
    {
        var auth = Auth;
        if (!auth.IsLoggedIn) throw new ApiException(-1, "请先登录");
        var session = auth.CaptureSession() ?? throw new ApiException(-1, "请先登录");
        var outcome = await FetchBagAsync(session, PanelBagGateWaitMs, cancellationToken);
        if (outcome.Success) return outcome.Items;
        if (outcome.Cancelled) throw new OperationCanceledException(cancellationToken);
        throw new ApiException(outcome.Code != 0 ? outcome.Code : -1, outcome.Error, outcome.RequestId, outcome.HttpStatus);
    }

    /// <summary>
    /// 只读 bags/list：网络异常/连接重置/超时/10007/HTTP 403 最多重试 3 次（间隔 1s、2s）。
    /// 合成 POST 非幂等，绝不在这里重试。门禁被占用时短等待后返回 Busy，不发起请求。
    /// </summary>
    async Task<BagFetchOutcome> FetchBagAsync(ApiClient session, int gateWaitMs, CancellationToken cancellationToken)
    {
        var outcome = new BagFetchOutcome { Path = BagListPath };
        var total = Stopwatch.StartNew();
        bool entered = false;
        try
        {
            try { entered = await _bagGate.WaitAsync(gateWaitMs, cancellationToken); }
            catch (OperationCanceledException)
            {
                outcome.Cancelled = true;
                outcome.Error = "操作已取消";
                outcome.ErrorDetail = "cancelled";
                return outcome;
            }
            if (!entered)
            {
                outcome.Busy = true;
                outcome.Error = "背包正在同步，请稍后再试";
                outcome.ErrorDetail = "skipped=bag_gate_busy";
                Log.Warn($"phase=bag_list attempt=0 path={BagListPath} skipped=bag_gate_busy elapsed={total.ElapsedMilliseconds}ms", "bag");
                return outcome;
            }

            for (int attempt = 1; attempt <= BagFetchPolicy.MaxAttempts; attempt++)
            {
                outcome.Attempts = attempt;
                var attemptWatch = Stopwatch.StartNew();
                try
                {
                    var data = await session.PostAsync(BagListPath);
                    if (data is JsonArray arr)
                    {
                        outcome.Success = true;
                        outcome.Items = arr;
                        // 重试后成功时清掉上一次失败的上下文，避免最终快照误报 code/request_id。
                        outcome.Code = 0;
                        outcome.HttpStatus = 0;
                        outcome.RequestId = "";
                        outcome.Error = "";
                        outcome.ErrorDetail = "";
                        if (attempt > 1)
                            Log.Info($"phase=bag_list attempt={attempt} path={BagListPath} ok elapsed={attemptWatch.ElapsedMilliseconds}ms total={total.ElapsedMilliseconds}ms", "bag");
                        else
                            Log.Debug($"phase=bag_list attempt=1 path={BagListPath} ok elapsed={attemptWatch.ElapsedMilliseconds}ms", "bag");
                        return outcome;
                    }

                    // 200 + 非数组属于格式错误，重试也不会变好；直接按失败返回，不能当空背包成功。
                    outcome.FormatError = true;
                    outcome.Code = 0;
                    outcome.HttpStatus = 0;
                    outcome.RequestId = "";
                    outcome.Error = "背包数据格式异常，请稍后重试";
                    string actualType = data == null ? "null" : data.GetType().Name;
                    outcome.ErrorDetail = $"format_error actual={actualType} attempts={attempt} elapsed={attemptWatch.ElapsedMilliseconds}ms";
                    Log.Warn($"phase=bag_list attempt={attempt} path={BagListPath} format_error actual={actualType} elapsed={attemptWatch.ElapsedMilliseconds}ms", "bag");
                    return outcome;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    outcome.Cancelled = true;
                    outcome.Error = "操作已取消";
                    outcome.ErrorDetail = "cancelled";
                    return outcome;
                }
                catch (Exception ex)
                {
                    FillBagError(outcome, ex);
                    bool retry = BagFetchPolicy.IsRetryable(ex, attempt);
                    Log.Warn(
                        $"phase=bag_list attempt={attempt}/{BagFetchPolicy.MaxAttempts} path={BagListPath}"
                        + $" exception={ex.GetType().Name} code={outcome.Code} http={outcome.HttpStatus}"
                        + $" request_id={outcome.RequestId} elapsed={attemptWatch.ElapsedMilliseconds}ms retry={retry}",
                        "bag");
                    if (!retry) return outcome;
                    try { await Task.Delay(BagFetchPolicy.RetryDelayMs(attempt), cancellationToken); }
                    catch (OperationCanceledException)
                    {
                        outcome.Cancelled = true;
                        outcome.Error = "操作已取消";
                        outcome.ErrorDetail = "cancelled";
                        return outcome;
                    }
                }
            }
            return outcome;
        }
        finally
        {
            outcome.ElapsedMs = total.ElapsedMilliseconds;
            if (entered) _bagGate.Release();
        }
    }

    static void FillBagError(BagFetchOutcome outcome, Exception ex)
    {
        if (ex is ApiException api)
        {
            outcome.Code = api.Code;
            outcome.HttpStatus = api.HttpStatus;
            outcome.RequestId = api.RequestId ?? "";
            outcome.Error = string.IsNullOrWhiteSpace(api.Message) ? "背包请求失败，请稍后重试" : api.Message;
            outcome.ErrorDetail = $"exception={ex.GetType().Name} code={api.Code} http={api.HttpStatus} request_id={api.RequestId ?? ""} message={api.Message}";
            return;
        }
        outcome.Code = 0;
        outcome.HttpStatus = 0;
        outcome.RequestId = "";
        outcome.Error = "背包请求失败，请检查网络后重试";
        outcome.ErrorDetail = $"exception={ex.GetType().Name} message={ex.Message}";
    }

    /// <summary>关闭自动升阶时取消正在运行的后台任务；手动“一键全部升阶”不调用此方法。</summary>
    public void CancelAutoSynth()
    {
        CancellationTokenSource cts;
        lock (_autoSynthCancelGate)
        {
            cts = _autoSynthCts;
            _autoSynthCts = null;
        }
        if (cts == null) return;
        try { cts.Cancel(); } catch { }
    }

    async Task AutoSynthTickAsync(ApiClient session = null)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (!Cfg.AutoSynthUp || !Auth.CanUseOnline || Cfg.AnyItemSynthesisEnabled) return;
        if (!string.IsNullOrEmpty(OnlineBlockedReason)) return;
        // 自动升阶网络失败后的冷却期内直接返回，不再消耗一次请求。
        if (_autoSynthBackoff.IsCoolingDown(Environment.TickCount64)) return;
        session ??= Auth.CaptureSession();
        if (session == null || !Auth.IsLoggedIn) return;

        // 先占用运行标志再等待：CancelAutoSynth 可以打断 500ms 延迟，
        // 也避免多个触发源在延迟期间各自排队、之后同时发请求。
        CancellationTokenSource cts;
        lock (_autoSynthCancelGate)
        {
            if (_autoSynthCts != null) return;
            _autoSynthCts = cts = new CancellationTokenSource();
        }
        try
        {
            // 等待开箱/背包同步落一拍，再重新确认所有门禁和开关，避免等待期间状态变化。
            try { await Task.Delay(500, cts.Token); }
            catch (OperationCanceledException) { return; }

            if (Volatile.Read(ref _disposed) != 0) return;
            if (!Cfg.AutoSynthUp || !Auth.CanUseOnline || Cfg.AnyItemSynthesisEnabled) return;
            if (!string.IsNullOrEmpty(OnlineBlockedReason)) return;
            if (!Auth.IsLoggedIn || !ReferenceEquals(Auth.CaptureSession(), session)) return;
            // 等待期间可能已有另一次自动检查失败并写入冷却，这里再确认一次。
            if (_autoSynthBackoff.IsCoolingDown(Environment.TickCount64)) return;

            AutoSynthResult result;
            try
            {
                result = await RunAutoSynthAsync(session, manual: false, cts.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log.Write("自动升阶任务异常: " + ex.Message);
                RecordAutoSynthFailure(ex.Message);
                return;
            }
            ApplyAutoSynthResult(result);
        }
        finally
        {
            lock (_autoSynthCancelGate)
            {
                if (ReferenceEquals(_autoSynthCts, cts)) _autoSynthCts = null;
            }
            cts.Dispose();
        }
    }

    /// <summary>自动升阶返回后更新失败冷却；手动 synth.auto 不经过这里。</summary>
    void ApplyAutoSynthResult(AutoSynthResult result)
    {
        if (result == null) return;
        switch (AutoSynthBackoffPolicy.Decide(result))
        {
            case AutoSynthBackoffAction.Reset:
                int previous = _autoSynthBackoff.Failures;
                _autoSynthBackoff.Reset();
                if (previous > 0) Log.Info("auto synth recovered failures=" + previous, "synth");
                break;
            case AutoSynthBackoffAction.RecordFailure:
                RecordAutoSynthFailure(result.Message);
                break;
            default:
                // 空成功（背包空/无满足材料）不是“恢复”：保留失败计数和冷却，避免下一轮开箱又立即请求。
                if (result.Success && result.NoWork)
                    Log.Debug("auto synth no work; backoff unchanged", "synth");
                break;
        }
    }

    void RecordAutoSynthFailure(string reason)
    {
        long now = Environment.TickCount64;
        int nextRetry = _autoSynthBackoff.RecordFailure(now);
        int failures = _autoSynthBackoff.Failures;
        string text = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (text.Length > 200) text = text.Substring(0, 200);
        Log.Warn($"auto synth backoff failure={failures} nextRetry={nextRetry}s reason={text}", "synth");
    }

    static string NextRarity(string rarity) => (rarity ?? "").ToLowerInvariant() switch
    {
        "common" => "uncommon",
        "uncommon" => "rare",
        "rare" => "epic",
        _ => null
    };

    static readonly string[] SynthTypes = { "character_skin", "accessory", "emote" };
    static readonly string[] SynthRarityOrder = { "common", "uncommon", "rare", "epic" };

    static bool IsSynthType(string type) =>
        type == "character_skin" || type == "accessory" || type == "emote";

    static int RarityRank(string rarity) => (rarity ?? "").ToLowerInvariant() switch
    {
        "common" => 1,
        "uncommon" => 2,
        "rare" => 3,
        "epic" => 4,
        _ => 0
    };

    /// <summary>按图鉴数据计算某类收集物的实际最高稀有度；数据不足时不做限制。</summary>
    string MaxSynthRarity(string type)
    {
        int max = 0;
        if (Catalog.ByType.TryGetValue(type, out var entries))
        {
            foreach (var id in entries.Keys)
            {
                if (Catalog.Rarities.TryGetValue(id, out var rarity))
                    max = Math.Max(max, RarityRank(rarity));
            }
        }
        return max <= 0 ? "epic" : SynthRarityOrder[max - 1];
    }

    // 合成接口不是幂等操作：只对服务器明确返回“本次未成功/限流”的错误重试，
    // 不对超时、HTTP 5xx 等结果未知的失败盲目重发，避免重复消耗材料。
    static bool IsRetryableSynthesisError(Exception ex) =>
        ex is ApiException api && (api.Code == 50002 || api.Code == 429);

    async Task<JsonNode> PostSynthesisWithRetryAsync(ApiClient session, long[] assetIds, CancellationToken token, AutoSynthesisRoundSnapshot round = null)
    {
        const int MaxAttempts = 3;
        var sw = Stopwatch.StartNew();
        try
        {
            for (int attempt = 1; ; attempt++)
            {
                // RetryCount 表示已经重试过的次数，首次请求为 0。
                if (round != null) round.RetryCount = Math.Max(0, attempt - 1);
                try
                {
                    var resp = await session.PostAsync("api/frontend/assets/synthesis", new { asset_ids = assetIds });
                    if (round != null) round.Error = "";
                    return resp;
                }
                catch (Exception ex) when (attempt < MaxAttempts && IsRetryableSynthesisError(ex))
                {
                    if (round != null)
                    {
                        round.RetryCount = attempt;
                        round.Error = ex.Message;
                    }
                    Log.Write($"自动升阶合成请求失败（第 {attempt} 次）：{ex.Message}");
                    await Task.Delay(500 * attempt, token);
                }
            }
        }
        finally
        {
            if (round != null) round.RequestElapsedMs = sw.ElapsedMilliseconds;
        }
    }

    /// <summary>图鉴名称优先，缺失时按类型查找；只用于日志快照，不影响业务逻辑。</summary>
    string ResolveCatalogName(string type, long id)
    {
        if (Catalog.Names.TryGetValue(id, out var name) && !string.IsNullOrEmpty(name)) return name;
        return Catalog.Find(type, id)?.Name ?? "";
    }

    /// <summary>
    /// 自动升阶合成：拥有 10 件以上相同类型和稀有度的多余材料时自动进行升阶合成。
    /// 每种收集物至少保留 1 份底物不消耗；上锁只影响高级合成时是否保留最后 1 件，
    /// 不影响普通 10/5 件合成和本自动升阶（本功能始终使用普通升阶接口）。
    /// 供后台定时/登录态恢复自动触发，也可供前端“一键全部升阶”主动调用；开箱路径不再立即触发。
    /// </summary>
    public async Task<AutoSynthResult> RunAutoSynthAsync(ApiClient session = null, bool manual = false, CancellationToken cancellationToken = default)
    {
        const int MaxBagItems = 2000;
        var snapshot = SynthesisLog.StartAuto(manual, Cfg);
        var totalSw = Stopwatch.StartNew();
        int processed = 0;
        var results = new List<long>();
        string lastResultType = "";
        string errorMessage = null;
        bool cancelled = false;
        string phase = "init";
        string snapshotPath = "";

        // 只有真正发过合成请求、用户手动点“一键全部升阶”，或 bag_list 阶段失败时才落盘；
        // 自动空检查（processed == 0 且非 manual）不写文件。
        void WriteSnapshot(AutoSynthResult result, string error, bool force = false)
        {
            if (!manual && processed <= 0 && !force) return;
            snapshot.Result.Success = result.Success;
            snapshot.Result.Busy = result.Busy;
            snapshot.Result.Processed = processed;
            snapshot.Result.Count = results.Count;
            snapshot.Result.AssetIds = new List<long>(results);
            snapshot.Result.Message = result.Message ?? "";
            snapshot.Result.ElapsedMs = totalSw.ElapsedMilliseconds;
            snapshot.Result.Cancelled = cancelled;
            snapshot.Result.Error = string.IsNullOrEmpty(error) && cancelled ? "操作已取消" : (error ?? "");
            snapshot.Result.Phase = phase;
            snapshot.Result.Path = snapshotPath;
            SynthesisLog.WriteAuto(snapshot);
            Log.Info(
                $"op={snapshot.OperationId} trigger={snapshot.Trigger} result={(result.Success ? "success" : (result.Busy ? "busy" : "fail"))}"
                + $" phase={phase} processed={processed} count={results.Count} assets={string.Join(",", results)} elapsed={snapshot.Result.ElapsedMs}ms",
                "synth");
        }

        AutoSynthResult Early(string message, bool success = false, bool busy = false, bool gate = false, bool noWork = false)
        {
            var result = new AutoSynthResult { Success = success, Busy = busy, GateBlocked = gate, NoWork = noWork, Message = message };
            WriteSnapshot(result, message);
            return result;
        }

        if (Cfg.AnyItemSynthesisEnabled)
            return Early("高级合成开启时不会自动升阶，请先关闭高级合成。", gate: true);
        if (!manual && !Cfg.AutoSynthUp)
            return Early("自动升阶合成已关闭。", gate: true);

        string blocked = OnlineBlockedReason;
        if (!string.IsNullOrEmpty(blocked))
            return Early(blocked, gate: true);
        if (!Auth.CanUseOnline) return Early(Auth.PauseMessage, gate: true);
        session ??= Auth.CaptureSession();
        if (session == null) return Early("请先登录", gate: true);
        if (!Auth.IsLoggedIn || !ReferenceEquals(Auth.CaptureSession(), session))
            return Early("登录状态已变化，请稍后重试", gate: true);

        if (!await _synthesisGate.WaitAsync(0))
            return Early("正在进行其他合成操作，请稍候", busy: true);

        try
        {
            phase = "bag_list";
            snapshotPath = BagListPath;
            var bagOutcome = await FetchBagAsync(session, AutoBagGateWaitMs, cancellationToken);
            snapshot.Result.Path = BagListPath;
            snapshot.Result.Attempts = bagOutcome.Attempts;
            snapshot.Result.HttpStatus = bagOutcome.HttpStatus;
            snapshot.Result.Code = bagOutcome.Code;
            snapshot.Result.RequestId = bagOutcome.RequestId ?? "";

            if (!bagOutcome.Success)
            {
                if (bagOutcome.Cancelled)
                {
                    cancelled = true;
                    var cancelledResult = new AutoSynthResult { Success = false, Cancelled = true, Message = "操作已取消" };
                    WriteSnapshot(cancelledResult, "操作已取消", force: bagOutcome.Attempts > 0);
                    return cancelledResult;
                }
                var failed = new AutoSynthResult
                {
                    Success = false,
                    Busy = bagOutcome.Busy,
                    Message = bagOutcome.Error,
                };
                // 自动任务在 bag_list 阶段失败也要留审计快照（包括 gate 忙、未发请求的情况）。
                WriteSnapshot(failed, bagOutcome.ErrorDetail, force: true);
                return failed;
            }

            var bagArr = bagOutcome.Items;
            if (bagArr.Count == 0)
                return Early("背包暂无收集物", success: true, noWork: true);

            phase = "synthesis";
            snapshotPath = "api/frontend/assets/synthesis";

            var maxRarityByType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var type in SynthTypes) maxRarityByType[type] = MaxSynthRarity(type);

            var ownedAssetIds = new HashSet<long>();
            var pool = new Dictionary<string, List<SynthCandidateItem>>(StringComparer.OrdinalIgnoreCase);

            foreach (var node in bagArr)
            {
                if (node is not JsonObject o) continue;
                long id = ApiClient.ReadLong(o["asset_id"]);
                string type = ApiClient.ReadString(o["asset_type"]);
                if (id <= 0 || !IsSynthType(type)) continue;

                ownedAssetIds.Add(id);

                // 普通升阶/自动升阶一律保留每件底物 1 个；锁定只影响高级合成，
                // 这里不能把锁定物资排除出池子（见 wwwroot/app.js 的 synthBaseUsable）。
                long qty = Math.Max(1, ApiClient.ReadLong(o["quantity"]));
                int usable = (int)Math.Max(0, qty - 1);

                string rarity = ApiClient.ReadString(o["rarity"]).ToLowerInvariant();
                if (string.IsNullOrEmpty(rarity))
                    rarity = Catalog.Rarities.TryGetValue(id, out var r) ? r.ToLowerInvariant() : "common";

                // 快照记录初始背包里的全部可合成物资，超过 2000 条时截断并标记。
                snapshot.Bag.Total++;
                if (snapshot.Bag.Items.Count < MaxBagItems)
                {
                    snapshot.Bag.Items.Add(new AutoSynthesisItemSnapshot
                    {
                        AssetId = id,
                        Name = ResolveCatalogName(type, id),
                        Type = type,
                        Rarity = rarity,
                        Quantity = qty,
                        Usable = usable,
                    });
                }

                if (usable <= 0) continue;

                // 按类型实际最高稀有度排除，而不是只排除 epic。
                if (rarity == maxRarityByType[type]) continue;

                string groupKey = $"{type}|{rarity}";
                if (!pool.TryGetValue(groupKey, out var list))
                    pool[groupKey] = list = new List<SynthCandidateItem>();

                list.Add(new SynthCandidateItem { Id = id, Usable = usable, Type = type, Rarity = rarity });
            }
            snapshot.Bag.Truncated = snapshot.Bag.Total > MaxBagItems;

            const int BatchNeed = 10;
            const int MaxRounds = 50;
            int round = 0;

            try
            {
                while (round < MaxRounds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!manual && (!Cfg.AutoSynthUp || Cfg.AnyItemSynthesisEnabled)) break;
                    if (!string.IsNullOrEmpty(OnlineBlockedReason))
                    {
                        errorMessage = OnlineBlockedReason;
                        break;
                    }
                    if (!Auth.CanUseOnline || Auth.CaptureSession() != session)
                    {
                        errorMessage = "登录状态已变化，自动升阶已停止。";
                        break;
                    }

                    string selectedGroupKey = null;
                    List<SynthCandidateItem> selectedList = null;

                    // 优先从 common -> uncommon -> rare 升阶，便于将低阶材料升阶后的新产物继续参与更高阶合成
                    foreach (var r in new[] { "common", "uncommon", "rare" })
                    {
                        foreach (var t in SynthTypes)
                        {
                            string k = $"{t}|{r}";
                            if (pool.TryGetValue(k, out var l) && l.Sum(x => x.Usable) >= BatchNeed)
                            {
                                selectedGroupKey = k;
                                selectedList = l;
                                break;
                            }
                        }
                        if (selectedGroupKey != null) break;
                    }

                    if (selectedGroupKey == null) break;

                    var batchIds = new List<long>(BatchNeed);
                    var usedCounts = new Dictionary<long, int>();
                    int needed = BatchNeed;
                    for (int i = 0; i < selectedList.Count && needed > 0; i++)
                    {
                        var item = selectedList[i];
                        if (item.Usable <= 0) continue;
                        int take = Math.Min(item.Usable, needed);
                        for (int k = 0; k < take; k++) batchIds.Add(item.Id);
                        usedCounts[item.Id] = (usedCounts.TryGetValue(item.Id, out var used) ? used : 0) + take;
                        item.Usable -= take;
                        needed -= take;
                    }

                    if (batchIds.Count != BatchNeed) break;

                    var roundSnapshot = new AutoSynthesisRoundSnapshot
                    {
                        Round = round + 1,
                        GroupKey = selectedGroupKey,
                        ConsumedAssetIds = new List<long>(batchIds),
                        Materials = usedCounts.Select(kv => new AutoSynthesisMaterialSnapshot
                        {
                            AssetId = kv.Key,
                            Quantity = kv.Value,
                        }).ToList(),
                    };
                    snapshot.Rounds.Add(roundSnapshot);

                    // 只要请求发出，服务器就可能已经消耗/产出；用于缺失 asset_id 或异常时仍刷新背包。
                    processed++;
                    JsonNode resp;
                    try
                    {
                        resp = await PostSynthesisWithRetryAsync(session, batchIds.ToArray(), cancellationToken, roundSnapshot);
                    }
                    catch (Exception ex)
                    {
                        roundSnapshot.Error = ex.ToString();
                        throw;
                    }
                    long newAssetId = ApiClient.ReadLong(resp?["asset_id"] ?? resp?["data"]?["asset_id"]);
                    round++;
                    roundSnapshot.ResponseAssetId = newAssetId;

                    if (newAssetId <= 0)
                    {
                        errorMessage = "自动升阶合成响应未包含 asset_id，请刷新背包确认结果。";
                        roundSnapshot.Error = errorMessage;
                        Log.Write("自动升阶合成响应未包含 asset_id");
                        break;
                    }

                    results.Add(newAssetId);
                    string[] parts = selectedGroupKey.Split('|');
                    string targetType = parts[0];
                    string currentRarity = parts[1];

                    // 普通升阶不会改变类型；优先使用服务器返回的 asset_type，缺失时沿用输入类型。
                    string responseType = ApiClient.ReadString(resp?["asset_type"]);
                    if (string.IsNullOrEmpty(responseType))
                        responseType = ApiClient.ReadString(resp?["data"]?["asset_type"]);
                    string newType = IsSynthType(responseType) ? responseType : targetType;
                    lastResultType = newType;

                    string responseRarity = ApiClient.ReadString(resp?["rarity"]).ToLowerInvariant();
                    if (string.IsNullOrEmpty(responseRarity))
                        responseRarity = ApiClient.ReadString(resp?["data"]?["rarity"]).ToLowerInvariant();
                    string nextRarity;
                    if (!string.IsNullOrEmpty(responseRarity))
                        nextRarity = responseRarity;
                    else if (Catalog.Rarities.TryGetValue(newAssetId, out var catalogRarity))
                        nextRarity = catalogRarity.ToLowerInvariant();
                    else
                        nextRarity = NextRarity(currentRarity);

                    string newName = null;
                    if (Catalog.Names.TryGetValue(newAssetId, out var cnName) && !string.IsNullOrEmpty(cnName))
                        newName = cnName;
                    else if (Catalog.Find(newType, newAssetId) is { } entry && !string.IsNullOrEmpty(entry.Name))
                        newName = entry.Name;
                    if (string.IsNullOrEmpty(newName)) newName = "新收集物";

                    roundSnapshot.ResponseAssetType = newType ?? "";
                    roundSnapshot.ResponseRarity = nextRarity ?? "";
                    roundSnapshot.ResponseName = newName;

                    Log.Write($"自动升阶合成成功 [{round}]: {selectedGroupKey} -> {newName}({newAssetId})");

                    if (!string.IsNullOrEmpty(nextRarity) && nextRarity != maxRarityByType[newType])
                    {
                        string nextGroupKey = $"{newType}|{nextRarity}";
                        if (!pool.TryGetValue(nextGroupKey, out var nextList))
                            pool[nextGroupKey] = nextList = new List<SynthCandidateItem>();

                        var existingInPool = nextList.Find(x => x.Id == newAssetId);
                        if (existingInPool != null)
                        {
                            existingInPool.Usable++;
                        }
                        else if (ownedAssetIds.Contains(newAssetId))
                        {
                            nextList.Add(new SynthCandidateItem { Id = newAssetId, Usable = 1, Type = newType, Rarity = nextRarity });
                        }
                    }
                    ownedAssetIds.Add(newAssetId);

                    await Task.Delay(300, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                Log.Write("自动升阶合成已取消");
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                Log.Write("自动升阶合成执行异常: " + ex);
                Log.Exception("自动升阶合成执行异常", ex);
            }

            if (results.Count > 0 || processed > 0)
                PublishAutoSynthResults(results, lastResultType, processed, manual, errorMessage ?? (cancelled ? "操作已取消" : null));

            bool emptyAfterProcessing = processed > 0 && results.Count == 0;
            bool noWork = !cancelled && errorMessage == null && processed == 0 && results.Count == 0;
            var finalResult = new AutoSynthResult
            {
                Success = !cancelled && errorMessage == null && !emptyAfterProcessing,
                NoWork = noWork,
                Cancelled = cancelled,
                Count = results.Count,
                Processed = processed,
                AssetIds = results,
                Message = cancelled
                    ? "操作已取消"
                    : (errorMessage ?? (results.Count > 0
                        ? ""
                        : (emptyAfterProcessing
                            ? "自动升阶合成未返回新物品，请刷新背包确认。"
                            : "当前没有满足升阶条件的材料"))),
            };
            WriteSnapshot(finalResult, errorMessage);
            return finalResult;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
            Log.Write("自动升阶合成已取消");
            var cancelledResult = new AutoSynthResult { Success = false, Cancelled = true, Message = "操作已取消" };
            WriteSnapshot(cancelledResult, "操作已取消", force: processed > 0);
            return cancelledResult;
        }
        catch (Exception ex)
        {
            Log.Write("自动升阶合成执行异常: " + ex.Message);
            Log.Exception("自动升阶合成执行异常", ex);
            var failed = new AutoSynthResult { Message = ex.Message };
            WriteSnapshot(failed, ex.ToString());
            return failed;
        }
        finally
        {
            _synthesisGate.Release();
        }
    }

    void PublishAutoSynthResults(List<long> results, string lastType, int processed, bool manual, string errorMessage)
    {
        long lastId = results.Count > 0 ? results[^1] : 0;
        if (lastId > 0)
        {
            string lastName = null;
            if (Catalog.Names.TryGetValue(lastId, out var cnName) && !string.IsNullOrEmpty(cnName))
                lastName = cnName;
            else if (Catalog.Find(lastType, lastId) is { } entry && !string.IsNullOrEmpty(entry.Name))
                lastName = entry.Name;
            if (string.IsNullOrEmpty(lastName)) lastName = "新收集物";

            try { Pet.BeginInvoke(new Action(() => Pet.ShowPrize(lastType, lastId))); } catch { }

            // 面板关闭时才把最终产物放进“最近获得”队列；中间产物和面板打开时都交给 synth.auto 事件处理。
            if (Panel == null || Panel.IsDisposed)
                RecentChestStore.Add(lastId, (Cfg.ServerRegion == "global" ? "global" : "cn") + ":" + (Auth.Cuid ?? ""));

            if (!manual)
            {
                if (string.IsNullOrEmpty(errorMessage))
                    Toast($"自动升阶合成完成：共完成 {results.Count} 次合成，获得了【{lastName}】等");
                else
                    Toast($"自动升阶部分完成（{results.Count} 次）：{errorMessage}", "err");
            }
        }
        else if (processed > 0 && !manual)
        {
            Toast(string.IsNullOrEmpty(errorMessage)
                ? "自动升阶合成已执行，正在刷新背包…"
                : "自动升阶合成失败：" + errorMessage,
                string.IsNullOrEmpty(errorMessage) ? "info" : "err");
        }

        Panel?.Push("synth.auto", new { count = results.Count, processed, last_asset_id = lastId, asset_ids = results });
    }

    public Task<bool> TryBeginSynthesisAsync() => _synthesisGate.WaitAsync(0);

    public void ReleaseSynthesis() => _synthesisGate.Release();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Interlocked.Exchange(ref _disposed, 1);
            try { CancelAutoSynth(); } catch { }
            try { _autoChestTimer?.Dispose(); } catch { }
            try { _autoBoostTimer?.Dispose(); } catch { }
            try { _clickRefreshTimer?.Dispose(); } catch { }
            try { _autoClickTimer?.Dispose(); } catch { }
            try { _gameModeRuleTimer?.Dispose(); } catch { }
            try { _clicksPushTimer?.Dispose(); } catch { }
            try { _memoryTimer?.Dispose(); } catch { }
            try { Room.Dispose(); } catch { }
            try { Store.Dispose(); } catch { }
            try { Auth.Api.Dispose(); } catch { }
        }
        base.Dispose(disposing);
    }
}

public sealed class AutoSynthResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
    [JsonPropertyName("busy")]
    public bool Busy { get; set; }
    [JsonPropertyName("count")]
    public int Count { get; set; }
    [JsonPropertyName("processed")]
    public int Processed { get; set; }
    [JsonPropertyName("assetIds")]
    public List<long> AssetIds { get; set; } = new();
    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
    /// <summary>门禁/配置/登录态拦截；仅用于自动升阶退避判断，不进入前端 JSON。</summary>
    [JsonIgnore]
    internal bool GateBlocked { get; set; }
    /// <summary>空扫描成功（背包空/无满足材料）：不能据此清零失败冷却，也不进入前端 JSON。</summary>
    [JsonIgnore]
    internal bool NoWork { get; set; }
    /// <summary>取消/退出导致的中止：不推进失败冷却，也不进入前端 JSON。</summary>
    [JsonIgnore]
    internal bool Cancelled { get; set; }
}

/// <summary>bags/list 的只读请求结果；记录重试次数、耗时和错误上下文供快照审计。</summary>
sealed class BagFetchOutcome
{
    public bool Success { get; set; }
    public JsonArray Items { get; set; }
    public bool Busy { get; set; }
    public bool Cancelled { get; set; }
    public bool FormatError { get; set; }
    public string Error { get; set; } = "";
    public string ErrorDetail { get; set; } = "";
    public string Path { get; set; } = "";
    public int Attempts { get; set; }
    public long ElapsedMs { get; set; }
    public int HttpStatus { get; set; }
    public int Code { get; set; }
    public string RequestId { get; set; } = "";
}

sealed class SynthCandidateItem
{
    public long Id;
    public int Usable;
    public string Type = "";
    public string Rarity = "";
}
