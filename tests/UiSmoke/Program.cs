using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using WuwatappoNative.Core;
using WuwatappoNative.Ui;

internal static class Program
{
    static readonly List<string> OffsetCommands = new();

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: UiSmoke <wwwroot> <output.png>");
            return 2;
        }
        string wwwroot = Path.GetFullPath(args[0]);
        string output = Path.GetFullPath(args[1]);
        string userData = Path.Combine(Path.GetTempPath(), "wuwa-webview-smoke-" + Guid.NewGuid().ToString("N"));
        string dataRoot = Path.Combine(Path.GetTempPath(), "wuwa-webview-data-" + Guid.NewGuid().ToString("N"));
        string presetFile = Path.Combine(dataRoot, "theme_presets.json");

        // 隔离数据根目录：绝不读写用户真实的 %LOCALAPPDATA%\WuwatappoNative。
        Paths.Root = dataRoot;
        Paths.EnsureDirs();
        var cfg = new Settings { ThemeAccentColor = "#454C73", ThemeFontColor = "#3A3D51" };
        ConfigStore.Save(cfg);
        using var manager = new ThemePresetManager(cfg, presetFile);
        var first = manager.SaveCurrent("说的是");
        cfg.ThemeAccentColor = "#123456";
        ConfigStore.Save(cfg);
        var second = manager.SaveCurrent("是的是的是的");

        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            using var form = new Form
            {
                Text = "Wuwatappo UI smoke",
                Width = 1280,
                Height = 800,
                StartPosition = FormStartPosition.CenterScreen,
            };
            var web = new WebView2 { Dock = DockStyle.Fill };
            form.Controls.Add(web);
            form.Shown += async (s, e) =>
            {
                try
                {
                    var env = await CoreWebView2Environment.CreateAsync(null, userData);
                    await web.EnsureCoreWebView2Async(env);
                    var core = web.CoreWebView2;
                    core.SetVirtualHostNameToFolderMapping("wuwatappo.local", wwwroot, CoreWebView2HostResourceAccessKind.Allow);

                    // 用真实 C# 映射产生的 camelCase JSON 作为页面初始主题预设。
                    string presetsJson = JsonSerializer.Serialize(PanelWindow.MapThemePresets(manager.Presets));
                    string rotationJson = JsonSerializer.Serialize(PanelWindow.MapThemeRotation(manager.Rotation));
                    string seed = "(() => { try { localStorage.setItem('wuwa.preview.themePresets.v1', " +
                                  JsonSerializer.Serialize(presetsJson) +
                                  "); localStorage.setItem('wuwa.preview.themeRotation.v1', " +
                                  JsonSerializer.Serialize(rotationJson) +
                                  "); } catch (e) { window.__uiSmokeSeedError = String(e); } })();";
                    await core.AddScriptToExecuteOnDocumentCreatedAsync(seed);
                    await core.AddScriptToExecuteOnDocumentCreatedAsync(
                        "window.__uiSmokeErrors=[];window.addEventListener('error',e=>window.__uiSmokeErrors.push(String(e.message||e.error||e)));window.addEventListener('unhandledrejection',e=>window.__uiSmokeErrors.push('rejection:'+String(e.reason?.message||e.reason||e)));");

                    var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    core.NavigationCompleted += (_, ev) => loaded.TrySetResult(ev.IsSuccess);

                    JsonObject boot = null;
                    string lastDeleteId = null;
                    core.WebMessageReceived += (_, ev) =>
                    {
                        try
                        {
                            var msg = JsonNode.Parse(ev.WebMessageAsJson) as JsonObject;
                            if (msg == null) return;
                            int id = (int)(msg["id"]?.GetValue<long>() ?? 0);
                            string cmd = msg["cmd"]?.GetValue<string>() ?? "";
                            var data = msg["data"] as JsonObject ?? new JsonObject();
                            JsonNode result = HandleCommand(cmd, data, ref boot, manager, ref lastDeleteId);
                            core.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, ok = true, data = result, error = (string)null }));
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine("bridge: " + ex.Message);
                        }
                    };

                    core.Navigate("https://wuwatappo.local/index.html?preview=settings#page=settings");
                    if (!await loaded.Task) throw new InvalidOperationException("WebView2 navigation failed.");
                    await Task.Delay(900);

                    string bootJson = JsonSerializer.Deserialize<string>(
                        await core.ExecuteScriptAsync("JSON.stringify(window.__previewBoot(window.__previewMode))"));
                    boot = JsonNode.Parse(bootJson) as JsonObject ?? throw new InvalidOperationException("preview boot missing");
                    RefreshThemePresetBoot(boot, manager);
                    // 以真实 C# 序列化出的 boot 再推送一次，让设置页用这份数据重绘。
                    core.PostWebMessageAsJson(JsonSerializer.Serialize(new { evt = "boot", data = boot }));
                    await Task.Delay(800);

                    // 关于按钮必须位于底部更新区，而不是“其他/程序”设置项里；统计说明要出现在弹窗中。
                    string aboutUi = await core.ExecuteScriptAsync(@"
                      (() => {
                        const btn = document.getElementById('aboutBtn');
                        const inFooter = !!document.querySelector('.settings-footer #aboutBtn');
                        const inSetting = !!document.querySelector('.setting-item #aboutBtn');
                        btn?.click();
                        const text = document.getElementById('modalHost')?.textContent || '';
                        return JSON.stringify({
                          inFooter,
                          inSetting,
                          hasStats: text.includes('数据统计说明'),
                          hasIp: text.includes('原始 IP 不持久化存储')
                        });
                      })()");
                    var aboutState = JsonNode.Parse(JsonSerializer.Deserialize<string>(aboutUi)!)!.AsObject();
                    if (!aboutState["inFooter"]!.GetValue<bool>() || aboutState["inSetting"]!.GetValue<bool>()
                        || !aboutState["hasStats"]!.GetValue<bool>() || !aboutState["hasIp"]!.GetValue<bool>())
                        throw new InvalidOperationException("About button must sit beside update controls and include the data-statistics notice. errors="
                            + JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("JSON.stringify(window.__uiSmokeErrors||[])")));
                    await core.ExecuteScriptAsync("document.getElementById('mCloseAbout')?.click()");
                    await Task.Delay(200);

                    string chestMatrixUi = await core.ExecuteScriptAsync(@"
                      (() => JSON.stringify({
                        rows: document.querySelectorAll('#chestNotifyMatrix [data-chest-rarity]').length,
                        categories: document.querySelectorAll('#chestNotifyMatrix [data-chest-notify=""character""], #chestNotifyMatrix [data-chest-notify=""accessory""], #chestNotifyMatrix [data-chest-notify=""emote""]').length,
                        rareOn: !!document.querySelector('#chestNotifyMatrix [data-chest-rarity=""rare""] [data-chest-notify=""enabled""]')?.checked,
                        commonOn: !!document.querySelector('#chestNotifyMatrix [data-chest-rarity=""common""] [data-chest-notify=""enabled""]')?.checked
                      }))()");
                    var chestMatrixState = JsonNode.Parse(JsonSerializer.Deserialize<string>(chestMatrixUi)!)!.AsObject();
                    if (chestMatrixState["rows"]!.GetValue<int>() != 4
                        || chestMatrixState["categories"]!.GetValue<int>() != 12
                        || !chestMatrixState["rareOn"]!.GetValue<bool>()
                        || chestMatrixState["commonOn"]!.GetValue<bool>())
                        throw new InvalidOperationException("Chest notification matrix did not render the expected 4×3 defaults: " + chestMatrixUi);
                    Console.WriteLine("PASS: chest notification settings render the 4×3 rarity/category matrix with Rare/Epic defaults.");

                    // 输入与控制：手柄协议扩展必须位于桌面键盘区域的第一排，并带有开关。
                    string controllerProtocolUi = await core.ExecuteScriptAsync(@"
                      (() => {
                        const section = document.querySelector('[data-section-id=""keyboard""]');
                        const first = section?.querySelector(':scope > .setting-item');
                        const sw = section?.querySelector('[data-switch=""controllerProtocolExtension""]');
                        return JSON.stringify({
                          text: first?.textContent || '',
                          isFirst: !!first && !!sw && first.contains(sw),
                          switches: !!sw
                        });
                      })()");
                    var controllerProtocolState = JsonNode.Parse(JsonSerializer.Deserialize<string>(controllerProtocolUi)!)!.AsObject();
                    if (!controllerProtocolState["switches"]!.GetValue<bool>()
                        || !controllerProtocolState["isFirst"]!.GetValue<bool>()
                        || !controllerProtocolState["text"]!.GetValue<string>().Contains("手柄协议扩展", StringComparison.Ordinal))
                        throw new InvalidOperationException("Controller protocol extension must be the first keyboard setting row.");
                    Console.WriteLine("PASS: controller protocol extension is the first input/keyboard setting row.");

                    // 公告历史：按钮必须位于刷新版本状态与关于之间；后端历史未接入时回退到当前公告。
                    string historyFallbackUi = await core.ExecuteScriptAsync(@"
                      (() => {
                        const footer = document.querySelector('.settings-footer');
                        const ids = [...footer.querySelectorAll('button')].map(b => b.id);
                        document.getElementById('announcementHistoryBtn')?.click();
                        const items = [...document.querySelectorAll('.announcement-history-item')];
                        return JSON.stringify({
                          ids,
                          inFooter: !!footer.querySelector('#announcementHistoryBtn'),
                          modal: !!document.querySelector('.announcement-history-modal'),
                          count: items.length,
                          title: document.querySelector('.announcement-history .about-title')?.textContent || '',
                          close: !!document.getElementById('mCloseAnnouncementHistory'),
                          action: !!document.querySelector('.announcement-history-actions .btn')
                        });
                      })()");
                    var historyFallbackState = JsonNode.Parse(JsonSerializer.Deserialize<string>(historyFallbackUi)!)!.AsObject();
                    var footerIds = historyFallbackState["ids"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
                    if (!footerIds.SequenceEqual(new[] { "checkUpdateBtn", "compatRefreshBtn", "announcementHistoryBtn", "aboutBtn" }))
                        throw new InvalidOperationException("Settings footer order is wrong: " + string.Join(",", footerIds));
                    if (!historyFallbackState["inFooter"]!.GetValue<bool>()
                        || !historyFallbackState["modal"]!.GetValue<bool>()
                        || historyFallbackState["title"]!.GetValue<string>() != "公告历史"
                        || !historyFallbackState["close"]!.GetValue<bool>()
                        || !historyFallbackState["action"]!.GetValue<bool>())
                        throw new InvalidOperationException("Announcement history modal did not render from fallback announcements.");
                    int fallbackCount = historyFallbackState["count"]!.GetValue<int>();
                    if (fallbackCount < 1)
                        throw new InvalidOperationException("Announcement history fallback rendered no entries.");
                    await core.ExecuteScriptAsync("document.getElementById('mCloseAnnouncementHistory')?.click()");
                    await Task.Delay(200);

                    // 底栏开关 + 快捷键单/双模式：只隐藏输入行，切回时原绑定仍在。
                    boot["settings"]!["hideMainBar"] = false;
                    boot["settings"]!["hideRemoteBars"] = false;
                    boot["settings"]!["separateBarHotkeys"] = false;
                    boot["settings"]!["hotkeys"] = new JsonObject
                    {
                        ["barToggle"] = "Ctrl+Alt+B",
                        ["barToggleMain"] = "Ctrl+Alt+M",
                        ["barToggleRemote"] = "Ctrl+Alt+R",
                    };
                    core.PostWebMessageAsJson(JsonSerializer.Serialize(new { evt = "boot", data = boot }));
                    await Task.Delay(600);
                    string singleBarUi = await core.ExecuteScriptAsync(@"
                      (() => {
                        const ids = [...document.querySelectorAll('[data-hotkey]')].map(el => el.dataset.hotkey);
                        const values = Object.fromEntries([...document.querySelectorAll('[data-hotkey]')].map(el => [el.dataset.hotkey, el.value]));
                        return JSON.stringify({
                          hideMainBar: !!document.querySelector(""[data-switch='hideMainBar']""),
                          hideRemoteBars: !!document.querySelector(""[data-switch='hideRemoteBars']""),
                          separateSwitch: !!document.querySelector(""[data-switch='separateBarHotkeys']""),
                          barIds: ids.filter(id => id.startsWith('barToggle')),
                          value: values.barToggle || ''
                        });
                      })()");
                    var singleBarState = JsonNode.Parse(JsonSerializer.Deserialize<string>(singleBarUi))!.AsObject();
                    if (!singleBarState["hideMainBar"]!.GetValue<bool>() ||
                        !singleBarState["hideRemoteBars"]!.GetValue<bool>() ||
                        !singleBarState["separateSwitch"]!.GetValue<bool>())
                        throw new InvalidOperationException("Bar visibility / separate-hotkey switches were not rendered.");
                    var singleIds = singleBarState["barIds"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
                    if (singleIds.Length != 1 || singleIds[0] != "barToggle")
                        throw new InvalidOperationException("Single mode must show exactly the barToggle row: " + string.Join(",", singleIds));
                    if (singleBarState["value"]!.GetValue<string>() != "Ctrl+Alt+B")
                        throw new InvalidOperationException("Single-mode bar hotkey input lost its binding.");

                    boot["settings"]!["separateBarHotkeys"] = true;
                    core.PostWebMessageAsJson(JsonSerializer.Serialize(new { evt = "boot", data = boot }));
                    await Task.Delay(600);
                    string separateBarUi = await core.ExecuteScriptAsync(@"
                      (() => {
                        const rows = [...document.querySelectorAll('[data-hotkey]')]
                          .filter(el => el.dataset.hotkey.startsWith('barToggle'))
                          .map(el => ({ id: el.dataset.hotkey, value: el.value }));
                        return JSON.stringify(rows);
                      })()");
                    var separateRows = JsonNode.Parse(JsonSerializer.Deserialize<string>(separateBarUi))!.AsArray();
                    if (separateRows.Count != 2 ||
                        separateRows[0]!["id"]!.GetValue<string>() != "barToggleMain" ||
                        separateRows[1]!["id"]!.GetValue<string>() != "barToggleRemote")
                        throw new InvalidOperationException("Separate mode must show exactly barToggleMain/barToggleRemote.");
                    if (separateRows[0]!["value"]!.GetValue<string>() != "Ctrl+Alt+M" ||
                        separateRows[1]!["value"]!.GetValue<string>() != "Ctrl+Alt+R")
                        throw new InvalidOperationException("Separate-mode bar hotkey inputs lost their bindings.");

                    boot["settings"]!["separateBarHotkeys"] = false;
                    core.PostWebMessageAsJson(JsonSerializer.Serialize(new { evt = "boot", data = boot }));
                    await Task.Delay(600);
                    string restoredBarUi = await core.ExecuteScriptAsync(@"
                      (() => {
                        const rows = [...document.querySelectorAll('[data-hotkey]')]
                          .filter(el => el.dataset.hotkey.startsWith('barToggle'))
                          .map(el => ({ id: el.dataset.hotkey, value: el.value }));
                        return JSON.stringify(rows);
                      })()");
                    var restoredRows = JsonNode.Parse(JsonSerializer.Deserialize<string>(restoredBarUi))!.AsArray();
                    if (restoredRows.Count != 1 || restoredRows[0]!["id"]!.GetValue<string>() != "barToggle" ||
                        restoredRows[0]!["value"]!.GetValue<string>() != "Ctrl+Alt+B")
                        throw new InvalidOperationException("Switching back to single mode must keep the original barToggle binding.");

                    string initial = await core.ExecuteScriptAsync(@"
                      (() => {
                        const group = document.getElementById('settings-group-theme');
                        if (group) group.open = true;
                        const section = document.getElementById('themeSection');
                        if (section) section.scrollIntoView({ block: 'start' });
                        const rows = [...document.querySelectorAll('.theme-preset-row')];
                        return JSON.stringify({
                          seedError: window.__uiSmokeSeedError || '',
                          lsPresets: (localStorage.getItem('wuwa.preview.themePresets.v1') || '').length,
                          lsRotation: (localStorage.getItem('wuwa.preview.themeRotation.v1') || '').length,
                          rows: rows.length,
                          names: rows.map(r => r.querySelector('.theme-preset-name')?.textContent || ''),
                          deleteIds: rows.map(r => r.querySelector('[data-theme-preset-delete]')?.dataset.themePresetDelete || ''),
                          applyIds: rows.map(r => r.querySelector('[data-theme-preset-apply]')?.dataset.themePresetApply || '')
                        });
                      })()");
                    Console.WriteLine("initial=" + initial);
                    var initialState = JsonNode.Parse(JsonSerializer.Deserialize<string>(initial))!.AsObject();
                    if (initialState["seedError"]!.GetValue<string>().Length > 0)
                        throw new InvalidOperationException("Preview seed failed: " + initialState["seedError"]);
                    if (initialState["rows"]!.GetValue<int>() != 2)
                        throw new InvalidOperationException("Expected 2 seeded theme presets, got " + initialState["rows"]);
                    var names = initialState["names"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
                    if (names[0] != "说的是" || names[1] != "是的是的是的")
                        throw new InvalidOperationException("Preset names rendered incorrectly: " + string.Join(" | ", names));
                    var deleteIds = initialState["deleteIds"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
                    if (deleteIds.Any(string.IsNullOrWhiteSpace))
                        throw new InvalidOperationException("Delete buttons must carry a non-empty preset id.");
                    if (!deleteIds.Contains(first.Id) || !deleteIds.Contains(second.Id))
                        throw new InvalidOperationException("Delete button ids do not match the real preset ids.");

                    // 重命名：覆盖 window.prompt，验证 rename 命令返回后 DOM 立即更新。
                    await core.ExecuteScriptAsync(@"
                      (() => {
                        window.prompt = () => '重命名后的预设';
                        document.querySelector('[data-theme-preset-rename]').click();
                      })()");
                    await Task.Delay(900);
                    string renamed = await core.ExecuteScriptAsync("document.querySelector('.theme-preset-name')?.textContent || ''");
                    string renamedText = JsonSerializer.Deserialize<string>(renamed);
                    if (renamedText != "重命名后的预设")
                        throw new InvalidOperationException("Rename did not update the preset list: " + renamedText);
                    if (manager.Presets.First(p => p.Id == first.Id).Name != "重命名后的预设")
                        throw new InvalidOperationException("Rename did not reach the real ThemePresetManager.");

                    // 应用第二个预设：验证 apply 命令 id 与轮换当前项。
                    await core.ExecuteScriptAsync("document.querySelectorAll('[data-theme-preset-apply]')[1].click()");
                    await Task.Delay(900);
                    string applied = await core.ExecuteScriptAsync(@"
                      (() => {
                        const rows = [...document.querySelectorAll('.theme-preset-row')];
                        return JSON.stringify({ active: rows.map(r => r.classList.contains('active')) });
                      })()");
                    var appliedState = JsonNode.Parse(JsonSerializer.Deserialize<string>(applied))!.AsObject();
                    var active = appliedState["active"]!.AsArray().Select(x => x!.GetValue<bool>()).ToArray();
                    if (!active[1])
                        throw new InvalidOperationException("Applying the second preset did not mark it active.");
                    if (manager.Rotation.CurrentPresetId != second.Id)
                        throw new InvalidOperationException("Apply did not update the real rotation currentPresetId.");

                    // 轮换开关：验证 rotation.set 字段。
                    await core.ExecuteScriptAsync("document.getElementById('themeRotationToggle').click()");
                    await Task.Delay(700);
                    if (!manager.Rotation.Enabled)
                        throw new InvalidOperationException("Rotation toggle did not reach the real ThemePresetManager.");

                    // 删除第一项：点击后应立即从 DOM 消失，后端文件同步删除。
                    await core.ExecuteScriptAsync("document.querySelectorAll('.theme-preset-row')[0].querySelector('[data-theme-preset-delete]').click()");
                    await Task.Delay(1400);
                    string afterDelete = await core.ExecuteScriptAsync(@"
                      (() => {
                        const rows = [...document.querySelectorAll('.theme-preset-row')];
                        return JSON.stringify({
                          rows: rows.length,
                          names: rows.map(r => r.querySelector('.theme-preset-name')?.textContent || '')
                        });
                      })()");
                    Console.WriteLine("afterDelete=" + afterDelete);
                    var deleteState = JsonNode.Parse(JsonSerializer.Deserialize<string>(afterDelete))!.AsObject();
                    if (deleteState["rows"]!.GetValue<int>() != 1)
                        throw new InvalidOperationException("Deleted preset still visible after click.");
                    string remainingName = deleteState["names"]![0]!.GetValue<string>();
                    if (remainingName != "是的是的是的")
                        throw new InvalidOperationException("Wrong preset remained after delete: " + remainingName);
                    if (lastDeleteId != first.Id)
                        throw new InvalidOperationException("Delete command carried the wrong id: " + (lastDeleteId ?? "<null>"));
                    string savedJson = File.ReadAllText(presetFile);
                    if (savedJson.Contains(first.Id, StringComparison.Ordinal))
                        throw new InvalidOperationException("Deleted preset id still present in theme_presets.json.");
                    if (!savedJson.Contains(second.Id, StringComparison.Ordinal))
                        throw new InvalidOperationException("Unrelated preset disappeared from theme_presets.json.");

                    using (var restarted = new ThemePresetManager(cfg, presetFile))
                    {
                        if (restarted.Presets.Count != 1 || restarted.Presets[0].Id != second.Id)
                            throw new InvalidOperationException("Deleted preset reappeared after manager restart.");
                    }

                    // 删除最后一项：列表应显示空状态，文件中的 Presets 应为空数组。
                    await core.ExecuteScriptAsync("document.querySelector('.theme-preset-row [data-theme-preset-delete]').click()");
                    await Task.Delay(1300);
                    string emptyState = await core.ExecuteScriptAsync(@"
                      (() => JSON.stringify({
                        rows: document.querySelectorAll('.theme-preset-row').length,
                        text: document.getElementById('themePresetList')?.textContent || ''
                      }))()");
                    var empty = JsonNode.Parse(JsonSerializer.Deserialize<string>(emptyState))!.AsObject();
                    if (empty["rows"]!.GetValue<int>() != 0)
                        throw new InvalidOperationException("Theme preset list is not empty after deleting the last preset.");
                    if (!empty["text"]!.GetValue<string>().Contains("还没有保存的主题预设", StringComparison.Ordinal))
                        throw new InvalidOperationException("Empty theme preset state was not rendered.");
                    if (lastDeleteId != second.Id)
                        throw new InvalidOperationException("Second delete command carried the wrong id: " + (lastDeleteId ?? "<null>"));
                    if (manager.Presets.Count != 0 || manager.Rotation.CurrentPresetId != "")
                        throw new InvalidOperationException("Deleting the last preset did not clear the real manager state.");
                    using (var fileDoc = JsonDocument.Parse(File.ReadAllText(presetFile)))
                    {
                        if (fileDoc.RootElement.GetProperty("Presets").GetArrayLength() != 0)
                            throw new InvalidOperationException("theme_presets.json still contains presets after deleting all.");
                    }
                    using (var restartedEmpty = new ThemePresetManager(cfg, presetFile))
                    {
                        if (restartedEmpty.Presets.Count != 0)
                            throw new InvalidOperationException("Deleted presets reappeared after restarting with an empty file.");
                    }

                    // 头饰微调：缩放/旋转保存、方向按钮保留 transform、归零同时复位四项并清掉脏值恢复。
                    await core.ExecuteScriptAsync(@"
                      (() => {
                        const group = document.getElementById('settings-group-window');
                        if (group) group.open = true;
                        const set = (id, value) => {
                          const el = document.getElementById(id);
                          if (!el) return;
                          el.value = String(value);
                          el.dispatchEvent(new Event('input', { bubbles: true }));
                        };
                        set('accOffsetX', 0);
                        set('accOffsetY', 0);
                        set('accScale', 150);
                        set('accRotation', 30);
                        document.getElementById('settings-group-window')?.scrollIntoView({ block: 'start' });
                      })()");
                    await Task.Delay(500);
                    string accInitial = await core.ExecuteScriptAsync(@"
                      (() => {
                        const val = id => document.getElementById(id)?.value ?? '';
                        const text = id => document.getElementById(id)?.textContent ?? '';
                        return JSON.stringify({
                          x: val('accOffsetX'), y: val('accOffsetY'), scale: val('accScale'), rotation: val('accRotation'),
                          scaleText: text('accScaleValue'), rotationText: text('accRotationValue'),
                          chip: text('accessoryCoords'),
                          resetDisabled: !!document.getElementById('resetAccessoryOffset')?.disabled
                        });
                      })()");
                    var accInitialState = JsonNode.Parse(JsonSerializer.Deserialize<string>(accInitial))!.AsObject();
                    if (accInitialState["scale"]!.GetValue<string>() != "150" ||
                        accInitialState["rotation"]!.GetValue<string>() != "30")
                        throw new InvalidOperationException("Accessory scale/rotation sliders did not keep the saved values: " + accInitial);
                    if (accInitialState["scaleText"]!.GetValue<string>() != "150%" ||
                        accInitialState["rotationText"]!.GetValue<string>() != "30°")
                        throw new InvalidOperationException("Accessory scale/rotation outputs are out of sync: " + accInitial);
                    if (accInitialState["resetDisabled"]!.GetValue<bool>())
                        throw new InvalidOperationException("Reset must be enabled when only scale/rotation differ from defaults.");

                    // 方向按钮只改 X/Y，缩放与旋转必须原样保留。
                    await core.ExecuteScriptAsync("document.querySelector('[data-nudge=\"0,-1\"]')?.click()");
                    await Task.Delay(500);
                    string accNudged = await core.ExecuteScriptAsync(@"
                      (() => {
                        const val = id => document.getElementById(id)?.value ?? '';
                        return JSON.stringify({
                          x: val('accOffsetX'), y: val('accOffsetY'), scale: val('accScale'), rotation: val('accRotation')
                        });
                      })()");
                    var accNudgedState = JsonNode.Parse(JsonSerializer.Deserialize<string>(accNudged))!.AsObject();
                    if (accNudgedState["x"]!.GetValue<string>() != "0" ||
                        accNudgedState["y"]!.GetValue<string>() != "-1" ||
                        accNudgedState["scale"]!.GetValue<string>() != "150" ||
                        accNudgedState["rotation"]!.GetValue<string>() != "30")
                        throw new InvalidOperationException("Direction nudge must only change X/Y: " + accNudged);

                    // 制造一个未提交的脏滑条值并立即点归零；节流定时器必须被取消，旧值不能再写回。
                    OffsetCommands.Clear();
                    await core.ExecuteScriptAsync(@"
                      (() => {
                        const el = document.getElementById('accOffsetX');
                        el.value = '77';
                        el.dispatchEvent(new Event('input', { bubbles: true }));
                        document.getElementById('resetAccessoryOffset')?.click();
                      })()");
                    await Task.Delay(700);
                    string accReset = await core.ExecuteScriptAsync(@"
                      (() => {
                        const val = id => document.getElementById(id)?.value ?? '';
                        const text = id => document.getElementById(id)?.textContent ?? '';
                        return JSON.stringify({
                          x: val('accOffsetX'), y: val('accOffsetY'), scale: val('accScale'), rotation: val('accRotation'),
                          xText: text('accOffsetXValue'), yText: text('accOffsetYValue'),
                          scaleText: text('accScaleValue'), rotationText: text('accRotationValue'),
                          chip: text('accessoryCoords'),
                          scaleMin: document.getElementById('accScale')?.min ?? '',
                          scaleMax: document.getElementById('accScale')?.max ?? '',
                          resetDisabled: !!document.getElementById('resetAccessoryOffset')?.disabled
                        });
                      })()");
                    var accResetState = JsonNode.Parse(JsonSerializer.Deserialize<string>(accReset))!.AsObject();
                    if (accResetState["x"]!.GetValue<string>() != "0" || accResetState["y"]!.GetValue<string>() != "0" ||
                        accResetState["scale"]!.GetValue<string>() != "100" || accResetState["rotation"]!.GetValue<string>() != "0")
                        throw new InvalidOperationException("Reset did not restore all four accessory sliders: " + accReset);
                    if (accResetState["xText"]!.GetValue<string>() != "0" || accResetState["yText"]!.GetValue<string>() != "0" ||
                        accResetState["scaleText"]!.GetValue<string>() != "100%" || accResetState["rotationText"]!.GetValue<string>() != "0°")
                        throw new InvalidOperationException("Reset did not synchronize accessory numeric outputs: " + accReset);
                    if (accResetState["scaleMin"]!.GetValue<string>() != "25" || accResetState["scaleMax"]!.GetValue<string>() != "175")
                        throw new InvalidOperationException("Scale slider must use the symmetric 25–175 range: " + accReset);
                    if ((int.Parse(accResetState["scale"]!.GetValue<string>()) - 25) * 2 != 175 - 25)
                        throw new InvalidOperationException("Reset scale thumb must sit exactly at the slider center: " + accReset);
                    if (!accResetState["chip"]!.GetValue<string>().Contains("100%") ||
                        !accResetState["chip"]!.GetValue<string>().Contains("0°"))
                        throw new InvalidOperationException("Reset did not synchronize the accessory summary chip: " + accReset);
                    if (!accResetState["resetDisabled"]!.GetValue<bool>())
                        throw new InvalidOperationException("Reset button must be disabled after all values return to defaults.");
                    if (OffsetCommands.Any(c => c.Contains("77", StringComparison.Ordinal)))
                        throw new InvalidOperationException("Reset left a queued old slider write that reached the native bridge: " + string.Join(",", OffsetCommands));

                    // 强制更新风险选项：forced 预览应提供“自担风险继续联网”入口，
                    // 且只有在 boot.settings 记录当前官方版本后才显示为已允许。
                    var forcedLoaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    core.NavigationCompleted += (_, ev) => forcedLoaded.TrySetResult(ev.IsSuccess);
                    core.Navigate("https://wuwatappo.local/index.html?preview=forced#page=settings");
                    if (!await forcedLoaded.Task) throw new InvalidOperationException("forced preview navigation failed.");
                    await Task.Delay(800);
                    string forcedBootJson = JsonSerializer.Deserialize<string>(
                        await core.ExecuteScriptAsync("JSON.stringify(window.__previewBoot(window.__previewMode))"));
                    var forcedBoot = JsonNode.Parse(forcedBootJson) as JsonObject
                        ?? throw new InvalidOperationException("forced preview boot missing");
                    core.PostWebMessageAsJson(JsonSerializer.Serialize(new { evt = "boot", data = forcedBoot }));
                    await Task.Delay(700);
                    string forcedUi = await core.ExecuteScriptAsync(@"
                      (() => JSON.stringify({
                        notice: document.querySelector('.official-notice-title')?.textContent || '',
                        allow: document.querySelector('[data-official-allow]')?.textContent || '',
                        setting: document.getElementById('forcedOnlineSettingBtn')?.textContent || ''
                      }))()");
                    var forcedState = JsonNode.Parse(JsonSerializer.Deserialize<string>(forcedUi))!.AsObject();
                    if (!forcedState["notice"]!.GetValue<string>().Contains("强制", StringComparison.Ordinal))
                        throw new InvalidOperationException("Forced update notice was not rendered.");
                    if (forcedState["allow"]!.GetValue<string>() != "仍要联网（自担风险）")
                        throw new InvalidOperationException("Forced update notice is missing the risk opt-in button.");
                    if (!forcedState["setting"]!.GetValue<string>().Contains("仍要联网", StringComparison.Ordinal))
                        throw new InvalidOperationException("Forced update setting entry was not rendered.");

                    await core.ExecuteScriptAsync("document.querySelector('[data-official-allow]').click()");
                    await Task.Delay(300);
                    string riskModal = await core.ExecuteScriptAsync("document.getElementById('modalHost')?.textContent || ''");
                    string riskModalText = JsonSerializer.Deserialize<string>(riskModal) ?? "";
                    if (!riskModalText.Contains("强制更新后继续联网", StringComparison.Ordinal)
                        || !riskModalText.Contains("我已了解", StringComparison.Ordinal))
                        throw new InvalidOperationException("Forced update risk confirmation modal was not rendered.");
                    await core.ExecuteScriptAsync("document.getElementById('forcedOnlineCancel')?.click()");
                    await Task.Delay(200);

                    forcedBoot["settings"]!["forcedOnlineAcceptedVersion"] = "1.0.5";
                    core.PostWebMessageAsJson(JsonSerializer.Serialize(new { evt = "boot", data = forcedBoot }));
                    await Task.Delay(600);
                    string acceptedUi = await core.ExecuteScriptAsync(@"
                      (() => JSON.stringify({
                        notice: document.querySelector('.official-notice-title')?.textContent || '',
                        setting: document.getElementById('forcedOnlineSettingBtn')?.textContent || '',
                        blocked: (document.getElementById('pageHost')?.textContent || '').includes('已暂停联网操作')
                      }))()");
                    var acceptedState = JsonNode.Parse(JsonSerializer.Deserialize<string>(acceptedUi))!.AsObject();
                    if (!acceptedState["notice"]!.GetValue<string>().Contains("已允许", StringComparison.Ordinal))
                        throw new InvalidOperationException("Accepted forced-update state was not rendered.");
                    if (!acceptedState["setting"]!.GetValue<string>().Contains("撤销风险授权", StringComparison.Ordinal))
                        throw new InvalidOperationException("Accepted forced-update setting entry did not switch to revoke mode.");
                    if (acceptedState["blocked"]!.GetValue<bool>())
                        throw new InvalidOperationException("Accepted forced-update state still shows the network-blocked banner.");

                    // 高级合成已移除：隐藏指令只显示纪念弹窗，不出现任何解锁/面板/二次确认。
                    var synthLoaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    core.NavigationCompleted += (_, ev) => synthLoaded.TrySetResult(ev.IsSuccess);
                    core.Navigate("https://wuwatappo.local/index.html?preview=synth#page=synth");
                    if (!await synthLoaded.Task) throw new InvalidOperationException("synth preview navigation failed.");
                    await Task.Delay(800);
                    string synthBootJson = JsonSerializer.Deserialize<string>(
                        await core.ExecuteScriptAsync("JSON.stringify(window.__previewBoot(window.__previewMode))"));
                    var synthBoot = JsonNode.Parse(synthBootJson) as JsonObject
                        ?? throw new InvalidOperationException("synth preview boot missing");
                    boot = synthBoot;
                    core.PostWebMessageAsJson(JsonSerializer.Serialize(new { evt = "boot", data = synthBoot }));
                    await Task.Delay(700);

                    const string konamiScript = @"
                      (() => {
                        const keys = ['ArrowUp','ArrowUp','ArrowDown','ArrowDown','ArrowLeft','ArrowRight','ArrowLeft','ArrowRight','b','a','b','a'];
                        for (const key of keys) document.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true }));
                        return true;
                      })()";
                    await core.ExecuteScriptAsync(konamiScript);
                    await Task.Delay(500);
                    string firstMemorial = JsonSerializer.Deserialize<string>(
                        await core.ExecuteScriptAsync("document.getElementById('modalHost')?.textContent || ''")) ?? "";
                    if (!firstMemorial.Contains("人生自古谁无死？不幸的，高级合成已经被牢库暗杀，无法再为您服务，让我们一起缅怀它", StringComparison.Ordinal)
                        || !firstMemorial.Contains("确定", StringComparison.Ordinal))
                        throw new InvalidOperationException("Advanced synth memorial modal did not show the exact Chinese text.");
                    string advancedPanelExists = JsonSerializer.Deserialize<string>(
                        await core.ExecuteScriptAsync("String(!!document.querySelector('.synth-advanced-panel'))")) ?? "";
                    if (advancedPanelExists == "true")
                        throw new InvalidOperationException("Advanced synth panel must not exist after removal.");
                    await core.ExecuteScriptAsync("document.getElementById('synthMemorialOk')?.click()");
                    await Task.Delay(300);
                    string memorialHidden = JsonSerializer.Deserialize<string>(
                        await core.ExecuteScriptAsync("String(document.getElementById('modalHost')?.classList.contains('hidden'))")) ?? "";
                    if (memorialHidden != "true")
                        throw new InvalidOperationException("Memorial modal did not close after clicking 确定.");

                    await core.ExecuteScriptAsync(konamiScript);
                    await Task.Delay(500);
                    string secondMemorial = JsonSerializer.Deserialize<string>(
                        await core.ExecuteScriptAsync("document.getElementById('modalHost')?.textContent || ''")) ?? "";
                    if (!secondMemorial.Contains("人生自古谁无死", StringComparison.Ordinal))
                        throw new InvalidOperationException("Advanced synth memorial did not reappear on a repeated sequence.");
                    await core.ExecuteScriptAsync("document.getElementById('synthMemorialOk')?.click()");
                    await Task.Delay(200);

                    // 登录态异常：顶部常驻条、桌宠统一“未登录”、暂停页和重新连接入口。
                    synthBoot["auth"]!["state"] = "conflict";
                    synthBoot["auth"]!["loggedIn"] = true;
                    synthBoot["auth"]!["onlineAvailable"] = false;
                    synthBoot["auth"]!["stateMessage"] = "登录态正在其他客户端使用；账号功能已暂停。";
                    core.PostWebMessageAsJson(JsonSerializer.Serialize(new { evt = "boot", data = synthBoot }));
                    await Task.Delay(700);
                    string conflictUi = await core.ExecuteScriptAsync(@"
                      (() => JSON.stringify({
                        notice: document.querySelector('.auth-notice .official-notice-title')?.textContent || '',
                        chip: document.getElementById('userChip')?.textContent || '',
                        paused: !!document.querySelector('.auth-paused-card'),
                        reconnect: !!document.querySelector('[data-auth-reconnect]')
                      }))()");
                    var conflictState = JsonNode.Parse(JsonSerializer.Deserialize<string>(conflictUi))!.AsObject();
                    if (!conflictState["notice"]!.GetValue<string>().Contains("其他客户端", StringComparison.Ordinal)
                        || conflictState["chip"]!.GetValue<string>() != "未登录"
                        || !conflictState["paused"]!.GetValue<bool>()
                        || !conflictState["reconnect"]!.GetValue<bool>())
                        throw new InvalidOperationException("Auth conflict UI did not render the persistent notice/paused page/reconnect action.");

                    synthBoot["auth"]!["state"] = "invalid";
                    synthBoot["auth"]!["loggedIn"] = false;
                    synthBoot["auth"]!["onlineAvailable"] = false;
                    synthBoot["auth"]!["stateMessage"] = "当前登录凭据已失效；请重新登录。";
                    core.PostWebMessageAsJson(JsonSerializer.Serialize(new { evt = "boot", data = synthBoot }));
                    await Task.Delay(700);
                    string invalidUi = await core.ExecuteScriptAsync(@"
                      (() => JSON.stringify({
                        notice: document.querySelector('.auth-notice .official-notice-title')?.textContent || '',
                        chip: document.getElementById('userChip')?.textContent || '',
                        login: !!document.getElementById('loginUid')
                      }))()");
                    var invalidState = JsonNode.Parse(JsonSerializer.Deserialize<string>(invalidUi))!.AsObject();
                    if (!invalidState["notice"]!.GetValue<string>().Contains("失效", StringComparison.Ordinal)
                        || invalidState["chip"]!.GetValue<string>() != "未登录"
                        || !invalidState["login"]!.GetValue<bool>())
                        throw new InvalidOperationException("Auth invalid UI did not switch to login with the credential-expired notice.");

                    synthBoot["auth"]!["state"] = "valid";
                    synthBoot["auth"]!["loggedIn"] = true;
                    synthBoot["auth"]!["onlineAvailable"] = true;
                    synthBoot["auth"]!["stateMessage"] = "";
                    core.PostWebMessageAsJson(JsonSerializer.Serialize(new { evt = "boot", data = synthBoot }));
                    await Task.Delay(700);
                    string validUi = await core.ExecuteScriptAsync(@"
                      (() => JSON.stringify({
                        notice: !!document.querySelector('.auth-notice'),
                        chip: document.getElementById('userChip')?.textContent || '',
                        paused: !!document.querySelector('.auth-paused-card')
                      }))()");
                    var validState = JsonNode.Parse(JsonSerializer.Deserialize<string>(validUi))!.AsObject();
                    if (validState["notice"]!.GetValue<bool>() || validState["paused"]!.GetValue<bool>()
                        || validState["chip"]!.GetValue<string>() != "Daniel兔兔")
                        throw new InvalidOperationException("Auth recovery did not clear the pause UI and restore the nickname chip.");

                    // 背包收藏置顶：角色/配件/表情分类里，收藏物品应进入最上方的“收藏”组。
                    var bagLoaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    core.NavigationCompleted += (_, ev) => bagLoaded.TrySetResult(ev.IsSuccess);
                    core.Navigate("https://wuwatappo.local/index.html?preview=bag-emote#page=bag");
                    if (!await bagLoaded.Task) throw new InvalidOperationException("bag preview navigation failed.");
                    await Task.Delay(900);
                    string bagBootJson = JsonSerializer.Deserialize<string>(
                        await core.ExecuteScriptAsync("JSON.stringify(window.__previewBoot(window.__previewMode))"));
                    var bagBoot = JsonNode.Parse(bagBootJson) as JsonObject
                        ?? throw new InvalidOperationException("bag preview boot missing");
                    boot = bagBoot;
                    core.PostWebMessageAsJson(JsonSerializer.Serialize(new { evt = "boot", data = bagBoot }));
                    await Task.Delay(900);
                    string beforeFav = await core.ExecuteScriptAsync(@"
                      (() => JSON.stringify({
                        groups: [...document.querySelectorAll('.bag-group-head')].map(x => (x.textContent || '').replace(/\s+/g, ' ').trim()),
                        cards: [...document.querySelectorAll('.card')].map(x => x.dataset.id)
                      }))()");
                    await core.ExecuteScriptAsync("document.querySelector('[data-id=\"201\"] .fav')?.click()");
                    await Task.Delay(500);
                    string afterFav = await core.ExecuteScriptAsync(@"
                      (() => JSON.stringify({
                        groups: [...document.querySelectorAll('.bag-group-head')].map(x => (x.textContent || '').replace(/\s+/g, ' ').trim()),
                        cards: [...document.querySelectorAll('.card')].map(x => x.dataset.id),
                        star: document.querySelector('[data-id=""201""] .fav')?.textContent || ''
                      }))()");
                    var beforeBagState = JsonNode.Parse(JsonSerializer.Deserialize<string>(beforeFav)!)!.AsObject();
                    var afterBagState = JsonNode.Parse(JsonSerializer.Deserialize<string>(afterFav)!)!.AsObject();
                    var beforeGroups = beforeBagState["groups"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
                    var afterGroups = afterBagState["groups"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
                    if (beforeGroups.Length == 0 || afterGroups.Length == 0 || !afterGroups[0].StartsWith("收藏", StringComparison.Ordinal)
                        || !afterBagState["cards"]!.AsArray().Any(x => x!.GetValue<string>() == "201")
                        || afterBagState["star"]!.GetValue<string>() != "★")
                        throw new InvalidOperationException("Bag favorite must move the item into the top 收藏 group.");
                    Console.WriteLine("PASS: bag favorite moves the item into the top 收藏 group for character/accessory/emote lists.");

                    Directory.CreateDirectory(Path.GetDirectoryName(output) ?? ".");
                    using (var stream = File.Create(output))
                        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);

                    // 公告历史预览：演示数据齐全、自动弹窗、日期倒序，并验证空状态/无效日期排序。
                    var historyLoaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    core.NavigationCompleted += (_, ev) => historyLoaded.TrySetResult(ev.IsSuccess);
                    core.Navigate("https://wuwatappo.local/index.html?preview=announcement-history#page=settings");
                    if (!await historyLoaded.Task) throw new InvalidOperationException("announcement-history preview navigation failed.");
                    await Task.Delay(1200);
                    string historyUi = await core.ExecuteScriptAsync(@"
                      (() => {
                        const modal = document.querySelector('.announcement-history-modal');
                        const items = [...document.querySelectorAll('.announcement-history-item')];
                        const dates = [...document.querySelectorAll('.announcement-history-date')].map(x => x.textContent.trim());
                        const severities = items.map(x => ['info', 'warning', 'critical'].find(c => x.classList.contains(c)) || '');
                        const actions = [...document.querySelectorAll('.announcement-history-actions .btn')];
                        const firstBody = document.querySelector('.announcement-history-body');
                        return JSON.stringify({
                          modal: !!modal,
                          count: items.length,
                          dates,
                          severities,
                          actionCount: actions.length,
                          actionTargets: actions.map(a => a.getAttribute('target') || ''),
                          actionRels: actions.map(a => a.getAttribute('rel') || ''),
                          close: !!document.getElementById('mCloseAnnouncementHistory'),
                          footerIds: [...document.querySelectorAll('.settings-footer button')].map(b => b.id),
                          footerVisible: !!document.querySelector('.settings-footer #announcementHistoryBtn'),
                          unread: !!document.querySelector('.announcement-history-unread'),
                          whiteSpace: firstBody ? getComputedStyle(firstBody).whiteSpace : '',
                          longBody: firstBody ? firstBody.textContent.length : 0,
                          newestTitle: document.querySelector('.announcement-history-title')?.textContent || ''
                        });
                      })()");
                    var historyState = JsonNode.Parse(JsonSerializer.Deserialize<string>(historyUi)!)!.AsObject();
                    if (!historyState["modal"]!.GetValue<bool>())
                        throw new InvalidOperationException("Announcement-history preview did not auto-open the modal.");
                    if (historyState["count"]!.GetValue<int>() < 5)
                        throw new InvalidOperationException("Announcement-history preview must show at least 5 demo entries.");
                    var historyDates = historyState["dates"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
                    if (historyDates.Any(d => !System.Text.RegularExpressions.Regex.IsMatch(d, @"^\d{4}-\d{2}-\d{2}$")))
                        throw new InvalidOperationException("Announcement history contains an unformatted date: " + string.Join(",", historyDates));
                    for (int i = 1; i < historyDates.Length; i++)
                        if (string.CompareOrdinal(historyDates[i - 1], historyDates[i]) <= 0)
                            throw new InvalidOperationException("Announcement history is not sorted newest first: " + string.Join(",", historyDates));
                    var historySeverities = historyState["severities"]!.AsArray().Select(x => x!.GetValue<string>()).ToHashSet();
                    if (!historySeverities.Contains("info") || !historySeverities.Contains("warning") || !historySeverities.Contains("critical"))
                        throw new InvalidOperationException("Announcement-history demo must cover info/warning/critical.");
                    if (historyState["actionCount"]!.GetValue<int>() < 1)
                        throw new InvalidOperationException("Announcement-history demo is missing an action button.");
                    var actionTargets = historyState["actionTargets"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
                    var actionRels = historyState["actionRels"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
                    if (actionTargets.Any(t => t != "_blank") || actionRels.Any(r => !r.Contains("noreferrer", StringComparison.Ordinal)))
                        throw new InvalidOperationException("Announcement-history action buttons must open in a new window with noreferrer.");
                    if (!historyState["close"]!.GetValue<bool>())
                        throw new InvalidOperationException("Announcement-history modal is missing the bottom close button.");
                    var historyFooterIds = historyState["footerIds"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
                    if (!historyFooterIds.SequenceEqual(new[] { "checkUpdateBtn", "compatRefreshBtn", "announcementHistoryBtn", "aboutBtn" })
                        || !historyState["footerVisible"]!.GetValue<bool>())
                        throw new InvalidOperationException("Settings footer disappeared behind the announcement-history modal.");
                    if (!historyState["unread"]!.GetValue<bool>())
                        throw new InvalidOperationException("Current announcement should be marked as unread in history.");
                    if (historyState["whiteSpace"]!.GetValue<string>() != "pre-wrap")
                        throw new InvalidOperationException("Announcement body must preserve line breaks with white-space: pre-wrap.");
                    if (historyState["longBody"]!.GetValue<int>() < 100)
                        throw new InvalidOperationException("Announcement-history demo should include a long body.");
                    if (!historyState["newestTitle"]!.GetValue<string>().Contains("0.1.8", StringComparison.Ordinal))
                        throw new InvalidOperationException("Newest announcement is not shown first: " + historyState["newestTitle"]);
                    Console.WriteLine("PASS: announcement-history preview auto-opens with " + historyState["count"]!.GetValue<int>() + " sorted entries, actions and close button.");

                    // 空状态：清空历史与当前公告后，入口仍可点击并显示“暂无公告历史”。
                    await core.ExecuteScriptAsync("document.getElementById('mCloseAnnouncementHistory')?.click()");
                    await Task.Delay(200);
                    string historyBootJson = JsonSerializer.Deserialize<string>(
                        await core.ExecuteScriptAsync("JSON.stringify(window.__previewBoot(window.__previewMode))"));
                    var historyBoot = JsonNode.Parse(historyBootJson) as JsonObject
                        ?? throw new InvalidOperationException("announcement-history preview boot missing");
                    historyBoot["announcements"] = new JsonArray();
                    historyBoot["announcementHistory"] = new JsonArray();
                    core.PostWebMessageAsJson(JsonSerializer.Serialize(new { evt = "boot", data = historyBoot }));
                    await Task.Delay(600);
                    await core.ExecuteScriptAsync("document.getElementById('announcementHistoryBtn')?.click()");
                    await Task.Delay(150);
                    string emptyHistoryUi = await core.ExecuteScriptAsync(@"
                      (() => JSON.stringify({
                        items: document.querySelectorAll('.announcement-history-item').length,
                        text: document.querySelector('.announcement-history-empty-title')?.textContent || '',
                        close: !!document.getElementById('mCloseAnnouncementHistory')
                      }))()");
                    var emptyHistoryState = JsonNode.Parse(JsonSerializer.Deserialize<string>(emptyHistoryUi)!)!.AsObject();
                    if (emptyHistoryState["items"]!.GetValue<int>() != 0
                        || emptyHistoryState["text"]!.GetValue<string>() != "暂无公告历史"
                        || !emptyHistoryState["close"]!.GetValue<bool>())
                        throw new InvalidOperationException("Announcement-history empty state was not rendered correctly.");
                    Console.WriteLine("PASS: announcement-history empty state renders 暂无公告历史 with a close button.");
                    await core.ExecuteScriptAsync("document.getElementById('mCloseAnnouncementHistory')?.click()");
                    await Task.Delay(200);

                    // 实时推送：C# 刷新公告后会单独发 announcementHistory 事件，不等下一次 boot。
                    core.PostWebMessageAsJson(JsonSerializer.Serialize(new
                    {
                        evt = "announcementHistory",
                        data = new[]
                        {
                            new
                            {
                                id = "push-1",
                                title = "实时推送公告",
                                body = "来自 announcementHistory 事件",
                                severity = "warning",
                                publishedAt = "2026-11-16T12:00:00+08:00",
                                action = new { label = "查看详情", url = "https://github.com/DanielToyama/WuwaTappoNative" },
                            },
                        },
                    }));
                    await Task.Delay(400);
                    await core.ExecuteScriptAsync("document.getElementById('announcementHistoryBtn')?.click()");
                    await Task.Delay(150);
                    string pushedHistoryUi = await core.ExecuteScriptAsync(@"
                      (() => JSON.stringify({
                        count: document.querySelectorAll('.announcement-history-item').length,
                        title: document.querySelector('.announcement-history-title')?.textContent || '',
                        body: document.querySelector('.announcement-history-body')?.textContent || ''
                      }))()");
                    var pushedHistoryState = JsonNode.Parse(JsonSerializer.Deserialize<string>(pushedHistoryUi)!)!.AsObject();
                    if (pushedHistoryState["count"]!.GetValue<int>() != 1
                        || pushedHistoryState["title"]!.GetValue<string>() != "实时推送公告")
                        throw new InvalidOperationException("announcementHistory event did not refresh the board modal.");
                    Console.WriteLine("PASS: announcementHistory event updates the board without a new boot.");
                    await core.ExecuteScriptAsync("document.getElementById('mCloseAnnouncementHistory')?.click()");
                    await Task.Delay(200);

                    // 容错排序：publishedAt 无效的条目必须排在最后，且不能抛异常。
                    historyBoot["announcementHistory"] = new JsonArray
                    {
                        new JsonObject { ["id"] = "valid-old", ["severity"] = "info", ["publishedAt"] = "2026-01-05T12:00:00+08:00", ["title"] = "旧公告", ["body"] = "旧公告正文" },
                        new JsonObject { ["id"] = "invalid-date", ["severity"] = "warning", ["publishedAt"] = "not-a-date", ["title"] = "日期缺失", ["body"] = "日期缺失正文" },
                        new JsonObject { ["id"] = "valid-new", ["severity"] = "critical", ["publishedAt"] = "2026-12-01T12:00:00+08:00", ["title"] = "新公告", ["body"] = "新公告正文" },
                    };
                    core.PostWebMessageAsJson(JsonSerializer.Serialize(new { evt = "boot", data = historyBoot }));
                    await Task.Delay(600);
                    await core.ExecuteScriptAsync("document.getElementById('announcementHistoryBtn')?.click()");
                    await Task.Delay(150);
                    string invalidHistoryUi = await core.ExecuteScriptAsync(@"
                      (() => JSON.stringify({
                        ids: [...document.querySelectorAll('.announcement-history-item')].map(x => x.dataset.annId),
                        dates: [...document.querySelectorAll('.announcement-history-date')].map(x => x.textContent.trim()),
                        error: window.__historySortError || ''
                      }))()");
                    var invalidHistoryState = JsonNode.Parse(JsonSerializer.Deserialize<string>(invalidHistoryUi)!)!.AsObject();
                    var invalidIds = invalidHistoryState["ids"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
                    var invalidDates = invalidHistoryState["dates"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
                    if (!invalidIds.SequenceEqual(new[] { "valid-new", "valid-old", "invalid-date" })
                        || invalidDates[2] != "时间未知")
                        throw new InvalidOperationException("Invalid publishedAt entries must sort last: " + string.Join(",", invalidIds));
                    Console.WriteLine("PASS: announcement-history sorts invalid publishedAt entries last.");
                    await core.ExecuteScriptAsync("document.getElementById('mCloseAnnouncementHistory')?.click()");
                    await Task.Delay(200);
                    Console.WriteLine("PASS: WebView2 bar switches render and single/separate hotkey rows filter without losing bindings.");
                    Console.WriteLine("PASS: WebView2 theme preset list/apply/rename/delete uses real C# data and persists deletes.");
                    Console.WriteLine("PASS: WebView2 accessory scale/rotation, nudge and reset keep native boot and sliders in sync.");
                    Console.WriteLine("PASS: WebView2 forced-update risk opt-in notice, modal and accepted state render correctly.");
                    Console.WriteLine("PASS: WebView2 advanced-synth removal shows only a repeated memorial modal.");
                    Console.WriteLine("PASS: About button sits in the settings footer and the data-statistics notice is present.");
                    Console.WriteLine("PASS: WebView2 auth conflict/invalid states render persistent notices, paused view and login view.");

                    // 自定义伙伴：联机与设置之间的独立页面、名称截断、添加/编辑/删除。
                    var companionLoaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    core.NavigationCompleted += (_, ev) => companionLoaded.TrySetResult(ev.IsSuccess);
                    core.Navigate("https://wuwatappo.local/index.html?preview=companion#page=companion");
                    if (!await companionLoaded.Task) throw new InvalidOperationException("companion preview navigation failed.");
                    await Task.Delay(900);
                    string companionBootJson = JsonSerializer.Deserialize<string>(
                        await core.ExecuteScriptAsync("JSON.stringify(window.__previewBoot(window.__previewMode))"));
                    var companionBoot = JsonNode.Parse(companionBootJson) as JsonObject
                        ?? throw new InvalidOperationException("companion preview boot missing");
                    boot = companionBoot;
                    core.PostWebMessageAsJson(JsonSerializer.Serialize(new { evt = "boot", data = companionBoot }));
                    await Task.Delay(500);
                    await core.ExecuteScriptAsync("document.querySelector('.tab[data-page=\"companion\"]')?.click()");
                    await Task.Delay(500);
                    string companionUi = await core.ExecuteScriptAsync(@"
                      (() => JSON.stringify({
                        tabs: [...document.querySelectorAll('.tab')].map(x => x.dataset.page),
                        tabText: [...document.querySelectorAll('.tab span')].map(x => x.textContent.trim()),
                        title: document.getElementById('pageTitle')?.textContent || '',
                        cards: [...document.querySelectorAll('.companion-card')].map(x => ({
                          name: x.querySelector('.companion-name')?.textContent || '',
                          full: x.querySelector('.companion-fullname')?.textContent || ''
                        }))
                      }))()");
                    var companionState = JsonNode.Parse(JsonSerializer.Deserialize<string>(companionUi)!)!.AsObject();
                    var companionTabs = companionState["tabs"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
                    if (Array.IndexOf(companionTabs, "companion") < 0
                        || Array.IndexOf(companionTabs, "room") > Array.IndexOf(companionTabs, "companion")
                        || Array.IndexOf(companionTabs, "companion") > Array.IndexOf(companionTabs, "settings")
                        || companionState["title"]!.GetValue<string>() != "自定义伙伴"
                        || !companionState["tabText"]!.AsArray().Any(x => x!.GetValue<string>() == "自定义伙伴"))
                        throw new InvalidOperationException("Custom companion page must sit between multiplayer and settings: " + companionUi);
                    var companionCard = companionState["cards"]!.AsArray().FirstOrDefault();
                    if (companionCard == null)
                    {
                        string debug = JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync(
                            "JSON.stringify({host:document.getElementById('pageHost')?.innerHTML?.slice(0,500),errors:window.__uiSmokeErrors||[]})")) ?? "";
                        throw new InvalidOperationException("Companion preview demo card missing: " + debug);
                    }
                    if (companionCard["name"]!.GetValue<string>() != "菲比"
                        || companionCard["full"]!.GetValue<string>() != "")
                        throw new InvalidOperationException("Companion list must show only the part before the middle dot: " + companionUi);

                    await core.ExecuteScriptAsync("document.getElementById('companionAdd')?.click()");
                    await Task.Delay(700);
                    string editorUi = await core.ExecuteScriptAsync(@"
                      (() => JSON.stringify({
                        title: document.querySelector('.preset-edit .preset-title')?.textContent || '',
                        hasName: !!document.getElementById('peName'),
                        hasClick: !!document.getElementById('peClickSeconds'),
                        hasRandom: !!document.getElementById('peRandomClick'),
                        hasBar: !!document.getElementById('peShowBar'),
                        hasEmoteTab: !!document.querySelector('[data-pe-tab=""emote""]'),
                        wide: !!document.querySelector('.modal.preset-edit-modal')
                      }))()");
                    var editorState = JsonNode.Parse(JsonSerializer.Deserialize<string>(editorUi)!)!.AsObject();
                    if (!editorState["title"]!.GetValue<string>().Contains("添加自定义伙伴", StringComparison.Ordinal)
                        || !editorState["hasName"]!.GetValue<bool>() || !editorState["hasClick"]!.GetValue<bool>()
                        || !editorState["hasRandom"]!.GetValue<bool>() || !editorState["hasBar"]!.GetValue<bool>()
                        || editorState["hasEmoteTab"]!.GetValue<bool>() || !editorState["wide"]!.GetValue<bool>())
                        throw new InvalidOperationException("Companion editor did not reuse the preset picker/options: " + editorUi);
                    string pickerOrderUi = await core.ExecuteScriptAsync(@"
                      (() => JSON.stringify({
                        groups: [...document.querySelectorAll('#peGrid [data-pe-rarity-head]')].map(x => x.dataset.peRarityHead),
                        cards: [...document.querySelectorAll('#peGrid [data-pe-type=""character_skin""]')].map(x => x.dataset.peRarity)
                      }))()");
                    var pickerOrder = JsonNode.Parse(JsonSerializer.Deserialize<string>(pickerOrderUi)!)!.AsObject();
                    var pickerGroups = pickerOrder["groups"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
                    var pickerCards = pickerOrder["cards"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
                    if (!pickerGroups.SequenceEqual(new[] { "epic", "rare", "common" })
                        || !pickerCards.SequenceEqual(new[] { "epic", "rare", "rare", "common" }))
                        throw new InvalidOperationException("Preset/companion picker must group by rarity from high to low: " + pickerOrderUi);
                    await core.ExecuteScriptAsync(@"
                      (() => {
                        document.getElementById('peName').value = '嘉贝莉娜·团团团团转';
                        document.getElementById('peClickSeconds').value = '7';
                        document.getElementById('peRandomClick').checked = true;
                        document.getElementById('peShowBar').checked = true;
                        document.getElementById('peSave').click();
                      })()");
                    await Task.Delay(900);
                    string addedUi = await core.ExecuteScriptAsync(@"
                      (() => JSON.stringify({
                        count: document.querySelectorAll('.companion-card').length,
                        names: [...document.querySelectorAll('.companion-name')].map(x => x.textContent.trim())
                      }))()");
                    var addedState = JsonNode.Parse(JsonSerializer.Deserialize<string>(addedUi)!)!.AsObject();
                    if (addedState["count"]!.GetValue<int>() != 2
                        || !addedState["names"]!.AsArray().Any(x => x!.GetValue<string>() == "嘉贝莉娜"))
                        throw new InvalidOperationException("Adding a companion did not persist/display the truncated name: " + addedUi);

                    await core.ExecuteScriptAsync("document.querySelectorAll('[data-companion-delete]')[1]?.click()");
                    await Task.Delay(300);
                    await core.ExecuteScriptAsync("document.getElementById('roomActionContinue')?.click()");
                    await Task.Delay(700);
                    string deletedUi = await core.ExecuteScriptAsync("String(document.querySelectorAll('.companion-card').length)");
                    if (JsonSerializer.Deserialize<string>(deletedUi) != "1")
                        throw new InvalidOperationException("Deleting a companion did not remove the card.");
                    Console.WriteLine("PASS: WebView2 custom companion page is ordered between multiplayer/settings and supports add/delete with name truncation.");

                    // 外观轮换：背包顶部子页、角色/配件分区、开关/间隔、卡片按钮和未拥有图鉴条目。
                    var rotationLoaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    core.NavigationCompleted += (_, ev) => rotationLoaded.TrySetResult(ev.IsSuccess);
                    core.Navigate("https://wuwatappo.local/index.html?preview=bag-rotation#page=bag");
                    if (!await rotationLoaded.Task) throw new InvalidOperationException("bag rotation preview navigation failed.");
                    await Task.Delay(1000);
                    string rotationBootJson = JsonSerializer.Deserialize<string>(
                        await core.ExecuteScriptAsync("JSON.stringify(window.__previewBoot(window.__previewMode))"));
                    var rotationBoot = JsonNode.Parse(rotationBootJson) as JsonObject
                        ?? throw new InvalidOperationException("bag rotation preview boot missing");
                    boot = rotationBoot;
                    core.PostWebMessageAsJson(JsonSerializer.Serialize(new { evt = "boot", data = rotationBoot }));
                    await Task.Delay(800);
                    string rotationUi = await core.ExecuteScriptAsync(@"
                      (() => JSON.stringify({
                        lastTab: [...document.querySelectorAll('[data-btab]')].pop()?.dataset.btab || '',
                        tabText: document.querySelector('[data-btab=""rotation""]')?.textContent.trim() || '',
                        sections: document.querySelectorAll('.rotation-section').length,
                        items: document.querySelectorAll('.rotation-item').length,
                        enabled: !!document.getElementById('rotationEnabled')?.checked,
                        interval: document.getElementById('rotationInterval')?.value || '',
                        hint: document.querySelector('.rotation-hint')?.textContent || '',
                        errors: window.__uiSmokeErrors || []
                      }))()");
                    var rotationState = JsonNode.Parse(JsonSerializer.Deserialize<string>(rotationUi)!)!.AsObject();
                    if (rotationState["lastTab"]!.GetValue<string>() != "rotation"
                        || !rotationState["tabText"]!.GetValue<string>().Contains("轮换", StringComparison.Ordinal)
                        || rotationState["sections"]!.GetValue<int>() != 2
                        || rotationState["items"]!.GetValue<int>() != 4
                        || !rotationState["enabled"]!.GetValue<bool>()
                        || rotationState["interval"]!.GetValue<string>() != "30"
                        || !rotationState["hint"]!.GetValue<string>().Contains("图鉴", StringComparison.Ordinal)
                        || !rotationState["hint"]!.GetValue<string>().Contains("未拥有", StringComparison.Ordinal)
                        || rotationState["errors"]!.AsArray().Count > 0)
                        throw new InvalidOperationException("Bag rotation page did not render the expected toolbar/sections/hint: " + rotationUi);
                    Console.WriteLine("PASS: bag rotation tab renders two stacked sections, enable/interval toolbar and gallery hint.");

                    await core.ExecuteScriptAsync(@"
                      (() => {
                        const el = document.getElementById('rotationInterval');
                        el.value = '45';
                        el.dispatchEvent(new Event('change', { bubbles: true }));
                      })()");
                    await Task.Delay(700);
                    if (boot["rotation"]?["intervalMinutes"]?.GetValue<int>() != 45)
                        throw new InvalidOperationException("Rotation interval change did not reach the bridge.");
                    string rotationIntervalUi = JsonSerializer.Deserialize<string>(
                        await core.ExecuteScriptAsync("document.getElementById('rotationInterval')?.value || ''")) ?? "";
                    if (rotationIntervalUi != "45")
                        throw new InvalidOperationException("Rotation interval did not refresh after saving: " + rotationIntervalUi);

                    await core.ExecuteScriptAsync("document.querySelector('[data-btab=\"character_skin\"]')?.click()");
                    await Task.Delay(700);
                    string bagRotationCards = await core.ExecuteScriptAsync(@"
                      (() => JSON.stringify({
                        has3060: !!document.querySelector('#bagGroups .card[data-id=""3060""] .rotation-toggle'),
                        active3060: !!document.querySelector('#bagGroups .card[data-id=""3060""] .rotation-toggle.active'),
                        has3062: !!document.querySelector('#bagGroups .card[data-id=""3062""] .rotation-toggle'),
                        hasEmoteButton: !!document.querySelector('#bagGroups .card[data-id=""201""] .rotation-toggle'),
                        lastActionRotation: (() => {
                          const card = document.querySelector('#bagGroups .card[data-id=""3060""]');
                          const actions = card ? [...card.querySelectorAll('.fav, .rotation-toggle')] : [];
                          return actions.length > 0 && actions[actions.length - 1].classList.contains('rotation-toggle');
                        })()
                      }))()");
                    var bagRotation = JsonNode.Parse(JsonSerializer.Deserialize<string>(bagRotationCards)!)!.AsObject();
                    if (!bagRotation["has3060"]!.GetValue<bool>() || !bagRotation["active3060"]!.GetValue<bool>()
                        || !bagRotation["has3062"]!.GetValue<bool>() || bagRotation["hasEmoteButton"]!.GetValue<bool>()
                        || !bagRotation["lastActionRotation"]!.GetValue<bool>())
                        throw new InvalidOperationException("Bag character cards must show active/inactive rotation buttons: " + bagRotationCards);

                    await core.ExecuteScriptAsync("document.querySelector('#bagGroups .card[data-id=\"3062\"] .rotation-toggle')?.click()");
                    await Task.Delay(700);
                    string bagToggled = await core.ExecuteScriptAsync("String(!!document.querySelector('#bagGroups .card[data-id=\"3062\"] .rotation-toggle.active'))");
                    if (JsonSerializer.Deserialize<string>(bagToggled) != "true")
                        throw new InvalidOperationException("Bag rotation button did not toggle active state.");
                    if (!((boot["rotation"]?["characterIds"] as JsonArray)?.Any(x => x!.GetValue<long>() == 3062) ?? false))
                        throw new InvalidOperationException("Bag rotation toggle did not reach the bridge.");

                    await core.ExecuteScriptAsync("document.querySelector('[data-btab=\"emote\"]')?.click()");
                    await Task.Delay(500);
                    string emoteRotation = await core.ExecuteScriptAsync("String(!!document.querySelector('#bagGroups .rotation-toggle'))");
                    if (JsonSerializer.Deserialize<string>(emoteRotation) != "false")
                        throw new InvalidOperationException("Emote bag cards must not show rotation buttons.");
                    Console.WriteLine("PASS: bag cards expose rotation toggles for characters/accessories only, with active state.");

                    await core.ExecuteScriptAsync("document.querySelector('.tab[data-page=\"atlas\"]')?.click()");
                    await Task.Delay(1000);
                    string atlasRotation = await core.ExecuteScriptAsync(@"
                      (() => JSON.stringify({
                        charActive: !!document.querySelector('#atlasGroups .card[data-key=""character_skin:3060""] .rotation-toggle.active'),
                        unownedButton: !!document.querySelector('#atlasGroups .card[data-key=""character_skin:3063""] .rotation-toggle'),
                        unownedActive: !!document.querySelector('#atlasGroups .card[data-key=""character_skin:3063""] .rotation-toggle.active'),
                        accessoryActive: !!document.querySelector('#atlasGroups .card[data-key=""accessory:1604""] .rotation-toggle.active'),
                        emoteButton: !!document.querySelector('#atlasGroups .card[data-key=""emote:201""] .rotation-toggle'),
                        lastActionRotation: (() => {
                          const card = document.querySelector('#atlasGroups .card[data-key=""character_skin:3060""]');
                          const actions = card ? [...card.querySelectorAll('.rotation-toggle')] : [];
                          return actions.length > 0 && actions[actions.length - 1].classList.contains('rotation-toggle');
                        })(),
                        modalHidden: !!document.getElementById('modalHost')?.classList.contains('hidden')
                      }))()");
                    var atlasRotationState = JsonNode.Parse(JsonSerializer.Deserialize<string>(atlasRotation)!)!.AsObject();
                    if (!atlasRotationState["charActive"]!.GetValue<bool>()
                        || !atlasRotationState["unownedButton"]!.GetValue<bool>()
                        || atlasRotationState["unownedActive"]!.GetValue<bool>()
                        || !atlasRotationState["accessoryActive"]!.GetValue<bool>()
                        || atlasRotationState["emoteButton"]!.GetValue<bool>()
                        || !atlasRotationState["lastActionRotation"]!.GetValue<bool>())
                        throw new InvalidOperationException("Atlas rotation buttons did not render/active correctly: " + atlasRotation);

                    await core.ExecuteScriptAsync("document.querySelector('#atlasGroups .card[data-key=\"character_skin:3063\"] .rotation-toggle')?.click()");
                    await Task.Delay(700);
                    string atlasToggled = await core.ExecuteScriptAsync(@"
                      (() => JSON.stringify({
                        active: !!document.querySelector('#atlasGroups .card[data-key=""character_skin:3063""] .rotation-toggle.active'),
                        modalHidden: !!document.getElementById('modalHost')?.classList.contains('hidden')
                      }))()");
                    var atlasToggledState = JsonNode.Parse(JsonSerializer.Deserialize<string>(atlasToggled)!)!.AsObject();
                    if (!atlasToggledState["active"]!.GetValue<bool>() || !atlasToggledState["modalHidden"]!.GetValue<bool>())
                        throw new InvalidOperationException("Atlas rotation button must stop propagation and update state: " + atlasToggled);
                    Console.WriteLine("PASS: atlas cards expose rotation toggles for owned/unowned character/accessory entries, not emotes.");

                    var i18nLoaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    core.NavigationCompleted += (_, ev) => i18nLoaded.TrySetResult(ev.IsSuccess);
                    core.Navigate("https://wuwatappo.local/index.html?preview=settings&lang=en#page=settings");
                    if (!await i18nLoaded.Task) throw new InvalidOperationException("i18n English preview navigation failed.");
                    await Task.Delay(900);
                    string enUiJson = await core.ExecuteScriptAsync(@"
                      (() => JSON.stringify({
                        lang: document.documentElement.lang,
                        tabs: [...document.querySelectorAll('.tab span')].map(x => x.textContent.trim()),
                        title: document.getElementById('pageTitle')?.textContent || '',
                        locale: document.getElementById('setLocale')?.value || '',
                        groups: [...document.querySelectorAll('.settings-group-title')].map(x => x.textContent.trim())
                      }))()");
                    var enUi = JsonNode.Parse(JsonSerializer.Deserialize<string>(enUiJson)!)!.AsObject();
                    var enTabs = enUi["tabs"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
                    var enGroups = enUi["groups"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
                    if (enUi["lang"]!.GetValue<string>() != "en"
                        || !enTabs.Contains("Bag") || !enTabs.Contains("Settings") || !enTabs.Contains("Custom companions")
                        || enUi["title"]!.GetValue<string>() != "Settings"
                        || enUi["locale"]!.GetValue<string>() != "en"
                        || !enGroups.Contains("Basic helpers") || !enGroups.Contains("Account & redemption"))
                        throw new InvalidOperationException("English i18n preview did not translate chrome/settings groups: " + enUiJson);

                    var jaLoaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    core.NavigationCompleted += (_, ev) => jaLoaded.TrySetResult(ev.IsSuccess);
                    core.Navigate("https://wuwatappo.local/index.html?preview=settings&lang=ja#page=settings");
                    if (!await jaLoaded.Task) throw new InvalidOperationException("i18n Japanese preview navigation failed.");
                    await Task.Delay(900);
                    string jaUiJson = await core.ExecuteScriptAsync(@"
                      (() => JSON.stringify({
                        lang: document.documentElement.lang,
                        tabs: [...document.querySelectorAll('.tab span')].map(x => x.textContent.trim()),
                        title: document.getElementById('pageTitle')?.textContent || '',
                        locale: document.getElementById('setLocale')?.value || '',
                        groups: [...document.querySelectorAll('.settings-group-title')].map(x => x.textContent.trim())
                      }))()");
                    var jaUi = JsonNode.Parse(JsonSerializer.Deserialize<string>(jaUiJson)!)!.AsObject();
                    var jaTabs = jaUi["tabs"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
                    var jaGroups = jaUi["groups"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
                    if (jaUi["lang"]!.GetValue<string>() != "ja"
                        || !jaTabs.Contains("バッグ") || !jaTabs.Contains("設定") || !jaTabs.Contains("カスタムコンパニオン")
                        || jaUi["title"]!.GetValue<string>() != "設定"
                        || jaUi["locale"]!.GetValue<string>() != "ja"
                        || !jaGroups.Contains("基本補助") || !jaGroups.Contains("アカウントと交換"))
                        throw new InvalidOperationException("Japanese i18n preview did not translate chrome/settings groups: " + jaUiJson);

                    string roundTripJson = await core.ExecuteScriptAsync(@"
                      (() => {
                        const btn = document.getElementById('closeBtn');
                        const theme = document.getElementById('themeBtn');
                        const original = { title: btn?.title || '', aria: btn?.getAttribute('aria-label') || '' };
                        I18N.setLocale('en');
                        I18N.applyStatic(document);
                        const en = { title: btn?.title || '', aria: btn?.getAttribute('aria-label') || '' };
                        I18N.setLocale('ja');
                        I18N.applyStatic(document);
                        const ja = { title: btn?.title || '', aria: btn?.getAttribute('aria-label') || '' };
                        I18N.setLocale('zh');
                        I18N.applyStatic(document);
                        const zh = { title: btn?.title || '', aria: btn?.getAttribute('aria-label') || '', lang: document.documentElement.lang, theme: theme?.title || '' };
                        return JSON.stringify({ original, en, ja, zh });
                      })()");
                    var roundTrip = JsonNode.Parse(JsonSerializer.Deserialize<string>(roundTripJson)!)!.AsObject();
                    var original = roundTrip["original"]!.AsObject();
                    var enStatic = roundTrip["en"]!.AsObject();
                    var jaStatic = roundTrip["ja"]!.AsObject();
                    var zhStatic = roundTrip["zh"]!.AsObject();
                    if (original["title"]!.GetValue<string>() != zhStatic["title"]!.GetValue<string>()
                        || original["aria"]!.GetValue<string>() != zhStatic["aria"]!.GetValue<string>()
                        || enStatic["title"]!.GetValue<string>() == original["title"]!.GetValue<string>()
                        || jaStatic["title"]!.GetValue<string>() == original["title"]!.GetValue<string>()
                        || zhStatic["lang"]!.GetValue<string>() != "zh-CN"
                        || zhStatic["theme"]!.GetValue<string>() != "主题设置")
                        throw new InvalidOperationException("i18n static title/aria did not round-trip back to Chinese: " + roundTripJson);
                    Console.WriteLine("PASS: English/Japanese i18n preview translates navigation, page title and settings groups.");
                    Console.WriteLine("screenshot=" + output);
                    form.Close();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex);
                    form.Close();
                }
            };
            Application.Run(form);
            return File.Exists(output) ? 0 : 1;
        }
        finally
        {
            try { Paths.Root = null; } catch { }
            try { Directory.Delete(userData, true); } catch { }
            try { Directory.Delete(dataRoot, true); } catch { }
        }
    }

    static JsonNode HandleCommand(string cmd, JsonObject data, ref JsonObject boot, ThemePresetManager manager, ref string lastDeleteId)
    {
        if (boot == null) return null;
        if (cmd == "boot") return boot;
        var settings = boot["settings"] as JsonObject ?? new JsonObject();
        switch (cmd)
        {
            case "companions.list":
                return (boot["companions"] as JsonArray)?.DeepClone() ?? new JsonArray();
            case "companions.save":
            {
                var list = boot["companions"] as JsonArray;
                if (list == null) { list = new JsonArray(); boot["companions"] = list; }
                string id = data["id"]?.GetValue<string>() ?? "";
                var item = list.OfType<JsonObject>().FirstOrDefault(x => (x["id"]?.GetValue<string>() ?? "") == id);
                if (item == null)
                {
                    item = new JsonObject { ["id"] = "preview-companion-" + (list.Count + 1), ["syntheticId"] = -1000 - list.Count };
                    list.Add(item);
                }
                string name = (data["name"]?.GetValue<string>() ?? "伙伴").Trim();
                if (name.Length == 0) name = "伙伴";
                int dot = name.IndexOfAny(new[] { '·', '・' });
                item["name"] = name;
                item["displayName"] = dot > 0 ? name[..dot].Trim() : name;
                item["characterAssetId"] = data["characterAssetId"]?.GetValue<long>() ?? 0;
                item["accessoryAssetId"] = data["accessoryAssetId"]?.GetValue<long>() ?? 0;
                item["clickIntervalSeconds"] = Math.Clamp(data["clickIntervalSeconds"]?.GetValue<int>() ?? 0, 0, 3600);
                item["randomClick"] = data["randomClick"]?.GetValue<bool>() ?? false;
                item["showBar"] = data["showBar"]?.GetValue<bool>() ?? true;
                return list.DeepClone();
            }
            case "companions.delete":
            {
                var list = boot["companions"] as JsonArray;
                if (list != null)
                {
                    string id = data["id"]?.GetValue<string>() ?? "";
                    var item = list.OfType<JsonObject>().FirstOrDefault(x => (x["id"]?.GetValue<string>() ?? "") == id);
                    if (item != null) list.Remove(item);
                }
                return (list ?? new JsonArray()).DeepClone();
            }
            case "bag.list":
                return new JsonObject
                {
                    ["items"] = new JsonArray
                    {
                        new JsonObject { ["id"] = 3060, ["type"] = "character_skin", ["assetName"] = "role_1407", ["name"] = "测试角色A", ["rarity"] = "epic", ["quantity"] = 1, ["isCollect"] = false, ["thumb"] = "" },
                        new JsonObject { ["id"] = 3062, ["type"] = "character_skin", ["assetName"] = "role_1409", ["name"] = "测试角色C", ["rarity"] = "rare", ["quantity"] = 1, ["isCollect"] = false, ["thumb"] = "" },
                        new JsonObject { ["id"] = 1604, ["type"] = "accessory", ["assetName"] = "acc_1604", ["name"] = "测试配件A", ["rarity"] = "rare", ["quantity"] = 1, ["isCollect"] = false, ["thumb"] = "" },
                        new JsonObject { ["id"] = 201, ["type"] = "emote", ["name"] = "收藏测试·普通", ["rarity"] = "common", ["quantity"] = 1, ["isCollect"] = false, ["thumb"] = "" },
                        new JsonObject { ["id"] = 202, ["type"] = "emote", ["name"] = "收藏测试·史诗", ["rarity"] = "epic", ["quantity"] = 1, ["isCollect"] = false, ["thumb"] = "" },
                        new JsonObject { ["id"] = 203, ["type"] = "emote", ["name"] = "收藏测试·稀有", ["rarity"] = "rare", ["quantity"] = 1, ["isCollect"] = false, ["thumb"] = "" },
                    }
                };
            case "catalog.list":
            {
                string type = data["type"]?.GetValue<string>() ?? "character_skin";
                return new JsonObject { ["items"] = CatalogItems(type), ["count"] = CatalogItems(type).Count };
            }
            case "atlas.list":
            {
                string type = data["type"]?.GetValue<string>() ?? "";
                var list = new JsonArray();
                if (type == "character_skin")
                {
                    list.Add(AtlasItem(3060, "role_1407", "character_skin", "测试角色A", "epic"));
                    list.Add(AtlasItem(3061, "role_1408", "character_skin", "测试角色B", "rare"));
                    list.Add(AtlasItem(3062, "role_1409", "character_skin", "测试角色C", "rare"));
                    list.Add(AtlasItem(3063, "role_1410", "character_skin", "测试角色D", "uncommon"));
                }
                else if (type == "accessory")
                {
                    list.Add(AtlasItem(1604, "acc_1604", "accessory", "测试配件A", "rare"));
                    list.Add(AtlasItem(1605, "acc_1605", "accessory", "测试配件B", "uncommon"));
                    list.Add(AtlasItem(1606, "acc_1606", "accessory", "测试配件C", "common"));
                }
                else
                {
                    list.Add(AtlasItem(201, "emote_2000_happy", "emote", "收藏测试·普通", "common"));
                }
                return new JsonObject { ["items"] = list, ["count"] = list.Count };
            }
            case "rotation.set":
            {
                var rot = boot["rotation"] as JsonObject;
                if (rot == null) { rot = new JsonObject(); boot["rotation"] = rot; }
                if (data.ContainsKey("enabled")) rot["enabled"] = data["enabled"]?.GetValue<bool>() ?? false;
                if (data.ContainsKey("intervalMinutes"))
                    rot["intervalMinutes"] = Math.Clamp(data["intervalMinutes"]?.GetValue<int>() ?? 30, 1, 1440);
                return boot;
            }
            case "rotation.toggle":
            {
                string type = data["type"]?.GetValue<string>() ?? "";
                long id = data["id"]?.GetValue<long>() ?? 0;
                var rot = boot["rotation"] as JsonObject;
                if (rot == null) { rot = new JsonObject(); boot["rotation"] = rot; }
                string key = type == "character_skin" ? "characterIds" : "accessoryIds";
                var values = (rot[key] as JsonArray)?.Select(x => x!.GetValue<long>()).ToList() ?? new List<long>();
                if (values.Contains(id)) values.Remove(id); else values.Add(id);
                var next = new JsonArray();
                foreach (long value in values) next.Add(value);
                rot[key] = next;
                return boot;
            }
            case "rotation.remove":
            {
                string type = data["type"]?.GetValue<string>() ?? "";
                long id = data["id"]?.GetValue<long>() ?? 0;
                var rot = boot["rotation"] as JsonObject;
                if (rot == null) { rot = new JsonObject(); boot["rotation"] = rot; }
                string key = type == "character_skin" ? "characterIds" : "accessoryIds";
                var values = (rot[key] as JsonArray)?.Select(x => x!.GetValue<long>()).Where(x => x != id).ToList() ?? new List<long>();
                var next = new JsonArray();
                foreach (long value in values) next.Add(value);
                rot[key] = next;
                return boot;
            }
            case "favorite":
                return new JsonObject { ["favorite"] = data["on"]?.GetValue<bool>() ?? false };
            case "settings.set":
                foreach (var kv in data)
                    settings[kv.Key] = kv.Value?.DeepClone();
                return boot;
            case "theme.presets.save":
            {
                var preset = manager.SaveCurrent(
                    data["name"]?.GetValue<string>() ?? "",
                    data["id"]?.GetValue<string>() ?? "");
                RefreshThemePresetBoot(boot, manager);
                return new JsonObject { ["preset"] = JsonSerializer.SerializeToNode(PanelWindow.MapThemePreset(preset)) };
            }
            case "theme.presets.apply":
            {
                string id = data["id"]?.GetValue<string>() ?? "";
                manager.Apply(id);
                RefreshThemePresetBoot(boot, manager);
                return new JsonObject { ["ok"] = true };
            }
            case "theme.presets.delete":
            {
                lastDeleteId = data["id"]?.GetValue<string>() ?? "";
                bool removed = manager.Delete(lastDeleteId);
                RefreshThemePresetBoot(boot, manager);
                return new JsonObject { ["ok"] = removed };
            }
            case "theme.presets.rename":
            {
                var preset = manager.Rename(
                    data["id"]?.GetValue<string>() ?? "",
                    data["name"]?.GetValue<string>() ?? "");
                RefreshThemePresetBoot(boot, manager);
                return new JsonObject { ["preset"] = JsonSerializer.SerializeToNode(PanelWindow.MapThemePreset(preset)) };
            }
            case "theme.presets.move":
            {
                string id = data["id"]?.GetValue<string>() ?? "";
                int direction = data["direction"]?.GetValue<int>() ?? 0;
                bool moved = manager.Move(id, direction);
                RefreshThemePresetBoot(boot, manager);
                return new JsonObject { ["ok"] = moved, ["id"] = id, ["direction"] = direction };
            }
            case "theme.rotation.set":
            {
                var rotation = manager.SetRotation(
                    data["enabled"]?.GetValue<bool>() ?? false,
                    data["intervalMinutes"]?.GetValue<int>() ?? 30);
                RefreshThemePresetBoot(boot, manager);
                return new JsonObject { ["rotation"] = JsonSerializer.SerializeToNode(PanelWindow.MapThemeRotation(rotation)) };
            }
            case "appearance.offset":
            {
                // 模拟原生 PetWindow 的完整 transform 协议，供设置页归零/方向按钮回归测试。
                var appearance = boot["appearance"] as JsonObject ?? new JsonObject();
                int x = appearance["accessoryOffsetX"]?.GetValue<int>() ?? 0;
                int y = appearance["accessoryOffsetY"]?.GetValue<int>() ?? 0;
                int scale = appearance["accessoryScalePercent"]?.GetValue<int>() ?? 100;
                int rotation = appearance["accessoryRotationDegrees"]?.GetValue<int>() ?? 0;
                bool reset = data["reset"]?.GetValue<bool>() ?? false;
                if (reset)
                {
                    x = 0; y = 0; scale = 100; rotation = 0;
                    OffsetCommands.Add("reset");
                }
                else if (data.ContainsKey("dx") || data.ContainsKey("dy"))
                {
                    x += data["dx"]?.GetValue<int>() ?? 0;
                    y += data["dy"]?.GetValue<int>() ?? 0;
                    OffsetCommands.Add($"nudge:{x},{y},{scale},{rotation}");
                }
                else
                {
                    if (data.ContainsKey("x")) x = data["x"]?.GetValue<int>() ?? x;
                    if (data.ContainsKey("y")) y = data["y"]?.GetValue<int>() ?? y;
                    if (data.ContainsKey("scale")) scale = data["scale"]?.GetValue<int>() ?? scale;
                    if (data.ContainsKey("rotation")) rotation = data["rotation"]?.GetValue<int>() ?? rotation;
                    OffsetCommands.Add($"absolute:{x},{y},{scale},{rotation}");
                }
                x = Math.Clamp(x, -160, 160);
                y = Math.Clamp(y, -160, 160);
                scale = Math.Clamp(scale <= 0 ? 100 : scale, 25, 175);
                rotation = Math.Clamp(rotation, -180, 180);
                appearance["accessoryOffsetX"] = x;
                appearance["accessoryOffsetY"] = y;
                appearance["accessoryScalePercent"] = scale;
                appearance["accessoryRotationDegrees"] = rotation;
                long accessoryId = appearance["accessoryAssetId"]?.GetValue<long>() ?? 0;
                if (accessoryId > 0)
                {
                    var offsets = appearance["accessoryOffsets"] as JsonObject ?? new JsonObject();
                    offsets[accessoryId.ToString()] = new JsonArray(x, y, scale, rotation);
                    appearance["accessoryOffsets"] = offsets;
                }
                return new JsonObject
                {
                    ["x"] = x,
                    ["y"] = y,
                    ["scale"] = scale,
                    ["rotation"] = rotation,
                    ["accessoryId"] = accessoryId,
                    ["boot"] = boot.DeepClone(),
                };
            }
            default:
                return null;
        }
    }

    static JsonArray CatalogItems(string type)
    {
        var list = new JsonArray();
        if (type == "character_skin")
        {
            list.Add(CatalogItem(3060, "测试角色A", "角色系列", "epic"));
            list.Add(CatalogItem(3061, "测试角色B", "角色系列", "rare"));
            list.Add(CatalogItem(3062, "测试角色C", "角色系列", "common"));
            list.Add(CatalogItem(3063, "测试角色D", "角色系列", "rare"));
        }
        else if (type == "accessory")
        {
            list.Add(CatalogItem(1604, "测试配件A", "配件系列", "rare"));
            list.Add(CatalogItem(1605, "测试配件B", "配件系列", "uncommon"));
            list.Add(CatalogItem(1606, "测试配件C", "配件系列", "common"));
        }
        else
        {
            list.Add(CatalogItem(201, "收藏测试·普通", ""));
        }
        return list;
    }

    static JsonObject CatalogItem(long id, string name, string series, string rarity = "rare") => new()
    {
        ["id"] = id,
        ["name"] = name,
        ["series"] = series,
        ["rarity"] = rarity,
        ["thumb"] = "",
        ["previewUrl"] = "",
        ["gameRoleId"] = 0,
    };

    static JsonObject AtlasItem(long mainId, string assetName, string type, string name, string rarity) => new()
    {
        ["id"] = mainId + 50000,
        ["mainId"] = mainId,
        ["assetName"] = assetName,
        ["type"] = type,
        ["name"] = name,
        ["rarity"] = rarity,
        ["series"] = "图鉴系列",
        ["thumb"] = "",
    };

    static void RefreshThemePresetBoot(JsonObject boot, ThemePresetManager manager)
    {
        if (boot == null) return;
        boot["themePresets"] = JsonSerializer.SerializeToNode(PanelWindow.MapThemePresets(manager.Presets));
        boot["themeRotation"] = JsonSerializer.SerializeToNode(PanelWindow.MapThemeRotation(manager.Rotation));
    }
}
