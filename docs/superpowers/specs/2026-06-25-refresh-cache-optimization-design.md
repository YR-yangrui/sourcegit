# SourceGit 刷新缓存优化设计

## 背景

在 `F:/work/nslg` 上，命令行 `git status` 很快，但 SourceGit 的 F5 刷新会触发 `RefreshAll()`，同时刷新分支、历史、标签、工作区、stash、worktree 和元数据。最近 profile 显示，两次 F5 总耗时约 10 到 11 秒，主要耗时来自：

- `refresh.branches`：约 10 到 11 秒。
- `refresh.commits`：约 6.7 到 7.4 秒。
- `refresh.working_copy`：约 1.7 到 1.9 秒。

`refresh.branches` 的主要成本不是基础分支枚举，而是 `%(upstream:trackshort)` 和后续多次 `rev-list --left-right`。在 nslg 中有 609 个分支、600 个远程分支、4 个 tracking mismatch，其中两个分支 behind 数超过 12000，每次刷新都会重复输出大量 SHA 列表。

## 目标

- 连续 F5 时，如果 refs、HEAD、history 参数和工作区相关状态没有变化，尽量复用内存结果，避免重复发起昂贵 Git 命令。
- 利用 Git SHA 不变性：同一组 SHA 输入对应的对象结果可跨仓库、跨刷新复用。
- 保持 UI 行为正确，尤其是 ahead/behind commit 标记、分支树排序、tag decorator 和 detached HEAD。
- 所有缓存仅为进程内缓存，不落盘。

## 非目标

- 不改变 Git 命令的语义结果。
- 不依赖文件系统 watcher 作为唯一失效来源。
- 不在本阶段处理工作区 `git status` 查询，也不实现 `-uall` 两步法优化。

## 实测结论

在 `F:/work/nslg` 上测得：

| 查询 | 平均耗时 | 说明 |
| --- | ---: | --- |
| `git for-each-ref --format="%(refname)%00%(objectname)%00%(HEAD)%00%(upstream)%00%(worktreepath)" refs/heads refs/remotes refs/tags` | 约 0.8s | 统一 ref snapshot，包含 branch 和 tag |
| `git branch -l --all --format="%(refname)%00%(objectname)"` | 约 0.8s | 不含 tags |
| `git branch -l --all ... %(upstream:trackshort) ...` | 约 3s+ | 显著变慢 |
| `git log -25000 ...` | 约 6.7 到 7.4s | F5 第二大瓶颈 |
| `git rev-parse HEAD` | 约 739ms | Windows 进程启动成本明显 |
| 直接读取 `.git/HEAD` 和 ref 文件 | 约 2.7ms | nslg 实测，结果等于 `rev-parse HEAD` |

普通仓库、detached HEAD 和 linked worktree 的临时实验均验证：按 Git 目录规则直接读取 HEAD/ref 文件可以得到与 `git rev-parse HEAD` 一致的结果。

## 统一 Ref Snapshot

每次 F5 先启动一个轻量 ref snapshot，供 `RefreshBranches`、`RefreshTags` 和 `RefreshCommits` 共享：

```bash
git for-each-ref --format="%(refname)%00%(objectname)%00%(HEAD)%00%(upstream)%00%(worktreepath)" refs/heads refs/remotes refs/tags
```

示例输出中用 `|` 表示 `NUL`：

```text
refs/heads/release_dev|dcd083ee636a1e2bcad7edf2a5cd160aba26df3f|*|refs/remotes/origin/release_dev|F:/work/nslg
refs/heads/release_dev_fix|7214bfe2f43580e037ca143fede761bc763693a1||refs/remotes/origin/release_dev|
refs/remotes/origin/release_dev|ddcc2376eda225bba94471a83a4ffa590ddc6183|||
refs/tags/v1.0.0|9a7b1c...|||
```

使用方式：

- `refs/heads/*` 和 `refs/remotes/*` 用于 branch cache。
- `refs/tags/*` 用于 tag cache 和 history fingerprint。
- 所有 `refname -> objectname` 用于 history cache 的 refs fingerprint。
- 如果某条 branch 的 `%(HEAD)` 为 `*`，该条 `objectname` 就是当前 HEAD SHA。

## Detached HEAD 处理

普通分支状态下，snapshot 已经能通过 `%(HEAD)=*` 得到 HEAD SHA，不需要额外查询。

