# WuwatappoNative 周期性负载分析报告

- 日期：2026-11-20
- 分析对象：`tappoNativeGit` 仓库中的 `WuwatappoNative`
- 本轮边界：**未修改业务源码、未提交 git**。只新增 `tools/PerfMonitor/` 下的监测工具与分析文档；根 `WuwatappoNative.csproj` 增加了一行 `Compile Remove="tools\**\*.cs"`，仅用于把工具项目排除出主程序编译，不改变任何业务逻辑。

## 0. 结论摘要

1. **未能取得 WuwatappoNative 的实时采样**：检查时 `Get-Process WuwatappoNative` 没有实例；当天日志最后一条是 15:59:23，之后进程已退出。按任务约束没有主动启动会弹 UI 的主程序。
2. **“每隔几分钟负载提高”最像的来源是 z-order（窗口层级）兜底链路**，不是 5 分钟内存定时器：
   - 当天应用日志中 `窗口层级兜底` 出现 **95 次**，其中 `MainNotAboveRemotes` 79 次、`NotContiguous` 15 次；诊断日志中有 **108 次 restack**（87 次 `MainNotAboveRemotes`、20 次 `NotContiguous`、1 次 `PetsBelowNonTopMost`）。
   - 触发间隔中位数 **138.7 秒（约 2.3 分钟）**，与“每隔几分钟”高度吻合。
   - 每次异常会生成两张大快照：当天 215 条 `[zorder]` 诊断，平均 **27.9 KB/条**、总计 **5.73 MB**，平均每个快照枚举 **92 个可见顶层窗口**；`DescribeTopLevelWindow` 对每个窗口调用 `Process.GetProcessById(pid)` 取进程名，再序列化 JSON 并同步写盘，全部在 UI 线程。
3. **第二位是联机桌宠重绘**：当天 `visiblePets` 峰值 **20**。每个远程成员每次按键动作都会走 `RemoteCatWindow.SetBody -> CatPainter.Request -> Compose`，而 `_baseFrameKey` 把身体帧也算进缓存键，导致每个动作都重建整张 200×341 基础位图（含 `new Font`、`GraphicsPath`、两次 `DrawImage`）。`CatPainter` 还在 UI 线程上“队列空就立即画”，多人同时按键时会阻塞 UI。
4. **5 分钟 `_memoryTimer` 基本可以排除**：它只读 WorkingSet/Private/GDI/USER 并写一行 diag，不触发 GC；当天 124 条 memory 快照，严格约 5 分钟一条，没有与之对应的 CPU 高峰证据。
5. **多核优化优先级**：P0 = 消除 z-order 瞬时误判 + 把 z-order 快照移出 UI 线程；P0/P1 = 远程桌宠按身体帧缓存基础位图、限制表情帧率、把位图合成放到有界工作线程；P1 = 悬停命中检测改 alpha 掩码、进程扫描降频、WebView2 推送节流；P2 = 日志/配置异步化、成员快照增量 diff。

## 1. 监测工具

### 1.1 文件清单

`tools/PerfMonitor/` 下新增：

| 文件 | 作用 |
|---|---|
| `PerfMonitor.csproj` | 独立 .NET 8 控制台项目，无 NuGet 依赖 |
| `Program.cs` | 参数解析、采样循环、Ctrl+C 收尾、报告编排 |
| `Options.cs` | 命令行参数与帮助 |
| `ProcessResolver.cs` | 按进程名/PID 附加，不启动、不结束目标进程 |
| `ProcessSampler.cs` | 进程/线程 CPU、内存、GDI/USER、句柄采样 |
| `NativeMethods.cs` | `GetGuiResources`、`GetThreadDescription` 等 P/Invoke |
| `SpikeAnalyzer.cs` | 中位数基线 + 连续 2 点阈值判定；统计中位数/p95/最大值 |
| `LogCorrelator.cs` | 读取 `app-*.log`，按尖峰窗口对齐，敏感字段脱敏 |
| `ReportWriter.cs` | 控制台表格、CSV、线程 CSV、JSON、日志关联文本 |
| `ConsoleTable.cs` | 控制台表格与摘要输出 |
| `ToolLocator.cs` | 检测 `dotnet-counters` / `dotnet-trace` / PerfView；可选 counters 采集 |
| `README.md` | 中文使用说明 |
| `ANALYSIS.md` | 本报告 |
| `.gitignore` | 忽略 `reports/` 运行产物 |

### 1.2 使用方法

在仓库根目录：

```powershell
# 默认：按进程名 WuwatappoNative，采样 300 秒，每秒 1 次
dotnet run --project tools/PerfMonitor -c Release -- --duration 300 --interval 1

# 附加指定 PID
dotnet run --project tools/PerfMonitor -c Release -- --pid 12345 --duration 120 --top 12

# 输出到指定目录
dotnet run --project tools/PerfMonitor -c Release -- --process WuwatappoNative --out out/perf
```

