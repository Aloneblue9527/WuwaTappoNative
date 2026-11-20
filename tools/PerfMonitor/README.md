# PerfMonitor —— WuwatappoNative 运行状态监测工具

独立 .NET 8 控制台工具，用于观察 `WuwatappoNative`（或指定 PID）在运行期间的 CPU、内存、GDI/USER 对象、句柄、线程和周期性尖峰。**只读采样，不会结束、挂起、注入或修改目标进程。**

- 目标框架：`net8.0-windows`
- 依赖：仅 .NET 8 SDK / Runtime，无 NuGet 包
- 报告：控制台表格 + CSV + JSON + 日志关联文本

## 快速开始

在仓库根目录执行：

```powershell
# 默认：按进程名 WuwatappoNative，采样 300 秒，每秒 1 次
dotnet run --project tools/PerfMonitor -c Release -- --duration 300 --interval 1

# 附加指定 PID，采样 2 分钟，每次输出前 12 个线程
dotnet run --project tools/PerfMonitor -c Release -- --pid 12345 --duration 120 --top 12

# 报告写到指定目录
dotnet run --project tools/PerfMonitor -c Release -- --process WuwatappoNative --out out/perf
```

`Ctrl+C` 会提前结束采样，但已采到的样本仍会写入报告。

## 参数

| 参数 | 默认值 | 说明 |
|---|---|---|
| `--pid <PID>` | 空 | 附加指定进程；优先级高于 `--process` |
| `--process <名称>` | `WuwatappoNative` | 按进程名查找，可省略 `.exe` |
| `--duration <秒>` | `300` | 总采样时长 |
| `--interval <秒>` | `1.0` | 采样间隔，最小 0.2 |
| `--top <N>` | `8` | 每次采样保留/输出前 N 个线程 |
| `--out <目录>` | `<当前目录>/out/perf` | 报告输出目录 |
| `--log-dir <目录>` | `%LOCALAPPDATA%\WuwatappoNative\logs` | `app-*.log` 所在目录 |
| `--no-log` | 关闭 | 不做日志关联 |
| `--with-counters` | 关闭 | 检测到 `dotnet-counters` 时顺带采集 counters CSV |
| `--spike-factor <倍数>` | `2.0` | 尖峰阈值 = 基线 × 倍数 |
| `--spike-min-delta <pp>` | `0.75` | 相对基线的最小增量（归一化百分点），基线接近 0 时兜底 |
| `--baseline-window <N>` | `60` | 基线取最近 N 个样本的中位数 |
| `--warmup <N>` | `5` | 前 N 个样本只预热，不参与尖峰判定 |
| `--log-before <秒>` | `20` | 日志关联：尖峰开始前窗口 |
| `--log-after <秒>` | `10` | 日志关联：尖峰结束后窗口 |

## 采样内容

每个采样点输出：

- **进程 CPU%**：`Process.TotalProcessorTime` 增量 ÷ 墙钟时间 ÷ 逻辑核数（0..100）
- **单核 CPU%**：同样的增量 ÷ 墙钟时间（单核口径，可能超过 100）
- WorkingSet / PrivateMemory（MB）
- GDI 对象数、USER 对象数（`GetGuiResources`）
- HandleCount、ThreadCount
- 线程 CPU 时间增量（毫秒）
- Top N 线程：线程 ID、线程描述（`GetThreadDescription`，拿不到则为空）、CPU%（单核）、累计 CPU ms、优先级、状态、等待原因

> GDI/USER 计数在受保护进程或权限不足时可能为 0；其他指标仍可用。

## 尖峰识别

对每个采样点计算历史基线：最近 `baseline-window` 个样本（跳过 warmup）的 CPU 中位数。

```text
阈值 = max(基线 × spike-factor, 基线 + spike-min-delta)
```

- 连续 **2 个**采样点都 ≥ 阈值才记为尖峰（避免单点抖动）
- 尖峰持续到最后一个仍 ≥ 阈值的样本；连续 2 个样本回落到结束阈值以下才结束
- 记录：起止时间、持续时间、峰值、峰值线程、基线、阈值
- 记录相邻尖峰间隔：距上一个尖峰结束的秒数，以及 start-to-start 间隔
- 输出全样本的中位数 / p95 / 最大值

默认阈值下，一个基线 0.2% 的空闲进程，CPU 归一化超过 1.0%（约 12 核里跑满 12% 单核）并持续 2 秒才会被记为尖峰。

## 日志关联

默认读取 `%LOCALAPPDATA%\WuwatappoNative\logs\app-*.log`（含最近分卷），按行解析：

```text
yyyy-MM-dd HH:mm:ss.fff [LEVEL][category] message
```

- 只保留采样时间窗口内的行
- 对每个尖峰，列出 `尖峰开始 - log-before` 到 `尖峰结束 + log-after` 的行
- 每个尖峰最多 200 行，避免报告过大
- `token` / `cookie` / `password` / `secret` / `authorization` 等字段会被替换为 `<redacted>`，不会写入报告
- 不读取 `diag-*.log` / `synth-*.jsonl`（避免把大体积诊断快照和合成数据带进报告）

## 报告文件

每次运行生成：

```text
perf-<进程名>-<yyyyMMdd-HHmmss>.csv            # 每个采样点一行
perf-<进程名>-<yyyyMMdd-HHmmss>-threads.csv    # 每个采样点的 Top N 线程
perf-<进程名>-<yyyyMMdd-HHmmss>.json           # 完整摘要、尖峰、线程汇总、日志关联
perf-<进程名>-<yyyyMMdd-HHmmss>-log.txt        # 与尖峰对齐的日志行（已脱敏）
perf-<进程名>-<yyyyMMdd-HHmmss>-counters.csv   # 仅 --with-counters 且工具存在时
```

CSV 使用 UTF-8 BOM，方便 Excel 直接打开；JSON 使用 UTF-8 无 BOM。

## 可选诊断工具

工具启动时会检测 `dotnet-counters`、`dotnet-trace`、`PerfView`，只打印检测结果，不会自动安装。检测到 `dotnet-counters` 且传入 `--with-counters` 时，会在采样期间额外启动一份 counters 采集，采样结束后只结束这个辅助进程。

手工排查示例：

```powershell
dotnet-counters monitor -p <PID> --refresh-interval 1
dotnet-trace collect -p <PID> --duration 00:01:00 -o trace.nettrace
```

## 自测

没有 `WuwatappoNative` 实例时，可以用任意现有进程验证采样链路：

```powershell
dotnet run --project tools/PerfMonitor -c Release -- --pid $PID --duration 15 --interval 1 --no-log
```

尖峰算法可以用合成样本单测（仓库外临时项目即可）：构造低基线 + 两段高 CPU 样本，调用 `SpikeAnalyzer.Detect`，应得到两段尖峰。日志关联与脱敏可以用临时 `app-2026-11-20.log` 验证。

## 与主项目的关系

`tools/PerfMonitor` 是独立项目。主项目 `WuwatappoNative.csproj` 使用 SDK 默认的 `**/*.cs` 编译项，因此根 csproj 增加了：

```xml
<Compile Remove="tools\**\*.cs" />
```

这行只用于把工具源码排除出主程序编译，不改变任何业务逻辑。