detached HEAD 下没有 branch 行带 `*`，需要快速读取 HEAD。流程如下：

1. 解析工作区根目录下的 `.git`。
   - 如果 `.git` 是目录，`gitDir = <repo>/.git`。
   - 如果 `.git` 是文件，读取 `gitdir: <path>`，相对路径按工作区根目录解析。这覆盖 linked worktree。
2. 解析 common dir。
   - 如果 `<gitDir>/commondir` 存在，读取其内容，相对路径按 `gitDir` 解析。
   - 否则 `commonDir = gitDir`。
3. 读取 `<gitDir>/HEAD`。
   - 如果内容是 40 或 64 位十六进制 SHA，直接作为 detached HEAD。
   - 如果内容是 `ref: refs/...`，先查 `<gitDir>/<ref>`，再查 `<commonDir>/<ref>`，最后查 `packed-refs`。
4. 任意步骤失败时，退回 `git rev-parse HEAD`。

这样 linked worktree 中的 `.git` 文件、每个 worktree 独立的 `HEAD`、共享 refs 的 `commondir` 都能覆盖。若将来遇到 reftable 或其他非文件 refs 存储，解析失败后会自动 fallback 到 Git 命令。

## Branch Cache

Branch cache key 按分支独立计算，不使用全局 upstream fingerprint：

```text
refname
objectname
HEAD marker
upstream
worktreepath
```

如果 key 不变，复用旧 `Models.Branch`。如果 key 变化，标记该分支为过期。过期分支需要批量重建，不能每个 ref 单独发起一次 Git 命令。

snapshot 已经能直接提供这些字段：

```text
FullName     <- refname
Head         <- objectname
IsCurrent    <- HEAD marker == "*"
Upstream     <- upstream
WorktreePath <- worktreepath
IsLocal      <- refname 是否 refs/heads/
Name/Remote  <- 从 refname 解析
```

`IsUpstreamGone` 通过 `Upstream` 是否存在于当前 remote refs map 计算。

### Branch Miss 批量重建

snapshot 已经提供了大部分 `Branch` 基础字段。对于新增或 key 变化的 branch，如果还需要 `CommitterDate` 等 snapshot 未包含字段，必须合并到一条 branch 查询中。例如：

```bash
git for-each-ref --format="%(refname)%00%(committerdate:unix)%00%(objectname)%00%(HEAD)%00%(upstream)%00%(worktreepath)" <branchRef1> <branchRef2> ...
```

注意该命令不包含 `%(upstream:trackshort)`。ahead/behind 始终由 SHA pair cache 处理。

如果 miss 的 branch 数量很多，命令行长度可能过长，需要按参数长度分批，但仍应是少量批量查询，而不是每个 ref 一条命令。

### CommitterDate

`CommitterDate` 主要用于分支树按提交时间排序。当前 UI 选择按名称排序时，它不是刷新必须字段。

当需要按提交时间排序，或新建分支、fast-forward 等流程需要继承该字段时，可以通过 branch miss 批量查询得到。也可以用 commit SHA 缓存补齐：

```bash
git show -s --format=%ct <headSha>
```

缓存：

```text
headSha -> committerDate
```

可以批量查询多个 miss 的 SHA：

```bash
git show -s --format="%H%x00%ct" <sha1> <sha2> <sha3>
```

## Ahead/Behind Cache

`QueryTrackStatus` 改为 SHA pair 缓存。`rev-list --left-right A...B` 的结果只由有方向的两个 SHA 决定。

缓存 key：

```text
localHead + "\0" + remoteHead
```

cache miss 时执行：

```bash
git rev-list --left-right <localHead>...<remoteHead>
```

示例输出：

```text
>aaa111...
>bbb222...
<ccc333...
```

解析：

```text
Behind = [aaa111..., bbb222...]
Ahead  = [ccc333...]
```

下次两端 SHA 不变时，直接复制缓存列表到 `Branch.Ahead` 和 `Branch.Behind`，不再跑 `rev-list`。

## Tag Cache

Ref snapshot 中的 tag 行只提供：

```text
refname
objectname
```

tag cache key：

```text
refname + "\0" + objectname
```

如果 tag key 不变，复用旧 `Models.Tag`。如果新增或变化，标记为过期。过期 tag 必须合并到一条 tag 查询中，不能每个 tag 单独发起一次 Git 命令：