只读采样，不会结束、挂起或注入目标进程。报告默认写入 `<当前目录>/out/perf`；本次自测写入 `tools/PerfMonitor/reports/`（已被 `tools/PerfMonitor/.gitignore` 忽略）。

### 1.3 采样内容

- 进程总 CPU%：按逻辑核归一化（0..100）
- 单核 CPU%：单核口径（可能 >100）
- WorkingSet / PrivateMemory（MB）
- GDI 对象数 / USER 对象数（`GetGuiResources`）
- HandleCount / ThreadCount
- 线程 CPU 时间增量（ms）
- Top N 线程：线程 ID、Windows 线程描述（`GetThreadDescription`）、CPU%（单核）、累计 CPU ms、优先级、状态、等待原因

### 1.4 尖峰判定

```text
基线 = 最近 baseline-window 个样本（跳过 warmup）的 CPU 中位数
阈值 = max(基线 × spike-factor, 基线 + spike-min-delta)
连续 2 个采样点 ≥ 阈值 => 尖峰
```

记录尖峰起止、峰值、持续时间、峰值线程、基线、阈值、距上一个尖峰结束的间隔及 start-to-start 间隔。默认 `spike-factor=2.0`、`spike-min-delta=0.75`（归一化百分点）、`baseline-window=60`、`warmup=5`。

### 1.5 报告文件

```text
perf-<进程名>-<yyyyMMdd-HHmmss>.csv            # 每个采样点
perf-<进程名>-<yyyyMMdd-HHmmss>-threads.csv    # 每个采样点 Top N 线程
perf-<进程名>-<yyyyMMdd-HHmmss>.json           # 完整摘要/尖峰/线程汇总/日志关联
perf-<进程名>-<yyyyMMdd-HHmmss>-log.txt        # 尖峰前后日志（已脱敏）
perf-<进程名>-<yyyyMMdd-HHmmss>-counters.csv   # 仅 --with-counters 且工具存在
```

## 2. 采样结果

### 2.1 真实目标进程

| 检查 | 结果 |
|---|---|
| `Get-Process WuwatappoNative` | 无实例 |
| 当天 `app-2026-11-20.log` 最后写入 | 2026-11-20 15:59:23 |
| 进程名模糊匹配 `*wuwa*` / `*tappo*` | 无 |
| 是否主动启动主程序 | 未启动（按任务约束，避免弹 UI） |

因此**本轮没有 WuwatappoNative 的真实 CPU/线程采样**；下文实时数据均来自工具自测，业务侧结论来自现有日志与代码静态分析。

### 2.2 工具自测（合成负载，非 WuwatappoNative）

用临时 .NET 控制台进程 `PerfLoad` 制造“3 秒空转 + 2 秒休眠”的周期性负载，运行：

```powershell
dotnet run --project tools/PerfMonitor -c Release --no-build -- `
  --pid 47880 --duration 30 --interval 1 --top 4 `
  --out tools/PerfMonitor/reports --log-dir <临时日志目录>
```

结果摘要：

| 项目 | 结果 |
|---|---|
| 样本数 / 时长 | 30 / 30.0s |
| 逻辑核 | 12 |
| CPU 归一化% | 中位数 5.60 / p95 7.29 / 最大 7.68 |
| CPU 单核% | 中位数 67.2 / p95 87.5 / 最大 92.2 |
| 尖峰 | 1 个：16:13:04 开始，持续 2.0s，峰值 7.16%（单核 85.9%），基线 0.13%，峰值线程 54476 |
| 线程 | 进程 4 线程，采样到的热线程 CPU 约 76-92% 单核 |
| 日志关联 | 扫描 301 行，尖峰窗口 16 行，`token`/`cookie` 均输出为 `<redacted>` |
| 报告 | `tools/PerfMonitor/reports/perf-PerfLoad-20261120-161255.{csv,-threads.csv,json,-log.txt}` |

另外用合成样本单测 `SpikeAnalyzer.Detect`：50 个样本中构造两段高 CPU，得到 2 个尖峰（持续 4s / 3s，峰值 2.40 / 1.60，基线 0.15），相邻间隔 15s；`LogCorrelator` 对 301 行临时日志正确筛出 17 行尖峰窗口日志并脱敏。说明采样、阈值、CSV/JSON、日志对齐链路可用。

### 2.3 现有日志中的资源快照

`RuntimeDiagnostics.LogPeriodic` 每 5 分钟写一条 `[memory]`：

| 日期 | 样本数 | WorkingSet 最大 | Private 最大 | GDI 范围 | USER 最大 | visiblePets 最大 |
|---|---:|---:|---:|---:|---:|---:|
| 2026-11-18 | 39 | 259 MB | 187 MB | 134-169 | 149 | 17 |
| 2026-11-19 | 220 | 439 MB | 287 MB | 30-169 | 149 | 17 |
| 2026-11-20 | 124 | 431 MB | 347 MB | 41-193 | 167 | 20 |

