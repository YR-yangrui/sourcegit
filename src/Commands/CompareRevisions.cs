using System.Collections.Generic;
using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public partial class CompareRevisions : Command
    {
        [GeneratedRegex(@"^([MAD])\s+(.+)$")]
        private static partial Regex REG_FORMAT();
        [GeneratedRegex(@"^([CR])[0-9]{0,4}\s+(.+)$")]
        private static partial Regex REG_RENAME_FORMAT();

        public CompareRevisions(string repo, string start, string end)
        {
            WorkingDirectory = repo;
            Context = repo;
            CanAbortProcessOnCancel = !string.IsNullOrEmpty(start) && !string.IsNullOrEmpty(end);

            var based = string.IsNullOrEmpty(start) ? "-R" : start;
            Args = $"diff --name-status {based} {end}";
        }

        public CompareRevisions(string repo, string start, string end, string path)
        {
            WorkingDirectory = repo;
            Context = repo;
            CanAbortProcessOnCancel = !string.IsNullOrEmpty(start) && !string.IsNullOrEmpty(end);

            var based = string.IsNullOrEmpty(start) ? "-R" : start;
            Args = $"diff --name-status {based} {end} -- {path.Quoted()}";
        }

        public async Task<List<Models.Change>> ReadAsync()
        {
            using var span = StartGitDiagnosticSpan("compare_revisions");
            var changes = new List<Models.Change>();
            var lineCount = 0;
            try
            {
                using var proc = new Process();
                proc.StartInfo = CreateGitStartInfo(true);
                proc.Start();

                using var cancellation = RegisterProcessCancellation(proc);
                while (await proc.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    lineCount++;
                    var match = REG_FORMAT().Match(line);
                    if (!match.Success)
                    {
                        match = REG_RENAME_FORMAT().Match(line);
                        if (match.Success)
                        {
                            var type = match.Groups[1].Value;
                            var renamed = new Models.Change() { Path = match.Groups[2].Value };
                            renamed.Set(type == "R" ? Models.ChangeState.Renamed : Models.ChangeState.Copied);
                            changes.Add(renamed);
                        }

                        continue;
                    }

                    var change = new Models.Change() { Path = match.Groups[2].Value };
                    var status = match.Groups[1].Value;

                    switch (status[0])
                    {
                        case 'M':
                            change.Set(Models.ChangeState.Modified);
                            changes.Add(change);
                            break;
                        case 'A':
                            change.Set(Models.ChangeState.Added);
                            changes.Add(change);
                            break;
                        case 'D':
                            change.Set(Models.ChangeState.Deleted);
                            changes.Add(change);
                            break;
                    }
                }

                await proc.WaitForExitAsync(CancellationToken).ConfigureAwait(false);
                span.Set("exitCode", proc.ExitCode);
                span.Set("canceled", CancellationToken.IsCancellationRequested);
                span.Set("lineCount", lineCount);
                span.Set("changesCount", changes.Count);

                if (!CancellationToken.IsCancellationRequested)
                {
                    changes.Sort((l, r) => Models.NumericSort.Compare(l.Path, r.Path));
                    span.Set("success", proc.ExitCode == 0);
                }
                else
                {
                    span.Set("success", false);
                }
            }
            catch (Exception e)
            {
                span.Set("error", e.GetType().Name);
                span.Set("canceled", CancellationToken.IsCancellationRequested);
                span.Set("lineCount", lineCount);
                span.Set("changesCount", changes.Count);
                span.Set("success", false);
                //ignore changes;
            }

            return changes;
        }
    }
}