```bash
git for-each-ref --format="%(refname)%00%(objecttype)%00%(objectname)%00%(*objectname)%00%(taggername)±%(taggeremail)%00%(creatordate:unix)%00%(contents:subject)%0a%0a%(contents:body)" <tagRef1> <tagRef2> ...
```

删除的 tag 从缓存和 UI 列表中移除。

## History Cache

`RefreshCommits` 当前使用：

```bash
git log --no-show-signature --decorate=full --format=... -25000 <historyArgs>
```

history cache key：

```text
historyArgs
HEAD SHA
refsFingerprint
```

`refsFingerprint` 由当前 snapshot 中所有 `refname -> objectname` 排序后 hash 生成，包含：

```text
refs/heads/*
refs/remotes/*
refs/tags/*
```

必须包含 tags，因为默认历史参数会包含 `--tags`，且 `--decorate=full` 的输出也会受 tag 移动、新增、删除影响。

如果开启 `--reflog`，第一阶段禁用 history cache，因为 reflog 结果不只由 refs snapshot 决定。

每个 repository view 只保留最近一次 history cache，不需要保留多份。原因是 F5 刷新通常重复同一组历史参数；当用户切换过滤器、排序或历史选项时，旧缓存命中概率低，保留多份会增加内存和失效复杂度。

单槽结构：

```text
lastHistoryKey
lastHistoryCommits
```

如果本次 key 等于 `lastHistoryKey`，复用 `lastHistoryCommits`；否则执行 `git log` 并覆盖这一槽。

## 刷新流程

推荐流程：

1. F5 启动共享 `QueryRefSnapshot`。
2. `RefreshBranches` 等 snapshot。
   - 对比每个 branch key。
   - 未变复用旧 `Branch`。
   - 新增或变化的 branch 汇总后，用一条 branch 批量查询补齐过期信息。
   - ahead/behind 通过 SHA pair cache 补齐。
3. `RefreshTags` 等 snapshot。
   - 未变复用旧 `Tag`。
   - 新增或变化的 tag 汇总后，用一条 tag 批量查询补齐过期信息。
4. `RefreshCommits` 等 snapshot 和 HEAD SHA。
   - 使用 `historyArgs + HEAD SHA + refsFingerprint` 查缓存。
   - 每个 repository view 只检查最近一次 history cache。
   - 命中则复用 commit list。
   - 未命中才跑 `git log`，并覆盖最近一次缓存。
5. 其他刷新任务继续并行执行。

## 缓存容量和失效

- 缓存均为进程内缓存。
- `QueryTrackStatus`、`CommitterDate` 和 object 类缓存可用全进程 LRU，容量建议 4096 到 16384。
- Branch、Tag cache 建议按 repository view 持有，关闭 repository 时释放。
- History cache 按 repository view 保留单槽最近结果，关闭 repository 时释放。
- 所有缓存命中都必须由当前 snapshot 或 SHA key 证明，不依赖 watcher。
- Git 命令执行失败、snapshot 解析失败或 HEAD 快速读取失败时，退回当前全量查询路径。

## Diagnostics 记录

新增或复用 profiler 字段：

```text
refSnapshot.duration
refSnapshot.refCount
refSnapshot.headSource = snapshot | file | git
branchCache.hit
branchCache.miss
branchCache.deleted
tagCache.hit
tagCache.miss
historyCache.hit
historyCache.miss
trackStatusCache.hit
trackStatusCache.miss
committerDateCache.hit
committerDateCache.miss
```

目标是让 F5 后能直接判断耗时是否从 `branches/rev-list/log` 转移到更小的 snapshot 成本。

## 验证计划

- 在普通分支、detached HEAD、linked worktree 中验证快速 HEAD 读取结果等于 `git rev-parse HEAD`。
- 在 nslg 连续 F5 两次，第二次应看到 `trackStatusCache.hit > 0`，`historyCache.hit=true`，`branchCache.hit` 接近分支总数。
- 新增、删除、移动 branch/tag 后，snapshot diff 应只影响对应 refs。
- 修改 upstream 后，对应 branch 的 `upstream` 字段变化，branch cache miss。
- 新增 linked worktree 或切换 worktree 绑定后，对应 branch 的 `worktreepath` 字段变化，branch cache miss。
- detached HEAD 下 history fingerprint 使用文件读取得到的 HEAD SHA。
