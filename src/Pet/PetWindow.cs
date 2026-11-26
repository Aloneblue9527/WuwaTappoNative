using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows.Forms;
using WuwatappoNative.Assets;
using WuwatappoNative.Core;
using WuwatappoNative.Net;

namespace WuwatappoNative.Pet;

/// <summary>
/// 桌宠主窗口。几何完全按原版 CSS：272×331 逻辑像素，猫 200×200 位于 (24,120)，
/// 信息条 y=299（猫底边上方 21px），左下计数条 136×32，右侧工具栏 61×32。
/// </summary>
public sealed class PetWindow : Form
{
    readonly Settings _cfg;
    readonly AuthService _auth;
    readonly Catalog _catalog;
    readonly AssetStore _store;
    readonly PetImageLoader _loader;
    readonly RoomManager _room;
    readonly GlobalInputHook _hook = new();
    readonly Dictionary<long, RemoteCatWindow> _cats = new();
    // 自定义伙伴与真实联机成员完全分开：独立窗口字典、独立位置、独立计时，
    // 不写入 RoomManager.Members，也不参与任何服务器点击/表情上报。
    readonly Dictionary<long, RemoteCatWindow> _companionCats = new();
    readonly Dictionary<long, long> _companionClickSide = new();
    readonly Dictionary<long, long> _companionNextClickMs = new();
    readonly Dictionary<long, (double rate, bool random)> _companionClickConfig = new();
    readonly Random _companionRandom = new();
    const long SelfSelectionId = CompanionPets.ReservedSelfId;
    readonly HashSet<long> _selectedCatIds = new();
    bool _selfSelected;
    bool _groupDragging;
    long _groupDragOwner = SelfSelectionId;
    Point _groupDragCursor;
    readonly Dictionary<long, Point> _groupDragOrigins = new();
    // Ctrl 按下后先等待鼠标越过系统拖拽阈值，避免 1~3px 抖动就把整组带跑。
    bool _groupDragPending;
    long _groupDragPendingOwner = SelfSelectionId;
    Point _groupDragPendingStart;
    bool _multiSelectClickPending;
    long _multiSelectClickId = SelfSelectionId;
    bool _multiSelectClickWasSelected;

    PetFrames _frames;
    readonly LayeredSurface _surface = new();
    readonly PetComposer.FrameCache _frameCache = new();
    /// <summary>取色器拖动时的临时底栏配色；不写入配置，保存/取消后清除。</summary>
    (Color bar, Color text, Color accent, Color icon, Color iconMuted)? _themePreview;
    (Color bar, Color text, Color accent, Color icon, Color iconMuted) _themeColors;
    int _themeColorHash;
    bool _themeColorsReady;
    AnimationTimer _composeTimer;
    int _bodyFrame = 1, _headFrame = 1;
    bool _dragging, _moved;
    // 本次左键手势是否从角色本体开始。底栏/轮盘/宝箱点击不会置位，
    // OnMouseUp 据此跳过拖动收尾，避免沿用上一次拖动的 _moved/_dragOrigin。
    bool _dragGesture;
    bool _handIsLeft;
    long _lastGeometryMs;
    // 高回报率鼠标每秒可产生上千条 WM_MOUSEMOVE；窗口移动按显示刷新节拍限频。
    long _lastDragMoveMs;
    long _lastGroupDragMs;
    // 渲染 OOM 后暂停重绘的时间点（Environment.TickCount64）；避免异常风暴。
    long _renderBlockedUntilMs;
    Point _dragCursor, _dragOrigin;
    int _lastDragCursorX;
    readonly EmoteBubbleLayer _bubbles = new(10);
    readonly KeyBubbleLayer _keyBubbles = new(10);
    readonly EmoteWheelLayer _wheel = new();
    System.Windows.Forms.Timer _blink, _blinkOff, _moveReset, _handReset, _countdownTimer, _wheelCloseTimer, _wheelShowTimer, _hoverFadeTimer, _loadingTimer, _hoverFadeAnimTimer, _hoverPollTimer;
    System.Windows.Forms.Timer _companionClickTimer;
    System.Windows.Forms.Timer _zOrderWatchdog;
    System.Windows.Forms.Timer _zOrderRestackTimer;
    bool _zOrderRepairing;
    bool _zOrderRestackQueued;
    bool _zOrderRestackTimerForce;
    // 拖动期间只记下需要重排，等松手后再执行，避免看门狗/成员更新在拖动中插入 60~130ms 冻结。
    bool _zOrderRestackPending;
    bool _zOrderRestackPendingForce;
    RemoteCatWindow _draggingCat;
    long _lastZOrderRepairLogMs;
    readonly object _zOrderDiagGate = new();
    readonly Dictionary<string, long> _lastZOrderSnapshotMs = new();
    static readonly JsonSerializerOptions ZOrderJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    long _lastSkin = -1, _lastAcc = -1;
    Image _chestBtnArt, _chestBgArt;
    ContextMenuStrip _menu;
    NotifyIcon _tray;
    ToolStripMenuItem _topmostItem, _passthroughItem, _passthroughOffItem, _hoverItem, _scaleMenuItem, _opacityMenuItem, _mirrorItem;
    ToolStripMenuItem _hideOtherPetsItem;
    ToolStripMenuItem _alignGridItem;
    ToolStripMenuItem _layoutHorizontalItem, _layoutVerticalItem;
    ToolStripMenuItem _keyboardMenuItem;
    Icon _icon;

    bool _hiddenByHotkey;
    bool _autoGameModeActive;
    bool _autoGameModeCustomApplied;
    bool _autoGameModeClickThroughOwned;
    AutoGameModeSnapshot _autoGameModeSnapshot;
    readonly object _gameModeSaveGate = new();
    CaptureWindow _capture;
    Bitmap _captureFrame;

    /// <summary>进入自动游戏模式前的用户设置快照；退出时原样恢复，不写入配置。</summary>
    readonly struct AutoGameModeSnapshot
    {
        public double Scale { get; init; }
        public double Opacity { get; init; }
        public bool ClickThrough { get; init; }
        public bool RemoteFollowMainScale { get; init; }
        public bool RemoteFollowMainOpacity { get; init; }
        public bool HideOtherPets { get; init; }
        public bool HideMainBar { get; init; }
        public bool HideRemoteBars { get; init; }
        public bool KeyBubbles { get; init; }
        public string KeyboardMode { get; init; }
        public int AnimationFps { get; init; }
        public Point PetLocation { get; init; }
        public Dictionary<string, int[]> MemberPositions { get; init; }
        public Dictionary<string, int[]> CompanionPositions { get; init; }
    }

    /// <summary>全局输入监听（App 用它给键盘显示转发按键高亮）。</summary>
    public GlobalInputHook Input => _hook;

    public event Action OpenPanelRequested;
    public event Action OpenMainPanelRequested;
    /// <summary>自动敲击/自动表情开关变化后，让 App 立即启停相关定时器。</summary>
    public event Action AutoSettingsChanged;
    public event Action<string> ChestOpenRequested;
    public event Action GeometryChanged;
    public event Action<bool> PetVisibilityChanged;
    /// <summary>F5：重新拉取目录/外观/房间等界面数据。</summary>
    public event Action RefreshRequested;
    /// <summary>窗口设置被快捷键/菜单改动后触发，面板据此刷新开关状态。</summary>
    public event Action SettingsChanged;
    /// <summary>右键远程角色隐藏成员后，通知面板同步成员列表。</summary>
    public event Action MemberVisibilityChanged;
    /// <summary>主角帧/手部/眨眼变化时通知绿幕采集窗口重绘。</summary>
    public event Action CaptureChanged;
    /// <summary>绿幕窗口需要一起绘制当前键盘/手柄 GUI。</summary>
    public KeyboardWindow KeyboardUI { get; set; }

    public PetWindow(Settings cfg, AuthService auth, Catalog catalog, AssetStore store, RoomManager room)
    {
        _cfg = cfg;
        _auth = auth;
        _catalog = catalog;
        _store = store;
        _room = room;
        _loader = new PetImageLoader(catalog, store);
        _chestBtnArt = store.LoadLocal(System.IO.Path.Combine(Paths.AppDir, "assets", "ui", "chest-btn.png"));
        _chestBgArt = store.LoadLocal(System.IO.Path.Combine(Paths.AppDir, "assets", "ui", "chest-countdown.png"));

        FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "Wuwatappo";
        CreateTimers();
        BuildMenu();
        BuildTray();

        _room.MemberUpdated += id => Safe(() => UpdateCat(id));
        // 按键动作只驱动对应桌宠的手部动画；不要走 UpdateCat（那会重算外观、缩放、位置）。
        _room.MemberAction += (id, action) => Safe(() => { if (_cats.TryGetValue(id, out var c)) c.ApplyMemberAction(action); });
        _room.RosterChanged += () => Safe(RefreshCats);
        _room.MemberRemoved += id => Safe(() => RemoveCat(id));
        _room.MemberEmote += (id, name) => Safe(() => { if (_cats.TryGetValue(id, out var c)) c.ShowEmote(name); });
        _room.StateChanged += () => Safe(RefreshCats);
        _room.ForcedDisconnect += code => Safe(() => Notify(code == 90002
            ? Localization.T("notify.accountOtherDevice", "账号已在其他设备登录")
            : Localization.T("notify.roomClosed", "房间已关闭")));
        _room.SocketClosed += reason => Safe(() =>
        {
            // 登录态异常由顶部条/未登录态统一提示，不再用气泡重复“连接断开”。
            if (_auth.CanUseOnline && _room.State == "joined")
                Notify(Localization.F("notify.connectionLost", "连接断开：{0}", reason));
        });
        _room.JoinRejected += message => Safe(() => Notify(message));
        _room.ServiceUnavailableChanged += down => Safe(() =>
            Notify(down
                ? Localization.T("notify.serviceUnavailable", "联机服务暂不可用，正在自动重试…")
                : Localization.T("notify.serviceRestored", "联机服务已恢复")));

        _auth.Changed += () => Safe(OnAuthChanged);
        _room.ClicksChanged += () => Safe(() => { CaptureChanged?.Invoke(); RequestCompose(); });
        _catalog.Loaded += () => Safe(() => { _loader.Clear(); ReloadFrames(); RefreshCats(); RefreshCompanions(); });

        ReloadFrames();
        Location = cfg.PetX >= 0 ? new Point(cfg.PetX, cfg.PetY) : DefaultLocation();
        _hook.KeyDown += vk => Safe(() =>
        {
            // VK_LBUTTON 只会收到“点在本程序所有窗口之外”的左键按下；
            // 有选中时点桌面/别的程序立即退出多选。
            if (vk == 0x01) ClearSelectionOnOutsideClick();
            if (_auth.CanUseOnline)
            {
                _room.AddClicks(1);
                _room.NotifyLocalAction();
            }
            if (!_dragging) HandDown();
        });
        _hook.KeyUp += vk => Safe(() => { if (!_dragging) HandUp(); });
        _hook.KeyEdge += (vk, down) => Safe(() => { if (down) PushKeyBubble(vk); });
        _hook.HotkeyKeyDown += OnHotkeyKeyDown;
        RebuildHotkeyMap();
        _hook.Start();
    }

    // ---------- 尺寸 / 渲染 ----------

    float DpiScale => DeviceDpi / 96f;
    double EffectiveRemoteScale => _cfg.RemoteFollowMainScale ? _cfg.Scale : _cfg.RemoteScale;
    double EffectiveRemoteOpacity => _cfg.RemoteFollowMainOpacity ? _cfg.Opacity : _cfg.RemoteOpacity;
    float RemoteScaleFactor => DpiScale * (float)EffectiveRemoteScale;

    Size WindowSize()
    {
        float s = DpiScale * (float)_cfg.Scale;
        return new Size(
            Math.Max(40, (int)Math.Round(PetComposer.WindowW * s)),
            Math.Max(40, (int)Math.Round(PetComposer.WindowH * s)));
    }

    void ReloadFrames()
    {
        if (_frames != null) _frames.Changed -= OnFramesChanged;
        _frames = _loader.Get(_cfg.CharacterAssetId, _cfg.AccessoryAssetId);
        _frames.Changed += OnFramesChanged;
        UpdateLoadingAnimation();
        Log.Write($"DBG reload frames cfg={_cfg.CharacterAssetId}/{_cfg.AccessoryAssetId} ready={_frames?.Ready} loading={_frames?.Loading}");
        if (Size != WindowSize()) Size = WindowSize();
        Compose();
    }

    void OnFramesChanged() => Safe(() =>
    {
        UpdateLoadingAnimation();
        Compose();
    });

    void UpdateLoadingAnimation()
    {
        if (_frames?.Loading == true)
        {
            if (!_loadingTimer.Enabled) _loadingTimer.Start();
        }
        else _loadingTimer.Stop();
    }

    /// <summary>换装后清缓存并立即重绘（本地与云端装扮变更都走这里）。</summary>
    public void ReloadAppearance()
    {
        Log.Write($"刷新本地外观 skin={_cfg.CharacterAssetId} accessory={_cfg.AccessoryAssetId}");
        _loader.Clear();
        _wheel.Close();
        _lastSkin = _cfg.CharacterAssetId;
        _lastAcc = _cfg.AccessoryAssetId;
        ReloadFrames();
    }

    void OnAuthChanged()
    {
        if (_lastSkin != _cfg.CharacterAssetId || _lastAcc != _cfg.AccessoryAssetId)
        {
            _lastSkin = _cfg.CharacterAssetId;
            _lastAcc = _cfg.AccessoryAssetId;
            ReloadFrames();
        }
        else Compose();
    }

    void Compose()
    {
        if (!IsHandleCreated) return;
        if (_frames == null || (!_frames.Ready && !_frames.Loading)) return;
        // OOM 后 10 秒内不重试：窗口仍在，等下一轮按键/计数/计时器重绘时再重新分配缓冲。
        if (Environment.TickCount64 < _renderBlockedUntilMs) return;
        try
        {
            var info = BuildInfo(_auth.ServerNowMs);
            info.Loading = _frames.Loading;
            info.LoadingPhase = (Environment.TickCount64 % 1000) / 1000.0;
            // 高频重绘（每次按键、每次计数变化）复用同一张后缓冲。
            var surface = _surface.Begin(Size.Width, Size.Height);
            if (_frames.Ready)
            {
                // 窗口内容始终按全不透明绘制；实际透明度交给 UpdateLayeredWindow 常量 alpha。
                // 这样悬停淡出不用每帧重画整张位图，鼠标频繁移动时 CPU 占用会低很多。
                PetComposer.ComposeInto(surface, _frames, _bodyFrame, _headFrame, info, _frameCache);
            }
            else
            {
                using var g = Graphics.FromImage(surface);
                g.Clear(Color.Transparent);
                float s = surface.Width / (float)PetComposer.WindowW;
                PetComposer.DrawLoading(g, s, info.LoadingPhase,
                    PetComposer.CatX + PetComposer.CatSize / 2f,
                    PetComposer.CatY + PetComposer.CatSize / 2f);
            }
            _surface.Apply(this, EffectiveOpacity);
            CaptureChanged?.Invoke();
        }
        catch (OutOfMemoryException ex)
        {
            // 丢弃后缓冲和帧缓存，下一次重试时重新分配；不把异常抛给 UI 线程/计时器。
            _renderBlockedUntilMs = Environment.TickCount64 + RuntimeDiagnostics.RenderRetrySeconds * 1000L;
            try { _frameCache.Dispose(); } catch { }
            try { _surface.Dispose(); } catch { }
            RuntimeDiagnostics.LogRenderOom("main-pet", 0, ex);
        }
    }

    double EffectiveOpacity
    {
        get
        {
            double baseOpacity = Math.Clamp(_cfg.Opacity, 0.1, 1.0);
            if (!_cfg.HoverFade) return baseOpacity;
            double hoverFactor = 1.0 - _hoverFadeAmount * (1.0 - Math.Clamp(_cfg.HoverFadeOpacity, 0.1, 1.0));
            return Math.Clamp(baseOpacity * hoverFactor, 0.01, 1.0);
        }
    }

    /// <summary>只更新窗口常量 alpha，不重新绘制内容。</summary>
    void ApplyOpacityOnly()
    {
        if (IsHandleCreated && !IsDisposed && _surface.Bitmap != null)
            _surface.Apply(this, EffectiveOpacity);
    }

    PetComposer.Info BuildInfo(long now)
    {
        long leftTs = ApiClient.ReadLong(_auth.UserInfo?["left_treasure_chest"]);
        long rightTs = ApiClient.ReadLong(_auth.UserInfo?["right_treasure_chest"]);
        var accTransform = AccessoryTransformFor(_cfg.AccessoryAssetId);
        var theme = _themePreview.HasValue ? _themePreview.Value : ThemeBarColors();
        var (barColor, barTextColor, _, barIconColor, barIconMutedColor) = theme;
        bool showLoggedOut = _auth.ShowLoggedOutOnPet;
        bool online = _auth.CanUseOnline;
        Color loggedOutColor = EnsureBarIconReadable(
            _auth.State == AuthState.Conflict
                ? Color.FromArgb(183, 121, 47)
                : Color.FromArgb(179, 74, 63),
            barColor);
        return new PetComposer.Info
        {
            Clicks = _room.AvailableClicks,
            ShowLoggedOut = showLoggedOut,
            LoggedOutColor = loggedOutColor,
            KeyboardOn = _cfg.KeyboardMode != "hidden",
            Nickname = _auth.Nickname,
            Bubbles = _bubbles,
            KeyBubbles = _keyBubbles,
            Wheel = _wheel,
            ChestLeftTs = leftTs,
            ChestRightTs = rightTs,
            NowMs = now,
            Selected = _selfSelected,
            Mirror = _cfg.MainMirrored,
            ShowBar = !_cfg.HideMainBar,
            AccOffsetX = accTransform.X,
            AccOffsetY = accTransform.Y,
            AccScalePercent = accTransform.ScalePercent,
            AccRotationDegrees = accTransform.RotationDegrees,
            // 关闭"总是显示宝箱"时：可领取按钮常显；鼠标悬停桌宠时像表情 UI 一样露出倒计时
            ShowChestLeft = online && leftTs > 0 && (_cfg.AlwaysShowChest || _hovering || leftTs <= now),
            ShowChestRight = online && rightTs > 0 && (_cfg.AlwaysShowChest || _hovering || rightTs <= now),
            ChestButton = _chestBtnArt,
            ChestCountdown = _chestBgArt,
            BarColor = barColor,
            BarTextColor = barTextColor,
            BarAccentColor = barIconColor,
            BarAccentMutedColor = barIconMutedColor,
        };
    }

    (Color bar, Color text, Color accent, Color icon, Color iconMuted) ThemeBarColors()
    {
        // BuildInfo 每帧都会取色；高级配色是 JSON 字符串，不能每帧解析。
        int hash = HashCode.Combine(
            _cfg.ThemeAccentColor,
            _cfg.ThemeFontColor,
            _cfg.ThemeAdvanced,
            _cfg.ThemeBarFollowStandard,
            _cfg.ThemeBarColor,
            _cfg.ThemeBarTextFollowTheme,
            _cfg.ThemeBarTextColor);
        if (!_themeColorsReady || hash != _themeColorHash)
        {
            _themeColors = ResolveThemeBarPalette(_cfg);
            _themeColorHash = hash;
            _themeColorsReady = true;
        }
        return _themeColors;
    }

    /// <summary>
    /// 计算主桌宠底栏与联机昵称条颜色（只保留原有三色契约）。
    /// 实际绘制按钮所需的派生图标色请使用 <see cref="ResolveThemeBarPalette"/>。
    /// </summary>
    public static (Color bar, Color text, Color accent) ResolveThemeBarColors(Settings cfg)
    {
        var palette = ResolveThemeBarPalette(cfg);
        return (palette.bar, palette.text, palette.accent);
    }

    /// <summary>
    /// 计算主桌宠底栏与联机昵称条颜色。默认跟随标准主题；用户关闭跟随后使用独立背景色/文字色。
    /// 高级配色中的 cream / ink 也会参与底栏推导，保证面板与桌宠一致。
    /// accent 保留用户输入的主题色值；icon/iconMuted 是实际绘制键盘、菜单按钮时使用的派生色，
    /// 始终与实际底栏背景保持至少 3:1 的对比度，避免浅色主题色在浅色底栏上不可见。
    /// </summary>
    public static (Color bar, Color text, Color accent, Color icon, Color iconMuted) ResolveThemeBarPalette(Settings cfg)
    {
        cfg ??= new Settings();
        var themeAccent = ParseThemeColor(cfg.ThemeAccentColor, PetComposer.Border);
        // 面板默认字体色会跟随主题色生成；底栏用同样的暗色推导，避免面板紫字、底栏棕字。
        bool themeFontIsDefault = string.Equals(cfg.ThemeFontColor, "#8E5E43", StringComparison.OrdinalIgnoreCase);
        string advancedInk = AdvancedThemeColor(cfg, "ink");
        var themeFont = themeFontIsDefault
            ? (advancedInk.Length > 0
                ? ParseThemeColor(advancedInk, PetComposer.InkStrong)
                : BlendColor(themeAccent, Color.FromArgb(43, 23, 16), 0.62f))
            : ParseThemeColor(cfg.ThemeFontColor, PetComposer.InkStrong);
        var barBase = ParseThemeColor(AdvancedThemeColor(cfg, "cream"), PetComposer.Cream);
        var standardBar = BlendColor(barBase, themeAccent, 0.22f);
        if (cfg.ThemeBarFollowStandard)
            return (standardBar, themeFont, themeAccent,
                EnsureBarIconReadable(themeAccent, standardBar), MutedBarIcon(themeAccent, standardBar));
        var bar = ParseThemeColor(cfg.ThemeBarColor, standardBar);
        var text = cfg.ThemeBarTextFollowTheme
            ? themeFont
            : ParseThemeColor(cfg.ThemeBarTextColor, themeFont);
        return (bar, text, themeAccent,
            EnsureBarIconReadable(themeAccent, bar), MutedBarIcon(themeAccent, bar));
    }

    /// <summary>底栏按钮图标的非文本对比度目标（WCAG 非文本组件最低 3:1）。</summary>
    internal const double MinBarIconContrast = 3.0;

    /// <summary>
    /// 把主题色调整为在实际底栏背景上可读的按钮/图标色。
    /// 对比度足够时原样返回；不足时沿明度向黑/白混合到阈值，保留原色相，
    /// 而不是套用固定的棕色。浅色底栏会得到同色相的深色图标，深色底栏会得到同色相的浅色图标。
    /// </summary>
    internal static Color EnsureBarIconReadable(Color icon, Color background)
    {
        if (background.IsEmpty) background = PetComposer.Cream;
        if (icon.IsEmpty) icon = PetComposer.Ink;
        double Ratio(Color color) => KeyboardTheme.ContrastRatio(color, background);
        if (Ratio(icon) >= MinBarIconContrast) return icon;

        double towardBlack = Ratio(Color.Black);
        double towardWhite = Ratio(Color.White);
        bool darken = towardBlack >= towardWhite;
        Color target = darken ? Color.Black : Color.White;
        // 极端半透明/中间色底栏可能黑白都达不到 3:1；此时至少取对比度更高的黑或白。
        if (Math.Max(towardBlack, towardWhite) < MinBarIconContrast) return target;

        // 找刚好达到阈值的最小改动量：保留尽可能多的原主题色。
        double low = 0, high = 1;
        for (int i = 0; i < 24; i++)
        {
            double mid = (low + high) / 2;
            var candidate = BlendColor(icon, target, (float)mid);
            if (Ratio(candidate) >= MinBarIconContrast) high = mid;
            else low = mid;
        }
        var result = BlendColor(icon, target, (float)high);
        // 字节取整可能让边界候选差一点点，向目标方向补两步。
        for (int i = 0; i < 4 && Ratio(result) < MinBarIconContrast; i++)
        {
            high = Math.Min(1, high + 0.02);
            result = BlendColor(icon, target, (float)high);
        }
        return Ratio(result) >= MinBarIconContrast ? result : target;
    }

