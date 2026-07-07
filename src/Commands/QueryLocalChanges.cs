using System.Collections.Generic;
using System;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public partial class QueryLocalChanges : Command
    {
        [GeneratedRegex(@"^(\s?[\w\?]{1,4})\s+(.+)$")]
        private static partial Regex REG_FORMAT();

        public QueryLocalChanges(string repo, bool includeUntracked = true, bool noOptionalLocks = true)
        {
            WorkingDirectory = repo;
            Context = repo;

            var builder = new StringBuilder();
            if (noOptionalLocks)
                builder.Append("--no-optional-locks ");
            builder.Append($"{GetStatusUntrackedArg(includeUntracked)} --ignore-submodules=dirty --porcelain");

            Args = builder.ToString();
        }

        public async Task<List<Models.Change>> GetResultAsync()
        {
            var outs = new List<Models.Change>();
            using var span = StartGitDiagnosticSpan("query_local_changes");

            try
            {
                using var proc = new Process();
                proc.StartInfo = CreateGitStartInfo(true);
                proc.Start();

                while (await proc.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    var match = REG_FORMAT().Match(line);
                    if (!match.Success)
                        continue;

                    var change = new Models.Change() { Path = match.Groups[2].Value };
                    var status = match.Groups[1].Value;

                    switch (status)
                    {
                        case " M":
                            change.Set(Models.ChangeState.None, Models.ChangeState.Modified);
                            break;
                        case " T":
                            change.Set(Models.ChangeState.None, Models.ChangeState.TypeChanged);
                            break;
                        case " A":
                            change.Set(Models.ChangeState.None, Models.ChangeState.Added);
                            break;
                        case " D":
                            change.Set(Models.ChangeState.None, Models.ChangeState.Deleted);
                            break;
                        case " R":
                            change.Set(Models.ChangeState.None, Models.ChangeState.Renamed);
                            break;
                        case " C":
                            change.Set(Models.ChangeState.None, Models.ChangeState.Copied);
                            break;
                        case "M":
                            change.Set(Models.ChangeState.Modified);
                            break;
                        case "MM":
                            change.Set(Models.ChangeState.Modified, Models.ChangeState.Modified);
                            break;
                        case "MT":
                            change.Set(Models.ChangeState.Modified, Models.ChangeState.TypeChanged);
                            break;
                        case "MD":
                            change.Set(Models.ChangeState.Modified, Models.ChangeState.Deleted);
                            break;
                        case "T":
                            change.Set(Models.ChangeState.TypeChanged);
                            break;
                        case "TM":
                            change.Set(Models.ChangeState.TypeChanged, Models.ChangeState.Modified);
                            break;
                        case "TT":
                            change.Set(Models.ChangeState.TypeChanged, Models.ChangeState.TypeChanged);
                            break;
                        case "TD":
                            change.Set(Models.ChangeState.TypeChanged, Models.ChangeState.Deleted);
                            break;
                        case "A":
                            change.Set(Models.ChangeState.Added);
                            break;
                        case "AM":
                            change.Set(Models.ChangeState.Added, Models.ChangeState.Modified);
                            break;
                        case "AT":
                            change.Set(Models.ChangeState.Added, Models.ChangeState.TypeChanged);
                            break;
                        case "AD":
                            change.Set(Models.ChangeState.Added, Models.ChangeState.Deleted);
                            break;
                        case "D":
                            change.Set(Models.ChangeState.Deleted);
                            break;
                        case "R":
                            change.Set(Models.ChangeState.Renamed);
                            break;
                        case "RM":
                            change.Set(Models.ChangeState.Renamed, Models.ChangeState.Modified);
                            break;
                        case "RT":
                            change.Set(Models.ChangeState.Renamed, Models.ChangeState.TypeChanged);
                            break;
                        case "RD":
                            change.Set(Models.ChangeState.Renamed, Models.ChangeState.Deleted);
                            break;
                        case "C":
                            change.Set(Models.ChangeState.Copied);
                            break;
                        case "CM":
                            change.Set(Models.ChangeState.Copied, Models.ChangeState.Modified);
                            break;
                        case "CT":
                            change.Set(Models.ChangeState.Copied, Models.ChangeState.TypeChanged);
                            break;
                        case "CD":
                            change.Set(Models.ChangeState.Copied, Models.ChangeState.Deleted);
                            break;
                        case "DD":
                            change.ConflictReason = Models.ConflictReason.BothDeleted;
                            change.Set(Models.ChangeState.None, Models.ChangeState.Conflicted);
                            break;
                        case "AU":
                            change.ConflictReason = Models.ConflictReason.AddedByUs;
                            change.Set(Models.ChangeState.None, Models.ChangeState.Conflicted);
                            break;
                        case "UD":
                            change.ConflictReason = Models.ConflictReason.DeletedByThem;
                            change.Set(Models.ChangeState.None, Models.ChangeState.Conflicted);
                            break;
                        case "UA":
                            change.ConflictReason = Models.ConflictReason.AddedByThem;
                            change.Set(Models.ChangeState.None, Models.ChangeState.Conflicted);
                            break;
                        case "DU":
                            change.ConflictReason = Models.ConflictReason.DeletedByUs;
                            change.Set(Models.ChangeState.None, Models.ChangeState.Conflicted);
                            break;
                        case "AA":
                            change.ConflictReason = Models.ConflictReason.BothAdded;
                            change.Set(Models.ChangeState.None, Models.ChangeState.Conflicted);
                            if (IsResolvedTextConflict(change))
                            {
                                change.IsResolvedConflict = true;
                                change.Set(Models.ChangeState.None, Models.ChangeState.Modified);
                            }
                            break;
                        case "UU":
                            change.ConflictReason = Models.ConflictReason.BothModified;
                            change.Set(Models.ChangeState.None, Models.ChangeState.Conflicted);
                            if (IsResolvedTextConflict(change))
                            {
                                change.IsResolvedConflict = true;
                                change.Set(Models.ChangeState.None, Models.ChangeState.Modified);
                            }
                            break;
                        case "??":
                            change.Set(Models.ChangeState.None, Models.ChangeState.Untracked);
                            break;
                    }

                    if (!change.CanResetToConflictState &&
                        change.Index != Models.ChangeState.None &&
                        IsResolvedConflictPath(change.Path))
                    {
                        change.IsResolvedConflict = true;
                    }

                    if (change.Index != Models.ChangeState.None || change.WorkTree != Models.ChangeState.None)
                        outs.Add(change);
                }

                await proc.WaitForExitAsync().ConfigureAwait(false);
                span.Set("exitCode", proc.ExitCode);
                span.Set("success", proc.ExitCode == 0);
                span.Set("changeCount", outs.Count);
            }
            catch (System.Exception e)
            {
                span.Set("success", false);
                span.Set("error", e.Message);
                // Ignore exceptions.
            }

            return outs;
        }

        private bool IsResolvedTextConflict(Models.Change change)
        {
            if (!IsUnmergedTextFile(change.Path))
                return false;

            return new IsConflictResolved(WorkingDirectory, change).GetResult();
        }

        private bool IsUnmergedTextFile(string path)
        {
            if (!_unmergedBinaryPathsLoaded)
            {
                _unmergedBinaryPaths = new QueryUnmergedBinaryPaths(WorkingDirectory).GetResult();
                _unmergedBinaryPathsLoaded = true;
            }

            return _unmergedBinaryPaths != null && !_unmergedBinaryPaths.Contains(path);
        }

        private bool IsResolvedConflictPath(string path)
        {
            if (!_resolvedConflictPathsLoaded)
            {
                _resolvedConflictPaths = new QueryResolvedConflictPaths(WorkingDirectory).GetResult();
                _resolvedConflictPathsLoaded = true;
            }

            return _resolvedConflictPaths != null && _resolvedConflictPaths.Contains(path);
        }

        private class QueryUnmergedBinaryPaths : Command
        {
            public QueryUnmergedBinaryPaths(string repo)
            {
                WorkingDirectory = repo;
                Context = repo;
                RaiseError = false;
                Args = "ls-files -u -z --format=\"%(stage)%x09%(eolinfo:index)%x09%(path)\"";
            }

            public HashSet<string> GetResult()
            {
                var rs = ReadToEnd();
                if (!rs.IsSuccess)
                    return null;

                var paths = new HashSet<string>(StringComparer.Ordinal);
                var records = rs.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries);
                foreach (var record in records)
                {
                    var parts = record.Split('\t', 3);
                    if (parts.Length != 3)
                        continue;

                    if (parts[1].Equals("-text", StringComparison.Ordinal))
                        paths.Add(parts[2]);
                }

                return paths;
            }
        }

        private class QueryResolvedConflictPaths : Command
        {
            public QueryResolvedConflictPaths(string repo)
            {
                WorkingDirectory = repo;
                Context = repo;
                RaiseError = false;
                Args = "ls-files --resolve-undo -z";
            }

            public HashSet<string> GetResult()
            {
                var rs = ReadToEnd();
                if (!rs.IsSuccess)
                    return null;

                var paths = new HashSet<string>(StringComparer.Ordinal);
                var records = rs.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries);
                foreach (var record in records)
                {
                    var tabIdx = record.IndexOf('\t');
                    if (tabIdx < 0 || tabIdx + 1 >= record.Length)
                        continue;

                    paths.Add(record.Substring(tabIdx + 1));
                }

                return paths;
            }
        }

        private bool _unmergedBinaryPathsLoaded = false;
        private HashSet<string> _unmergedBinaryPaths = null;
        private bool _resolvedConflictPathsLoaded = false;
        private HashSet<string> _resolvedConflictPaths = null;
    }
}
