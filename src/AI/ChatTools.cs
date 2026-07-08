using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using OpenAI.Chat;

namespace SourceGit.AI
{
    public static class ChatTools
    {
        public static readonly ChatTool GetDetailChangesInFile = ChatTool.CreateFunctionTool(
            "GetDetailChangesInFile",
            "Get the detailed changes in the specified file in the specified repository.",
            BinaryData.FromBytes(Encoding.UTF8.GetBytes("""
            {
                "type": "object",
                "properties": {
                    "repo": {
                        "type": "string",
                        "description": "The path to the repository."
                    },
                    "file": {
                        "type": "string",
                        "description": "The path to the file."
                    },
                    "originalFile": {
                        "type": "string",
                        "description": "The path to the original file when it has been renamed or copied."
                    },
                    "baseRevision": {
                        "type": "string",
                        "description": "Optional base revision or tree for reviewing a historical commit."
                    },
                    "revision": {
                        "type": "string",
                        "description": "Optional target revision for reviewing a historical commit."
                    }
                 },
                 "required": ["repo", "file"]
            }
            """)), false);

        public static async Task<ToolChatMessage> ProcessAsync(ChatToolCall call, Action<string> output)
        {
            using var doc = JsonDocument.Parse(call.FunctionArguments);

            if (call.FunctionName.Equals(GetDetailChangesInFile.FunctionName))
            {
                var hasRepo = doc.RootElement.TryGetProperty("repo", out var repoPath);
                var hasFile = doc.RootElement.TryGetProperty("file", out var filePath);
                var hasOriginalFile = doc.RootElement.TryGetProperty("originalFile", out var originalFilePath);
                var hasBaseRevision = doc.RootElement.TryGetProperty("baseRevision", out var baseRevision);
                var hasRevision = doc.RootElement.TryGetProperty("revision", out var revision);
                if (!hasRepo)
                    throw new ArgumentException("repo", "The repo argument is required");
                if (!hasFile)
                    throw new ArgumentException("file", "The file argument is required");

                output?.Invoke($"Read changes in file: {filePath.GetString()}");

                var repo = repoPath.GetString();
                var file = filePath.GetString();
                var orgFilePath = hasOriginalFile ? originalFilePath.GetString() : string.Empty;
                var based = hasBaseRevision ? baseRevision.GetString() : string.Empty;
                var target = hasRevision ? revision.GetString() : string.Empty;
                var message = await ReadDetailChangesAsync(repo, file, orgFilePath, based, target).ConfigureAwait(false);
                return new ToolChatMessage(call.Id, message);
            }

            throw new NotSupportedException($"The tool {call.FunctionName} is not supported");
        }

        private static async Task<string> ReadDetailChangesAsync(string repo, string file, string originalFile, string baseRevision, string revision)
        {
            var option = CreateDiffOption(file, originalFile, baseRevision, revision);

            var custom = await TryReadCustomDiffRendererAsync(repo, option).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(custom))
                return custom;

            var structured = await TryReadStructuredDiffAsync(repo, option).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(structured))
                return structured;

