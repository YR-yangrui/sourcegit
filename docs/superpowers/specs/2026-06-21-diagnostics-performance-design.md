# Diagnostics 性能设计

## 目标

增加轻量级 diagnostics 和性能 trace 基础设施，使普通桌面构建也能启用诊断，并导出 Perfetto 兼容的 trace 文件。

## 背景

SourceGit 已经有面向用户的 Git 命令日志和 crash log。这些日志对可见 Git 操作有帮助，但无法为仓库刷新、Git 子进程、diff 加载、watcher 触发的刷新，以及 UI 附近的慢路径提供结构化应用诊断、跨操作关联和时间线分析。

实现应保持无新增依赖，因为应用会使用 trimming/AOT 发布，并运行在 Windows、macOS 和 Linux 上。

## 需求

- 在 `Native.OS.DataDir/logs` 下以 JSON Lines 写入结构化诊断事件。
- UI 路径和 Git worker 路径上的日志记录不能阻塞主流程。
- 提供一个小型 `PerfScope` 风格 API，用于测量应用 span。
- 维护一个有界的内存 recent-event buffer，用于 crash 上下文和导出。
- 将最近的 span/log event 导出为 Chrome Trace Event JSON，供 Perfetto 打开。
- 对 app 启动、未处理异常、Git 命令包装器和仓库刷新路径增加低风险 instrumentation。
- 仓库级 event 记录完整本地仓库路径，使 Perfetto 分析时能直接识别来源仓库。
- 默认对敏感值做脱敏或 hash。
- 避免记录文件内容、diff 内容、完整 stdout/stderr 或 secrets。

## 设计

创建 `SourceGit.Diagnostics` 作为小型内部 diagnostics 层：

- `DiagnosticManager` 负责 setup、shutdown、event 记录、后台 JSONL 写入、recent-event buffer、redaction helper 和 Perfetto 导出入口。
- `DiagnosticScope` 使用 `Stopwatch.GetTimestamp()` 测量耗时，并在 dispose 时发出一个完成的 span。
- `DiagnosticEvent` 是 JSONL writer 和 Perfetto exporter 共用的不可变 event payload。
- `PerfettoTraceExporter` 把 recent event 转换成包含 `traceEvents` 的 JSON 文件，其中 span 使用 `X` duration event，log 使用 `i` instant event。

第一版会记录所有显式 instrumentation 的 span，因为选定入口都是粗粒度路径。后续可以通过偏好设置加入 sampling 或更丰富的诊断窗口。

仓库级 event 同时包含 `repo`、用于分组的稳定 hash，以及 `repoPath` 完整本地路径，便于本地 diagnostics 和 Perfetto 直接定位。

## 运行时用法

- JSONL diagnostics 写入 `Native.OS.DataDir/logs/sourcegit-YYYYMMDD.jsonl`。
- 导出时，最近 Perfetto trace 写入 `Native.OS.DataDir/profiles/sourcegit-YYYYMMDD_HHMMSS.perfetto.json`。
- crash 处理会在现有 crash log 旁边写入一个同级 `*.perfetto.json` 文件。
- 设置 `SOURCEGIT_DIAGNOSTICS=0` 可禁用轻量 diagnostics 层。
- 启用 `Preferences > General > Export Perfetto trace on exit` 后，应用正常退出时会写入 recent-events Perfetto trace。
- 可以把任意 `*.perfetto.json` 文件拖入 `https://ui.perfetto.dev` 的 Perfetto UI 打开。

## 埋点

- `App.Main` 初始化并关闭 diagnostics，同时记录未处理 task/domain exception。
- `Native.OS.LogException` 写入最近诊断事件，并生成 crash-adjacent Perfetto trace。
- `Commands.Command` 测量 `ExecAsync`、`ReadToEnd` 和 `ReadToEndAsync`。
- 热路径中的 streaming command 按需要增加局部 span。
- `Repository.RefreshBranches`、`RefreshWorktrees`、`RefreshTags`、`RefreshCommits`、`RefreshSubmodules`、`RefreshWorkingCopyChanges` 和 `RefreshStashes` 记录 refresh 耗时、取消状态和低成本可获得的结果数量。

## 验证

构建应用；如果本地 submodule 依赖不可用，则记录缺失的 baseline 依赖，并运行能覆盖当前改动的最窄 compile check。确认没有移除现有面向用户的 command log 行为。

## 不在范围内

- 不上传外部 telemetry。
- 不增加常驻 profiler。
- 不在本次改动中增加完整 diagnostics UI。
- 不引入 Serilog、OpenTelemetry 或其他新的运行时包依赖。
