# 自定义差异渲染器

SourceGit 可以把指定文件类型的 diff 交给外部渲染器处理，并在差异视图中显示渲染器生成的 HTML 页面。它适合用于二进制文件、生成资源、游戏引擎资源、表格，或任何用专用报告查看更清楚的文件格式。

## 创建渲染器

打开 `偏好设置` -> `自定义差异`，点击新增，然后填写这些字段：

- `启用`：打开或关闭该渲染器。
- `加载时清空上一份内容`：渲染器执行期间先清空旧 HTML，避免误看旧结果。
- `名称`：SourceGit 中展示的渲染器名称。
- `匹配模式`：用分号分隔的 glob，例如 `*.prefab;*.unity`。
- `可执行文件`：要运行的脚本或程序。
- `参数`：传给脚本或程序的命令行参数，可使用 SourceGit 变量。

## 可用变量

SourceGit 启动渲染器前会替换这些变量：

- `$OLD`：旧版本/base 侧文件内容路径。
- `$NEW`：新版本/target 侧文件内容路径。
- `$LOCAL`：本地/新版本侧别名。
- `$REMOTE`：远端/旧版本侧别名。
- `$REPO`：仓库根目录。
- `$PATH`：仓库内相对文件路径。
- `$ORG_PATH`：重命名文件的原路径（如果有）。
- `$BASE`：base revision（如果有）。
- `$TARGET`：target revision（如果有）。
- `$COMMIT`：提交详情场景中的 commit SHA（如果有）。
- `$CONTEXT`：SourceGit diff 上下文。
- `$MODE`：SourceGit 自定义 diff 模式。
- `$TITLE`：建议展示标题。
- `$TEMP`：临时输出目录。

路径可能包含空格，建议始终给变量加引号：

```text
"$OLD" "$NEW" --repo "$REPO" --path "$PATH" --output-dir "$TEMP" --no-open
```

## 输出约定

渲染器应生成一个 HTML 文件，并把该 HTML 的绝对路径输出到 stdout。SourceGit 会读取 stdout，打开输出的 HTML 文件，并在内置 WebView 中展示。

如果渲染器需要输出日志，请写到 stderr 或单独的日志文件，避免 stdout 中混入其它内容。

## 最小示例

PowerShell 渲染器：

```powershell
param(
    [string]$Old,
    [string]$New,
    [string]$Temp
)

$out = Join-Path $Temp "custom-diff.html"
@"
<!doctype html>
<html>
<body>
  <h1>自定义差异</h1>
  <p>旧文件: $Old</p>
  <p>新文件: $New</p>
</body>
</html>
"@ | Set-Content -Path $out -Encoding UTF8
Write-Output $out
```

SourceGit 配置：

```text
可执行文件: powershell
参数: -ExecutionPolicy Bypass -File "C:\Tools\my-renderer.ps1" -Old "$OLD" -New "$NEW" -Temp "$TEMP"
```

## Unity Prefab 示例

Unity prefab/scene 文件可以用外部工具生成 HTML 层级变更报告：

```text
匹配模式:   *.prefab;*.unity
可执行文件: C:\Tools\Unity-Prefab-Diff\prefab_diff.cmd
参数:       "$OLD" "$NEW" --repo "$REPO" --path "$PATH" --context "$CONTEXT" --mode "$MODE" --base "$BASE" --target "$TARGET" --commit "$COMMIT" --title "$TITLE" --output-dir "$TEMP" --print-output --no-open
```

## 建议

- 渲染器输出应尽量稳定：同一组 old/new 输入应生成相同报告。
- stdout 只输出 HTML 路径，日志写到 stderr 或日志文件。
- 需要仓库上下文时，使用 `$REPO` 和 `$PATH`。
- 生成文件写到 `$TEMP`，不要写到源码旁边。
- 耗时缓存建议放到系统临时目录或渲染器自己的缓存目录。