GDI/USER 随桌宠数量上升，但没有明显的“只增不减”证据；Private 峰值 347 MB、GDI 193，仍在单进程常见范围。需要更长采样才能判断是否有泄漏。

## 3. 周期性尖峰证据

### 3.1 z-order 兜底：最强证据

当天统计：

| 指标 | 数值 |
|---|---:|
| 应用日志 `窗口层级兜底` | 95 次 |
| `MainNotAboveRemotes` | 79 次 |
| `NotContiguous` | 15 次 |
| 诊断日志 `[zorder]` 快照 | 215 条 |
| `restack` / `restack-after` | 108 / 107 条 |
| restack 原因 | `MainNotAboveRemotes` 87、`NotContiguous` 20、`PetsBelowNonTopMost` 1 |
| 快照平均长度 | 27.9 KB（最大 34.6 KB） |
| 快照总大小 | 5.73 MB（当天 diag 日志主要构成） |
| 每个快照平均顶层窗口 | 92（最大 119） |
| 触发间隔中位数 | 138.7 秒（约 2.3 分钟） |
| 间隔 ≤35 秒的连续记录 | 10 次（其中 3 次 30-32 秒，说明异常可持续数十秒） |

可用的事件时间线（应用日志中的 `窗口层级兜底`，括号内为距上一条的秒数）：

```text
13:04:10
13:06:23 (+133)   13:06:49 (+26)
13:09:52 (+183)   13:11:10 (+78)   13:13:21 (+131)   13:16:02 (+161)
14:18:49
14:23:11 (+262)   14:24:56 (+105)
14:32:40 (+464)   14:41:42 (+542)
14:43:29 (+107)   14:44:00 (+31)    14:45:23 (+83)    14:45:54 (+31)
14:54:18 (+504)   14:58:29 (+251)   15:00:24 (+115)
15:26:14 (+1550)
```

其中 26-31 秒的间隔基本就是日志 30 秒限频的边界，说明异常在那段时间持续存在；1-2 分钟的间隔则是新的成员/窗口事件再次触发。**日志没有 CPU 数值，不能给出真实 CPU 峰值**；CPU 峰值需要下次 WuwatappoNative 运行时用本工具采样确认。

一次 15:26:14 的 `MainNotAboveRemotes` 快照（18 个联机桌宠）：

```text
Main              z=7
Remote(181135)    z=4   <-- 唯一跑到主桌宠上方的远程窗口
Remote(9226)      z=8
...               ...
Remote(206667)    z=24
```

同一时刻的 `restack-after` 快照：

```text
Main              z=3
Remote(9226)      z=4
...               ...
Remote(206667)    z=21
```

说明重排本身有效，问题在于**重排被反复触发**：新远程窗口 `Show()` 时 `RemoteCatWindow.OnHandleCreated -> ApplyWindowStyles -> SetWindowPos(HWND_TOPMOST)` 会先把自己放到主桌宠上方，而 `PetWindow.UpdateCat` 只是 `QueueRestackPetWindows()`（200ms 合并定时器）。如果 1 秒一次的 `_zOrderWatchdog` 在这个窗口期先跑，就会看到 `MainNotAboveRemotes`，执行一次全量重排并写两张大快照。

代码链路（均为 UI 线程）：

```text
PetWindow.CreateTimers()
  _zOrderWatchdog = new Forms.Timer { Interval = 1000 }        // 1020-1022
  -> EnsurePetZOrder()                                        // 1632
  -> RestackPetWindows(force:false)                           // 1694
  -> VisibleTopLevelZOrder()                                  // 1849：枚举全部可见顶层窗口
  -> PetZOrderGuard.Check(...)                                // 1716
  -> 若异常：
       LogZOrderSnapshot("restack", ...)                      // 1876
         -> DescribeTopLevelWindow(each)                      // 2021
            -> Process.GetProcessById(pid).ProcessName        // 2056，每个窗口一次
         -> JsonSerializer.Serialize(...)                     // 28 KB/次
         -> Log.Diagnostic(...)                               // AutoFlush 同步写盘
       SetWindowPos(...) 2*(N+1) 次                            // 1742-1765
       LogZOrderSnapshot("restack-after", ...)                // 1777
```

成本估算（按当天数据）：

- 108 次 restack × 2 张快照 × 92 个窗口 ≈ **19,900 次 `Process.GetProcessById`/天**；
- 215 张 × 27.9 KB ≈ **5.7 MB JSON 序列化 + 同步写盘/天**；
- 108 次 × 约 40 次 `SetWindowPos`（19 个远程 + 主宠）≈ **4,300 次 SetWindowPos/天**；
- 此外 `_zOrderWatchdog` 每秒还会枚举全部可见顶层窗口，约 **8 万次/天** 的 `IsWindowVisible` + 保护检查。

这组数据与“每隔几分钟负载提高”最吻合：不是固定 5 分钟一次，而是由成员/窗口事件触发，间隔中位数 2.3 分钟，忙时连续 30 秒一次。

