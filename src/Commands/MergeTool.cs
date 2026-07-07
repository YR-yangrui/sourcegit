using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class MergeTool : Command
    {
        public class ExternalToolConfig
        {
            public string Name { get; init; } = string.Empty;
            public string Exec { get; init; } = string.Empty;
            public string Args { get; init; } = string.Empty;
            public string FallbackReason { get; init; } = string.Empty;
            public bool CanRunDirectly => !string.IsNullOrEmpty(Exec) && !string.IsNullOrEmpty(Args);
        }

        public class MergeFiles
        {
            public string Base { get; init; } = string.Empty;
            public string Local { get; init; } = string.Empty;
            public string Remote { get; init; } = string.Empty;
            public string Merged { get; init; } = string.Empty;
            public string TempDir { get; init; } = string.Empty;
        }

        public MergeTool(string repo, string file)
        {
            WorkingDirectory = repo;
            Context = repo;
            _file = string.IsNullOrEmpty(file) ? string.Empty : file.Quoted();
        }

        public async Task<bool> OpenAsync()
        {
            var tool = Native.OS.GetDiffMergeTool(false);
            if (tool == null)
            {
                RaiseException("Invalid diff/merge tool in preference setting!");
                return false;
            }

            if (string.IsNullOrEmpty(tool.Cmd))
            {
                var ok = await CheckGitConfigurationAsync();
                if (!ok)
                    return false;

                Args = $"mergetool -g --no-prompt {_file}";
            }
            else
            {
                var cmd = $"{tool.Exec.Quoted()} {tool.Cmd}";
                Args = $"-c mergetool.sourcegit.cmd={cmd.Quoted()} -c mergetool.writeToTemp=true -c mergetool.keepBackup=false -c mergetool.trustExitCode=true mergetool --tool=sourcegit {_file}";
            }

            return await ExecAsync().ConfigureAwait(false);
        }

        public static async Task<ExternalToolConfig> ResolveGitConfiguredToolAsync(string repo)
        {
            var config = await new Config(repo).ReadAllAsync().ConfigureAwait(false);
            if (!config.TryGetValue("merge.guitool", out var name) || string.IsNullOrWhiteSpace(name))
                config.TryGetValue("merge.tool", out name);

            name = name?.Trim();
            if (string.IsNullOrEmpty(name))
                return new ExternalToolConfig { FallbackReason = "Missing git configuration: merge.guitool" };

            if (name.StartsWith("vimdiff", StringComparison.Ordinal) ||
                name.StartsWith("nvimdiff", StringComparison.Ordinal))
                return new ExternalToolConfig { Name = name, FallbackReason = $"CLI based merge tool \"{name}\" is not supported by this app!" };

            var keyPrefix = $"mergetool.{name.ToLowerInvariant()}.";
            if (config.TryGetValue($"{keyPrefix}cmd", out var cmd) && IsSimpleMergeCommand(cmd) && TrySplitCommand(cmd, out var exec, out var args))
                return new ExternalToolConfig { Name = name, Exec = exec, Args = args };

            if (config.TryGetValue($"{keyPrefix}path", out var path) && TryBuildKnownGitTool(name, path, out var known))
                return known;

            return new ExternalToolConfig
            {
                Name = name,
                FallbackReason = $"Git merge tool \"{name}\" does not expose mergetool.{name}.cmd",
            };
        }

        public static bool TryStartExternal(string repo, ExternalToolConfig tool, MergeFiles files, out Process proc, out string error)
        {
            var args = tool.Args
                .Replace("$LOCAL", files.Local, StringComparison.Ordinal)
                .Replace("$REMOTE", files.Remote, StringComparison.Ordinal)
                .Replace("$BASE", files.Base, StringComparison.Ordinal)
                .Replace("$MERGED", files.Merged, StringComparison.Ordinal);

            var start = new ProcessStartInfo
            {
                WorkingDirectory = repo,
                FileName = tool.Exec,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            proc = null;
            error = string.Empty;
            try
            {
                proc = Process.Start(start);
                if (proc == null)
                {
                    error = "Failed to start external merge tool.";
                    return false;
                }

                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        public static async Task<bool> OpenExternalAsync(string repo, ExternalToolConfig tool, MergeFiles files)
        {
            if (!TryStartExternal(repo, tool, files, out var proc, out var error))
            {
                Models.Notification.Send(repo, error, true);
                return false;
            }

            using (proc)
            {
                await proc.WaitForExitAsync().ConfigureAwait(false);
                return proc.ExitCode == 0;
            }
        }

        private async Task<bool> CheckGitConfigurationAsync()
        {
            var tool = await new Config(WorkingDirectory).GetAsync("merge.guitool");
            if (string.IsNullOrEmpty(tool))
                tool = await new Config(WorkingDirectory).GetAsync("merge.tool");

            if (string.IsNullOrEmpty(tool))
            {
                RaiseException("Missing git configuration: merge.guitool");
                return false;
            }

            if (tool.StartsWith("vimdiff", StringComparison.Ordinal) ||
                tool.StartsWith("nvimdiff", StringComparison.Ordinal))
            {
                RaiseException($"CLI based merge tool \"{tool}\" is not supported by this app!");
                return false;
            }

            return true;
        }

        private static bool TrySplitCommand(string command, out string exec, out string args)
        {
            exec = string.Empty;
            args = string.Empty;

            var parts = SplitCommandLine(command);
            if (parts.Count == 0)
                return false;

            exec = parts[0];
            args = GetArgumentsPart(command);

            return !string.IsNullOrWhiteSpace(exec);
        }

        private static bool IsSimpleMergeCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                return false;

            if (!command.Contains("$LOCAL", StringComparison.Ordinal) ||
                !command.Contains("$REMOTE", StringComparison.Ordinal) ||
                !command.Contains("$MERGED", StringComparison.Ordinal))
                return false;

            return !command.Contains('\n') &&
                   !command.Contains('\r') &&
                   !command.Contains(';') &&
                   !command.Contains("&&", StringComparison.Ordinal) &&
                   !command.Contains("||", StringComparison.Ordinal) &&
                   !command.Contains('|');
        }

        private static string GetArgumentsPart(string command)
        {
            var trimmed = command.TrimStart();
            if (trimmed.Length == 0)
                return string.Empty;

            var inQuotes = false;
            var escaped = false;
            for (var i = 0; i < trimmed.Length; i++)
            {
                var c = trimmed[i];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (c == '"')
                {
                    inQuotes = !inQuotes;
                    continue;
                }

                if (char.IsWhiteSpace(c) && !inQuotes)
                    return trimmed.Substring(i).TrimStart();
            }

            return string.Empty;
        }

        private static List<string> SplitCommandLine(string command)
        {
            var parts = new List<string>();
            var start = -1;
            var inQuotes = false;
            var escaped = false;

            for (var i = 0; i < command.Length; i++)
            {
                var c = command[i];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (c == '"')
                {
                    if (start < 0)
                        start = i + 1;

                    inQuotes = !inQuotes;
                    continue;
                }

                if (char.IsWhiteSpace(c) && !inQuotes)
                {
                    if (start >= 0)
                    {
                        parts.Add(TrimToken(command.Substring(start, i - start)));
                        start = -1;
                    }
                }
                else if (start < 0)
                {
                    start = i;
                }
            }

            if (start >= 0)
                parts.Add(TrimToken(command.Substring(start)));

            return parts;
        }

        private static string TrimToken(string token)
        {
            token = token.Trim();
            if (token.Length >= 2 && token[0] == '"' && token[^1] == '"')
                return token.Substring(1, token.Length - 2);

            return token;
        }

        private static bool TryBuildKnownGitTool(string name, string path, out ExternalToolConfig tool)
        {
            tool = null;

            if (string.IsNullOrWhiteSpace(path))
                return false;

            var normalized = name.ToLowerInvariant();
            switch (normalized)
            {
                case "bc":
                case "bc3":
                case "bc4":
                    tool = new ExternalToolConfig
                    {
                        Name = name,
                        Exec = path,
                        Args = "\"$LOCAL\" \"$REMOTE\" \"$BASE\" -mergeoutput=\"$MERGED\"",
                    };
                    return true;
                case "kdiff3":
                    tool = new ExternalToolConfig
                    {
                        Name = name,
                        Exec = path,
                        Args = "\"$REMOTE\" -b \"$BASE\" \"$LOCAL\" -o \"$MERGED\"",
                    };
                    return true;
                case "p4merge":
                    tool = new ExternalToolConfig
                    {
                        Name = name,
                        Exec = path,
                        Args = "-tw 4 \"$BASE\" \"$LOCAL\" \"$REMOTE\" \"$MERGED\"",
                    };
                    return true;
                case "winmerge":
                    tool = new ExternalToolConfig
                    {
                        Name = name,
                        Exec = path,
                        Args = "\"$MERGED\"",
                    };
                    return true;
                case "tortoisemerge":
                    tool = new ExternalToolConfig
                    {
                        Name = name,
                        Exec = path,
                        Args = "-base:\"$BASE\" -theirs:\"$REMOTE\" -mine:\"$LOCAL\" -merged:\"$MERGED\"",
                    };
                    return true;
                case "meld":
                    tool = new ExternalToolConfig
                    {
                        Name = name,
                        Exec = path,
                        Args = "\"$LOCAL\" \"$BASE\" \"$REMOTE\" --output \"$MERGED\"",
                    };
                    return true;
                default:
                    return false;
            }
        }

        private string _file;
    }
}