            var rs = await new Commands.GetFileChangeForAI(repo, file, originalFile, baseRevision, revision)
                .ReadAsync()
                .ConfigureAwait(false);
            return rs.IsSuccess ? rs.StdOut : string.Empty;
        }

        private static Models.DiffOption CreateDiffOption(string file, string originalFile, string baseRevision, string revision)
        {
            var change = new Models.Change
            {
                Path = file ?? string.Empty,
                OriginalPath = originalFile ?? string.Empty,
                Index = string.IsNullOrEmpty(originalFile) ? Models.ChangeState.Modified : Models.ChangeState.Renamed,
            };

            if (!string.IsNullOrWhiteSpace(revision))
                return new Models.DiffOption(baseRevision, revision, change);

            return new Models.DiffOption(change, false);
        }

        private static async Task<string> TryReadStructuredDiffAsync(string repo, Models.DiffOption option)
        {
            if (!Models.StructuredDiffBuilder.CanHandle(option.Path))
                return string.Empty;

            var structured = await Models.StructuredDiffBuilder.BuildAsync(repo, option).ConfigureAwait(false);
            if (structured == null)
                return string.Empty;

            return StructuredDiffToText(structured);
        }

        private static async Task<string> TryReadCustomDiffRendererAsync(string repo, Models.DiffOption option)
        {
            var pref = ViewModels.Preferences.Instance;
            var renderer = pref.EnableCustomDiffRenderers ? pref.FindCustomDiffRenderer(option.Path) : null;
            if (renderer == null)
                return string.Empty;

            var command = new Commands.RenderCustomDiff(repo, option, renderer, option.Path);
            var text = await command.RunForAIAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            return $"# Custom diff renderer: {renderer.Name}\n\n{text}";
        }

        private static string StructuredDiffToText(Models.StructuredDiff diff)
        {
            if (!string.IsNullOrWhiteSpace(diff.FormattedText))
                return $"# {diff.Summary}\n\n{diff.FormattedText.Trim()}";

            var lines = new List<string> { $"# {diff.Summary}" };
            if (diff.Sheets is { Count: > 0 })
                AppendSheets(lines, diff.Sheets);
            if (diff.Rows is { Count: > 0 })
                AppendNodes(lines, diff.Rows);

            return string.Join('\n', lines).Trim();
        }

        private static void AppendSheets(List<string> lines, List<Models.StructuredDiffSheet> sheets)
        {
            foreach (var sheet in sheets)
            {
                if (sheet.Added == 0 && sheet.Deleted == 0 && sheet.Modified == 0)
                    continue;

                lines.Add(string.Empty);
                lines.Add($"## {sheet.Header}");
                foreach (var row in sheet.DataRows)
                {
                    if (!row.HasChanges)
                        continue;

                    var rowLabel = row.SourceRowNumber > 0 ? row.SourceRowNumber.ToString() : "?";
                    for (var i = 0; i < row.Cells.Count; i++)
                    {
                        var cell = row.Cells[i];
                        if (cell.IsHeader || cell.Change == Models.StructuredDiffChangeKind.None)
                            continue;

                        var column = i < sheet.HeaderCells.Count ? sheet.HeaderCells[i].DisplayText : $"Column {i + 1}";
                        lines.Add($"- Row {rowLabel}, {column}: {FormatCellChange(cell)}");
                    }
                }
            }
        }

        private static string FormatCellChange(Models.StructuredDiffCell cell)
        {
            return cell.Change switch
            {
                Models.StructuredDiffChangeKind.Added => $"Added '{cell.NewText}'",
                Models.StructuredDiffChangeKind.Deleted => $"Deleted '{cell.OldText}'",
                Models.StructuredDiffChangeKind.Modified => $"'{cell.OldText}' => '{cell.NewText}'",
                _ => cell.DisplayText,
            };
        }

        private static void AppendNodes(List<string> lines, List<Models.StructuredDiffNode> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.Change == Models.StructuredDiffChangeKind.None && node.Properties.Count == 0)
                    continue;

                lines.Add(string.Empty);
                lines.Add($"## {node.ChangeLabel} {node.Path}".Trim());
                foreach (var prop in node.Properties)
                    lines.Add($"- {prop.Key}: {FormatPropertyChange(prop)}");
            }
        }

        private static string FormatPropertyChange(Models.StructuredPropertyChange prop)
        {
            return prop.Change switch
            {
                Models.StructuredDiffChangeKind.Added => $"Added '{prop.NewValue}'",
                Models.StructuredDiffChangeKind.Deleted => $"Deleted '{prop.OldValue}'",
                Models.StructuredDiffChangeKind.Modified => $"'{prop.OldValue}' => '{prop.NewValue}'",
                _ => prop.NewValue,
            };
        }
    }
}