### 3.2 联机桌宠重绘

当天 `visiblePets` 峰值 20（主宠 + 19 个远程）。关键代码：

- `RemoteCatWindow.ComposeInto`（484-590）用 `_baseFrameKey` 缓存整张底图，但 key 包含 `selectedBody`（493-495）。`SetBody`（374-379）每次左右手切换都会改变 `selectedBody`，于是每个按键动作都重建整张 200×341：
  - `new Bitmap`（若尺寸变化才会新建，通常复用）
  - `Graphics.Clear + DrawImage(head/body/accessory)`
  - `GraphicsPath` + `SolidBrush` + **`new Font("Microsoft YaHei UI", ...)`**（558）
  - 裁剪后再画一次身体，最后 `UpdateLayeredWindow`
- `CatPainter`（870-935）：`PerTick=64`、`BudgetMs=10`、`ImmediateGapMs=8`；队列为空且距上次绘制 ≥8ms 时**在调用线程（UI 线程）立即 `PaintNow()`**。
- `RemoteEmoteAnimator`（820-863）以 16ms 时钟泵所有活动表情窗口；`TickEmote()`（428-433）每帧都 `CatPainter.Request(this)`，即使气泡画面没有变化。
- `PollHover`（594-604）每 150ms 遍历所有 `_cats`，`ContainsCatScreenPoint`（300-311）对每只猫做 `bmp.GetPixel` + `PetComposer.HitTestCat`（700-735，最多 3 次 `GetPixel`）。20 只猫时约 **400 次 GDI+ GetPixel/秒**（6.7 × 20 × 3），全部在 UI 线程。

推算：20 个成员、每人 10Hz 动作时，每秒 200 次远程重绘；每次重建基础图（2 次 `Graphics.FromImage`、1-2 次 `Font`/`GraphicsPath` 分配、约 5 次 DrawImage）远高于 60fps 预算。这是“房间热闹时 CPU 高”的主因，也会通过 GC 产生周期性尖峰。

### 3.3 游戏模式 / 全屏切换

当天日志：

- `游戏模式自动开启：全屏:mstsc` 18 次
- `游戏模式条件结束，恢复鼠标交互` 11 次
- `游戏模式自动开启：全屏:QQ` 8 次

`App.CheckGameModeRules` 每 1 秒执行一次（215），进程名扫描每 2 秒一次（563、573）；`FullscreenDetector.IsForegroundFullscreen` 在检测到全屏时每秒 `Process.GetProcessById`（74）。这些本身是轻量 Win32/Process 调用，但每次全屏切换若导致 `SetClickThrough` 真正变化，会走：

```text
PetWindow.SetClickThrough()          // 1369
  -> ConfigStore.Save()              // 同步 JSON 序列化 + File.WriteAllText（424-432）
  -> ApplySettings()                 // 1403
     -> ApplyWindowStyles()          // 1607 -> RestackPetWindows(force:true)
     -> ApplyBarVisibility()         // Compose 主宠 + 设置全部远程条
     -> UpdateCatScales()            // 每只远程 SetScale/SetWindowOptions/SetRemoteStyle
     -> ApplyThemeToCats()
     -> ApplyCaptureMode()
     -> ResolveOverlaps()
     -> RefreshMenu()
```

当前 `config.json` 的 `clickThrough=true`，所以 `SetClickThrough(true)` 会提前返回，这条路径不一定每次都触发；但一旦用户关闭穿透、或设置页/快捷键改动触发 `ApplySettings`，就会在 UI 线程同步写配置并全量重排。mstsc/QQ 的全屏切换频率足以造成“几分钟一次”的卡顿感。

### 3.4 定时器逐项判断

| 定时器 | 周期 | 线程 | 实际工作 | 判断 |
|---|---:|---|---|---|
| `App._gameModeRuleTimer` | 1s | 线程池 | 每 2s `Process.GetProcessesByName(rule)`；全屏检测每秒 | 中低；有规则时持续扫描，建议降频/合并 |
| `App._autoChestTimer` | 5s | 线程池 | 大多只读 `left/right` 时间戳；到点才 HTTP | 低；日志显示多为冷却跳过 |
| `App._autoBoostTimer` | 1s | 线程池 | 只检查可用点击 | 低 |
| `App._clickRefreshTimer` | 30s | 线程池 | HTTP `user/me`，解析 JSON | 中低；当天 21 次“刷新点击数失败”说明网络路径不稳 |
| `App._memoryTimer` | 5min | 线程池 | `Pet.BeginInvoke` + 读 4 个计数器 + 写 diag | **极低，基本可排除** |
| `RoomManager._reportTimer` | 30s | 线程池 | socket `cr` + 点击账本落盘 | 低；只有 pending clicks 时才有网络/写盘 |
| `RoomManager._reconnectTimer` | 5s | 线程池 | 已连接直接返回；断线才 `JoinAsync` | 低；当天 11 次重连、7 次失败 |
| `PetWindow._hoverPollTimer` | 150ms | UI | 遍历所有猫做 alpha 命中 | 中；20 只猫时每秒约 400 次 GetPixel |
| `PetWindow._composeTimer` | 16ms | UI | 有动画/按键才启动 | 中；繁忙时与远程重绘叠加 |
| `PetWindow._zOrderWatchdog` | 1s | UI | 枚举顶层窗口 + 保护判断 | **高（异常时）**；见 3.1 |
| `RemoteEmoteAnimator` | 16ms | UI | 所有活动表情窗口 Tick + Request | 中高；活动表情多时每帧 N 次 |
| `ThemePresetManager` | 10s | UI | 仅开启轮换时应用预设 | 低（当前未开启） |

