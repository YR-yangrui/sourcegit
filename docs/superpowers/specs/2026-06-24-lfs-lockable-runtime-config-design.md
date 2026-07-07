# LFS Lockable 运行时配置设计

## 目标

增加一个仓库级选项，用于控制 Git LFS lockable 文件行为，并一致地应用到 SourceGit 启动的所有 Git 进程。

该选项有三种状态：

- `false`：追加 `-c lfs.setlockablereadonly=false`；这是默认值，优先保证性能。
- `true`：追加 `-c lfs.setlockablereadonly=true`；保留 Git LFS lockable 文件的只读/可写行为。
- `null`：不追加任何 `lfs.setlockablereadonly` 覆盖参数，让 Git 和 Git LFS 使用正常配置解析结果。

现有的 `-c core.hooksPath=` 绕行逻辑需要移除。

## 架构

在 `Models.RepositorySettings` 上保存该配置，类型为可空布尔值。仓库配置窗口在 Git 页签中提供三态选择。

在 commands 层增加一个小型共享 Git 运行时配置辅助模块。仓库打开时将工作区路径、git dir、git common dir 与 `RepositorySettings` 注册到该模块；Git 命令启动时只查询这份内存映射，并追加命令行形式的 `-c key=value` 参数。以后如果还要增加类似的运行时 Git 覆盖项，可以集中扩展这个模块，而不是把硬编码的 `-c` 参数散落到各个命令类里。

## 数据流

仓库打开后，现有的 `RepositorySettings` 对象仍然是配置来源。`Repository` 构造或打开阶段把路径到 settings 的映射注册到共享辅助模块。任何 SourceGit Git 命令在构建 `ProcessStartInfo` 时，都通过共享辅助模块查询当前仓库应使用的运行时覆盖参数。

对于 `EnableLFSLockableFiles`：

- `true` 生成 `-c lfs.setlockablereadonly=true`。
- `false` 生成 `-c lfs.setlockablereadonly=false`。
- `null` 不生成任何参数。

共享辅助模块既要被 `Command` 基类使用，也要被当前直接创建 `ProcessStartInfo`、没有继承基类路径的少量静态 helper 使用。

## UI

在仓库配置窗口的 Git 页签增加一个 ComboBox，用于配置 Git LFS lockable 文件支持。选项为：

- 禁用
- 启用
- 使用 Git 配置

该控件需要提供 tooltip，说明该开关存在的原因：Git LFS 默认会在 `post-checkout` 中检查 lockable 文件的只读/可写状态；在大型仓库里，discard、checkout、restore 等文件恢复操作可能因此触发较重的文件扫描。禁用该选项可以提升这些操作的性能，但 SourceGit 启动的 Git 命令将不再维护 LFS lockable 文件的只读状态。

新仓库和旧仓库缺省值都是 `false`，因此 SourceGit 默认会避免 Git LFS `post-checkout` 中的 lockable 扫描。

## 错误处理

如果某个 Git 进程无法从注册表中找到仓库设置，SourceGit 应该省略运行时覆盖参数，而不是让命令失败。这样可以保持临时路径、异常路径、仓库尚未打开前的探测命令或上下文不完整命令的现有行为。

现有 settings 反序列化需要兼容缺失字段。旧的 `sourcegit.settings` 文件会映射到新默认值 `false`。

## 移除项

移除 `Checkout.ResetFilesToConflictStateAsync` 中当前使用的 `-c core.hooksPath=`。hook 行为应由仓库正常 hook 配置和新的 LFS lockable 设置控制，而不是为某个命令禁用所有 hook。

## 测试

实现后需要验证：

- 构建通过，确保绑定和序列化没有错误。
- 针对 `true`、`false`、`null` 做聚焦的命令参数构建检查。
- 手动或脚本化仓库测试，证明禁用时 SourceGit 会生成 `git -c lfs.setlockablereadonly=false ...`。
- 用 grep 确认不再保留 `core.hooksPath=` 绕行逻辑。