    /// <summary>键盘关闭时使用的次要图标色：向底栏混合后再校正回可读阈值，避免 75% 透明叠加后对比度不足。</summary>
    static Color MutedBarIcon(Color icon, Color background)
    {
        var seed = BlendColor(EnsureBarIconReadable(icon, background), background, 0.28f);
        return EnsureBarIconReadable(seed, background);
    }

    /// <summary>读取高级配色 JSON 中的某个颜色；任何格式问题都安静回退。</summary>
    internal static string AdvancedThemeColor(Settings cfg, string key)
    {
        string json = cfg?.ThemeAdvanced;
        if (string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return "";
            if (!doc.RootElement.TryGetProperty(key, out var node)) return "";
            if (node.ValueKind != System.Text.Json.JsonValueKind.String) return "";
            return (node.GetString() ?? "").Trim();
        }
        catch { return ""; }
    }

    /// <summary>颜色转 #RRGGBB，供 BootPayload 给前端颜色选择器显示实际值。</summary>
    public static string ThemeColorHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    internal static Color ParseThemeColor(string value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        try
        {
            var color = ColorTranslator.FromHtml(value.Trim());
            return color.IsEmpty ? fallback : color;
        }
        catch { return fallback; }
    }

    internal static Color BlendColor(Color a, Color b, float amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            (int)Math.Round(a.A + (b.A - a.A) * amount),
            (int)Math.Round(a.R + (b.R - a.R) * amount),
            (int)Math.Round(a.G + (b.G - a.G) * amount),
            (int)Math.Round(a.B + (b.B - a.B) * amount));
    }

    /// <summary>取当前头饰的本地微调参数；旧 [x,y] 数据自动补默认缩放/旋转。</summary>
    public AccessoryTransform AccessoryTransformFor(long accessoryId)
        => AccessoryTransformFor(_cfg.CharacterAssetId, accessoryId);

    /// <summary>
    /// 取指定角色+头饰的本地微调。头饰位置按角色独立保存；旧版仅按头饰保存的
    /// key 仍作为回退兼容，迁移后新角色不会继承其他角色的偏移。
    /// </summary>
    public AccessoryTransform AccessoryTransformFor(long characterId, long accessoryId)
    {
        if (accessoryId <= 0) return AccessoryTransform.Default;
        var offsets = _cfg.AccessoryOffsets;
        if (offsets == null) return AccessoryTransform.Default;
        if (characterId > 0 &&
            offsets.TryGetValue(AccessoryKey(characterId, accessoryId), out var perCharacter) &&
            perCharacter != null)
            return AccessoryTransform.FromArray(perCharacter);
        return offsets.TryGetValue(accessoryId.ToString(), out var legacy) && legacy != null
            ? AccessoryTransform.FromArray(legacy)
            : AccessoryTransform.Default;
    }

    static string AccessoryKey(long characterId, long accessoryId)
        => characterId > 0 ? characterId + ":" + accessoryId : accessoryId.ToString();

    /// <summary>取当前头饰的用户微调偏移（逻辑像素）。</summary>
    public (int x, int y) AccessoryOffsetFor(long accessoryId)
    {
        var transform = AccessoryTransformFor(accessoryId);
        return (transform.X, transform.Y);
    }

    /// <summary>设置当前头饰的完整本地 transform（滑条用），返回限制后的值。</summary>
    public AccessoryTransform SetAccessoryTransform(int x, int y, int scalePercent, int rotationDegrees)
        => SetAccessoryTransform(new AccessoryTransform(x, y, scalePercent, rotationDegrees));

    public AccessoryTransform SetAccessoryTransform(AccessoryTransform transform)
    {
        long characterId = _cfg.CharacterAssetId;
        long id = _cfg.AccessoryAssetId;
        if (id <= 0) return AccessoryTransform.Default;
        var next = transform.Clamped();
        _cfg.AccessoryOffsets ??= new Dictionary<string, int[]>();
        _cfg.AccessoryOffsets[AccessoryKey(characterId, id)] = next.ToArray();
        // 迁移/规范化后只保留角色级 key，避免其他角色继续共用旧偏移。
        if (characterId > 0) _cfg.AccessoryOffsets.Remove(id.ToString());
        Save();
        Compose();
        return next;
    }

    /// <summary>只改 X/Y，保留当前缩放与旋转。</summary>
    public (int x, int y) SetAccessoryOffset(int x, int y)
    {
        var current = AccessoryTransformFor(_cfg.AccessoryAssetId);
        var next = SetAccessoryTransform(x, y, current.ScalePercent, current.RotationDegrees);
        return (next.X, next.Y);
    }

    /// <summary>微调当前头饰位置；reset=true 时 X/Y、缩放、旋转全部回到默认值。</summary>
    public AccessoryTransform NudgeAccessory(int dx, int dy, bool reset = false)
    {
        long id = _cfg.AccessoryAssetId;
        if (id <= 0) return AccessoryTransform.Default;
        var current = AccessoryTransformFor(id);
        return reset
            ? SetAccessoryTransform(AccessoryTransform.Default)
            : SetAccessoryTransform(current.X + dx, current.Y + dy, current.ScalePercent, current.RotationDegrees);
    }

    bool _hovering, _catHovering, _hoverFadeTarget;
    double _hoverFadeAmount;

    /// <summary>统一用光标轮询判断悬停：穿透/非穿透行为一致，也避免窗口透明区域造成误触发。</summary>
    void SetHovering(bool hovering, bool catHovering)
    {
        bool contentChanged = _hovering != hovering;
        bool catChanged = _catHovering != catHovering;
        if (!contentChanged && !catChanged) return;

        if (contentChanged)
        {
            _hovering = hovering;
            if (hovering)
            {
                _wheelCloseTimer.Stop();
                // 轮盘也延迟出现；鼠标只是在角色身上快速划过时，不反复启动展开动画。
                if (!_cfg.ClickThrough)
                {
                    _wheelShowTimer.Stop();
                    _wheelShowTimer.Start();
                }
            }
            else
            {
                _wheelShowTimer.Stop();
                _wheelCloseTimer.Start();
            }
        }

        if (catChanged)
        {
            _catHovering = catHovering;
            _hoverFadeTimer.Stop();
            if (catHovering)
            {
                // 停留 500ms 才淡出：只是划过桌宠时不应该闪一下透明度。
                if (_cfg.HoverFade) _hoverFadeTimer.Start();
            }
            else StartHoverFade(false);
        }
    }

    void PollHover()
    {
        if (!IsHandleCreated || IsDisposed) return;
        // 拖动时不更新悬停淡出/轮盘状态，避免 16ms 的 UpdateLayeredWindow 和拖动抢 UI 线程。
        if (IsPetDragActive) return;
        var p = Cursor.Position;
        bool visible = Visible && !_hiddenByHotkey;
        SetHovering(visible && ContainsPetPoint(p), visible && ContainsCatPoint(p));
        foreach (var cat in AllPetWindows())
            cat.SetHovered(cat.Visible && !_hiddenByHotkey && cat.ContainsCatScreenPoint(p));
    }

    /// <summary>按当前合成位图的 alpha 判断光标是否落在实际可见内容上，透明留白不触发悬停。</summary>
    bool ContainsPetPoint(Point p)
    {
        if (!Visible || IsDisposed || !IsHandleCreated) return false;
        var pt = PointToClient(p);
        if (pt.X < 0 || pt.Y < 0 || pt.X >= Width || pt.Y >= Height) return false;
        var bmp = _surface.Bitmap;
        if (bmp == null) return false;
        try { return bmp.GetPixel(pt.X, pt.Y).A > 8; } catch { return false; }
    }

    /// <summary>只命中主角图片本体，不计入表情/键帽气泡、HUD 和轮盘；用于悬停虚化。</summary>
    bool ContainsCatPoint(Point p)
    {
        if (!Visible || IsDisposed || !IsHandleCreated || _frames == null) return false;
        var pt = PointToClient(p);
        if (pt.X < 0 || pt.Y < 0 || pt.X >= Width || pt.Y >= Height) return false;
        float s = Width / (float)PetComposer.WindowW;
        float logicalX = pt.X / s;
        float logicalY = pt.Y / s;
        float centerX = PetComposer.CatX + PetComposer.CatSize / 2f;
        float centerY = PetComposer.CatY + PetComposer.CatSize / 2f;
        if (_bubbles.HitTest(logicalX, logicalY, centerX, centerY) ||
            _keyBubbles.HitTest(logicalX, logicalY, centerX, centerY))
            return false;
        int x = (int)Math.Round(logicalX) - PetComposer.CatX;
        int y = (int)Math.Round(logicalY) - PetComposer.CatY;
        var acc = AccessoryTransformFor(_cfg.AccessoryAssetId);
        return PetComposer.HitTestCat(
            _frames, _bodyFrame, _headFrame, x, y,
            (int)Math.Round(_frames.AccOffsetX + acc.X),
            (int)Math.Round(_frames.AccOffsetY + acc.Y),
            acc.ScalePercent, acc.RotationDegrees);
    }

    bool IsSelected(long id)
        => id == SelfSelectionId ? _selfSelected : _selectedCatIds.Contains(id);

    /// <summary>真实联机角色和本地自定义伙伴共用同一套选中/拖动逻辑。</summary>
    bool TryGetPetWindow(long id, out RemoteCatWindow cat)
    {
        if (_cats.TryGetValue(id, out cat)) return true;
        return _companionCats.TryGetValue(id, out cat);
    }

    int SelectedCount()
        => (_selfSelected ? 1 : 0) + _selectedCatIds.Count(id => TryGetPetWindow(id, out _));

    void SetSelected(long id, bool selected)
    {
        if (id == SelfSelectionId)
        {
            if (_selfSelected == selected) return;
            _selfSelected = selected;
            Compose();
            return;
        }
        if (TryGetPetWindow(id, out var cat))
        {
            if (selected) _selectedCatIds.Add(id);
            else _selectedCatIds.Remove(id);
            cat.SetSelected(selected);
        }
        else _selectedCatIds.Remove(id);
    }

    void ClearSelection()
    {
        if (!_selfSelected && _selectedCatIds.Count == 0) return;
        _selfSelected = false;
        foreach (var id in _selectedCatIds.ToList())
            if (TryGetPetWindow(id, out var cat)) cat.SetSelected(false);
        _selectedCatIds.Clear();
        Compose();
    }

    /// <summary>Ctrl+A：把所有可见联机成员和本地伙伴加入选中；主角是否选中完全保持原样。</summary>
    void SelectAllRemoteCats()
    {
        _multiSelectClickPending = false;
        CancelPendingGroupDrag();
        var ids = _cats.Keys.Concat(_companionCats.Keys);
        var selection = PetSelectionLogic.BuildCtrlASelection(_selfSelected, ids);
        if (selection.SelfSelected != _selfSelected) SetSelected(SelfSelectionId, selection.SelfSelected);
        foreach (var id in selection.CatIds)
            SetSelected(id, true);
    }

    /// <summary>多选拖拽开始时记录所有选中角色的起始位置。</summary>
    void BeginGroupDrag(long ownerId, Point cursor)
    {
        _groupDragging = true;
        _groupDragOwner = ownerId;
        _groupDragCursor = cursor;
        _lastGroupDragMs = 0;
        _groupDragOrigins.Clear();
        if (_selfSelected) _groupDragOrigins[SelfSelectionId] = Location;
        foreach (var id in _selectedCatIds)
            if (TryGetPetWindow(id, out var cat) && cat.Visible)
                _groupDragOrigins[id] = cat.Location;
    }

    /// <summary>按住 Ctrl 点中角色后先进入等待，鼠标越过系统阈值才真正开始整组移动。</summary>
    void BeginPendingGroupDrag(long ownerId, Point cursor)
    {
        _groupDragPending = true;
        _groupDragPendingOwner = ownerId;
        _groupDragPendingStart = cursor;
        _groupDragging = false;
        _groupDragOrigins.Clear();
    }

    bool TryStartPendingGroupDrag(Point cursor)
    {
        if (!_groupDragPending) return false;
        var systemDrag = SystemInformation.DragSize;
        // 系统设置偶尔会给出 0/负数（远程桌面、精简系统），至少按 4px，避免刚按下就起拖。
        var threshold = new Size(Math.Max(4, systemDrag.Width), Math.Max(4, systemDrag.Height));
        if (!PetSelectionLogic.ExceedsDragThreshold(_groupDragPendingStart, cursor, threshold))
            return false;

        long owner = _groupDragPendingOwner;
        CancelPendingGroupDrag();
        // 以刚越过阈值的光标为锚点：死区内那段距离不回补，角色不会跳一下。
        BeginGroupDrag(owner, cursor);
        return true;
    }

    void CancelPendingGroupDrag()
    {
        _groupDragPending = false;
        _groupDragPendingOwner = SelfSelectionId;
    }

    /// <summary>点击桌面/其他程序或主角窗口空白时清空选择，同时丢弃未完成的 Ctrl 单击状态。</summary>
    void ClearSelectionOnOutsideClick()
    {
        _multiSelectClickPending = false;
        _multiSelectClickId = SelfSelectionId;
        _multiSelectClickWasSelected = false;
        CancelPendingGroupDrag();
        ClearSelection();
    }

    void UpdateGroupDrag(Point cursor, bool force = false)
    {
        if (!_groupDragging) return;
        // 整组拖动每次要移动 N 个窗口，必须按显示刷新节拍限频；否则 1000Hz 鼠标会把
        // 19 个窗口放大成每秒上万次 SetWindowPos。force 用于松手时补齐最后一帧。
        long now = AnimationTimer.NowMs();
        if (!force && now - _lastGroupDragMs < 16) return;
        _lastGroupDragMs = now;
        int dx = cursor.X - _groupDragCursor.X, dy = cursor.Y - _groupDragCursor.Y;
        foreach (var pair in _groupDragOrigins)
        {
            var target = new Point(pair.Value.X + dx, pair.Value.Y + dy);
            if (pair.Key == SelfSelectionId) Location = target;
            else if (TryGetPetWindow(pair.Key, out var cat)) cat.Location = target;
        }
        long tick = Environment.TickCount64;
        if (tick - _lastGeometryMs >= 16)
        {
            _lastGeometryMs = tick;
            GeometryChanged?.Invoke();
        }
    }

    void EndGroupDrag(bool moved)
    {
        if (!_groupDragging) return;
        _groupDragging = false;
        if (moved)
        {
            // 只有真正移动过才贴边；整组共用一个偏移，避免各吸各的拆散队形。
            SnapGroupToWorkingArea(Cursor.Position);
            // 网格模式下再按各自最近格子吸附；主角色不参与整组，故不会被移动。
            SnapSelectedPetsToGrid();
            if (_selfSelected)
            {
                _cfg.PetX = Left;
                _cfg.PetY = Top;
            }
            foreach (var id in _selectedCatIds)
                if (TryGetPetWindow(id, out var cat))
                    SavePetPosition(id, cat, custom: true);
            Save();
            GeometryChanged?.Invoke();
            if (!_cfg.AlignPetsToGrid && !_cfg.AllowOverlap) ResolveOverlaps();
        }
        CompleteMultiSelectClick(moved);
        _groupDragOrigins.Clear();
        FlushPendingZOrderRestack();
    }

    /// <summary>整组松手贴边：先求所有选中角色可见矩形的并集，再按同一偏移移动。</summary>
    void SnapGroupToWorkingArea(Point cursor)
    {
        var bounds = SelectedGroupVisibleBounds();
        if (bounds.IsEmpty) return;
        var wa = Screen.FromPoint(cursor).WorkingArea;
        int threshold = Math.Max(8, (int)Math.Round(28 * DpiScale));
        var delta = PetSelectionLogic.ComputeGroupSnapDelta(bounds, wa, threshold);
        if (delta.X == 0 && delta.Y == 0) return;

        if (_selfSelected) Location = new Point(Left + delta.X, Top + delta.Y);
        foreach (var id in _selectedCatIds)
            if (TryGetPetWindow(id, out var cat) && cat.Visible)
                cat.Location = new Point(cat.Left + delta.X, cat.Top + delta.Y);
    }

    /// <summary>
    /// 组可见范围：主角与 SnapToWorkingArea 的内容范围保持一致（猫 + 底栏），
    /// 远程角色用 CatVisibleRect。这里只求并集，不对单个角色做吸附。
    /// </summary>
    Rectangle SelectedGroupVisibleBounds()
    {
        Rectangle bounds = Rectangle.Empty;
        bool any = false;
        if (_selfSelected)
        {
            float s = Width / (float)PetComposer.WindowW;
            int leftOffset = (int)Math.Round(PetComposer.CatX * s);
            int rightOffset = (int)Math.Round(Math.Max(PetComposer.ToolbarX + PetComposer.ToolbarW, PetComposer.ChestRightRect.Right) * s);
            bounds = new Rectangle(Left + leftOffset, Top, Math.Max(1, rightOffset - leftOffset), Height);
            any = true;
        }
        foreach (var id in _selectedCatIds)
        {
            if (!TryGetPetWindow(id, out var cat) || !cat.Visible) continue;
            var rect = CatVisibleRect(cat);
            bounds = any ? Rectangle.Union(bounds, rect) : rect;
            any = true;
        }
        return any ? bounds : Rectangle.Empty;
    }

    /// <summary>Ctrl+单击但没有拖动时，取消点击前已选中的那个角色；拖动过则保留选择。</summary>
    void CompleteMultiSelectClick(bool moved)
    {
        if (!_multiSelectClickPending) return;
        long id = _multiSelectClickId;
        bool wasSelected = _multiSelectClickWasSelected;
        _multiSelectClickPending = false;
        if (!moved && wasSelected) SetSelected(id, false);
    }

    void OnCatDragStarted(RemoteCatWindow cat)
    {
        _draggingCat = cat;
        _multiSelectClickPending = false;
        bool ctrl = (Control.ModifierKeys & Keys.Control) != 0;
        // 透明气泡区域不参与选择/整组拖动；Ctrl 点在昵称条等非猫体区域时
        // 不要留下 ExternalDrag=true 的死手势，仍允许窗口自身拖动。
        if (!cat.ContainsCatScreenPoint(Cursor.Position))
        {
            cat.ExternalDrag = false;
            return;
        }
        // 按住 Ctrl 时先由主窗口接管，不能让 RemoteCatWindow 在死区内自己先挪动。
        cat.ExternalDrag = ctrl;
        long id = cat.MemberId;
        if (!ctrl)
        {
            ClearSelection();
            cat.ExternalDrag = false;
            return;
        }

        bool wasSelected = _selectedCatIds.Contains(id);
        if (!wasSelected) SetSelected(id, true);
        _multiSelectClickPending = true;
        _multiSelectClickId = id;
        _multiSelectClickWasSelected = wasSelected;
        // Ctrl 点击一律先进入死区等待，由 PetWindow 统一移动；
        // 不能等确认拖动后再接管，否则 RemoteCatWindow 已经先挪了 2px。
        cat.ExternalDrag = true;
        BeginPendingGroupDrag(id, Cursor.Position);
    }

    void OnCatDragMoved(RemoteCatWindow cat, Point cursor)
    {
        if (_groupDragPending && _groupDragPendingOwner == cat.MemberId)
        {
            if (!TryStartPendingGroupDrag(cursor)) return;
            UpdateGroupDrag(cursor);
            return;
        }
        if (_groupDragging && _groupDragOwner == cat.MemberId) UpdateGroupDrag(cursor);
    }

    void OnCatDragEnded(RemoteCatWindow cat, bool moved)
    {
        if (ReferenceEquals(_draggingCat, cat)) _draggingCat = null;
        try
        {
            // pending 未启动说明还在死区内：RemoteCatWindow 的 moved 只按 >2px 判断，
            // 比我们的阈值更敏感，不能让它把一次 Ctrl+单击误判成拖动。
            if (_groupDragPending && _groupDragPendingOwner == cat.MemberId && !_groupDragging)
            {
                CancelPendingGroupDrag();
                cat.ExternalDrag = false;
                CompleteMultiSelectClick(false);
                return;
            }
            if (_groupDragging && _groupDragOwner == cat.MemberId)
            {
                cat.ExternalDrag = false;
                UpdateGroupDrag(Cursor.Position, force: true);
                EndGroupDrag(true);
                return;
            }
            cat.ExternalDrag = false;
            CompleteMultiSelectClick(moved);
        }
        finally
        {
            FlushPendingZOrderRestack();
        }
    }

    /// <summary>
    /// 真实成员和本地伙伴的位置分开持久化；custom=true 表示用户手动拖过，
    /// 用于 F8 的“仅移动默认位置成员”策略。
    /// </summary>
    void SavePetPosition(long id, RemoteCatWindow cat, bool custom)
    {
        if (cat == null) return;
        if (_cats.ContainsKey(id))
        {
            string key = CatKey(id);
            _cfg.MemberPositions[key] = new[] { cat.Left, cat.Top };
            if (custom)
            {
                _cfg.MemberCustomPositions ??= new Dictionary<string, bool>();
                _cfg.MemberCustomPositions[key] = true;
            }
            return;
        }

        var companion = CompanionBySynthetic(id);
        if (companion == null) return;
        string companionKey = CompanionKey(companion.Id);
        _cfg.CompanionPositions ??= new Dictionary<string, int[]>();
        _cfg.CompanionPositions[companionKey] = new[] { cat.Left, cat.Top };
        if (custom)
        {
            _cfg.CompanionCustomPositions ??= new Dictionary<string, bool>();
            _cfg.CompanionCustomPositions[companionKey] = true;
        }
    }

    /// <summary>单个桌宠拖动结束：保存位置，并在关闭重叠时优先让被拖动的角色让位。</summary>
    void OnPetMoved(long id, Point _)
    {
        if (!TryGetPetWindow(id, out var cat)) return;
        if (_cfg.AlignPetsToGrid) SnapPetToGrid(cat);
        SavePetPosition(id, cat, custom: true);
        Save();
        if (!_cfg.AlignPetsToGrid && !_cfg.AllowOverlap) ResolveOverlaps(id);
    }

    void StartHoverFade(bool on)
    {
        if (!_cfg.HoverFade)
        {
            _hoverFadeAmount = 0;
            _hoverFadeAnimTimer.Stop();
            ApplyOpacityOnly();
            return;
        }
        _hoverFadeTarget = on;
        if (!_hoverFadeAnimTimer.Enabled) _hoverFadeAnimTimer.Start();
    }

    /// <summary>
    /// 拖动开始时取消悬停淡出。鼠标按住角色后 500ms 的淡出计时器仍会触发，
    /// 随后 16ms 一次的 UpdateLayeredWindow 正好落在拖动中，表现为半秒后顿一下。
    /// </summary>
    void CancelHoverFadeForDrag()
    {
        _hoverFadeTimer?.Stop();
        _hoverFadeAnimTimer?.Stop();
        _hoverFadeTarget = false;
        if (_hoverFadeAmount <= 0.001) return;
        _hoverFadeAmount = 0;
        ApplyOpacityOnly();
    }

    // ---------- 动画 ----------

    void CreateTimers()
    {
        _blink = new System.Windows.Forms.Timer { Interval = 5000 };
        _blink.Tick += (s, e) => { SetHead(2); _blinkOff.Start(); };
        _blinkOff = new System.Windows.Forms.Timer { Interval = 250 };
        _blinkOff.Tick += (s, e) => { _blinkOff.Stop(); SetHead(1); };
        _moveReset = new System.Windows.Forms.Timer { Interval = 220 };
        _moveReset.Tick += (s, e) => { _moveReset.Stop(); SetBody(1); };
        _handReset = new System.Windows.Forms.Timer { Interval = 220 };
        _handReset.Tick += (s, e) => { _handReset.Stop(); SetBody(1); };
        // 按键、计数、气泡和轮盘统一绘制；没有活动动画或新请求时立即停钟。
        _composeTimer = new AnimationTimer();
        _composeTimer.Tick += (s, e) =>
        {
            bool active = _bubbles.Tick();
            bool keyActive = _keyBubbles.Tick();
            Compose();
            if (!active && !keyActive && !_wheel.Animating) _composeTimer.Stop();
        };
        _countdownTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _countdownTimer.Tick += (s, e) =>
        {
            if (!_auth.CanUseOnline) return;
            long now = _auth.ServerNowMs;
            long left = ApiClient.ReadLong(_auth.UserInfo?["left_treasure_chest"]);
            long right = ApiClient.ReadLong(_auth.UserInfo?["right_treasure_chest"]);
            // 宝箱倒计时没显示时不需要每秒重画整只桌宠。
            bool showLeft = left > 0 && (_cfg.AlwaysShowChest || _hovering || left <= now);
            bool showRight = right > 0 && (_cfg.AlwaysShowChest || _hovering || right <= now);
            if (showLeft || showRight) RequestCompose();
        };
        _countdownTimer.Start();
        _wheelShowTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _wheelShowTimer.Tick += (s, e) =>
        {
            _wheelShowTimer.Stop();
            if (_hovering && !_cfg.ClickThrough && !_hiddenByHotkey) ShowWheel();
        };
        _wheelCloseTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _wheelCloseTimer.Tick += (s, e) =>
        {
            if (!_hovering)
            {
                bool wasOpen = _wheel.Open;
                _wheel.Close();
                if (wasOpen) Compose();
            }
            // 已经重新悬停到实际角色上（或轮盘已关闭）就停止，避免空转。
            _wheelCloseTimer.Stop();
        };
        _hoverFadeTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _hoverFadeTimer.Tick += (s, e) =>
        {
            _hoverFadeTimer.Stop();
            if (!_catHovering || !_cfg.HoverFade) return;
            StartHoverFade(true);
        };
        _hoverFadeAnimTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _hoverFadeAnimTimer.Tick += (s, e) =>
        {
            double target = _hoverFadeTarget ? 1.0 : 0.0;
            double diff = target - _hoverFadeAmount;
            if (Math.Abs(diff) < 0.06)
            {
                _hoverFadeAmount = target;
                _hoverFadeAnimTimer.Stop();
            }
            else _hoverFadeAmount += Math.Sign(diff) * 0.08;
            ApplyOpacityOnly();
        };
        // 悬停有 500ms 停留判定，150ms 轮询足够；降低轮询频率可减少多开联机成员时的 UI 线程开销。
        _hoverPollTimer = new System.Windows.Forms.Timer { Interval = 150 };
        _hoverPollTimer.Tick += (s, e) => PollHover();
        _hoverPollTimer.Start();
        // 兜底检测：Windows/DWM 或其它置顶窗口偶尔会把桌宠层级拆开；
        // 每秒检查一次主桌宠 + 全部可见联机桌宠是否仍在顶层且主桌宠在最上方。
        _zOrderWatchdog = new System.Windows.Forms.Timer { Interval = 2000 };
        _zOrderWatchdog.Tick += (s, e) => EnsurePetZOrder();
        _zOrderWatchdog.Start();
        // 成员更新/新建窗口会在短时间内连续触发重排；合并成一次，避免 2N 次 SetWindowPos 风暴。
        _zOrderRestackTimer = new System.Windows.Forms.Timer { Interval = 200 };
        _zOrderRestackTimer.Tick += (s, e) =>
        {
            _zOrderRestackTimer.Stop();
            _zOrderRestackQueued = false;
            bool force = _zOrderRestackTimerForce;
            _zOrderRestackTimerForce = false;
            if (IsPetDragActive)
            {
                _zOrderRestackPending = true;
                _zOrderRestackPendingForce |= force;
                return;
            }
            RestackPetWindows(force);
        };
        _loadingTimer = new System.Windows.Forms.Timer { Interval = 80 };
        _loadingTimer.Tick += (s, e) =>
        {
            if (_frames?.Loading != true)
            {
                _loadingTimer.Stop();
                Compose();
                return;
            }
            Compose();
        };
        // 自定义伙伴自动点击：250ms 检查一次即可，真正到点才切换左右手动画。
        // 计时完全在本地，不经过 RoomManager.AddClicks，也不上传任何点击。
        _companionClickTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _companionClickTimer.Tick += (s, e) => TickCompanionClicks();
        _companionClickTimer.Start();
        _blink.Start();
    }

    void SetHead(int f) { if (_headFrame == f) return; _headFrame = f; CaptureChanged?.Invoke(); RequestCompose(); }
    void SetBody(int f) { if (_bodyFrame == f) return; _bodyFrame = f; CaptureChanged?.Invoke(); RequestCompose(); }

    /// <summary>同一帧内的按键、计数和动画请求只触发一次绘制，不积压 UI 消息。</summary>
    void RequestCompose()
    {
        if (IsDisposed || !IsHandleCreated) return;
        _composeTimer.Start();
    }

    /// <summary>每次按下：左右手交替落下，220ms 后若未抬起自动回位。</summary>
    void HandDown(bool log = true)
    {
        _handIsLeft = !_handIsLeft;
        if (log) Log.Write("DBG hand down " + (_handIsLeft ? "left" : "right"));
        SetBody(_handIsLeft ? 2 : 3);
        _handReset.Stop();
        _handReset.Start();
    }

    void HandUp()
    {
        Log.Write("DBG hand up");
        _handReset.Stop();
        SetBody(1);
    }

    /// <summary>自动敲击的桌宠动作；只驱动本地手部动画，不点亮键位显示器。</summary>
    public void VirtualTap()
    {
        if (_hiddenByHotkey || _dragging) return;
        HandDown(false);
        Compose();
    }

    /// <summary>手柄按键：与键盘按下完全一致——计数 + 广播 + 挥手。</summary>
    public void GamepadTap()
    {
        if (_hiddenByHotkey) return;
        if (_auth.CanUseOnline)
        {
            _room.AddClicks(1);
            _room.NotifyLocalAction();
        }
        if (!_dragging) HandDown();
        Compose();
    }

    public void GamepadRelease()
    {
        if (_hiddenByHotkey) return;
        if (!_dragging) HandUp();
    }

    // ---------- 鼠标 ----------

    sealed record HitZone(bool Cat, bool Counter, bool Keyboard, bool Menu, string Chest, bool ChestReady);

    HitZone HitTest(Point p)
    {
        float s = Size.Width / (float)PetComposer.WindowW;
        int x = (int)Math.Round(p.X / s), y = (int)Math.Round(p.Y / s);
        // 底栏隐藏时不保留不可见的点击热区，避免误触打开面板/切换键盘。
        bool barVisible = !_cfg.HideMainBar;
        bool kb = barVisible && PetComposer.KeyboardRect.Contains(x, y);
        bool menu = barVisible && PetComposer.MenuRect.Contains(x, y);
        bool counter = barVisible && PetComposer.CounterRect.Contains(x, y) && !kb && !menu;
        bool cat = PetComposer.CatRect.Contains(x, y);

        long now = _auth.ServerNowMs;
        long leftTs = ApiClient.ReadLong(_auth.UserInfo?["left_treasure_chest"]);
        long rightTs = ApiClient.ReadLong(_auth.UserInfo?["right_treasure_chest"]);
        bool online = _auth.CanUseOnline;
        string chest = null;
        bool chestReady = false;
        if (online && leftTs > 0 && (_cfg.AlwaysShowChest || _hovering || leftTs <= now) && PetComposer.ChestLeftRect.Contains(x, y))
        { chest = "left"; chestReady = leftTs <= now; }
        else if (online && rightTs > 0 && (_cfg.AlwaysShowChest || _hovering || rightTs <= now) && PetComposer.ChestRightRect.Contains(x, y))
        { chest = "right"; chestReady = rightTs <= now; }
        return new HitZone(cat, counter, kb, menu, chest, chestReady);
    }

    /// <summary>
    /// 非拖动按下（底栏/轮盘/宝箱/空白）先清掉上一次拖动可能残留的状态，
    /// 否则随后的 OnMouseUp 会拿旧的 _moved/_dragOrigin 把窗口挪走。
    /// </summary>
    void ResetDragGesture()
    {
        _dragGesture = false;
        _dragging = false;
        _moved = false;
        _groupDragging = false;
        _groupDragOrigins.Clear();
        CancelPendingGroupDrag();
    }

    /// <summary>角色本体按下左键：本次 MouseUp 才允许执行贴边、保存位置和重叠重排。</summary>
    void BeginDragGesture()
    {
        _dragGesture = true;
        _dragging = true;
        _moved = false;
        _dragCursor = Cursor.Position;
        _lastDragCursorX = _dragCursor.X;
        _dragOrigin = Location;
        _lastDragMoveMs = 0;
        Capture = true;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Right) { RefreshMenu(); _menu.Show(this, e.Location); return; }
        if (e.Button != MouseButtons.Left) return;

        // 任何一次新的左键按下都从干净的拖动状态开始；只有命中角色本体才重新进入拖动手势。
        ResetDragGesture();

        var zone = HitTest(e.Location);

        float scale = Size.Width / (float)PetComposer.WindowW;
        var logical = new PointF(e.X / scale, e.Y / scale);
        if (_wheel.Open && _wheel.Hit(logical, PetComposer.CatX, PetComposer.CatY, out long emoteId))
        {
            Log.Write("DBG wheel click emote=" + emoteId + " at " + logical.X + "," + logical.Y);
            SendEmoteById(emoteId);
            Compose();
            return;
        }

        if (!string.IsNullOrEmpty(zone.Chest))
        {
            if (!zone.ChestReady) return;
            Log.Write("DBG chest click " + zone.Chest);
            ChestOpenRequested?.Invoke(zone.Chest);
            return;
        }
        if (zone.Keyboard) { CycleKeyboard(); return; }
        if (zone.Menu || zone.Counter)
        {
            if (_auth.CanUseOnline) _room.AddClicks(1);
            Compose();
            OpenPanelRequested?.Invoke();
            return;
        }
        if (!zone.Cat)
        {
            // 非交互的空白/HUD 区域：顺手退出多选，但不影响上面表情轮盘/宝箱/键盘/菜单的点击逻辑。
            ClearSelectionOnOutsideClick();
            return;
        }

        bool ctrl = (Control.ModifierKeys & Keys.Control) != 0;
        if (ctrl)
        {
            CancelHoverFadeForDrag();
            bool wasSelected = _selfSelected;
            if (!wasSelected) SetSelected(SelfSelectionId, true);
            _multiSelectClickPending = true;
            _multiSelectClickId = SelfSelectionId;
            _multiSelectClickWasSelected = wasSelected;
            BeginPendingGroupDrag(SelfSelectionId, Cursor.Position);
            BeginDragGesture();
            return;
        }

        _multiSelectClickPending = false;
        ClearSelection();
        if (_auth.CanUseOnline)
        {
            _room.AddClicks(1);
            _room.NotifyLocalAction();
        }
        CancelHoverFadeForDrag();
        HandDown();
        Compose();
        BeginDragGesture();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging || !_dragGesture)
        {
            return;
        }

        // 抽干堆积的 WM_MOUSEMOVE 消息，防止 1000Hz+ 高回报率电竞鼠标队列延迟积压
        while (PeekMessage(out _, Handle, WM_MOUSEMOVE, WM_MOUSEMOVE, PM_REMOVE)) { }

        var now = Cursor.Position;
        if (_groupDragPending && _groupDragPendingOwner == SelfSelectionId)
        {
            // 没越过系统拖拽阈值前一动不动，避免点选第二个角色时手抖导致整组位移。
            if (!TryStartPendingGroupDrag(now)) return;
            _moved = true;
            UpdateGroupDrag(now);
            return;
        }
        int dx = now.X - _dragCursor.X, dy = now.Y - _dragCursor.Y;
        if (!_moved && Math.Abs(dx) + Math.Abs(dy) < 3) return;
        _moved = true;

        if (_groupDragging && _groupDragOwner == SelfSelectionId)
        {
            UpdateGroupDrag(now);
            return;
        }

        // 拖动过程中纯 1:1 硬件级跟随，不执行贴边吸附，彻底消除边缘拖拽粘滞感与阻力。
        // 高回报率鼠标按 8ms（约 125Hz）节流；否则每秒上千次 SetWindowPos 会占满 UI 线程。
        Point target = new Point(_dragOrigin.X + dx, _dragOrigin.Y + dy);
        long moveNow = AnimationTimer.NowMs();
        if (target != Location && moveNow - _lastDragMoveMs >= 8)
        {
            _lastDragMoveMs = moveNow;
            Location = target;
        }

        // 转向死区（移动超过 15 像素才切换朝向），避免高频抖动狂刷 Compose 重绘
        int stepDx = now.X - _lastDragCursorX;
        if (stepDx < -15)
        {
            _lastDragCursorX = now.X;
            SetBody(2);
            _moveReset.Stop();
            _moveReset.Start();
        }
        else if (stepDx > 15)
        {
            _lastDragCursorX = now.X;
            SetBody(3);
            _moveReset.Stop();
            _moveReset.Start();
        }

        // 高刷新率鼠标每秒能产生上千次移动；键盘跟随最多按 60Hz 更新即可，
        // 否则每次移动都重排键盘窗口，拖动时会吃掉一个核。
        long tick = Environment.TickCount64;
        if (tick - _lastGeometryMs >= 16)
        {
            _lastGeometryMs = tick;
            GeometryChanged?.Invoke();
        }
    }

    /// <summary>拖动到屏幕/任务栏边缘附近时自动贴边，松手后位置照常保存。</summary>
    Point SnapToWorkingArea(Point target, Point cursor)
    {
        var wa = Screen.FromPoint(cursor).WorkingArea;
        int threshold = Math.Max(8, (int)Math.Round(28 * DpiScale));
        float s = Width / (float)PetComposer.WindowW;
        int contentLeftOffset = (int)Math.Round(PetComposer.CatX * s);
        int contentRightOffset = (int)Math.Round(Math.Max(PetComposer.ToolbarX + PetComposer.ToolbarW, PetComposer.ChestRightRect.Right) * s);
        int left = target.X, top = target.Y;

        // 左右按角色/HUD 的可见范围判断，避免窗口透明边距导致看起来没有贴住。
        if (Math.Abs(left + contentLeftOffset - wa.Left) <= threshold)
            left = wa.Left - contentLeftOffset;
        else if (Math.Abs(left + contentRightOffset - wa.Right) <= threshold)
            left = wa.Right - contentRightOffset;

        // 下方窗口底边就是计数条底边，正好落在任务栏上沿；上方保留气泡净空。
        if (Math.Abs(top - wa.Top) <= threshold) top = wa.Top;
        else if (Math.Abs(top + Height - wa.Bottom) <= threshold) top = wa.Bottom - Height;

        return new Point(left, top);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left)
        {
            Capture = false;
            // 只有从角色本体开始的手势才允许拖动收尾。底栏/轮盘/宝箱/空白点击的
            // OnMouseDown 已提前返回，这里必须直接结束，不能沿用上一次拖动的 _moved。
            if (!_dragGesture) return;

            if (_groupDragPending && _groupDragPendingOwner == SelfSelectionId && !_groupDragging)
            {
                // 死区内松手仍按 Ctrl+单击处理，不能因为 1~3px 抖动当成拖动。
                CancelPendingGroupDrag();
                _dragging = false;
                _dragGesture = false;
                _moved = false;
                CompleteMultiSelectClick(false);
                FlushPendingZOrderRestack();
                return;
            }
            if (_groupDragging && _groupDragOwner == SelfSelectionId)
            {
                bool moved = _moved;
                if (moved) UpdateGroupDrag(Cursor.Position, force: true);
                _dragging = false;
                _dragGesture = false;
                _moved = false;
                EndGroupDrag(moved);
                return;
            }
            bool dragged = _moved;
            if (dragged)
            {
                // 8ms 节流可能丢掉最后一帧；松手前按当前光标补一次最终位置，避免落点偏移。
                var finalTarget = new Point(
                    _dragOrigin.X + (Cursor.Position.X - _dragCursor.X),
                    _dragOrigin.Y + (Cursor.Position.Y - _dragCursor.Y));
                if (finalTarget != Location) Location = finalTarget;
                Location = SnapToWorkingArea(Location, Cursor.Position);
                _moveReset.Stop();
                SetBody(1);
            }
            _dragging = false;
            _dragGesture = false;
            _moved = false;
            if (dragged)
            {
                _cfg.PetX = Left;
                _cfg.PetY = Top;
                Save();
                GeometryChanged?.Invoke();
                if (!_cfg.AlignPetsToGrid && !_cfg.AllowOverlap) ResolveOverlaps();
            }
            CompleteMultiSelectClick(dragged);
            FlushPendingZOrderRestack();
        }
    }

    // ---------- 窗口样式 ----------

    bool StyleDirty;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style = unchecked((int)0x80000000L) | 0x10000000;
            cp.ExStyle = 0x00080000 | 0x00000080 | 0x08000000
                | ((_cfg == null || _cfg.Topmost) ? 0x00000008 : 0)
                | ((_cfg != null && _cfg.ClickThrough) ? 0x00000020 : 0);
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    /// <summary>供游戏模式规则/外部控制切换鼠标穿透；会同步保存并刷新菜单与面板。</summary>
    public void SetClickThrough(bool enabled)
    {
        if (_cfg.ClickThrough == enabled) return;
        _cfg.ClickThrough = enabled;
        OnManualClickThroughChanged();
        Save();
        ApplySettings();
        NotifySettingsChanged();
    }

    /// <summary>
    /// 用户通过 F7/托盘/设置页手动切换穿透时调用；legacy 自动游戏模式不再在退出时覆盖它。
    /// </summary>
    public void OnManualClickThroughChanged()
    {
        lock (_gameModeSaveGate)
        {
            if (!_autoGameModeActive || _autoGameModeCustomApplied || !_autoGameModeClickThroughOwned) return;
            _autoGameModeClickThroughOwned = false;
            _autoGameModeSnapshot = _autoGameModeSnapshot with { ClickThrough = _cfg.ClickThrough };
        }
    }

    /// <summary>
    /// 全屏/进程规则进入或退出游戏模式。启用自定义配置时临时应用一份完整桌面配置，
    /// 退出时恢复进入前的所有值；未启用时保持旧行为，只切换鼠标穿透。
    /// </summary>
    public void SetAutoGameMode(bool active)
    {
        if (active) EnterAutoGameMode();
        else ExitAutoGameMode();
    }

    void EnterAutoGameMode()
    {
        if (_autoGameModeActive) return;
        lock (_gameModeSaveGate)
        {
            if (_autoGameModeActive) return;
            _autoGameModeSnapshot = CaptureAutoGameModeState(_cfg, Location);
            _autoGameModeActive = true;
            _autoGameModeCustomApplied = _cfg.GameModeCustomEnabled;
            _autoGameModeClickThroughOwned = !_autoGameModeCustomApplied;
            ConfigStore.SaveOverride = _autoGameModeCustomApplied ? SaveGameModeBase : null;

            if (_autoGameModeCustomApplied)
            {
                _cfg.HideOtherPets = _cfg.GameModeHideOtherPets;
                _cfg.Opacity = Math.Clamp(_cfg.GameModeOpacity, 0.2, 1.0);
                _cfg.ClickThrough = _cfg.GameModeClickThrough;
                _cfg.Scale = Math.Clamp(_cfg.GameModeScale, 0.25, 2.0);
                // 自定义项明确作用于主角；不要让联机角色/伙伴因“跟随主角”被一起改变。
                _cfg.RemoteFollowMainScale = false;
                _cfg.RemoteFollowMainOpacity = false;
                if (_cfg.GameModeShowKeyboard)
                {
                    string keyboard = _cfg.KeyboardMode;
                    _cfg.KeyboardMode = string.IsNullOrEmpty(keyboard) || keyboard == "hidden" ? "letters" : keyboard;
                }
                _cfg.HideMainBar = _cfg.GameModeHideBars;
                _cfg.HideRemoteBars = _cfg.GameModeHideBars;
                _cfg.KeyBubbles = _cfg.GameModeKeyBubbles;
                _cfg.AnimationFps = Settings.NormalizeAnimationFps(_cfg.GameModeAnimationFps);
            }
            else
            {
                _cfg.ClickThrough = true;
            }
        }

        try
        {
            ApplyAutoGameModeState(_autoGameModeCustomApplied);
        }
        catch
        {
            lock (_gameModeSaveGate)
            {
                bool appliedProfile = _autoGameModeCustomApplied;
                _autoGameModeActive = false;
                _autoGameModeCustomApplied = false;
                _autoGameModeClickThroughOwned = false;
                if (appliedProfile)
                {
                    RestoreAutoGameModeConfig();
                    RestoreAutoGameModePositions();
                }
                else
                {
                    _cfg.ClickThrough = _autoGameModeSnapshot.ClickThrough;
                }
                ConfigStore.SaveOverride = null;
            }
            throw;
        }
        Log.Write(_autoGameModeCustomApplied ? "游戏模式自定义已启用" : "游戏模式自动开启（鼠标穿透）");
    }

    void ExitAutoGameMode()
    {
        bool appliedProfile;
        lock (_gameModeSaveGate)
        {
            if (!_autoGameModeActive) return;
            appliedProfile = _autoGameModeCustomApplied;
            _autoGameModeCustomApplied = false;
            _autoGameModeActive = false;
            ConfigStore.SaveOverride = null;
            if (appliedProfile)
            {
                RestoreAutoGameModeConfig();
                RestoreAutoGameModePositions();
            }
            else if (_autoGameModeClickThroughOwned)
            {
                // 旧行为只接管鼠标穿透；用户游戏期间手动切过穿透则不再覆盖。
                _cfg.ClickThrough = _autoGameModeSnapshot.ClickThrough;
            }
            _autoGameModeClickThroughOwned = false;
        }
        ApplyAutoGameModeState(appliedProfile);
        if (appliedProfile)
        {
            lock (_gameModeSaveGate) RestoreAutoGameModePositions();
        }
        Save();
        Log.Write(appliedProfile ? "游戏模式已退出，已恢复进入前设置" : "游戏模式已退出，已恢复鼠标交互");
    }

    AutoGameModeSnapshot CaptureAutoGameModeState(Settings cfg, Point petLocation)
    {
        return new AutoGameModeSnapshot
        {
            Scale = cfg.Scale,
            Opacity = cfg.Opacity,
            ClickThrough = cfg.ClickThrough,
            RemoteFollowMainScale = cfg.RemoteFollowMainScale,
            RemoteFollowMainOpacity = cfg.RemoteFollowMainOpacity,
            HideOtherPets = cfg.HideOtherPets,
            HideMainBar = cfg.HideMainBar,
            HideRemoteBars = cfg.HideRemoteBars,
            KeyBubbles = cfg.KeyBubbles,
            KeyboardMode = cfg.KeyboardMode,
            AnimationFps = cfg.AnimationFps,
            PetLocation = petLocation,
            MemberPositions = ClonePositions(cfg.MemberPositions),
            CompanionPositions = ClonePositions(cfg.CompanionPositions),
        };
    }

    static void ApplyAutoGameModeConfigTo(Settings cfg, AutoGameModeSnapshot snapshot)
    {
        cfg.Scale = snapshot.Scale;
        cfg.Opacity = snapshot.Opacity;
        cfg.ClickThrough = snapshot.ClickThrough;
        cfg.RemoteFollowMainScale = snapshot.RemoteFollowMainScale;
        cfg.RemoteFollowMainOpacity = snapshot.RemoteFollowMainOpacity;
        cfg.HideOtherPets = snapshot.HideOtherPets;
        cfg.HideMainBar = snapshot.HideMainBar;
        cfg.HideRemoteBars = snapshot.HideRemoteBars;
        cfg.KeyBubbles = snapshot.KeyBubbles;
        cfg.KeyboardMode = snapshot.KeyboardMode;
        cfg.AnimationFps = Settings.NormalizeAnimationFps(snapshot.AnimationFps);
        cfg.PetX = snapshot.PetLocation.X;
        cfg.PetY = snapshot.PetLocation.Y;
        cfg.MemberPositions = ClonePositions(snapshot.MemberPositions);
        cfg.CompanionPositions = ClonePositions(snapshot.CompanionPositions);
    }

    void RestoreAutoGameModeConfig()
        => ApplyAutoGameModeConfigTo(_cfg, _autoGameModeSnapshot);

    void RestoreAutoGameModePositions()
    {
        Location = _autoGameModeSnapshot.PetLocation;
        ApplyAutoGameModeConfigTo(_cfg, _autoGameModeSnapshot);
    }

    /// <summary>
    /// 自定义游戏模式期间拦截 ConfigStore.Save：写入时把临时字段换回进入前的基础值，
    /// 但主题、房间、热键等与游戏模式无关的修改仍正常落盘。
    /// </summary>
    void SaveGameModeBase(Settings cfg)
    {
        lock (_gameModeSaveGate)
        {
            if (!_autoGameModeCustomApplied)
            {
                ConfigStore.SaveCore(cfg);
                return;
            }
            var live = CaptureAutoGameModeState(cfg, new Point(cfg.PetX, cfg.PetY));
            ApplyAutoGameModeConfigTo(cfg, _autoGameModeSnapshot);
            try { ConfigStore.SaveCore(cfg); }
            finally { ApplyAutoGameModeConfigTo(cfg, live); }
        }
    }

    static Dictionary<string, int[]> ClonePositions(Dictionary<string, int[]> source)
    {
        var result = new Dictionary<string, int[]>();
        if (source == null) return result;
        foreach (var pair in source)
            result[pair.Key] = pair.Value?.ToArray();
        return result;
    }

    void ApplyAutoGameModeState(bool fullProfile)
    {
        ApplySettings();
        if (fullProfile)
        {
            RefreshMembers();
            RefreshCompanions();
            KeyboardModeChanged?.Invoke();
            if (!_cfg.KeyBubbles) _keyBubbles.Clear();
        }
        NotifySettingsChanged();
    }

    /// <summary>主桌宠镜像：只翻转角色皮肤/头饰，底栏保持正方向。</summary>
    public void SetMainMirrored(bool mirrored)
    {
        if (_cfg.MainMirrored == mirrored) return;
        _cfg.MainMirrored = mirrored;
        Save();
        Compose();
        NotifySettingsChanged();
    }

    /// <summary>联机成员镜像：按成员 id 持久化，只影响该角色。</summary>
    public void SetMemberMirrored(long id, bool mirrored)
    {
        _cfg.MemberMirrored ??= new Dictionary<string, bool>();
        string key = CatKey(id);
        if (mirrored) _cfg.MemberMirrored[key] = true;
        else _cfg.MemberMirrored.Remove(key);
        Save();
        if (_cats.TryGetValue(id, out var cat)) cat.SetMirrored(mirrored);
        // 自定义伙伴的合成 id 也是负数，但只存在于 _companionCats。
        else if (_companionCats.TryGetValue(id, out var companionCat)) companionCat.SetMirrored(mirrored);
        NotifySettingsChanged();
    }

    bool IsMemberMirrored(long id)
        => _cfg.MemberMirrored != null && _cfg.MemberMirrored.TryGetValue(CatKey(id), out bool mirrored) && mirrored;

    public void ApplySettings()
    {
        _themePreview = null;
        if (!_cfg.HoverFade)
        {
            _hoverFadeTimer?.Stop();
            _hoverFadeAnimTimer?.Stop();
            _hoverFadeTarget = false;
            _hoverFadeAmount = 0;
        }
        else if (_catHovering && !_hoverFadeTarget)
        {
            // 运行中打开悬停半透明时，也按正常停留时间开始淡出。
            _hoverFadeTimer.Stop();
            _hoverFadeTimer.Start();
        }
        if (_cfg.ClickThrough)
        {
            _wheelShowTimer?.Stop();
            _wheel.Close();
        }
        AnimationTimer.FrameRate = Settings.NormalizeAnimationFps(_cfg.AnimationFps);
        Size = WindowSize();
        ApplyWindowStyles();
        ApplyBarVisibility();
        ApplyThemeToCats();
        UpdateCatScales();
        UpdateCompanionScales();
        foreach (var cat in AllPetWindows())
        {
            cat.SetMirrored(IsMemberMirrored(cat.MemberId));
            cat.SetGridAlignment(_cfg.AlignPetsToGrid);
        }
        ApplyCaptureMode();
        // 全局“隐藏其他角色桌宠”同样作用于本地自定义伙伴；统一在这里对齐窗口集合。
        RefreshCompanions(applyPlacement: false);
        ApplyPlacementRules();
        RefreshMenu();
    }

    /// <summary>把当前底栏隐藏设置同步到所有联机角色/本地伙伴并立即重绘主角。</summary>
    void ApplyBarVisibility()
    {
        foreach (var cat in _cats.Values) cat.SetBarVisible(!_cfg.HideRemoteBars);
        foreach (var companion in _cfg.Companions ?? new List<CompanionPet>())
            if (companion != null && _companionCats.TryGetValue(companion.SyntheticId, out var cat))
                cat.SetBarVisible(CompanionBarVisible(companion));
        Compose();
    }

    /// <summary>
    /// 取色器拖动时的轻量预览：只重绘主桌宠底栏和远程昵称条，不写入配置，
    /// 也不触发布局/菜单/窗口样式刷新。保存时由 ApplySettings 清除预览并应用正式配置。
    /// </summary>
    public void PreviewTheme(Settings preview)
    {
        if (preview == null) return;
        var theme = ResolveThemeBarPalette(preview);
        _themePreview = theme;
        Compose();
        ApplyThemeToCats(theme);
    }

    /// <summary>取消取色时恢复当前配置中的正式底栏配色。</summary>
    public void ClearThemePreview()
    {
        if (!_themePreview.HasValue) return;
        _themePreview = null;
        Compose();
        ApplyThemeToCats();
    }

    /// <summary>打开/关闭/调整 OBS 绿幕窗口。绿幕窗口只复制主角，不影响桌面桌宠。</summary>
    public void ApplyCaptureMode()
    {
        int w = Math.Clamp(_cfg.CaptureWidth, 160, 1600);
        int h = Math.Clamp(_cfg.CaptureHeight, 160, 1600);
        if (!_cfg.CaptureMode)
        {
            var old = _capture;
            _capture = null;
            if (old != null)
            {
                try { old.ClosingFromOwner = true; old.Close(); old.Dispose(); } catch { }
            }
            ApplyMainPetVisibility();
            return;
        }

        bool recreate = _capture != null && !_capture.IsDisposed && _capture.Compatibility != _cfg.CaptureCompatibilityMode;
        if (recreate)
        {
            var old = _capture;
            _capture = null;
            if (old != null)
            {
                try { old.ClosingFromOwner = true; old.Close(); old.Dispose(); } catch { }
            }
        }
        if (_capture == null || _capture.IsDisposed)
        {
            _capture = new CaptureWindow(this, _cfg.CaptureCompatibilityMode);
            _capture.FormClosed += (s, e) => { if (ReferenceEquals(_capture, s)) _capture = null; };
            _capture.SetBackground(_cfg.CaptureColor, _cfg.CaptureTransparent);
            _capture.Show();
        }
        else
        {
            _capture.SetBackground(_cfg.CaptureColor, _cfg.CaptureTransparent);
        }
        _capture.SetCaptureSize(w, h);
        _capture.Invalidate();
        ApplyMainPetVisibility();

        if (!_cfg.CaptureObsNoticeShown)
        {
            _cfg.CaptureObsNoticeShown = true;
            Save();
            try
            {
                MessageBox.Show(_capture,
                    Localization.T("obs.noticeBody",
                        "请务必在 OBS 的窗口采集属性中，把「采集方式」改为「Windows 10（1903 及以上）」。\n" +
                        "默认的自动采集方式可能只显示黑影或黑屏。\n\n" +
                        "透明背景还需要勾选「允许透明」。\n" +
                        "如果仍然异常，可在设置中开启「兼容模式」，改用纯色背景 + 色度键。"),
                    Localization.T("obs.noticeTitle", "OBS 采集提示"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch { }
        }
    }

    /// <summary>OBS 模式下按设置停止在桌面渲染主角；采集窗口仍正常绘制。</summary>
    void ApplyMainPetVisibility()
    {
        bool hiddenForCapture = _cfg.CaptureMode && _cfg.CaptureStopRenderingMain;
        if (hiddenForCapture)
        {
            if (Visible) Hide();
        }
        else if (!_hiddenByHotkey && !Visible)
        {
            Show();
            QueueRestackPetWindows();
        }
    }

    /// <summary>绿幕窗口右键关闭时同步关掉设置开关，避免下次 ApplySettings 又弹出来。</summary>
    public void DisableCaptureMode()
    {
        _cfg.CaptureMode = false;
        Save();
        ApplyCaptureMode();
        NotifySettingsChanged();
    }

    /// <summary>用户拖动绿幕窗口边框调整大小后回写设置，下次打开保持该尺寸。</summary>
    public void SetCaptureSizeFromWindow(int width, int height)
    {
        width = Math.Clamp(width, 160, 1600);
        height = Math.Clamp(height, 160, 1600);
        if (_cfg.CaptureWidth == width && _cfg.CaptureHeight == height) return;
        _cfg.CaptureWidth = width;
        _cfg.CaptureHeight = height;
        Save();
        NotifySettingsChanged();
    }

    /// <summary>用户从任务栏/Alt+F4 关闭绿幕窗口时，把设置开关同步关掉。</summary>
    public void OnCaptureWindowUserClosed()
    {
        if (!_cfg.CaptureMode) return;
        _cfg.CaptureMode = false;
        Save();
        ApplyMainPetVisibility();
        NotifySettingsChanged();
    }

    /// <summary>
    /// 把主角完整 GUI（角色、数字、计数条、宝箱、昵称和按钮）画到 OBS 绿幕窗口。
    /// 键盘/手柄 GUI 由 CaptureWindow 另外调用 KeyboardWindow.DrawTo 拼接。
    /// </summary>
    public void DrawCapture(Graphics g, Rectangle dest)
    {
        if (_frames == null || dest.Width <= 0 || dest.Height <= 0) return;
        if (!_frames.Ready)
        {
            if (!_frames.Loading) return;
            var state = g.Save();
            try
            {
                g.TranslateTransform(dest.X, dest.Y);
                float ls = dest.Width / (float)PetComposer.WindowW;
                PetComposer.DrawLoading(g, ls, (Environment.TickCount64 % 1000) / 1000.0,
                    PetComposer.CatX + PetComposer.CatSize / 2f,
                    PetComposer.CatY + PetComposer.CatSize / 2f);
            }
            finally { g.Restore(state); }
            return;
        }
        // 按采集窗口的最终像素尺寸合成，而不是拿缩小后的桌宠窗口位图放大。
        // 桌宠 Scale 很小时窗口可能只有 136×165；直接放大到 OBS 绿幕尺寸会明显糊。
        int w = Math.Max(1, dest.Width), h = Math.Max(1, dest.Height);
        if (_captureFrame == null || _captureFrame.Width != w || _captureFrame.Height != h)
        {
            _captureFrame?.Dispose();
            _captureFrame = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        }
        var info = BuildInfo(_auth.ServerNowMs);
        info.Loading = _frames.Loading;
        info.LoadingPhase = (Environment.TickCount64 % 1000) / 1000.0;
        PetComposer.ComposeInto(_captureFrame, _frames, _bodyFrame, _headFrame, info, 1.0);
        // 位图已经就是目标尺寸，一比一贴图，避免再次双三次插值把文字和边缘磨糊。
        g.DrawImageUnscaled(_captureFrame, dest.X, dest.Y);
    }

    void ApplyWindowStyles()
    {
        if (!IsHandleCreated) return;
        long ex = GetWindowLongPtr(Handle, -20).ToInt64();
        if (_cfg.ClickThrough) ex |= 0x00000020L;
        else ex &= ~0x00000020L;
        if (_cfg.Topmost) ex |= 0x00000008L;
        else ex &= ~0x00000008L;
        SetWindowLongPtr(Handle, -20, new IntPtr(ex));
        SetWindowPos(Handle, _cfg.Topmost ? HwndTopMost : HwndNoTopMost, 0, 0, 0, 0,
            0x0001 | 0x0002 | 0x0010); // NOSIZE | NOMOVE | NOACTIVATE
        RestackPetWindows(force: true);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyWindowStyles();
    }

    /// <summary>
    /// 兜底层级检测。正常时只做一次很轻量的 z-order/ex-style 检查；
    /// 发现主桌宠/联机桌宠被普通窗口拆开、丢失置顶或主桌宠被联机桌宠压住时才重排。
    /// 主窗口可能被 OBS 停止渲染（Hide），此时联机桌宠仍需要守护，不能在这里一票否决。
    /// </summary>
    void EnsurePetZOrder()
    {
        if (!IsHandleCreated || IsDisposed) return;
        // 已有合并重排在排队时不要再用 watchdog 插入一次全量重排/快照。
        if (_zOrderRestackQueued || _zOrderRestackTimer?.Enabled == true) return;
        RestackPetWindows(force: false);
    }

    /// <summary>主宠、整组或联机角色正在拖动时，所有层级修复都应延后，避免插入 UI 线程造成冻结。</summary>
    bool IsPetDragActive => _dragging || _groupDragging || _draggingCat != null;

    void QueueRestackPetWindows()
    {
        if (IsPetDragActive)
        {
            _zOrderRestackPending = true;
            _zOrderRestackPendingForce = true;
            return;
        }
        if (_zOrderRestackQueued) return;
        _zOrderRestackQueued = true;
        _zOrderRestackTimerForce = true;
        try
        {
            if (IsHandleCreated && !IsDisposed)
            {
                _zOrderRestackTimer.Stop();
                _zOrderRestackTimer.Start();
            }
            else
            {
                _zOrderRestackQueued = false;
                _zOrderRestackTimerForce = false;
            }
        }
        catch
        {
            _zOrderRestackQueued = false;
            _zOrderRestackTimerForce = false;
        }
    }

    /// <summary>拖动结束后把期间积压的层级修复合并成一次，稍微延后执行，先让松手逻辑走完。</summary>
    void FlushPendingZOrderRestack()
    {
        if (!_zOrderRestackPending || IsPetDragActive) return;
        _zOrderRestackPending = false;
        bool force = _zOrderRestackPendingForce;
        _zOrderRestackPendingForce = false;
        if (!IsHandleCreated || IsDisposed) return;
        _zOrderRestackQueued = true;
        _zOrderRestackTimerForce |= force;
        try
        {
            _zOrderRestackTimer.Stop();
            _zOrderRestackTimer.Start();
        }
        catch
        {
            _zOrderRestackQueued = false;
            _zOrderRestackTimerForce = false;
        }
    }

    void RestackPetWindows(bool force)
    {
        if (!IsHandleCreated || IsDisposed || _zOrderRepairing) return;

        var remotes = _cats.Values
            .Concat(_companionCats.Values)
            .Where(c => c != null && !c.IsDisposed && c.Visible && c.IsHandleCreated && IsWindowVisible(c.Handle))
            .OrderBy(c => c.MemberId)
            .ToList();

        // OBS 的 CaptureStopRenderingMain 会把主窗口 Hide()；此时联机桌宠仍可能可见，
        // 不能因为主窗口不可见就跳过整个守护流程。
        bool mainVisible = !IsDisposed && IsHandleCreated && Visible && !_hiddenByHotkey
            && IsWindowVisible(Handle);
        if (remotes.Count == 0 && !mainVisible) return;

        var pets = new List<IntPtr>(remotes.Count + 1);
        if (mainVisible) pets.Add(Handle);
        foreach (var cat in remotes) pets.Add(cat.Handle);

        bool topmost = _cfg.Topmost;
        var topLevel = VisibleTopLevelZOrder();
        if (topLevel.Count == 0) return; // 枚举失败时不要每秒盲目重排。
        var problem = PetZOrderGuard.Check(
            pets,
            topLevel,
            topmost ? HasTopMostStyle : null,
            requireContiguous: topmost,
            isExemptWindow: IsShellOrSystemZOrderWindow,
            hasMainPet: mainVisible,
            isOwnWindow: IsOwnProcessWindow);
        if (!force && problem == PetZOrderProblem.None) return;

        // 拖动中不要做全量重排：2N 次 SetWindowPos + 快照会让鼠标跟手出现明显顿挫。
        // 先记下请求，等松手后由 FlushPendingZOrderRestack 合并执行一次。
        if (IsPetDragActive)
        {
            _zOrderRestackPending = true;
            _zOrderRestackPendingForce |= force;
            return;
        }

        // 只记录现场，不改变重排行为。同一原因 30 秒内最多一条快照。
        if (problem != PetZOrderProblem.None)
            LogZOrderSnapshot("restack", problem.ToString());

        _zOrderRepairing = true;
        try
        {
            uint flags = SwpNoSize | SwpNoMove | SwpNoActivate;
            if (topmost)
            {
                // 两段式恢复置顶：先用 NOTOPMOST 把窗口摘出 topmost band，再 TOPMOST 放回 band 顶部。
                // 只改样式位（ApplyWindowStyles）无法修复“样式位还在、实际 z-order 已掉到普通窗口下方”
                // 的假置顶，必须真正重进一次 band。
                // 顺序：最底层远程 → 最上层远程 → 主桌宠，后一个调用位于 band 更上方。
                void ForceTopMost(IntPtr hwnd)
                {
                    SetWindowPos(hwnd, HwndNoTopMost, 0, 0, 0, 0, flags);
                    SetWindowPos(hwnd, HwndTopMost, 0, 0, 0, 0, flags);
                    // 暂不加 SWP_FRAMECHANGED：它会触发非客户区重算，实测前先避免闪烁。
                }

                for (int i = remotes.Count - 1; i >= 0; i--)
                    ForceTopMost(remotes[i].Handle);
                if (mainVisible) ForceTopMost(Handle);
            }
            else if (mainVisible)
            {
                // 未开启置顶：只恢复“联机桌宠在主桌宠下方”的相对顺序，不引入 TOPMOST。
                for (int i = remotes.Count - 1; i >= 0; i--)
                    SetWindowPos(remotes[i].Handle, Handle, 0, 0, 0, 0, flags);
            }
        }
        finally { _zOrderRepairing = false; }

        long now = Environment.TickCount64;
        if (problem != PetZOrderProblem.None && now - Interlocked.Read(ref _lastZOrderRepairLogMs) >= 30_000)
        {
            Interlocked.Exchange(ref _lastZOrderRepairLogMs, now);
            Log.Write($"窗口层级兜底：{problem}，已重排主桌宠与 {remotes.Count} 个联机桌宠");
        }

        // 重排后补一条快照，便于确认假置顶是否真的被修复（同一原因 30 秒内只记一次）。
        if (problem != PetZOrderProblem.None)
            LogZOrderSnapshot("restack-after", problem.ToString());
    }

    static bool HasTopMostStyle(IntPtr hwnd)
        => (GetWindowLongPtr(hwnd, -20).ToInt64() & 0x00000008L) != 0;

    /// <summary>本进程窗口（键盘 UI、面板、提示层等）不参与假置顶判定。</summary>
    static bool IsOwnProcessWindow(IntPtr hwnd)
        => hwnd != IntPtr.Zero && HwndProcessId(hwnd) == (uint)Environment.ProcessId;

    /// <summary>
    /// 桌面/任务栏/开始菜单/输入法等 shell 或系统窗口允许留在桌宠组上方，
    /// 不参与“普通窗口压住 topmost 桌宠”的判定。窗口类名为主；
    /// 进程名只用于区分同样使用 Windows.UI.Core.CoreWindow 的系统宿主与普通 UWP 应用。
    /// </summary>
    static bool IsShellOrSystemZOrderWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return true;
        if (GetDwmCloaked(hwnd) == 1) return true; // 其它虚拟桌面/不可见 UWP 窗口
        Rectangle rect = GetWindowRectOrEmpty(hwnd);
        if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) return true;

        string cls = WindowClassName(hwnd);
        if (string.IsNullOrEmpty(cls)) return true;
        if (ShellZOrderWindowClasses.Contains(cls)) return true;

        // CoreWindow 也被普通 UWP 应用使用；只有系统宿主进程才豁免，
        // 否则会把 Photos 之类真实的普通窗口放过，漏掉假置顶。
        if (cls.Equals("Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase))
            return ShellHostProcesses.Contains(ProcessName(HwndProcessId(hwnd)));

        return false;
    }

    /// <summary>无需参与假置顶判定的 shell/系统窗口类名。</summary>
    static readonly HashSet<string> ShellZOrderWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow",
        "TaskSwitcherWnd",
        "TaskListThumbnailWnd",
        "MultitaskingViewFrame",
        "ForegroundStaging",
        "XamlExplorerHostIslandWindow",
        "ApplicationManager_DesktopShellWindow",
        "Windows.Internal.Shell.TabProxyWindow",
        "Shell_InputSwitchTopLevelWindow",
        "DV2ControlHost",
        "SysShadow",
        // 输入法候选窗/皮肤条、安全软件托盘等 topmost 小窗口；它们经常夹在
        // 桌宠 z-order 中间，但不会遮住猫体，不能触发全量重排。
        "LibImeWindow",
        "MoqiSogouSkinStatusBar",
        "HRNETFLOWTRAY",
    };

    /// <summary>使用 Windows.UI.Core.CoreWindow 的系统宿主进程（开始菜单/搜索/输入法等）。</summary>
    static readonly HashSet<string> ShellHostProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "StartMenuExperienceHost",
        "SearchHost",
        "ShellExperienceHost",
        "TextInputHost",
        "ShellHost",
        "LockApp",
        "LogonUI",
    };

    static List<IntPtr> VisibleTopLevelZOrder()
    {
        var result = new List<IntPtr>(64);
        for (IntPtr h = GetTopWindow(IntPtr.Zero); h != IntPtr.Zero; h = GetWindow(h, GwHwndNext))
        {
            if (IsWindowVisible(h)) result.Add(h);
        }
        return result;
    }

    /// <summary>主桌宠猫体（不含 HUD）的屏幕矩形，用于 z-order 快照的命中测试。</summary>
    Rectangle LocalCatBodyRect()
    {
        if (!Visible || _hiddenByHotkey) return Rectangle.Empty;
        float s = Width / (float)PetComposer.WindowW;
        return new Rectangle(
            Left + (int)Math.Round(PetComposer.CatX * s),
            Top + (int)Math.Round(PetComposer.CatY * s),
            (int)Math.Round(PetComposer.CatSize * s),
            (int)Math.Round(PetComposer.CatSize * s));
    }

    /// <summary>
    /// 记录 z-order 现场到 diag 日志：版本/管理员/前台窗口、可见顶层窗口顺序、
    /// 每个桌宠的 topmost/band/cloaked/rect、猫体中心 WindowFromPoint 命中，
    /// 以及桌宠组上方最近的 10 个外部可见窗口。只读，不修改任何窗口状态。
    /// </summary>
    public void LogZOrderSnapshot(string reason, string detail = null)
    {
        try
        {
            string key = (reason ?? "") + "|" + (detail ?? "");
            lock (_zOrderDiagGate)
            {
                long now = Environment.TickCount64;
                if (_lastZOrderSnapshotMs.TryGetValue(key, out long last) && now - last < 30_000) return;
                _lastZOrderSnapshotMs[key] = now;
            }

            var order = VisibleTopLevelZOrder();
            var indexByHwnd = new Dictionary<IntPtr, int>(order.Count);
            for (int i = 0; i < order.Count; i++) indexByHwnd[order[i]] = i;

            var petRows = new List<object>();
            var petRects = new List<Rectangle>();
            var petIndexes = new List<int>();
            var petGroupIndexes = new List<int>();

            void AddPet(string tag, IntPtr hwnd, Rectangle bodyRect, bool visible, bool partOfPetGroup = true)
            {
                if (hwnd == IntPtr.Zero) return;
                if (visible && !bodyRect.IsEmpty) petRects.Add(bodyRect);
                if (indexByHwnd.TryGetValue(hwnd, out int pos))
                {
                    petIndexes.Add(pos);
                    if (partOfPetGroup) petGroupIndexes.Add(pos);
                }

                // ClickThrough=true 时 WS_EX_TRANSPARENT 会让 WindowFromPoint 穿透到下层窗口，
                // 命中结果必然不是自己；这种 false 会误导排查，因此直接标记为不可测。
                bool hitTestable = !_cfg.ClickThrough && visible && !bodyRect.IsEmpty;
                IntPtr hit = IntPtr.Zero;
                bool? hitOwn = null;
                int hitPid = 0;
                object point = null;
                if (hitTestable)
                {
                    var center = new Point(bodyRect.Left + bodyRect.Width / 2, bodyRect.Top + bodyRect.Height / 2);
                    hit = WindowFromPoint(new POINT { X = center.X, Y = center.Y });
                    if (hit != IntPtr.Zero)
                    {
                        uint pid = HwndProcessId(hit);
                        hitPid = (int)pid;
                        hitOwn = pid == (uint)Environment.ProcessId;
                    }
                    point = new { x = center.X, y = center.Y };
                }

                petRows.Add(new
                {
                    tag,
                    hwnd = HwndText(hwnd),
                    visible,
                    topmost = HasTopMostStyle(hwnd),
                    windowBand = GetWindowBandValue(hwnd),
                    cloaked = GetDwmCloaked(hwnd),
                    rect = RectDto(GetWindowRectOrEmpty(hwnd)),
                    bodyRect = RectDto(bodyRect),
                    hitTest = !_cfg.ClickThrough ? (hitTestable ? "window-from-point" : "skipped-not-visible")
                                                 : "not-testable-click-through",
                    hitPoint = point,
                    hitHwnd = hit == IntPtr.Zero ? "" : HwndText(hit),
                    hitPid,
                    hitOwn,
                });
            }

            AddPet("Main", IsHandleCreated ? Handle : IntPtr.Zero, LocalCatBodyRect(),
                Visible && !_hiddenByHotkey && IsHandleCreated);
            foreach (var pair in _cats.OrderBy(x => x.Key))
            {
                var cat = pair.Value;
                if (cat == null || cat.IsDisposed || !cat.IsHandleCreated) continue;
                bool visible = cat.Visible && IsWindowVisible(cat.Handle);
                AddPet("Remote(" + pair.Key + ")", cat.Handle,
                    visible ? CatVisibleRect(cat) : Rectangle.Empty, visible);
            }
            foreach (var pair in _companionCats.OrderBy(x => x.Key))
            {
                var cat = pair.Value;
                if (cat == null || cat.IsDisposed || !cat.IsHandleCreated) continue;
                bool visible = cat.Visible && IsWindowVisible(cat.Handle);
                AddPet("Companion(" + pair.Key + ")", cat.Handle,
                    visible ? CatVisibleRect(cat) : Rectangle.Empty, visible);
            }
            var keyboard = KeyboardUI;
            if (keyboard != null && !keyboard.IsDisposed && keyboard.IsHandleCreated)
            {
                bool visible = keyboard.Visible && IsWindowVisible(keyboard.Handle);
                AddPet("Keyboard", keyboard.Handle, visible ? keyboard.Bounds : Rectangle.Empty, visible,
                    partOfPetGroup: false);
            }

            int groupTop = petIndexes.Count > 0 ? petIndexes.Min() : -1;
            int petGroupTop = petGroupIndexes.Count > 0 ? petGroupIndexes.Min() : -1;
            bool petsBelowNormalWindows = false;
            if (_cfg.Topmost && petGroupTop > 0)
            {
                // 与 PetZOrderGuard 相同规则：只看桌宠组上方是否存在可见、非本进程、
                // 非 shell/系统、非 cloaked、非空矩形的普通（非 topmost）窗口。
                for (int i = 0; i < petGroupTop; i++)
                {
                    IntPtr hwnd = order[i];
                    if (IsOwnProcessWindow(hwnd) || IsShellOrSystemZOrderWindow(hwnd)) continue;
                    if (HasTopMostStyle(hwnd)) continue;
                    petsBelowNormalWindows = true;
                    break;
                }
            }
            var above = new List<object>();
            if (groupTop > 0)
            {
                for (int i = groupTop - 1; i >= 0 && above.Count < 10; i--)
                {
                    IntPtr hwnd = order[i];
                    if (HwndProcessId(hwnd) == (uint)Environment.ProcessId) continue;
                    above.Add(DescribeTopLevelWindow(hwnd, i, petRects));
                }
            }

            var topLevel = new List<object>(order.Count);
            for (int i = 0; i < order.Count; i++)
                topLevel.Add(DescribeTopLevelWindow(order[i], i, petRects));

            var payload = new
            {
                schemaVersion = 1,
                time = DateTimeOffset.Now,
                reason = reason ?? "",
                detail = detail ?? "",
                version = Application.ProductVersion,
                informationalVersion = typeof(PetWindow).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "",
                topmostConfigured = _cfg.Topmost,
                clickThrough = _cfg.ClickThrough,
                petsBelowNormalWindows,
                elevated = Elevation.IsElevated,
                foreground = HwndText(GetForegroundWindow()),
                topLevel,
                pets = petRows,
                above,
            };
            Log.Diagnostic("zorder", JsonSerializer.Serialize(payload, ZOrderJsonOptions));
        }
        catch (Exception ex)
        {
            Log.DiagnosticException("zorder", "生成 z-order 快照失败", ex);
        }
    }

    object DescribeTopLevelWindow(IntPtr hwnd, int z, List<Rectangle> petRects)
    {
        Rectangle rect = GetWindowRectOrEmpty(hwnd);
        uint pid = HwndProcessId(hwnd);
        bool overlap = false;
        foreach (var petRect in petRects)
        {
            if (!petRect.IsEmpty && !rect.IsEmpty && rect.IntersectsWith(petRect)) { overlap = true; break; }
        }
        return new
        {
            z,
            hwnd = HwndText(hwnd),
            className = WindowClassName(hwnd),
            pid = (int)pid,
            process = ProcessName(pid),
            title = TruncateText(WindowTitle(hwnd), 100),
            topmost = HasTopMostStyle(hwnd),
            cloaked = GetDwmCloaked(hwnd),
            rect = RectDto(rect),
            own = pid == (uint)Environment.ProcessId,
            overlapsPet = overlap,
        };
    }

    static string HwndText(IntPtr hwnd)
        => hwnd == IntPtr.Zero ? "0x0" : "0x" + hwnd.ToInt64().ToString("X");

    static uint HwndProcessId(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid;
    }

    static readonly ConcurrentDictionary<uint, (string Name, long ExpiresMs)> ProcessNameCache = new();

    static string ProcessName(uint pid)
    {
        if (pid == 0) return "";
        long now = Environment.TickCount64;
        if (ProcessNameCache.TryGetValue(pid, out var cached) && now < cached.ExpiresMs)
            return cached.Name;
        string name = "";
        try
        {
            using var process = Process.GetProcessById((int)pid);
            name = process.ProcessName;
        }
        catch { }
        ProcessNameCache[pid] = (name, now + 60_000);
        return name;
    }

    static string WindowClassName(IntPtr hwnd)
    {
        try
        {
            var sb = new StringBuilder(256);
            GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }
        catch { return ""; }
    }

    static string WindowTitle(IntPtr hwnd)
    {
        try
        {
            var sb = new StringBuilder(256);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }
        catch { return ""; }
    }

    static string TruncateText(string text, int max)
        => string.IsNullOrEmpty(text) || text.Length <= max ? text : text[..max];

    static Rectangle GetWindowRectOrEmpty(IntPtr hwnd)
    {
        try
        {
            return GetWindowRect(hwnd, out RECT rect)
                ? Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom)
                : Rectangle.Empty;
        }
        catch { return Rectangle.Empty; }
    }

    static object RectDto(Rectangle rect)
        => rect.IsEmpty ? null : new { left = rect.Left, top = rect.Top, width = rect.Width, height = rect.Height };

    static uint? GetWindowBandValue(IntPtr hwnd)
    {
        try { return GetWindowBand(hwnd, out uint band) ? band : null; }
        catch { return null; }
    }

    static int? GetDwmCloaked(IntPtr hwnd)
    {
        try
        {
            const int DwmwaCloaked = 14;
            return DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int value, sizeof(int)) == 0 ? value : null;
        }
        catch { return null; }
    }

    static readonly IntPtr HwndTopMost = new(-1);
    static readonly IntPtr HwndNoTopMost = new(-2);
    const uint GwHwndNext = 2;
    // SetWindowPos 标志：不改变尺寸/位置，也不激活窗口；不包含 SWP_FRAMECHANGED，避免重排时闪烁。
    const uint SwpNoSize = 0x0001;
    const uint SwpNoMove = 0x0002;
    const uint SwpNoActivate = 0x0010;

    public bool IsDragging => _dragging;

    public void RefreshHud() => Compose();

    /// <summary>表情栏本地槽位变更后刷新轮盘。</summary>
    public void OnEmoteSlotsChanged()
    {
        _wheel.Close();
        if (_hovering) ShowWheel();
        else Compose();
    }

    void CycleKeyboard()
    {
        _cfg.KeyboardMode = _cfg.KeyboardMode switch
        {
            "hidden" => "letters",
            "letters" => _cfg.RhythmMode ? "rhythm" : "wuwa",
            "wuwa" => "controller",
            "rhythm" => "controller",
            _ => "hidden"
        };
        Save();
        KeyboardModeChanged?.Invoke();
        Compose();
    }

    public event Action KeyboardModeChanged;

    // ---------- 表情轮盘 / 表情发送 ----------

    void ShowWheel()
    {
        if (_hiddenByHotkey || _wheel.Open || IsPetDragActive) return;
        Log.Write("DBG wheel open");
        var wheelImages = new Dictionary<long, Image>();
        var wheelLoading = new HashSet<long>();
        var wheelFailed = new HashSet<long>();
        _wheel.Show(_cfg.EmoteAssetIds, id =>
        {
            if (wheelImages.TryGetValue(id, out var cached)) return cached;
            if (wheelFailed.Contains(id) || !wheelLoading.Add(id)) return null;
            var entry = _catalog.Find("emote", id);
            if (entry == null)
            {
                wheelLoading.Remove(id);
                wheelFailed.Add(id);
                return null;
            }
            string url = _catalog.CdnUrl(entry.Url);
            Image image = null;
            try
            {
                // 轮盘只需要几十像素的缩略图；避免每帧把原图重新高质量缩放。
                image = _store.GetBubbleImage(url, 96, () => Safe(() =>
                {
                    wheelLoading.Remove(id);
                    var loaded = _store.GetBubbleImage(url, 96, allowDownload: false);
                    if (loaded != null) wheelImages[id] = loaded;
                    else wheelFailed.Add(id);
                    Compose();
                }));
                if (image != null)
                {
                    wheelImages[id] = image;
                    wheelLoading.Remove(id);
                }
            }
            catch
            {
                wheelLoading.Remove(id);
                wheelFailed.Add(id);
            }
            return image;
        });
        RequestCompose();
    }

    /// <summary>宝箱奖励在头顶弹一个奖励图气泡（自动领取时也给用户可见反馈）。</summary>
    public void ShowPrize(string assetType, long assetId)
    {
        string url = null;
        var entry = _catalog.Find(assetType, assetId);
        if (entry != null) url = _catalog.CdnUrl(entry.Url);
        if (string.IsNullOrEmpty(url)) return;
        var img = _store.GetBubbleImage(url, onReady: () => Safe(() => { var loaded = _store.GetBubbleImage(url, allowDownload: false); if (loaded != null) PushBubble(loaded); }));
        if (img != null) PushBubble(img);
    }

    public async void SendEmoteById(long id)
    {
        if (id <= 0) return;
        var entry = _catalog.Find("emote", id);
        if (entry != null)
        {
            string url = _catalog.CdnUrl(entry.Url);
            var img = _store.GetBubbleImage(url, onReady: () => Safe(() => { var loaded = _store.GetBubbleImage(url, allowDownload: false); if (loaded != null) PushBubble(loaded); }));
            if (img != null) PushBubble(img);
        }
        try { await _room.SendEmoteAsync(id); } catch (Exception ex) { Log.Write("发送表情失败 " + ex.Message); }
    }

    void PushBubble(Image image)
    {
        _bubbles.Add(image);
        if (_capture != null && !_capture.IsDisposed) Safe(() => CaptureChanged?.Invoke());
        RequestCompose();
    }

    /// <summary>按键边沿生成一个键帽气泡；与表情和动作共享活动动画时钟。</summary>
    void PushKeyBubble(int vk)
    {
        bool captureOnly = _cfg.CaptureMode && _cfg.CaptureStopRenderingMain;
        if (!_cfg.KeyBubbles || _hiddenByHotkey || (!Visible && !captureOnly)) return;
        if (vk == 0x01 || vk == 0x02) return; // 鼠标按键只计入点击，不生成键帽气泡
        if (!_keyBubbles.Add(vk)) return;
        if (_capture != null && !_capture.IsDisposed) CaptureChanged?.Invoke();
        RequestCompose();
    }

    /// <summary>手柄按键边沿生成一个键帽气泡；肩键/扳机用浅金色强调。</summary>
    public void PushGamepadBubble(string label)
    {
        bool captureOnly = _cfg.CaptureMode && _cfg.CaptureStopRenderingMain;
        if (!_cfg.KeyBubbles || _hiddenByHotkey || (!Visible && !captureOnly)) return;
        bool accent = label is "LB" or "RB" or "LT" or "RT";
        if (!_keyBubbles.AddLabel(label, accent)) return;
        if (_capture != null && !_capture.IsDisposed) CaptureChanged?.Invoke();
        RequestCompose();
    }

    // ---------- 全局快捷键 ----------
    //
    // 不用 RegisterHotKey：那会独占按键，其他程序收不到 F5/F7/F8/F9，
    // 而且注册失败还会留下“按了没反应”的半残状态。
    // 改成在 Raw Input 的被动事件流里匹配：按键照常传给前台程序，只额外触发我们的动作。

    // Raw Input 线程会并发读取：重建时整体替换引用，避免字典写入时读线程抛异常。
    volatile Dictionary<(uint Mods, uint Vk), string> _hotkeyMap = new();

    /// <summary>
    /// 键位默认与原版 Helper 一致（F5/F7/F8/F9、Ctrl+Alt+1~6），
    /// 但全部取自配置，设置页改键后调用 RebindHotkeys 立即生效。
    /// </summary>
    void RebuildHotkeyMap()
    {
        var map = new Dictionary<(uint, uint), string>();
        var bound = new List<string>();
        foreach (var pair in Hotkeys.ActiveFromSettings(_cfg.Hotkeys, _cfg.SeparateBarHotkeys))
        {
            if (!Hotkeys.TryParse(pair.Value, out uint mods, out uint vk) || vk == 0) continue;
            map[(mods, vk)] = pair.Key;
            bound.Add($"{pair.Key}={pair.Value}");
        }
        _hotkeyMap = map;
        if (bound.Count > 0) Log.Write("快捷键已启用（被动监听） " + string.Join(" ", bound));
    }

    /// <summary>设置页改键后重建匹配表。</summary>
    public void RebindHotkeys() => RebuildHotkeyMap();

    /// <summary>Raw Input 线程回调：按键照常下发给前台程序，这里只做匹配。</summary>
    void OnHotkeyKeyDown(int vk, bool ctrl, bool alt, bool shift)
    {
        // Ctrl+A 是桌宠多选快捷键，不是普通热键：设置面板输入框里要保留文本全选，
        // 因此前台窗口属于本进程时直接放行。数量条件切回 UI 线程后再检查。
        if (PetSelectionLogic.ShouldSelectAllOnCtrlA(vk, ctrl, alt, shift, 1))
        {
            if (IsOwnProcessForeground()) return;
            Safe(() =>
            {
                if (PetSelectionLogic.ShouldSelectAllOnCtrlA(vk, ctrl, alt, shift, SelectedCount()))
                    SelectAllRemoteCats();
            });
            return;
        }

        uint mods = (ctrl ? Hotkeys.ModControl : 0) | (alt ? Hotkeys.ModAlt : 0) | (shift ? Hotkeys.ModShift : 0);
        var map = _hotkeyMap;
        if (map.TryGetValue((mods, (uint)vk), out string action))
            Safe(() => HandleHotkey(action));
    }

    /// <summary>设置中的“默认屏幕”：启动/重置位置/区服切换时使用；F8 本身从当前实际屏幕开始。</summary>
    public Screen ConfiguredScreen()
    {
        var screens = Screen.AllScreens;
        if (screens.Length == 0) return Screen.PrimaryScreen;
        if (!string.IsNullOrWhiteSpace(_cfg.PetScreen))
        {
            var configured = screens.FirstOrDefault(s =>
                string.Equals(s.DeviceName, _cfg.PetScreen, StringComparison.OrdinalIgnoreCase));
            if (configured != null) return configured;
        }
        return screens.FirstOrDefault(s => s.Primary) ?? screens[0];
    }

    public static string ScreenLabel(Screen screen, int index)
    {
        var bounds = screen.Bounds;
        string primary = screen.Primary ? Localization.T("screen.primary", "（主屏幕）") : "";
        return Localization.F("screen.label", "屏幕 {0} · {1}{2}", index + 1, $"{bounds.Width}×{bounds.Height}", primary);
    }

    /// <summary>
    /// 切换主桌宠所在屏幕。移动策略由设置项 ScreenSwitchMode 决定：
    /// default 只移动默认排序位置的成员，main 只移动主角，all 全组移动（两屏都有成员时互换）。
    /// </summary>
    public void MoveToScreen(string deviceName, bool announce = false)
    {
        var screens = Screen.AllScreens;
        if (screens.Length == 0) return;
        var target = screens.FirstOrDefault(s =>
            string.Equals(s.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            ?? screens.FirstOrDefault(s => s.Primary)
            ?? screens[0];
        var source = Screen.FromControl(this);

        _cfg.PetScreen = target.DeviceName;
        var wa = target.WorkingArea;
        int targetX = wa.Right - Width - 40;
        int targetY = wa.Bottom - Height - 60;
        Location = new Point(targetX, targetY);
        _cfg.PetX = Left;
        _cfg.PetY = Top;

        if (!string.Equals(source.DeviceName, target.DeviceName, StringComparison.OrdinalIgnoreCase))
        {
            string mode = _cfg.ScreenSwitchMode switch
            {
                "main" => "main",
                "all" => "all",
                _ => "default",
            };
            if (mode == "all")
                MoveAllCatsBetweenScreens(source, target);
            else if (mode == "default")
                MoveDefaultCatsToScreen(source, target);
        }

        ApplyPlacementRules();
        Save();
        // 先通知设置面板，再做键盘布局刷新；任何后续布局异常都不应阻断设置同步。
        NotifySettingsChanged();
        GeometryChanged?.Invoke();
        if (announce)
            NotifyHotkey(Localization.F("notify.switchedToScreen", "已切换到 {0}", ScreenLabel(target, Array.IndexOf(screens, target))));
    }

    bool IsCustomCatPosition(long id)
        => _cfg.MemberCustomPositions.TryGetValue(CatKey(id), out bool custom) && custom;

    bool IsCustomPetPosition(long id)
    {
        if (_cats.ContainsKey(id)) return IsCustomCatPosition(id);
        var companion = CompanionBySynthetic(id);
        return companion != null
            && _cfg.CompanionCustomPositions != null
            && _cfg.CompanionCustomPositions.TryGetValue(CompanionKey(companion.Id), out bool custom)
            && custom;
    }

    void MoveDefaultCatsToScreen(Screen source, Screen target)
    {
        foreach (var cat in AllPetWindows())
        {
            if (!cat.Visible || IsCustomPetPosition(cat.MemberId)) continue;
            var catScreen = Screen.FromRectangle(CatVisibleRect(cat));
            if (!string.Equals(catScreen.DeviceName, source.DeviceName, StringComparison.OrdinalIgnoreCase)) continue;
            MoveCatToScreen(cat.MemberId, cat, catScreen.WorkingArea, target.WorkingArea);
        }
    }

    void MoveAllCatsBetweenScreens(Screen source, Screen target)
    {
        foreach (var cat in AllPetWindows())
        {
            if (!cat.Visible) continue;
            var catScreen = Screen.FromRectangle(CatVisibleRect(cat));
            Screen dest;
            if (string.Equals(catScreen.DeviceName, source.DeviceName, StringComparison.OrdinalIgnoreCase))
                dest = target;
            else if (string.Equals(catScreen.DeviceName, target.DeviceName, StringComparison.OrdinalIgnoreCase))
                dest = source;
            else
                continue;
            MoveCatToScreen(cat.MemberId, cat, catScreen.WorkingArea, dest.WorkingArea);
        }
    }

    void MoveCatToScreen(long id, RemoteCatWindow cat, Rectangle from, Rectangle to)
    {
        var visible = CatVisibleRect(cat);
        float s = RemoteScaleFactor;
        int head = (int)Math.Round(RemoteCatWindow.Headroom * s);
        float nx = (visible.Left + visible.Width / 2f - from.Left) / Math.Max(1, from.Width);
        float ny = (visible.Top + visible.Height / 2f - from.Top) / Math.Max(1, from.Height);
        int left = to.Left + (int)Math.Round(nx * to.Width) - visible.Width / 2;
        int top = to.Top + (int)Math.Round(ny * to.Height) - visible.Height / 2;
        left = Math.Clamp(left, to.Left, Math.Max(to.Left, to.Right - visible.Width));
        top = Math.Clamp(top, to.Top, Math.Max(to.Top, to.Bottom - visible.Height));
        cat.Location = new Point(left, top - head);
        SavePetPosition(id, cat, custom: false);
    }

    bool PetsOnMultipleScreens()
    {
        string first = null;
        bool multiple = false;

        void Track(Rectangle rect)
        {
            if (rect.IsEmpty || multiple) return;
            var center = new Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
            string key = Screen.FromPoint(center).DeviceName;
            if (first == null) first = key;
            else if (!string.Equals(first, key, StringComparison.OrdinalIgnoreCase)) multiple = true;
        }

        Track(LocalVisibleRect());
        foreach (var cat in AllPetWindows())
            if (cat.Visible && !_hiddenByHotkey) Track(CatVisibleRect(cat));
        return multiple;
    }

    void HandleHotkey(string action)
    {
        if (action == "reload")
        {
            RefreshRequested?.Invoke();
            NotifyHotkey(Localization.T("notify.uiRefreshed", "已刷新界面"));
        }
        else if (action == "game")
        {
            // 原版“游戏模式”就是鼠标穿透：桌宠只作画面存在，不响应点击。
            _cfg.ClickThrough = !_cfg.ClickThrough;
            OnManualClickThroughChanged();
            Save();
            ApplySettings();
            NotifyHotkey(_cfg.ClickThrough
                ? Localization.T("notify.gameModeOn", "已开启鼠标穿透")
                : Localization.T("notify.gameModeOff", "已关闭鼠标穿透"));
            NotifySettingsChanged();
        }
        else if (action == "monitor")
        {
            var screens = Screen.AllScreens;
            if (screens.Length < 2) return;
            // F8 的语义是“从当前实际所在屏幕切到下一块”，不是从设置里的默认屏幕开始。
            var current = Screen.FromControl(this);
            int idx = Array.FindIndex(screens, s => string.Equals(s.DeviceName, current.DeviceName, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) idx = 0;
            var next = screens[(idx + 1) % screens.Length];
            MoveToScreen(next.DeviceName, true);
        }
        else if (action == "visibility")
        {
            if (!_hiddenByHotkey) LogZOrderSnapshot("f9-hide");
            _hiddenByHotkey = !_hiddenByHotkey;
            Visible = !_hiddenByHotkey && !(_cfg.CaptureMode && _cfg.CaptureStopRenderingMain);
            foreach (var c in AllPetWindows()) c.Visible = !_hiddenByHotkey;
            if (!_hiddenByHotkey)
            {
                Compose();
                ApplyPlacementRules();
                QueueRestackPetWindows();
            }
            PetVisibilityChanged?.Invoke(!_hiddenByHotkey);
        }
        else if (action == "barToggle")
        {
            // 两边都已经隐藏时一起恢复，否则一起隐藏。
            bool show = _cfg.HideMainBar && _cfg.HideRemoteBars;
            SetBarsHidden(!show, !show);
            NotifyHotkey(show
                ? Localization.T("notify.barsShown", "已显示主角与远程角色底栏")
                : Localization.T("notify.barsHidden", "已隐藏主角与远程角色底栏"));
        }
        else if (action == "barToggleMain")
        {
            SetBarsHidden(!_cfg.HideMainBar, _cfg.HideRemoteBars);
            NotifyHotkey(_cfg.HideMainBar
                ? Localization.T("notify.mainBarShown", "已显示主角底栏")
                : Localization.T("notify.mainBarHidden", "已隐藏主角底栏"));
        }
        else if (action == "barToggleRemote")
        {
            SetBarsHidden(_cfg.HideMainBar, !_cfg.HideRemoteBars);
            NotifyHotkey(_cfg.HideRemoteBars
                ? Localization.T("notify.remoteBarShown", "已显示远程角色底栏")
                : Localization.T("notify.remoteBarHidden", "已隐藏远程角色底栏"));
        }
        else if (action.StartsWith("emote", StringComparison.Ordinal) && int.TryParse(action[5..], out int slot))
        {
            var emotes = _cfg.EmoteAssetIds ?? Array.Empty<long>();
            if (slot >= 1 && slot <= emotes.Length) SendEmoteById(emotes[slot - 1]);
        }
        else if (action == "capture")
        {
            _cfg.CaptureMode = !_cfg.CaptureMode;
            Save();
            ApplyCaptureMode();
            NotifyHotkey(_cfg.CaptureMode
                ? Localization.T("notify.captureOn", "已开启 OBS 采集窗口")
                : Localization.T("notify.captureOff", "已关闭 OBS 采集窗口"));
            NotifySettingsChanged();
        }
        else if (action == "mainPanel")
        {
            OpenMainPanelRequested?.Invoke();
        }
        else if (action == "catLayout")
        {
            ToggleCatLayout(true);
        }
        else if (action == "hideOtherPets")
        {
            _cfg.HideOtherPets = !_cfg.HideOtherPets;
            Save();
            RefreshMembers();
            RefreshCompanions();
            NotifyHotkey(_cfg.HideOtherPets
                ? Localization.T("notify.otherPetsHidden", "已隐藏其他成员桌宠")
                : Localization.T("notify.otherPetsShown", "已显示其他成员桌宠"));
            NotifySettingsChanged();
        }
        else if (action == "keyBubbles")
        {
            _cfg.KeyBubbles = !_cfg.KeyBubbles;
            if (!_cfg.KeyBubbles) _keyBubbles.Clear();
            Save();
            Compose();
            NotifyHotkey(_cfg.KeyBubbles
                ? Localization.T("notify.keyBubblesOn", "已开启按键气泡")
                : Localization.T("notify.keyBubblesOff", "已关闭按键气泡"));
            NotifySettingsChanged();
        }
        else if (action == "rhythmMode")
        {
            _cfg.RhythmMode = !_cfg.RhythmMode;
            if (_cfg.RhythmMode) _cfg.KeyboardMode = "rhythm";
            else if (_cfg.KeyboardMode == "rhythm") _cfg.KeyboardMode = "wuwa";
            Save();
            KeyboardModeChanged?.Invoke();
            Compose();
            NotifyHotkey(_cfg.RhythmMode
                ? Localization.T("notify.rhythmOn", "已开启音游模式")
                : Localization.T("notify.rhythmOff", "已关闭音游模式"));
            NotifySettingsChanged();
        }
        else if (action == "autoEmote")
        {
            _cfg.AutoEmoteEnabled = !_cfg.AutoEmoteEnabled;
            Save();
            AutoSettingsChanged?.Invoke();
            NotifyHotkey(_cfg.AutoEmoteEnabled
                ? Localization.T("notify.autoEmoteOn", "已开启自动表情")
                : Localization.T("notify.autoEmoteOff", "已关闭自动表情"));
            NotifySettingsChanged();
        }
        else if (action == "autoClick")
        {
            _cfg.AutoClickEnabled = !_cfg.AutoClickEnabled;
            Save();
            AutoSettingsChanged?.Invoke();
            NotifyHotkey(_cfg.AutoClickEnabled
                ? Localization.T("notify.autoClickOn", "已开启自动敲击")
                : Localization.T("notify.autoClickOff", "已关闭自动敲击"));
            NotifySettingsChanged();
        }
    }

    /// <summary>快捷键切换底栏可见性：写配置、立即重绘/更新远程角色，并同步设置页。</summary>
    void SetBarsHidden(bool hideMain, bool hideRemote)
    {
        _cfg.HideMainBar = hideMain;
        _cfg.HideRemoteBars = hideRemote;
        Save();
        ApplyBarVisibility();
        NotifySettingsChanged();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public Point pt; }
    [DllImport("user32.dll")] static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);
    const uint WM_MOUSEMOVE = 0x0200;
    const uint PM_REMOVE = 0x0001;

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    /// <summary>
    /// 只有“本进程窗口在前台，且光标也停在这个前台窗口上”时才把 Ctrl+A 让给输入框。
    /// 点击桌宠不会激活窗口，前台可能仍是设置面板；此时光标在桌宠/桌面上，
    /// 不能误判成正在输入而吞掉桌宠多选快捷键。
    /// </summary>
    static bool IsOwnProcessForeground()
    {
        try
        {
            IntPtr h = GetForegroundWindow();
            if (h == IntPtr.Zero) return false;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid != (uint)Environment.ProcessId) return false;
            if (!GetCursorPos(out var cursor)) return true;
            IntPtr under = WindowFromPoint(cursor);
            if (under == IntPtr.Zero) return false;
            IntPtr root = GetAncestor(under, 2 /* GA_ROOT */);
            if (root == IntPtr.Zero) root = under;
            return root == h;
        }
        catch { return false; }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetTopWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetWindowBand(IntPtr hWnd, out uint pdwBand);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    // ---------- 菜单 / 托盘 ----------

    void BuildMenu()
    {
        _menu?.Dispose();
        _menu = new ContextMenuStrip();
        _menu.Items.Add(Localization.T("tray.openPanel", "打开面板"), null, (s, e) => OpenPanelRequested?.Invoke());
        _menu.Items.Add(Localization.T("tray.emoteWheel", "表情轮盘"), null, (s, e) => ShowWheel());
        _topmostItem = new ToolStripMenuItem(Localization.T("tray.topmost", "窗口置顶")) { Checked = _cfg.Topmost };
        _topmostItem.Click += (s, e) => { _cfg.Topmost = _topmostItem.Checked = !_cfg.Topmost; Save(); ApplySettings(); NotifySettingsChanged(); };
        _menu.Items.Add(_topmostItem);
        _mirrorItem = new ToolStripMenuItem(Localization.T("tray.mirror", "镜像角色")) { Checked = _cfg.MainMirrored };
        _mirrorItem.Click += (s, e) =>
        {
            SetMainMirrored(!_cfg.MainMirrored);
            _mirrorItem.Checked = _cfg.MainMirrored;
        };
        _menu.Items.Add(_mirrorItem);
        _hoverItem = new ToolStripMenuItem(Localization.T("tray.hoverFade", "鼠标悬停时半透明")) { Checked = _cfg.HoverFade };
        _hoverItem.Click += (s, e) => { _cfg.HoverFade = _hoverItem.Checked = !_hoverItem.Checked; Save(); ApplySettings(); NotifySettingsChanged(); };
        _menu.Items.Add(_hoverItem);
        // 与联机页“隐藏其他成员桌宠”共用 HideOtherPets：托盘/右键菜单和页面双向同步。
        // 规则：开启后真实远程成员和本地自定义伙伴都隐藏；关闭后恢复各自原有的显示状态。
        _hideOtherPetsItem = new ToolStripMenuItem(Localization.T("tray.hideOtherPets", "隐藏其他角色桌宠")) { Checked = _cfg.HideOtherPets };
        _hideOtherPetsItem.Click += (s, e) =>
        {
            _cfg.HideOtherPets = _hideOtherPetsItem.Checked = !_cfg.HideOtherPets;
            Save();
            RefreshMembers();
            RefreshCompanions();
            NotifySettingsChanged();
        };
        _menu.Items.Add(_hideOtherPetsItem);
        _passthroughItem = new ToolStripMenuItem(Localization.T("tray.clickThrough", "鼠标穿透"));
        _passthroughItem.Click += (s, e) =>
        {
            _cfg.ClickThrough = true;
            OnManualClickThroughChanged();
            Save();
            ApplySettings();
            NotifySettingsChanged();
        };
        _menu.Items.Add(_passthroughItem);
        _passthroughOffItem = new ToolStripMenuItem(Localization.T("tray.cancelClickThrough", "取消鼠标穿透"), null, (s, e) =>
        {
            _cfg.ClickThrough = false;
            OnManualClickThroughChanged();
            Save();
            ApplySettings();
            NotifySettingsChanged();
        });
        _menu.Items.Add(_passthroughOffItem);

        _scaleMenuItem = new ToolStripMenuItem(Localization.T("tray.petScale", "桌宠大小"));
        foreach (var v in new[] { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 })
        {
            var item = new ToolStripMenuItem((int)(v * 100) + "%") { Tag = v };
            double value = v;
            item.Click += (s, e) =>
            {
                _cfg.Scale = value;
                Save();
                ApplySettings();
                // 面板里的滑条要跟着右键菜单的预设档位更新。
                NotifySettingsChanged();
            };
            _scaleMenuItem.DropDownItems.Add(item);
        }
        _menu.Items.Add(_scaleMenuItem);

        _opacityMenuItem = new ToolStripMenuItem(Localization.T("tray.petOpacity", "桌宠透明度"));
        foreach (var v in new[] { 1.0, 0.9, 0.75, 0.5 })
        {
            var item = new ToolStripMenuItem((int)(v * 100) + "%") { Tag = v };
            double value = v;
            item.Click += (s, e) =>
            {
                _cfg.Opacity = value;
                Save();
                ApplySettings();
                NotifySettingsChanged();
            };
            _opacityMenuItem.DropDownItems.Add(item);
        }
        _menu.Items.Add(_opacityMenuItem);

        var layout = new ToolStripMenuItem(Localization.T("tray.layout", "排列方向"));
        _layoutHorizontalItem = new ToolStripMenuItem(Localization.T("tray.layoutHorizontal", "横向排列"), null, (s, e) => SetCatLayout("horizontal"));
        _layoutVerticalItem = new ToolStripMenuItem(Localization.T("tray.layoutVertical", "纵向排列"), null, (s, e) => SetCatLayout("vertical"));
        layout.DropDownItems.Add(_layoutHorizontalItem);
        layout.DropDownItems.Add(_layoutVerticalItem);
        _menu.Items.Add(layout);

        _alignGridItem = new ToolStripMenuItem(Localization.T("tray.alignGrid", "将桌宠与网格对齐"))
        {
            Checked = _cfg.AlignPetsToGrid,
        };
        _alignGridItem.Click += (s, e) => ToggleGridAlignment();
        _menu.Items.Add(_alignGridItem);

        _keyboardMenuItem = new ToolStripMenuItem(Localization.T("tray.keyboard", "桌面键盘"));
        foreach (var (key, label) in new[]
                 {
                     ("hidden", Localization.T("tray.keyboardHidden", "不显示")),
                     ("letters", Localization.T("tray.keyboardLetters", "26 键")),
                     ("wuwa", Localization.T("tray.keyboardWuwa", "鸣潮专属")),
                     ("rhythm", Localization.T("tray.keyboardRhythm", "音游模式")),
                     ("controller", Localization.T("tray.keyboardController", "手柄")),
                 })
        {
            string k = key;
            // 用稳定的模式 id 作为 Tag 做勾选判断，不再依赖可翻译的菜单文字。
            var item = new ToolStripMenuItem(label) { Tag = key, Checked = _cfg.KeyboardMode == key };
            item.Click += (s, e) =>
            {
                _cfg.KeyboardMode = k;
                // 鸣潮和音游在右键菜单里始终并列显示；选择哪个就直接切换对应模式，
                // 不能只改 KeyboardMode 而让 RhythmMode 把它又强制改回去。
                if (k == "wuwa") _cfg.RhythmMode = false;
                else if (k == "rhythm") _cfg.RhythmMode = true;
                Save();
                KeyboardModeChanged?.Invoke();
                Compose();
                NotifySettingsChanged();
                RefreshMenu();
            };
            _keyboardMenuItem.DropDownItems.Add(item);
        }
        _menu.Items.Add(_keyboardMenuItem);

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Localization.T("tray.resetPosition", "重置位置"), null, (s, e) => { Location = DefaultLocation(); _cfg.PetX = Left; _cfg.PetY = Top; Save(); GeometryChanged?.Invoke(); });
        _menu.Items.Add(Localization.T("tray.restart", "重启桌宠"), null, (s, e) => RestartPetApp());
        _menu.Items.Add(Localization.T("tray.exit", "退出"), null, (s, e) => Close());
        _menu.Opening += (s, e) => RefreshMenu();
        if (_tray != null) _tray.ContextMenuStrip = _menu;
    }

    void RefreshMenu()
    {
        if (_menu == null) return;
        _topmostItem.Checked = _cfg.Topmost;
        _mirrorItem.Checked = _cfg.MainMirrored;
        _hoverItem.Checked = _cfg.HoverFade;
        if (_hideOtherPetsItem != null)
        {
            _hideOtherPetsItem.Checked = _cfg.HideOtherPets;
            _hideOtherPetsItem.Text = Localization.T("tray.hideOtherPets", "隐藏其他角色桌宠");
        }
        _passthroughOffItem.Visible = _cfg.ClickThrough;
        _passthroughItem.Enabled = !_cfg.ClickThrough;
        if (_scaleMenuItem != null)
        {
            int scalePct = (int)Math.Round(Math.Clamp(_cfg.Scale, 0.25, 2.0) * 100);
            _scaleMenuItem.Text = Localization.F("tray.petScalePct", "桌宠大小（{0}%）", scalePct);
            foreach (ToolStripItem sub in _scaleMenuItem.DropDownItems)
            {
                if (sub is ToolStripMenuItem scaleItem && scaleItem.Tag is double preset)
                    scaleItem.Checked = Math.Abs(_cfg.Scale - preset) < 0.005;
            }
        }
        if (_opacityMenuItem != null)
        {
            int opacityPct = (int)Math.Round(Math.Clamp(_cfg.Opacity, 0.2, 1.0) * 100);
            _opacityMenuItem.Text = Localization.F("tray.petOpacityPct", "桌宠透明度（{0}%）", opacityPct);
            foreach (ToolStripItem sub in _opacityMenuItem.DropDownItems)
            {
                if (sub is ToolStripMenuItem opacityItem && opacityItem.Tag is double preset)
                    opacityItem.Checked = Math.Abs(_cfg.Opacity - preset) < 0.005;
            }
        }
        bool verticalLayout = string.Equals(_cfg.CatLayout, "vertical", StringComparison.OrdinalIgnoreCase);
        if (_layoutHorizontalItem != null) _layoutHorizontalItem.Checked = !verticalLayout;
        if (_layoutVerticalItem != null) _layoutVerticalItem.Checked = verticalLayout;
        if (_alignGridItem != null) _alignGridItem.Checked = _cfg.AlignPetsToGrid;
        if (_keyboardMenuItem != null)
        {
            foreach (ToolStripItem sub in _keyboardMenuItem.DropDownItems)
            {
                var subItem = (ToolStripMenuItem)sub;
                // 鸣潮专属和音游模式常驻，不再根据 RhythmMode 隐藏其中一个。
                subItem.Visible = true;
                // 用 Tag 中的稳定模式 id 勾选，语言切换后不需要按中文文案反查。
                subItem.Checked = string.Equals(subItem.Tag as string, _cfg.KeyboardMode, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>界面语言切换后重建托盘/右键菜单，并同步已经打开的键盘、采集窗口和联机桌宠。</summary>
    public void ApplyLocale()
    {
        if (!IsHandleCreated || IsDisposed) return;
        BuildMenu();
        RefreshMenu();
        try { KeyboardUI?.RefreshLocale(); } catch { }
        try { _capture?.ApplyLocale(); } catch { }
        foreach (var cat in AllPetWindows())
        {
            try { cat.ApplyLocale(); } catch { }
        }
        try { _frameCache.Dispose(); } catch { }
        try { _keyBubbles.ClearCache(); } catch { }
        Compose();
    }

    void RestartPetApp()
    {
        bool started = false;
        try
        {
            string exe = Application.ExecutablePath;
            // 先释放单实例锁，否则新进程会被旧实例判断为第二个实例而直接退出。
            SingleInstance.ReleaseForRelaunch();
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory,
            });
            started = true;
        }
        catch (Exception ex)
        {
            Log.Write("重启桌宠失败 " + ex);
            Notify(Localization.T("notify.restartFailed", "重启桌宠失败，请查看日志"));
            // 已经释放过单实例锁；启动失败时尽量重新拿回来，避免当前实例失去单实例保护。
            try
            {
                if (SingleInstance.Acquire())
                    SingleInstance.SetActivationHandler(() => BeginInvoke(new Action(() => OpenPanelRequested?.Invoke())));
            }
            catch { }
        }
        if (started) Close();
    }

    void Save() => ConfigStore.Save(_cfg);

    /// <summary>快捷键动作的托盘通知可关闭；不传按键本身的输入，只报告动作结果。</summary>
    void NotifyHotkey(string text)
    {
        if (_cfg.NotifyHotkeys) Notify(text);
    }

    void NotifySettingsChanged() => SettingsChanged?.Invoke();

    void BuildTray()
    {
        _icon = AppIcon.Tray;
        _tray = new NotifyIcon
        {
            Icon = _icon,
            Text = "Wuwatappo",
            Visible = true,
            ContextMenuStrip = _menu
        };
        _tray.DoubleClick += (s, e) => OpenPanelRequested?.Invoke();
        _tray.BalloonTipClicked += (s, e) => OpenPanelRequested?.Invoke();
    }

    /// <summary>托盘气泡。后台线程（如自动领宝箱）也会调用，必须切回 UI 线程。</summary>
    public void Notify(string text)
    {
        if (!IsHandleCreated || IsDisposed) return;
        if (InvokeRequired)
        {
            Safe(() => Notify(text));
            return;
        }
        try { _tray.ShowBalloonTip(3000, "Wuwatappo", text, ToolTipIcon.Info); } catch { }
    }

    // ---------- 成员桌宠 ----------

    void Safe(Action a)
    {
        try { if (IsHandleCreated) BeginInvoke(a); } catch { }
    }

    void UpdateCat(long id)
    {
        if (_room.Members.TryGetValue(id, out var m))
        {
            // 单个成员的显示开关与“隐藏其他成员”总开关互相独立：任一为隐藏就移除。
            if (!m.Online || !m.Visible || _cfg.HideOtherPets) { RemoveCat(id); return; }
            // 外观快照还没到（RoleKey 为空）时先不建；已有桌宠不要因为动作包清空外观而删掉
            if (string.IsNullOrEmpty(m.RoleKey) && !_cats.ContainsKey(id)) return;
            if (_cats.TryGetValue(id, out var existing))
            {
                existing.SetScale(RemoteScaleFactor);
                existing.SetMirrored(IsMemberMirrored(id));
                bool topmostChanged = existing.SetWindowOptions(EffectiveRemoteOpacity, _cfg.HoverFade, _cfg.ClickThrough, _cfg.Topmost, _cfg.HoverFadeOpacity);
                existing.SetRemoteStyle(EffectiveRemoteScale, EffectiveRemoteOpacity, _cfg.RemoteNameScale);
                existing.SetBarVisible(!_cfg.HideRemoteBars);
                existing.SetGridAlignment(_cfg.AlignPetsToGrid);
                var theme = ThemeBarColors();
                existing.SetTheme(theme.bar, theme.text);
                existing.RefreshMember();
                // 普通成员更新（动作、昵称、房间快照）不改变 z-order，不再触发 2N 次重排。
                if (topmostChanged) QueueRestackPetWindows();
            }
            else
            {
                var cat = new RemoteCatWindow(m, _loader, _catalog, _store);
                cat.SetScale(RemoteScaleFactor);
                cat.SetMirrored(IsMemberMirrored(id));
                cat.SetWindowOptions(EffectiveRemoteOpacity, _cfg.HoverFade, _cfg.ClickThrough, _cfg.Topmost, _cfg.HoverFadeOpacity);
                cat.SetRemoteStyle(EffectiveRemoteScale, EffectiveRemoteOpacity, _cfg.RemoteNameScale);
                cat.SetBarVisible(!_cfg.HideRemoteBars);
                var theme = ThemeBarColors();
                cat.SetTheme(theme.bar, theme.text);
                cat.RemoteScaleRequested += ApplyRemoteScale;
                cat.RemoteOpacityRequested += ApplyRemoteOpacity;
                cat.HideRequested += OnCatHideRequested;
                cat.MirrorRequested += (id, mirrored) => SetMemberMirrored(id, mirrored);
                cat.GridAlignmentRequested += ToggleGridAlignment;
                cat.SetGridAlignment(_cfg.AlignPetsToGrid);
                cat.Location = PlaceNewCat(id);
                cat.DragStarted += OnCatDragStarted;
                cat.DragMoved += OnCatDragMoved;
                cat.DragEnded += OnCatDragEnded;
                cat.Moved += p => OnPetMoved(id, p);
                _cats[id] = cat;
                if (_cfg.AlignPetsToGrid)
                {
                    SnapPetToGrid(cat);
                    SavePetPosition(id, cat, custom: false);
                }
                // 主桌宠被 F9 隐藏时，新上线/新创建的远程成员也不能直接冒出来；
                // 取消隐藏时由 ApplyMainPetVisibility 的统一循环恢复显示。
                if (!_hiddenByHotkey)
                {
                    cat.Show();
                    // 新远程窗口默认是 topmost，会先插到主宠上方；先把它放到主宠后面，
                    // 避免 2 秒 watchdog 在这个窗口期误判 MainNotAboveRemotes 并拍全量快照。
                    if (Visible && IsWindowVisible(Handle))
                        SetWindowPos(cat.Handle, Handle, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
                    QueueRestackPetWindows();
                }
            }
        }
    }

    string CatKey(long id) => id.ToString();
    static string CompanionKey(string companionId) => "companion:" + (companionId ?? "");

    /// <summary>真实联机桌宠 + 本地自定义伙伴；任何会“应用到所有角色”的窗口遍历都走这里。</summary>
    IEnumerable<RemoteCatWindow> AllPetWindows()
    {
        foreach (var cat in _cats.Values) yield return cat;
        foreach (var cat in _companionCats.Values) yield return cat;
    }

    CompanionPet CompanionBySynthetic(long syntheticId)
    {
        foreach (var companion in _cfg.Companions ?? new List<CompanionPet>())
        {
            if (companion != null && companion.SyntheticId == syntheticId) return companion;
        }
        return null;
    }

    /// <summary>
    /// 本地伙伴底栏规则：伙伴自己的 ShowBar 控制是否显示；
    /// 全局“隐藏联机角色底栏”(HideRemoteBars) 优先级更高，开启时全部隐藏。
    /// </summary>
    bool CompanionBarVisible(CompanionPet companion)
        => companion != null && companion.ShowBar && !_cfg.HideRemoteBars;

    /// <summary>
    /// 让桌面伙伴与配置列表对齐：新增、修改、删除和全局隐藏都在这里收口。
    /// 自定义伙伴只创建 RemoteCatWindow，不写入 RoomManager.Members。
    /// </summary>
    public void RefreshCompanions() => RefreshCompanions(applyPlacement: true);

    public void RefreshCompanions(bool applyPlacement)
    {
        if (IsDisposed) return;
        _cfg.Companions ??= new List<CompanionPet>();
        _cfg.CompanionPositions ??= new Dictionary<string, int[]>();
        var desired = _cfg.Companions;
        bool globallyHidden = _cfg.HideOtherPets;
        var wanted = new HashSet<long>();
        if (!globallyHidden)
            foreach (var companion in desired)
                if (companion != null && companion.SyntheticId < 0 && !companion.Hidden)
                    wanted.Add(companion.SyntheticId);

        foreach (long id in _companionCats.Keys.ToList())
            if (!wanted.Contains(id)) RemoveCompanionCat(id);

        // 配置中已删除的伙伴不再保留位置记录；避免 state.json 无限积累。
        var validKeys = new HashSet<string>(desired.Where(c => c != null).Select(c => CompanionKey(c.Id)));
        var staleKeys = _cfg.CompanionPositions.Keys
            .Where(k => k.StartsWith("companion:", StringComparison.Ordinal) && !validKeys.Contains(k))
            .ToList();
        var staleCustomKeys = (_cfg.CompanionCustomPositions ?? new Dictionary<string, bool>()).Keys
            .Where(k => k.StartsWith("companion:", StringComparison.Ordinal) && !validKeys.Contains(k))
            .ToList();
        if (staleKeys.Count > 0 || staleCustomKeys.Count > 0)
        {
            foreach (string key in staleKeys) _cfg.CompanionPositions.Remove(key);
            foreach (string key in staleCustomKeys) _cfg.CompanionCustomPositions.Remove(key);
            Save();
        }

        if (globallyHidden) return;
        foreach (var companion in desired)
        {
            if (companion == null || companion.SyntheticId >= 0 || companion.Hidden) continue;
            UpdateCompanionCat(companion);
        }
        if (applyPlacement) ApplyPlacementRules();
        QueueRestackPetWindows();
    }

    void UpdateCompanionCat(CompanionPet companion)
    {
        long id = companion.SyntheticId;
        if (_companionCats.TryGetValue(id, out var existing))
        {
            existing.UpdateLocalCompanion(companion);
            existing.SetScale(RemoteScaleFactor);
            existing.SetMirrored(IsMemberMirrored(id));
            bool topmostChanged = existing.SetWindowOptions(EffectiveRemoteOpacity, _cfg.HoverFade, _cfg.ClickThrough, _cfg.Topmost, _cfg.HoverFadeOpacity);
            existing.SetRemoteStyle(EffectiveRemoteScale, EffectiveRemoteOpacity, _cfg.RemoteNameScale);
            existing.SetBarVisible(CompanionBarVisible(companion));
            existing.SetGridAlignment(_cfg.AlignPetsToGrid);
            var theme = ThemeBarColors();
            existing.SetTheme(theme.bar, theme.text);
            if (topmostChanged) QueueRestackPetWindows();
        }
        else
        {
            var member = new RoomMember
            {
                Id = id,
                Nickname = companion.Name ?? "",
                RoleKey = companion.CharacterAssetId.ToString(),
                PartKey = companion.AccessoryAssetId.ToString(),
                Online = true,
                Visible = true,
            };
            var cat = new RemoteCatWindow(member, _loader, _catalog, _store, localCompanion: true);
            cat.SetScale(RemoteScaleFactor);
            cat.SetMirrored(IsMemberMirrored(id));
            cat.SetWindowOptions(EffectiveRemoteOpacity, _cfg.HoverFade, _cfg.ClickThrough, _cfg.Topmost, _cfg.HoverFadeOpacity);
            cat.SetRemoteStyle(EffectiveRemoteScale, EffectiveRemoteOpacity, _cfg.RemoteNameScale);
            cat.SetBarVisible(CompanionBarVisible(companion));
            var theme = ThemeBarColors();
            cat.SetTheme(theme.bar, theme.text);
            cat.RemoteScaleRequested += ApplyRemoteScale;
            cat.RemoteOpacityRequested += ApplyRemoteOpacity;
            cat.MirrorRequested += (memberId, mirrored) => SetMemberMirrored(memberId, mirrored);
            cat.LocalHideRequested += OnCompanionHideRequested;
            cat.GridAlignmentRequested += ToggleGridAlignment;
            cat.SetGridAlignment(_cfg.AlignPetsToGrid);
            cat.Location = PlaceNewCompanion(companion);
            cat.DragStarted += OnCatDragStarted;
            cat.DragMoved += OnCatDragMoved;
            cat.DragEnded += OnCatDragEnded;
            cat.Moved += p => OnPetMoved(id, p);
            _companionCats[id] = cat;
            if (_cfg.AlignPetsToGrid)
            {
                SnapPetToGrid(cat);
                SavePetPosition(id, cat, custom: false);
            }
            if (!_hiddenByHotkey)
            {
                cat.Show();
                // 与真实远程成员一致：先放到主桌宠后面，再由层级守护统一排序。
                if (Visible && IsWindowVisible(Handle))
                    SetWindowPos(cat.Handle, Handle, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
            }
        }

        var config = (companion.ClickRatePerSecond, companion.RandomClick);
        if (!_companionClickConfig.TryGetValue(id, out var previous) || previous != config)
        {
            _companionClickConfig[id] = config;
            ResetCompanionClickSchedule(id, companion.ClickRatePerSecond);
        }
    }

    /// <summary>移除桌宠时清理多选/整组拖动状态，避免 owner 消失后 _groupDragging 永久为 true。</summary>
    void ClearPetDragState(long id, RemoteCatWindow cat)
    {
        if (ReferenceEquals(_draggingCat, cat)) _draggingCat = null;
        _selectedCatIds.Remove(id);
        _groupDragOrigins.Remove(id);
        if (_groupDragPendingOwner == id) CancelPendingGroupDrag();
        if (_groupDragging && _groupDragOwner == id)
        {
            _groupDragging = false;
            _groupDragOrigins.Clear();
        }
        if (_multiSelectClickId == id)
        {
            _multiSelectClickPending = false;
            _multiSelectClickId = SelfSelectionId;
            _multiSelectClickWasSelected = false;
        }
    }

    void RemoveCompanionCat(long syntheticId)
    {
        if (!_companionCats.TryGetValue(syntheticId, out var cat)) return;
        ClearPetDragState(syntheticId, cat);
        _companionCats.Remove(syntheticId);
        _companionClickSide.Remove(syntheticId);
        _companionNextClickMs.Remove(syntheticId);
        _companionClickConfig.Remove(syntheticId);
        cat.Close();
        cat.Dispose();
        FlushPendingZOrderRestack();
    }

    Point PlaceNewCompanion(CompanionPet companion)
    {
        bool wasCustom = IsCustomPetPosition(companion.SyntheticId);
        var stored = StoredPoint(CompanionKey(companion.Id));
        // 与联机成员一致：没手动拖过的伙伴如果保存位置在别的屏幕，按当前“显示屏幕”重新落位。
        if (stored.HasValue && !IsCustomPetPosition(companion.SyntheticId))
        {
            var screen = Screen.FromPoint(new Point(
                stored.Value.X + RemoteCatWindow.W / 2,
                stored.Value.Y + RemoteCatWindow.Headroom + RemoteCatWindow.CatH / 2));
            if (!string.Equals(screen.DeviceName, ConfiguredScreen().DeviceName, StringComparison.OrdinalIgnoreCase))
                stored = null;
        }
        if (stored.HasValue && !HasSignificantOverlap(CatVisibleRectAt(stored.Value), VisibleCatRects()))
            return stored.Value;
        var point = DefaultCatPoint();
        string key = CompanionKey(companion.Id);
        _cfg.CompanionPositions[key] = new[] { point.X, point.Y };
        // 手动摆过的伙伴即使因碰撞重新落位，也保留 custom 标记，避免 F8 default 再次搬走。
        if (!wasCustom) _cfg.CompanionCustomPositions?.Remove(key);
        Save();
        return point;
    }

    static long CompanionClickBaseMs(double rate)
        => Math.Max(50L, (long)Math.Round(1000.0 / Math.Max(0.001, rate)));

    void ResetCompanionClickSchedule(long syntheticId, double rate)
    {
        if (rate <= 0)
        {
            _companionNextClickMs[syntheticId] = 0;
            return;
        }
        _companionNextClickMs[syntheticId] = Environment.TickCount64 + CompanionClickBaseMs(rate);
    }

    /// <summary>
    /// 自动点击只是本地装饰动画：按“次/秒”切换左右手帧，绝不调用 RoomManager.AddClicks，
    /// 也不发送 cr 点击包。0 次/秒 = 关闭该伙伴的自动点击。
    /// </summary>
    void TickCompanionClicks()
    {
        if (_hiddenByHotkey || _companionCats.Count == 0) return;
        long now = Environment.TickCount64;
        foreach (var companion in _cfg.Companions ?? new List<CompanionPet>())
        {
            if (companion == null || companion.SyntheticId >= 0 || companion.ClickRatePerSecond <= 0) continue;
            if (!_companionCats.TryGetValue(companion.SyntheticId, out var cat) || !cat.Visible) continue;
            if (!_companionNextClickMs.TryGetValue(companion.SyntheticId, out long next) || next <= 0)
            {
                ResetCompanionClickSchedule(companion.SyntheticId, companion.ClickRatePerSecond);
                continue;
            }
            if (now < next) continue;

            long side = _companionClickSide.TryGetValue(companion.SyntheticId, out long previousSide) ? previousSide : 0;
            side = companion.RandomClick ? _companionRandom.Next(2) : (side == 0 ? 1 : 0);
            _companionClickSide[companion.SyntheticId] = side;
            cat.ApplyLocalClickAction(side == 0 ? "left" : "right");

            long baseMs = CompanionClickBaseMs(companion.ClickRatePerSecond);
            long delay = companion.RandomClick
                ? (long)(baseMs * (0.5 + _companionRandom.NextDouble()))
                : baseMs;
            _companionNextClickMs[companion.SyntheticId] = now + Math.Max(50L, delay);
        }
    }

    void OnCompanionHideRequested(long syntheticId)
    {
        var companion = CompanionBySynthetic(syntheticId);
        if (companion == null) return;
        companion.Hidden = true;
        Save();
        // 与真实成员一致：等右键菜单关闭后再销毁伙伴窗口，避免在菜单 Click 回调里 Dispose 菜单。
        Safe(() =>
        {
            RefreshCompanions();
            NotifySettingsChanged();
        });
    }

    void UpdateCompanionScales()
    {
        foreach (var companion in _cfg.Companions ?? new List<CompanionPet>())
        {
            if (companion == null || !_companionCats.TryGetValue(companion.SyntheticId, out var cat)) continue;
            int oldWidth = cat.Width;
            cat.SetScale(RemoteScaleFactor);
            cat.SetWindowOptions(EffectiveRemoteOpacity, _cfg.HoverFade, _cfg.ClickThrough, _cfg.Topmost, _cfg.HoverFadeOpacity);
            cat.SetRemoteStyle(EffectiveRemoteScale, EffectiveRemoteOpacity, _cfg.RemoteNameScale);
            cat.SetBarVisible(CompanionBarVisible(companion));
            if (cat.Width != oldWidth) ClampCompanionToScreen(companion, cat);
        }
    }

    void ClampCompanionToScreen(CompanionPet companion, RemoteCatWindow cat)
    {
        var rect = CatVisibleRect(cat);
        var wa = Screen.FromRectangle(rect).WorkingArea;
        int dx = 0, dy = 0;
        if (rect.Right > wa.Right) dx = wa.Right - rect.Right;
        if (rect.Left + dx < wa.Left) dx = wa.Left - rect.Left;
        if (rect.Bottom > wa.Bottom) dy = wa.Bottom - rect.Bottom;
        if (rect.Top + dy < wa.Top) dy = wa.Top - rect.Top;
        if (dx == 0 && dy == 0) return;
        cat.Location = new Point(cat.Left + dx, cat.Top + dy);
        _cfg.CompanionPositions[CompanionKey(companion.Id)] = new[] { cat.Left, cat.Top };
        Save();
    }


    void ApplyThemeToCats((Color bar, Color text, Color accent, Color icon, Color iconMuted)? preview = null)
    {
        var theme = preview.HasValue ? preview.Value : ThemeBarColors();
        foreach (var cat in AllPetWindows())
            cat.SetTheme(theme.bar, theme.text);
    }

    Point? StoredCatPoint(long id) => StoredPoint(CatKey(id));

    /// <summary>按位置字典 key 恢复窗口左上角；位置字典对真实成员和自定义伙伴完全独立。</summary>
    Point? StoredPoint(string key)
    {
        if (!_cfg.MemberPositions.TryGetValue(key, out var a) || a == null || a.Length < 2)
        {
            if (!_cfg.CompanionPositions.TryGetValue(key, out a) || a == null || a.Length < 2) return null;
        }
        var p = new Point(a[0], a[1]);
        float s = RemoteScaleFactor;
        int w = (int)Math.Round(RemoteCatWindow.W * s);
        int head = (int)Math.Round(RemoteCatWindow.Headroom * s);
        int catH = (int)Math.Round(RemoteCatWindow.CatH * s);
        // 窗口上方 130px 是透明的气泡净空。靠近屏幕顶部时窗口 Y 本来就可以是负数，
        // 以前按整个 341px 窗口夹紧会把猫向下推 130px，看起来像坐标记错了。
        // 只要实际可见的猫/昵称条和任一屏幕工作区有交集，就原样恢复。
        var visible = new Rectangle(p.X, p.Y + head, w, catH);
        if (Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(visible))) return p;

        // 屏幕布局变了或位置完全丢到屏幕外时才夹紧；夹的是可见区域，不是透明净空。
        var wa = Screen.FromPoint(new Point(visible.Left + visible.Width / 2, visible.Top + visible.Height / 2)).WorkingArea;
        int x = Math.Clamp(visible.Left, wa.Left, Math.Max(wa.Left, wa.Right - w));
        int y = Math.Clamp(visible.Top, wa.Top, Math.Max(wa.Top, wa.Bottom - catH));
        return new Point(x, y - head);
    }

    /// <summary>
    /// 新成员/回房成员位置判定：两张桌宠重叠面积超过新桌宠可见面积的 30% 才算冲突。
    /// 贴边或手动微调时允许少量重叠，避免因为一两像素就强制换位。
    /// </summary>
    const double CatOverlapTolerance = 0.30;

    static double OverlapRatio(Rectangle candidate, Rectangle obstacle)
    {
        var inter = Rectangle.Intersect(candidate, obstacle);
        if (inter.IsEmpty || candidate.Width <= 0 || candidate.Height <= 0) return 0;
        return (double)inter.Width * inter.Height / ((double)candidate.Width * candidate.Height);
    }

    static bool HasSignificantOverlap(Rectangle candidate, IEnumerable<Rectangle> obstacles)
    {
        foreach (var obstacle in obstacles)
        {
            if (OverlapRatio(candidate, obstacle) > CatOverlapTolerance) return true;
        }
        return false;
    }

    Rectangle CatVisibleRectAt(Point location)
    {
        float s = RemoteScaleFactor;
        int w = (int)Math.Round(RemoteCatWindow.W * s);
        int head = (int)Math.Round(RemoteCatWindow.Headroom * s);
        int catH = (int)Math.Round(RemoteCatWindow.CatH * s);
        return new Rectangle(location.X, location.Y + head, w, catH);
    }

    List<Rectangle> VisibleCatRects()
    {
        var list = new List<Rectangle>();
        foreach (var cat in AllPetWindows())
        {
            if (cat.Visible) list.Add(CatVisibleRect(cat));
        }
        var local = LocalVisibleRect();
        if (!local.IsEmpty) list.Add(local);
        return list;
    }

    Point PlaceNewCat(long id)
    {
        var stored = StoredCatPoint(id);
        // 默认位置成员如果还保存着旧屏幕的坐标，换屏后按当前“显示屏幕”重新落位；
        // 手动拖过的成员继续保留原位置。
        if (stored.HasValue && !IsCustomCatPosition(id))
        {
            var screen = Screen.FromPoint(new Point(
                stored.Value.X + RemoteCatWindow.W / 2,
                stored.Value.Y + RemoteCatWindow.Headroom + RemoteCatWindow.CatH / 2));
            if (!string.Equals(screen.DeviceName, ConfiguredScreen().DeviceName, StringComparison.OrdinalIgnoreCase))
                stored = null;
        }
        if (stored.HasValue && !HasSignificantOverlap(CatVisibleRectAt(stored.Value), VisibleCatRects()))
            return stored.Value;

        var point = DefaultCatPoint();
        // 保存自动安排的位置；下次回房时优先恢复，若与后来的人冲突再重新安排。
        _cfg.MemberPositions[CatKey(id)] = new[] { point.X, point.Y };
        _cfg.MemberCustomPositions.Remove(CatKey(id));
        Save();
        return point;
    }

    /// <summary>
    /// 新成员默认从“显示屏幕”设置的屏幕右下角开始摆放：横向时贴着工作区底边从右往左，
    /// 排满后向上换行；纵向时贴着工作区右边，第一列从右下往上，排满后向左换列并从顶部往下。
    /// 不能跟随主桌宠当前位置，否则用户把主桌宠拖到别的屏幕后，新成员会出现在错误屏幕。
    /// 碰撞按桌宠实际可见区域判断，透明的气泡净空不占位置；本机桌宠也视为已占位。
    /// </summary>
    Point DefaultCatPoint()
    {
        float s = RemoteScaleFactor;
        int w = (int)Math.Round(RemoteCatWindow.W * s);
        int h = (int)Math.Round(RemoteCatWindow.H * s);
        int head = (int)Math.Round(RemoteCatWindow.Headroom * s);
        int catH = (int)Math.Round(RemoteCatWindow.CatH * s);
        int gap = Math.Max(4, (int)Math.Round(8 * s));
        int stepX = Math.Max(1, w + gap);
        int stepY = Math.Max(1, catH + gap);
        var wa = ConfiguredScreen().WorkingArea;

        var taken = VisibleCatRects();

        if (string.Equals(_cfg.CatLayout, "vertical", StringComparison.OrdinalIgnoreCase))
        {
            int columnRight = wa.Right;
            bool firstColumn = true;
            for (int col = 0; col < 32; col++)
            {
                int start = firstColumn ? wa.Bottom - catH : wa.Top;
                int end = firstColumn ? wa.Top : wa.Bottom - catH;
                for (int y = start; firstColumn ? y >= end : y <= end; y += firstColumn ? -stepY : stepY)
                {
                    int x = columnRight - w;
                    if (x < wa.Left) break;
                    var slot = new Rectangle(x, y, w, catH);
                    if (!HasSignificantOverlap(slot, taken)) return new Point(x, y - head);
                }
                firstColumn = false;
                columnRight -= stepX;
                if (columnRight - w < wa.Left) break;
            }
            return new Point(wa.Left, wa.Top);
        }

        int startX = wa.Right - w;
        int startY = wa.Bottom - h;
        for (int row = 0; row < 32; row++)
        {
            int y = startY - row * stepY;
            if (y < wa.Top) break;
            for (int x = startX; x >= wa.Left; x -= stepX)
            {
                var slot = new Rectangle(x, y + head, w, catH);
                if (!HasSignificantOverlap(slot, taken)) return new Point(x, y);
            }
        }
        return new Point(wa.Left, wa.Top);
    }

    /// <summary>托盘/右键菜单切换排列方向；切换后立即按新方向重排桌宠。</summary>
    public void SetCatLayout(string layout, bool announce = false)
    {
        string next = string.Equals(layout, "vertical", StringComparison.OrdinalIgnoreCase) ? "vertical" : "horizontal";
        _cfg.CatLayout = next;
        Save();
        ResetCatOrder();
        RefreshMenu();
        NotifySettingsChanged();
        if (announce)
            NotifyHotkey(next == "vertical"
                ? Localization.T("notify.layoutVertical", "已切换为纵向排列")
                : Localization.T("notify.layoutHorizontal", "已切换为横向排列"));
    }

    /// <summary>快捷键切换横纵排列。</summary>
    public void ToggleCatLayout(bool announce = false)
        => SetCatLayout(string.Equals(_cfg.CatLayout, "vertical", StringComparison.OrdinalIgnoreCase) ? "horizontal" : "vertical", announce);

    /// <summary>
    /// 联机面板“重置排序”：横向时主角贴右下、其他成员从右往左排成底部横排；
    /// 纵向时沿屏幕右侧向下排列，排满后向左换列并从顶部继续。
    /// 锚点使用可见区域并贴住工作区底边/右边，和手动拖动贴边后的位置一致。
    /// 自动排序使用设置中的“默认显示屏幕”，不跟随主角当前所在屏幕；
    /// 显式传入 targetScreen 的调用仍以显式目标为准。
    /// </summary>
    public void ResetCatOrder() => ResetCatOrder(ConfiguredScreen());

    public void ResetCatOrder(Screen targetScreen)
    {
        _cfg.MemberCustomPositions.Clear();
        _cfg.CompanionCustomPositions?.Clear();
        _cfg.CompanionPositions ??= new Dictionary<string, int[]>();
        var wa = (targetScreen ?? ConfiguredScreen()).WorkingArea;
        float s = RemoteScaleFactor;
        float ms = Width / (float)PetComposer.WindowW;
        int gap = Math.Max(6, (int)Math.Round(10 * s));

        // 主窗口右侧有透明边距和宝箱按钮；用实际内容右边界定位，避免主角看起来没贴住屏幕。
        int mainContentRight = (int)Math.Round(Math.Max(PetComposer.ToolbarX + PetComposer.ToolbarW, PetComposer.ChestRightRect.Right) * ms);
        int mainVisibleOffsetX = (int)Math.Round(PetComposer.CatX * ms);
        int mainVisibleW = (int)Math.Round(PetComposer.CatSize * ms);
        int mainVisibleRight = wa.Right - Math.Max(0, mainContentRight - (mainVisibleOffsetX + mainVisibleW));
        int localX = mainVisibleRight - mainVisibleOffsetX - mainVisibleW;
        int localY = Math.Max(wa.Top, wa.Bottom - Height);
        Location = new Point(localX, localY);
        _cfg.PetX = localX;
        _cfg.PetY = localY;

        // 只排列当前房间仍在显示的人；离线/隐藏/已离开的成员即使还残留窗口也不占位。
        // 自定义伙伴排在真实成员后面，使用同一套横向/纵向排版和间距。
        var layoutItems = new List<(long Id, bool Companion, string Key)>();
        foreach (long id in _cats.Keys
            .Where(id => _room.Members.TryGetValue(id, out var m) && m.Online && m.Visible)
            .OrderBy(id => id))
            layoutItems.Add((id, false, CatKey(id)));
        if (!_cfg.HideOtherPets)
        {
            foreach (var companion in _cfg.Companions ?? new List<CompanionPet>())
            {
                if (companion == null || companion.SyntheticId >= 0 || companion.Hidden) continue;
                layoutItems.Add((companion.SyntheticId, true, CompanionKey(companion.Id)));
            }
        }

        if (layoutItems.Count > 0)
        {
            int rw = Math.Max(1, (int)Math.Round(RemoteCatWindow.W * s));
            int catH = Math.Max(1, (int)Math.Round(RemoteCatWindow.CatH * s));
            int head = (int)Math.Round(RemoteCatWindow.Headroom * s);
            int leftLimit = wa.Left;
            int mainVisibleH = (int)Math.Round(PetComposer.CatSize * ms);
            // 主角比联机角色大时，其他角色先贴屏幕边缘排好，主角单独放到整排/列的外侧。
            bool mainLargerThanRemote = mainVisibleW > rw + gap || mainVisibleH > catH + gap;

            if (string.Equals(_cfg.CatLayout, "vertical", StringComparison.OrdinalIgnoreCase))
            {
                // 第一列从底部往上排；排满后向左换列，从工作区顶部往下排。
                int columnRight = wa.Right;
                int cursorBottom = mainLargerThanRemote
                    ? wa.Bottom
                    : localY + (int)Math.Round(PetComposer.CatY * ms) - gap;
                int topCursor = wa.Top;
                int blockLeft = int.MaxValue;
                bool firstColumn = true;

                foreach (var item in layoutItems)
                {
                    int visibleTop;
                    if (firstColumn)
                    {
                        if (cursorBottom - catH >= wa.Top)
                        {
                            visibleTop = cursorBottom - catH;
                            cursorBottom -= catH + gap;
                        }
                        else
                        {
                            firstColumn = false;
                            columnRight -= rw + gap;
                            if (columnRight - rw < leftLimit) break;
                            visibleTop = wa.Top;
                            topCursor = wa.Top + catH + gap;
                        }
                    }
                    else
                    {
                        if (topCursor + catH > wa.Bottom)
                        {
                            columnRight -= rw + gap;
                            if (columnRight - rw < leftLimit) break;
                            topCursor = wa.Top;
                        }
                        visibleTop = topCursor;
                        topCursor += catH + gap;
                    }

                    int x = Math.Max(leftLimit, columnRight - rw);
                    int y = visibleTop - head;
                    if (item.Companion)
                    {
                        if (_companionCats.TryGetValue(item.Id, out var companionCat)) companionCat.Location = new Point(x, y);
                        _cfg.CompanionPositions[item.Key] = new[] { x, y };
                    }
                    else
                    {
                        if (_cats.TryGetValue(item.Id, out var cat)) cat.Location = new Point(x, y);
                        _cfg.MemberPositions[item.Key] = new[] { x, y };
                    }
                    if (mainLargerThanRemote) blockLeft = Math.Min(blockLeft, x);
                }

                if (mainLargerThanRemote && blockLeft != int.MaxValue)
                {
                    // 右侧列保持不动，主角整体移到这些角色的左侧。
                    int mainVisibleRightTarget = blockLeft - gap;
                    localX = mainVisibleRightTarget - mainVisibleOffsetX - mainVisibleW;
                    Location = new Point(localX, localY);
                    _cfg.PetX = localX;
                }
            }
            else
            {
                // 底部横排：其他成员和自定义伙伴先从右侧底部排；主角默认贴右下。
                // 主角明显更大时，把其他角色完整排在底部，主角单独移到整排上方。
                int rowStep = Math.Max(1, catH + gap);
                int rightEdge = mainLargerThanRemote ? wa.Right : localX + mainVisibleOffsetX - gap;
                // 按主角实际猫体底边定位，避免远程猫窗口的 11px 底部留白和缩放取整造成 1px 错位。
                int mainCatBottom = localY + (int)Math.Round(PetComposer.CatY * ms) + (int)Math.Round(PetComposer.CatSize * ms);
                int remoteHead = (int)Math.Round(RemoteCatWindow.Headroom * s);
                int remoteCatSize = (int)Math.Round(RemoteCatWindow.CatSize * s);
                int baseY = mainLargerThanRemote
                    ? Math.Max(wa.Top, wa.Bottom - remoteHead - remoteCatSize)
                    : Math.Max(wa.Top, mainCatBottom - remoteHead - remoteCatSize);
                int cursorRight = rightEdge;
                int row = 0;
                int blockTop = int.MaxValue;

                foreach (var item in layoutItems)
                {
                    int rawX = cursorRight - rw;
                    if (rawX < leftLimit && cursorRight != rightEdge)
                    {
                        row++;
                        cursorRight = rightEdge;
                        rawX = cursorRight - rw;
                    }
                    int x = Math.Max(leftLimit, rawX);
                    int y = Math.Max(wa.Top, baseY - row * rowStep);
                    if (item.Companion)
                    {
                        if (_companionCats.TryGetValue(item.Id, out var companionCat)) companionCat.Location = new Point(x, y);
                        _cfg.CompanionPositions[item.Key] = new[] { x, y };
                    }
                    else
                    {
                        if (_cats.TryGetValue(item.Id, out var cat)) cat.Location = new Point(x, y);
                        _cfg.MemberPositions[item.Key] = new[] { x, y };
                    }
                    if (mainLargerThanRemote) blockTop = Math.Min(blockTop, y + remoteHead);
                    cursorRight = x - gap;
                }

                if (mainLargerThanRemote && blockTop != int.MaxValue)
                {
                    // 底部两排保持不动，主角整体上移到这些角色的上方外侧。
                    localY = blockTop - gap - (int)Math.Round((PetComposer.CatY + PetComposer.CatSize) * ms);
                    Location = new Point(localX, localY);
                    _cfg.PetY = localY;
                }
            }
        }

        Save();
        GeometryChanged?.Invoke();
        ApplyPlacementRules();
    }

    /// <summary>
    /// 关闭“允许重叠”后，把与主角/其他桌宠相交的联机角色和本地伙伴向附近空位挪动。
    /// 本地主角位置保持不动，作为固定障碍物；刚被拖动的桌宠优先让位。
    /// </summary>
    public void ResolveOverlaps(long? priorityId = null)
    {
        if (_cfg.AllowOverlap) return;
        var pets = AllPetWindows()
            .Where(cat => cat != null && !cat.IsDisposed && cat.Visible)
            .Select(cat => (Id: cat.MemberId, Cat: cat))
            .OrderBy(pet => pet.Id)
            .ToList();
        if (priorityId.HasValue)
            pets = pets.OrderBy(pet => pet.Id == priorityId.Value ? 0 : 1).ThenBy(pet => pet.Id).ToList();

        bool changed = false;
        for (int pass = 0; pass < 8; pass++)
        {
            bool movedThisPass = false;
            foreach (var pet in pets)
            {
                var rect = CatVisibleRect(pet.Cat);
                var obstacles = new List<Rectangle>();
                var local = LocalVisibleRect();
                if (!local.IsEmpty) obstacles.Add(local);
                foreach (var other in pets)
                    if (other.Id != pet.Id) obstacles.Add(CatVisibleRect(other.Cat));
                if (!HasSignificantOverlap(rect, obstacles)) continue;

                var target = FindFreeCatPoint(pet.Cat, obstacles);
                if (!target.HasValue) continue;
                pet.Cat.Location = target.Value;
                SavePetPosition(pet.Id, pet.Cat, custom: false);
                movedThisPass = true;
                changed = true;
            }
            if (!movedThisPass) break;
        }
        if (changed) Save();
    }

    Rectangle CatVisibleRect(RemoteCatWindow cat)
    {
        float s = RemoteScaleFactor;
        int head = (int)Math.Round(RemoteCatWindow.Headroom * s);
        int catH = (int)Math.Round(RemoteCatWindow.CatH * s);
        return new Rectangle(cat.Left, cat.Top + head, cat.Width, catH);
    }

    Rectangle LocalVisibleRect()
    {
        if (!Visible || _hiddenByHotkey) return Rectangle.Empty;
        float s = Width / (float)PetComposer.WindowW;
        var cat = new Rectangle(
            Left + (int)Math.Round(PetComposer.CatX * s),
            Top + (int)Math.Round(PetComposer.CatY * s),
            (int)Math.Round(PetComposer.CatSize * s),
            (int)Math.Round(PetComposer.CatSize * s));
        var hud = new Rectangle(
            Left + (int)Math.Round(PetComposer.CatX * s),
            Top + (int)Math.Round(PetComposer.InfoY * s),
            (int)Math.Round(PetComposer.CatSize * s),
            (int)Math.Round(PetComposer.CounterH * s));
        return Rectangle.Union(cat, hud);
    }

    Point? FindFreeCatPoint(RemoteCatWindow cat, List<Rectangle> obstacles)
    {
        var current = CatVisibleRect(cat);
        var wa = Screen.FromPoint(new Point(current.Left + current.Width / 2, current.Top + current.Height / 2)).WorkingArea;
        float s = RemoteScaleFactor;
        int head = (int)Math.Round(RemoteCatWindow.Headroom * s);
        int w = cat.Width;
        int h = (int)Math.Round(RemoteCatWindow.CatH * s);
        int stepX = Math.Max(24, (int)Math.Round(w * 0.5));
        int stepY = Math.Max(24, (int)Math.Round(h * 0.5));
        int maxX = Math.Max(wa.Left, wa.Right - w);
        int maxY = Math.Max(wa.Top, wa.Bottom - h);

        for (int row = 0; row < 8; row++)
        {
            int y = Math.Clamp(current.Top - row * stepY, wa.Top, maxY);
            for (int dist = 0; dist <= 8; dist++)
            {
                int[] signs = dist == 0 ? new[] { 0 } : new[] { -1, 1 };
                foreach (int sign in signs)
                {
                    int x = Math.Clamp(current.Left + sign * dist * stepX, wa.Left, maxX);
                    var candidate = new Rectangle(x, y, w, h);
                    if (candidate.Left < wa.Left || candidate.Top < wa.Top ||
                        candidate.Right > wa.Right || candidate.Bottom > wa.Bottom) continue;
                    if (!HasSignificantOverlap(candidate, obstacles))
                        return new Point(x, y - head);
                }
            }
        }
        return DefaultCatPoint();
    }

    /// <summary>把单个联机角色/自定义伙伴吸附到最近网格；主角色不调用。</summary>
    void SnapPetToGrid(RemoteCatWindow cat)
    {
        if (!_cfg.AlignPetsToGrid || cat == null || cat.IsDisposed || !cat.Visible) return;
        var visible = CatVisibleRect(cat);
        var wa = Screen.FromRectangle(visible).WorkingArea;
        float s = RemoteScaleFactor;
        int gap = Math.Max(4, (int)Math.Round(8 * s));
        int head = (int)Math.Round(RemoteCatWindow.Headroom * s);
        var cell = PetGridLogic.CellSize(new Size(cat.Width, visible.Height), gap);
        var obstacles = new List<Rectangle>();
        var local = LocalVisibleRect();
        if (!local.IsEmpty) obstacles.Add(local);
        foreach (var other in AllPetWindows())
            if (!ReferenceEquals(other, cat) && other.Visible && !other.IsDisposed)
                obstacles.Add(CatVisibleRect(other));
        var target = PetGridLogic.SnapVisibleRect(visible, wa, cell, head, obstacles, _cfg.GridAlignRight);
        if (cat.Location != target) cat.Location = target;
    }

    /// <summary>把全部非主角桌宠吸附到网格；save=false 时由调用方统一落盘。</summary>
    public void SnapPetsToGrid(bool save = true)
    {
        if (!_cfg.AlignPetsToGrid) return;
        bool changed = false;
        foreach (var cat in AllPetWindows())
        {
            if (cat == null || cat.IsDisposed || !cat.Visible) continue;
            var before = cat.Location;
            SnapPetToGrid(cat);
            if (cat.Location == before) continue;
            SavePetPosition(cat.MemberId, cat, custom: false);
            changed = true;
        }
        if (changed && save) Save();
    }

    /// <summary>整组拖动结束前只吸附本次选中的桌宠，保持未选中桌宠不动。</summary>
    void SnapSelectedPetsToGrid()
    {
        if (!_cfg.AlignPetsToGrid) return;
        foreach (var id in _selectedCatIds)
            if (TryGetPetWindow(id, out var cat)) SnapPetToGrid(cat);
    }

    /// <summary>
    /// 托盘/右键菜单和设置页共用：切换网格对齐，启用时立即整理现有桌宠。
    /// 主角色不在 AllPetWindows 中，因此不会被移动。
    /// </summary>
    public void SetPetsGridAlignment(bool enabled, bool snapExisting)
    {
        bool changed = _cfg.AlignPetsToGrid != enabled;
        _cfg.AlignPetsToGrid = enabled;
        if (changed) Save();
        if (_alignGridItem != null) _alignGridItem.Checked = enabled;
        foreach (var cat in AllPetWindows()) cat.SetGridAlignment(enabled);
        if (enabled && snapExisting) SnapPetsToGrid();
        NotifySettingsChanged();
    }

    void ToggleGridAlignment()
        => SetPetsGridAlignment(!_cfg.AlignPetsToGrid, snapExisting: true);

    /// <summary>网格对齐优先于自动避让；关闭网格后才执行重叠挪位。</summary>
    void ApplyPlacementRules()
    {
        if (_cfg.AlignPetsToGrid) SnapPetsToGrid();
        else if (!_cfg.AllowOverlap) ResolveOverlaps();
    }

    void UpdateCatScales()
    {
        float s = RemoteScaleFactor;
        bool topmostChanged = false;
        foreach (var pair in _cats)
        {
            var c = pair.Value;
            int oldWidth = c.Width;
            c.SetScale(s);
            topmostChanged |= c.SetWindowOptions(EffectiveRemoteOpacity, _cfg.HoverFade, _cfg.ClickThrough, _cfg.Topmost, _cfg.HoverFadeOpacity);
            c.SetRemoteStyle(EffectiveRemoteScale, EffectiveRemoteOpacity, _cfg.RemoteNameScale);
            c.SetBarVisible(!_cfg.HideRemoteBars);
            // 缩放后窗口从左上角变大，贴边排列的成员会被顶出屏幕；按可见猫体修正回工作区。
            if (c.Width != oldWidth) ClampCatToScreen(pair.Key, c);
        }
        // 缩放/透明度本身不改变层级，只有置顶状态真正切换时才需要重排。
        if (topmostChanged) QueueRestackPetWindows();
    }

    void ClampCatToScreen(long id, RemoteCatWindow cat)
    {
        var rect = CatVisibleRect(cat);
        var wa = Screen.FromRectangle(rect).WorkingArea;
        int dx = 0, dy = 0;
        if (rect.Right > wa.Right) dx = wa.Right - rect.Right;
        if (rect.Left + dx < wa.Left) dx = wa.Left - rect.Left;
        if (rect.Bottom > wa.Bottom) dy = wa.Bottom - rect.Bottom;
        if (rect.Top + dy < wa.Top) dy = wa.Top - rect.Top;
        if (dx == 0 && dy == 0) return;
        cat.Location = new Point(cat.Left + dx, cat.Top + dy);
        _cfg.MemberPositions[CatKey(id)] = new[] { cat.Left, cat.Top };
    }

    void ApplyRemoteScale(double value)
    {
        _cfg.RemoteFollowMainScale = false;
        _cfg.RemoteScale = Math.Clamp(value, 0.25, 2.0);
        Save();
        UpdateCatScales();
        UpdateCompanionScales();
        ApplyPlacementRules();
        NotifySettingsChanged();
    }

    void ApplyRemoteOpacity(double value)
    {
        _cfg.RemoteFollowMainOpacity = false;
        _cfg.RemoteOpacity = Math.Clamp(value, 0.2, 1.0);
        Save();
        UpdateCatScales();
        UpdateCompanionScales();
        NotifySettingsChanged();
    }

    void OnCatHideRequested(long id)
    {
        // 等右键菜单关闭后再销毁远程窗口，避免在菜单 Click 回调里 Dispose 当前菜单。
        Safe(() =>
        {
            // 与面板里的“隐藏成员”共用同一份持久化状态；重新进房也会保持隐藏。
            _room.SetMemberVisible(id, false);
            RemoveCat(id);
            MemberVisibilityChanged?.Invoke();
        });
    }

    void RemoveCat(long id)
    {
        if (_cats.TryGetValue(id, out var cat))
        {
            ClearPetDragState(id, cat);
            _cats.Remove(id);
            cat.Close();
            cat.Dispose();
            // 删除窗口不会在剩余桌宠之间留下 z-order 空洞，不需要重排。
            FlushPendingZOrderRestack();
        }
    }

    public void RefreshMembers() => RefreshCats();

    /// <summary>当前实际可见的桌宠窗口数（主角 + 远程成员 + 自定义伙伴）。只应在 UI 线程读取。</summary>
    public int VisiblePetCount
    {
        get
        {
            int count = Visible ? 1 : 0;
            foreach (var cat in AllPetWindows())
            {
                if (!cat.IsDisposed && cat.Visible) count++;
            }
            return count;
        }
    }

    void RefreshCats()
    {
        foreach (var id in _cats.Keys.ToList())
            if (!_room.Members.ContainsKey(id)) RemoveCat(id);
        foreach (var id in _room.Members.Keys.ToList()) UpdateCat(id);
        ApplyPlacementRules();
    }

    Point DefaultLocation()
    {
        var wa = ConfiguredScreen().WorkingArea;
        var size = WindowSize();
        return new Point(wa.Right - size.Width - 40, wa.Bottom - size.Height - 60);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Size = WindowSize();
        Compose();
        // 构造阶段主窗口还没有句柄；首次显示时再把已保存的本地伙伴放上桌面。
        RefreshCompanions();
        GeometryChanged?.Invoke();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Size = WindowSize();
        Compose();
        UpdateCatScales();
        UpdateCompanionScales();
        ApplyPlacementRules();
        GeometryChanged?.Invoke();
    }

    protected override void OnClosed(EventArgs e)
    {
        // 退出程序时如果游戏模式仍生效，先把临时配置恢复并落盘，避免下次启动继承游戏模式值。
        lock (_gameModeSaveGate)
        {
            if (_autoGameModeActive)
            {
                bool appliedProfile = _autoGameModeCustomApplied;
                _autoGameModeActive = false;
                _autoGameModeCustomApplied = false;
                if (appliedProfile)
                {
                    RestoreAutoGameModeConfig();
                    RestoreAutoGameModePositions();
                }
                else if (_autoGameModeClickThroughOwned)
                {
                    _cfg.ClickThrough = _autoGameModeSnapshot.ClickThrough;
                }
                _autoGameModeClickThroughOwned = false;
                ConfigStore.SaveOverride = null;
                try { ConfigStore.Save(_cfg); } catch { }
            }
        }
        try { if (_capture != null) { _capture.ClosingFromOwner = true; _capture.Close(); _capture.Dispose(); } } catch { }
        _captureFrame?.Dispose();
        _captureFrame = null;
        _capture = null;        foreach (var t in new[] { _blink, _blinkOff, _moveReset, _handReset, _countdownTimer, _wheelCloseTimer, _wheelShowTimer, _hoverFadeTimer, _hoverFadeAnimTimer, _hoverPollTimer, _loadingTimer, _zOrderWatchdog, _zOrderRestackTimer, _companionClickTimer }) { try { t?.Stop(); t?.Dispose(); } catch { } }
        foreach (var c in AllPetWindows().ToList()) { try { c.Close(); c.Dispose(); } catch { } }
        _cats.Clear();
        _companionCats.Clear();
        _companionClickSide.Clear();
        _companionNextClickMs.Clear();
        _companionClickConfig.Clear();
        _composeTimer.Dispose();
        _bubbles.Clear();
        _keyBubbles.Dispose();
        _frameCache.Dispose();
        _surface.Dispose();
        _hook.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _cfg.PetX = Left;
        _cfg.PetY = Top;
        ConfigStore.Save(_cfg);
        base.OnClosed(e);
    }
}