### 3.5 30 秒 / 5 秒网络任务

当天应用日志：

- `刷新点击数失败 An error occurred while sending the request.` 21 次
- `自动重连房间 ...` 11 次，其中 7 次失败
- `点击上报被拒绝 c=80013` 24 次、`c=-1` 4 次

这些任务在 `System.Threading.Timer` 线程池执行，HTTP 本身异步，不直接占用 UI 线程；但在网络不稳时会造成：
- 每秒/每 30 秒的 `HttpClient` 请求、DNS/TLS、异常构造和日志写盘；
- 连接失败时 `JoinAsync` 走完整的房间进入流程（socket + rm/rd 快照解析），可能产生短时 CPU/GC 尖峰。

它们是“每个 30 秒/5 秒一次”的周期性负载，而不是多核 CPU 的主要来源；如果用户看到的是“每隔几分钟卡一下”，z-order 与远程重绘仍排在前面。

## 4. UI 线程串行 vs 可并行

### 4.1 目前确定在 UI 线程串行执行

| 工作 | 位置 | 说明 |
|---|---|---|
| 主宠合成/提交 | `PetWindow.Compose` 245；`LayeredSurface.Apply` 796 | 单窗口，量不大 |
| 远程桌宠合成/提交 | `RemoteCatWindow.Compose` 460、`ComposeInto` 484 | 每个动作/表情都整窗绘制；主要热点 |
| CatPainter 泵 | `CatPainter.Pump` 917 | UI 计时器，队列大时连续绘制 |
| RemoteEmoteAnimator 泵 | `Pump` 845 | UI 计时器，16ms |
| 悬停轮询 | `PetWindow.PollHover` 594 | 150ms × 所有远程猫 |
| z-order 守护/重排 | `EnsurePetZOrder` 1632、`RestackPetWindows` 1694 | 1s 一次枚举；异常时 2N 次 SetWindowPos |
| z-order 快照 | `LogZOrderSnapshot` 1876 | 每窗口 `Process.GetProcessById` + 28KB JSON + 同步写盘 |
| 成员更新 | `PetWindow.UpdateCat` 2846 | 每个 `MemberUpdated` 一次，含 SetScale/SetWindowOptions/RefreshMember |
| 面板推送 | `PanelWindow.Push` 342 | UI 线程 `JsonSerializer.Serialize` + `PostWebMessageAsJson` |
| 配置保存 | `ConfigStore.Save` 424 | UI 线程同步 JSON + File.WriteAllText |
| 日志写入 | `Log.WriteLine` 193 | 全局锁 + AutoFlush 同步写盘（z-order 快照尤其重） |

### 4.2 已经在线程池/后台执行

- `SocketClient.PumpAsync` 接收 WebSocket，`DispatchEvent`/`RoomManager.OnEvent`/`ApplyMemberSnapshot` 在 socket 任务线程解析 JSON（这是对的）。
- `_autoChestTimer`、`_autoBoostTimer`、`_clickRefreshTimer`、`_reportTimer`、`_reconnectTimer`、`_gameModeRuleTimer`、`_memoryTimer` 回调都在线程池；其中需要碰 UI 的部分用 `BeginInvoke`。
- `Auth` 的 HTTP 请求、图片下载 `AssetStore.Prefetch` 都是 async/后台。

### 4.3 可并行化且安全的边界

| 可并行工作 | 建议方式 | 安全边界 |
|---|---|---|
| 多只远程桌宠的静态帧/位图合成 | 每窗口一个渲染任务，`SemaphoreSlim` 限制 2-4 并发；各自持有自己的 `Bitmap`，完成后把结果交给 UI 线程 | GDI+ 对象不要跨线程共享；`Graphics`/`Bitmap` 同一时刻只能一个线程使用 |
| 远程桌宠首次出现时的帧缓存构建 | `Task.Run`/`Parallel.ForEach` 预构建 3 个身体帧；UI 线程只做 `Show` | 构建期间窗口可先显示默认帧；不可在 worker 里 `UpdateLayeredWindow` |
| z-order 快照采集/序列化 | 后台线程枚举窗口 + 后台 JSON 序列化 + `Channel<string>` 写日志 | `SetWindowPos`/窗口样式仍在 UI 线程；快照只读 Win32 |
| 日志序列化/写盘 | 有界 `Channel` + 单写线程；批量 flush | 崩溃前可能丢最后一批；`Warn/Error` 可保留同步 |
| 成员快照解析/外观 diff | socket 线程完成 JSON → 成员 DTO；UI 线程只应用差异 | `ConcurrentDictionary` 的成员对象更新需保持线程安全；UI 只碰窗口 |
| 面板消息序列化 | 后台 `JsonSerializer.Serialize` 成字符串，UI 线程 `PostWebMessageAsJson(string)` | WebView2 只能在 UI 线程访问；消息顺序用队列保证 |

**不能并行/不能跨线程的**：

- `UpdateLayeredWindow`、`SetWindowPos`、`SetWindowLongPtr`、`ShowWindow`、`Control.BeginInvoke` 的目标操作必须在窗口所属 UI 线程执行（或至少严格串行）。
- WinForms 控件、`WebView2.CoreWebView2` 严格 UI 线程亲和。
- `Graphics.FromImage`/`Bitmap.GetPixel` 不是线程安全的；同一个 `Bitmap` 不能同时被两个线程读写。
- `System.Windows.Forms.Timer` 的 Tick 本身就在 UI 线程；不要在里面做重活。

## 5. 根因排序

| 排名 | 根因 | 证据 | 置信度 |
|---|---|---|---|
| 1 | z-order 守护每 1s 全量检查；新远程窗口 `Show` 时先置顶，触发 `MainNotAboveRemotes`，随后 2 张 92 窗口快照 + 2N 次 SetWindowPos + 28KB JSON 同步写盘 | 95 次应用日志、108 次 restack、215 张快照、5.73MB、平均 92 窗口、中位间隔 138.7s；快照显示 remote z=4 在主宠 z=7 上方，修复后主宠 z=3 | 高 |
| 2 | 远程桌宠每个动作重建整张基础位图；CatPainter/RemoteEmoteAnimator 在 UI 线程高频绘制 | 20 个 visiblePets；`_baseFrameKey` 含 selectedBody；558 行每次 new Font；CatPainter 立即绘制；16ms 表情时钟 | 中高 |
| 3 | 全屏/游戏模式切换触发 `ApplySettings`，同步 `ConfigStore.Save` + 全量重排/重绘 | 18 次 mstsc 全屏开启、11 次结束、8 次 QQ；SetClickThrough/ApplySettings 路径 | 中 |
| 4 | 60 秒 `user/me`、30 秒点击上报、5 秒重连在坏网络下产生周期性 HTTP/socket/异常/日志 | 21 次刷新失败、11 次重连、7 次失败、24 次点击拒绝 | 中低（对 CPU 而言） |
| 5 | 每 2 秒 `Process.GetProcessesByName` + 全屏时每秒 `Process.GetProcessById` | 代码 563/573、74；频率固定 | 低中 |
| 6 | 5 分钟 `_memoryTimer` | 124 条/天、5 分钟严格周期、只读计数器 | 很低（基本排除） |

## 6. 多核优化方案（按收益/风险排序）

> 本轮只给方案，不直接改业务代码。

### P0-1：消除 z-order 瞬时误判，并把快照移出 UI 线程

**改动点**

1. `src/Pet/RemoteCatWindow.cs`
   - `OnHandleCreated`（605-609）/`ApplyWindowStyles`（313-324）：首次显示不要无条件 `SetWindowPos(Handle, HWND_TOPMOST, ...)`。增加 `SetZOrderBelow(IntPtr mainHandle)`，用 `SetWindowPos(Handle, mainHandle, 0,0,0,0, SWP_NOSIZE|SWP_NOMOVE|SWP_NOACTIVATE)` 把新远程窗口放到主宠下方；或先 `Show()` 再让 `PetWindow` 统一 `RestackPetWindows`。
2. `src/Pet/PetWindow.cs`
   - `EnsurePetZOrder`（1632）：若 `_zOrderRestackQueued || _zOrderRestackTimer.Enabled` 直接返回，避免在 200ms 合并窗口内误判。
   - `CreateTimers`（1020）：`_zOrderWatchdog` 从 1000ms 放宽到 2000-3000ms；或改成 `System.Threading.Timer` 在后台做 `VisibleTopLevelZOrder + PetZOrderGuard.Check`，只有 `problem != None` 才 `BeginInvoke` 到 UI 重排。
   - `RestackPetWindows`（1694）：同一个 problem 连续 2 次检查才修复；修复前若已有 queued restack，不写 `restack` 快照。
3. `src/Pet/PetWindow.cs` `LogZOrderSnapshot`（1876）/`DescribeTopLevelWindow`（2021）/`ProcessName`（2056）
   - PID→进程名加 `ConcurrentDictionary<uint,(string Name,long ExpireMs)>` 缓存（30-60s TTL），禁止每个窗口 `Process.GetProcessById`。
   - `topLevel` 只保留：桌宠组、`above` 前 10 个、与任一宠物矩形重叠的窗口；其余窗口只保留 z/pid/class，不取 title/process/rect。
   - `JsonSerializer.Serialize` + `Log.Diagnostic` 移到后台 `Channel<string>`；UI 线程只采集轻量数据（或整体在后台采集）。
   - 同一 reason 的快照频率从 30s 提高到 5 分钟，或仅在调试开关下开启。
4. `src/Pet/PetZOrderGuard.cs`
   - `NotContiguous` 判定跳过 `overlapsPet == false` 的外部 topmost 窗口（QQ/输入法/NVIDIA 覆盖层等），避免“修不好的连续性问题”导致每秒重排。

**收益**：预计消除当天 108 次 restack 中的大部分（尤其 87 次 `MainNotAboveRemotes`），减少约 2 万次 `Process.GetProcessById`、5.7MB JSON/写盘、4 千次 SetWindowPos；对应“每隔几分钟”的 UI/IO 尖峰应显著下降。

**风险**：低-中。z-order 修复可能延迟 100-300ms，肉眼几乎不可见；快照裁剪会降低极端问题的事后诊断信息，可保留 `--verbose-zorder` 开关。

### P0-2：远程桌宠按身体帧缓存基础位图

**改动点**

1. `src/Pet/RemoteCatWindow.cs` `ComposeInto`（484-590）
   - 把 `_baseFrame` 改为 `_baseFrames[3]`（Body1/2/3），key 不包含 `selectedBody`，而是用 `_bodyFrame` 索引选择缓存；昵称/主题/缩放/选中/镜像变化时统一失效 3 张。
   - `Font`、`GraphicsPath`、`SolidBrush` 按（字号/字体/颜色/圆角）缓存并复用，避免每次重建。
   - 或者拆成“静态底栏+昵称层”和“角色层（3 帧）”两张缓存，每帧只 `DrawImageUnscaled` 合成。
2. `src/Pet/RemoteCatWindow.cs` `SetBody`（374-379）
   - 只切换 `_bodyFrame` + 选择缓存帧，不触发完整 `CatPainter` 重建；保留 `CatPainter.Request` 做合并。
3. `src/Pet/RemoteCatWindow.cs` `RemoteEmoteAnimator`（820-863）/`TickEmote`（428-433）
   - 远程表情动画限制 30fps（33ms），或 `TickEmote` 返回“画面是否变化”，只有变化才 `CatPainter.Request`。
4. `src/Pet/RemoteCatWindow.cs` `CatPainter`（870-935）
   - `Queue` 从 `List.RemoveAt(0)` 改为 `Queue<RemoteCatWindow>`；当可见远程窗口 >4 时禁用 `ImmediateGapMs` 的同步立即绘制，统一走 16ms 泵。

**收益**：每次按键动作从“整图重建 + Font/Path 分配”降为“缓存位图 blit + UpdateLayeredWindow”，预计 UI 线程 CPU 降 50-70%，GC 明显减少；20 只猫时效果最大。

**风险**：中。内存增加约 3×200×341×4 ≈ 0.8MB/窗口（20 只约 16MB）；需要逐像素对比测试动作帧/昵称条/选中框。

### P1-1：悬停命中改 alpha 掩码

**改动点**

- `src/Pet/PetComposer.cs` `HitTestCat`（700-735）/`HasOpaquePixel`（728-735）：在 `PetFrames` 加载时预生成 200×200 的 `byte[]`/`bool[]` alpha 掩码（或 1bpp），命中时直接索引，不再 `Bitmap.GetPixel`。
- `src/Pet/RemoteCatWindow.cs` `ContainsCatScreenPoint`（300-311）使用掩码；`ContainsScreenPoint`（286-297）也不要直接 `_surface.Bitmap.GetPixel`，用主表面 alpha 掩码。
- `src/Pet/PetWindow.cs` `PollHover`（594-604）：先做窗口矩形/联合包围盒预判；`_hoverPollTimer` 150ms 可放宽到 250ms（非悬停时）。

**收益**：20 只猫时每秒约 400 次 GDI+ GetPixel 降为数组索引；UI 线程更稳。

**风险**：低。需保证 alpha 阈值（>8）与原逻辑一致。

### P1-2：进程扫描与全屏检测降频/缓存

**改动点**

- `src/App.cs` `CheckGameModeRules`（556-636）：规则为空时直接跳过；`Process.GetProcessesByName(rule)` 改为每 5 秒一次 `Process.GetProcesses()` 快照 + 名称集合比对；避免每个规则各枚举一遍。
- `src/Core/FullscreenDetector.cs`（49-81）：PID→进程名缓存 5-10 秒；用 `QueryFullProcessImageName` 替代 `Process.GetProcessById`（减少 Process 对象/句柄）。
- 全屏检测 1s 可以保留（只读 Win32 很便宜），但进程名解析只在“确认全屏”后做一次。

**收益**：减少每秒/每 2 秒的进程枚举和 Process 对象分配，降低 GC 与后台 CPU。

**风险**：低。规则切换延迟最多 5 秒，可接受。

### P1-3：WebView2 推送与房间状态序列化

**改动点**

- `src/Ui/PanelWindow.cs` `Push`（342-354）：`JsonSerializer.Serialize` 放到后台线程，UI 线程只 `PostWebMessageAsJson(string)`；用 `ConcurrentQueue`/序列号保证消息顺序。
- `QueuePushRoom`（318-327）：从“每个 UI 消息一次”改为 150-250ms 时间节流；成员列表只推增量（加入/离开/改名/外观），不要每次全量 `RoomState()`。
- `src/App.cs` `RoomState`（1112-1142）：成员 DTO 缓存，只有变化时重建。

**收益**：面板打开 + 20 人房间时，减少 UI 线程 JSON 序列化和 WebView2 消息量。

**风险**：中。WebView2 线程亲和与消息顺序需要仔细处理；建议先做时间节流，再做后台序列化。

### P2：其他低风险优化

| 项目 | 位置 | 改动 | 收益/风险 |
|---|---|---|---|
| 底栏字体缓存 | `PetComposer.DrawStrip` 472、`DrawChests` 596 | 按字号/样式缓存 `Font`，不要每次点击/每秒重建 | 低/低 |
| 成员快照 diff | `RoomManager.ApplyMemberSnapshot` 766 | 复用 `HashSet<long>`，只对变化成员触发 `MemberUpdated`；避免 `Members.Keys.Where(...).ToList()` | 中/低 |
| 日志批量写 | `Core/Log.cs` 193-206、278 | 有界 Channel + 批量 flush；`Warn/Error` 保持同步 | 中/低-中（崩溃可能丢日志） |
| 配置保存异步 | `Core/Config.cs` 424-435 | 300ms 防抖 + 单后台写线程；退出时 flush | 中/中（崩溃丢最后设置） |
| 表情解析缓存 | `RemoteCatWindow.ResolveEmote` 435-445 | 文件名→表情 id 字典缓存，避免每次遍历 504 条 | 低/低 |
| 图片预取 | `AssetStore.GetBubbleImage` 69-95 | 首次磁盘解码/缩放移到后台，先显示占位 | 低/低 |

## 7. 未确认项

1. **没有 WuwatappoNative 实时样本**：无法确认尖峰时具体是哪个线程（UI 线程、GC、ThreadPool、WebView2）占 CPU。上面的根因排序基于日志频率和代码路径，仍需在用户实际运行时用 `tools/PerfMonitor` 采 5-10 分钟确认。
2. **z-order 修复的实际 CPU 成本未直接测量**：28KB 快照 + 92 次 `Process.GetProcessById` 的成本是按代码和日志量推算的；需要 live 采样验证。
3. **`MainNotAboveRemotes` 的触发源**：快照显示某个远程窗口跑到主宠上方，但无法仅凭日志确认是“新成员 Show”还是其他 topmost 应用（mstsc/QQ）插入；建议修复 P0-1 后用同一工具复测。
4. **GDI/USER 是否泄漏**：当天 GDI 41-193、USER 56-167，随桌宠数变化，没有单调增长证据；需要 30-60 分钟连续采样（工具已支持）观察增长斜率。
5. **网络失败对 CPU 的影响**：21 次 `刷新点击数失败`、7 次重连失败是否能造成可感知尖峰，取决于 DNS/TLS/服务端行为；工具只能看到进程总 CPU，需配合 `dotnet-trace`/ETW 才能定位到 socket/HTTP 线程。
6. **`dotnet-counters` / `dotnet-trace` / PerfView 均未安装**：本次只能用 Process/Thread API；GC、线程池、异常、HTTP 指标未采集。

## 8. 附录：本次工具验证命令

```powershell
# 编译
dotnet build tools/PerfMonitor/PerfMonitor.csproj -c Release --nologo

# 帮助
dotnet run --project tools/PerfMonitor -c Release --no-build -- --help

# 无目标进程时的预期提示
dotnet run --project tools/PerfMonitor -c Release --no-build -- --duration 5 --interval 1
# -> 错误：没有找到进程 WuwatappoNative...

# 合成负载自测（PerfLoad：3s 空转 + 2s 休眠）
dotnet run --project tools/PerfMonitor -c Release --no-build -- `
  --pid 47880 --duration 30 --interval 1 --top 4 `
  --out tools/PerfMonitor/reports --log-dir <临时日志目录>
```

主项目编译验证：

```powershell
dotnet build WuwatappoNative.csproj -c Release --nologo -v:q
# EXIT=0
```

根 `WuwatappoNative.csproj` 的唯一改动：

```xml
<Compile Remove="tools\**\*.cs" />
```

它只防止主程序把 `tools/PerfMonitor` 的源码/obj 生成文件编进来，不影响主程序任何业务逻辑。
