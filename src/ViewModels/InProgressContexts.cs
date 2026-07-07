using System.IO;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public abstract class InProgressContext
    {
        public string Name
        {
            get;
            protected set;
        }

        /// <summary>
        /// Returns null when this context uses simple conflict cards instead of the history panel.
        /// </summary>
        public virtual ConflictHistoryPlan CreateConflictHistoryPlan(Repository repo)
        {
            return null;
        }

        public virtual (object Mine, object Theirs) GetConflictSides(Models.Commit head)
        {
            return (head, (object)"Stash or Patch");
        }

        public virtual (string Mine, string Theirs) GetExternalMergeFileSideNames()
        {
            return ("HEAD", string.Empty);
        }

        public virtual string GetContinueMessageFile(Repository repo)
        {
            var file = Path.Combine(repo.GitDir, "MERGE_MSG");
            return File.Exists(file) ? file : string.Empty;
        }

        public async Task ContinueAsync(CommandLog log)
        {
            if (_continueCmd != null)
                await _continueCmd.Use(log).ExecAsync();
        }

        public async Task SkipAsync(CommandLog log)
        {
            if (_skipCmd != null)
                await _skipCmd.Use(log).ExecAsync();
        }

        public async Task AbortAsync(CommandLog log)
        {
            if (_abortCmd != null)
                await _abortCmd.Use(log).ExecAsync();

            OnAborted();
        }

        protected virtual void OnAborted()
        {
        }

        protected Commands.Command _continueCmd = null;
        protected Commands.Command _skipCmd = null;
        protected Commands.Command _abortCmd = null;
    }

    public class CherryPickInProgress : InProgressContext
    {
        public Models.Commit Head
        {
            get;
        }

        public string HeadName
        {
            get;
        }

        public CherryPickInProgress(Repository repo)
        {
            Name = "Cherry-Pick";

            _continueCmd = new Commands.Command
            {
                WorkingDirectory = repo.FullPath,
                Context = repo.FullPath,
                Editor = Commands.Command.EditorType.None,
                Args = "-c commit.cleanup=verbatim -c commit.status=false cherry-pick --edit --continue",
            };

            _skipCmd = new Commands.Command
            {
                WorkingDirectory = repo.FullPath,
                Context = repo.FullPath,
                Args = "cherry-pick --skip",
            };

            _abortCmd = new Commands.Command
            {
                WorkingDirectory = repo.FullPath,
                Context = repo.FullPath,
                Args = "cherry-pick --abort",
            };

            var headSHA = File.ReadAllText(Path.Combine(repo.GitDir, "CHERRY_PICK_HEAD")).Trim();
            Head = new Commands.QuerySingleCommit(repo.FullPath, headSHA).GetResult() ?? new Models.Commit() { SHA = headSHA };
            HeadName = Head.GetFriendlyName();
        }

        public override (object Mine, object Theirs) GetConflictSides(Models.Commit head)
        {
            return (head, Head);
        }

        public override (string Mine, string Theirs) GetExternalMergeFileSideNames()
        {
            return ("HEAD", string.IsNullOrEmpty(HeadName) ? Head.SHA : HeadName);
        }
    }

    public class RebaseInProgress : InProgressContext
    {
        public string HeadName
        {
            get;
        }

        public string BaseName
        {
            get;
        }

        public Models.Commit StoppedAt
        {
            get;
        }

        public Models.Commit Onto
        {
            get;
        }

        public RebaseInProgress(Repository repo)
        {
            _gitDir = repo.GitDir;
            _rebaseDir = Directory.Exists(Path.Combine(repo.GitDir, "rebase-merge"))
                ? Path.Combine(repo.GitDir, "rebase-merge")
                : Path.Combine(repo.GitDir, "rebase-apply");
            Name = "Rebase";

            _continueCmd = new Commands.Command
            {
                WorkingDirectory = repo.FullPath,
                Context = repo.FullPath,
                Editor = Commands.Command.EditorType.None,
                Args = "-c commit.cleanup=verbatim -c commit.status=false rebase --continue",
            };

            _skipCmd = new Commands.Command
            {
                WorkingDirectory = repo.FullPath,
                Context = repo.FullPath,
                Args = "rebase --skip",
            };

            _abortCmd = new Commands.Command
            {
                WorkingDirectory = repo.FullPath,
                Context = repo.FullPath,
                Args = "rebase --abort",
                RaiseError = false,
            };

            HeadName = File.ReadAllText(Path.Combine(_rebaseDir, "head-name")).Trim();
            if (HeadName.StartsWith("refs/heads/"))
                HeadName = HeadName.Substring(11);
            else if (HeadName.StartsWith("refs/tags/"))
                HeadName = HeadName.Substring(10);

            var stoppedSHAFile = IsMergeBackend ? "stopped-sha" : "original-commit";
            var stoppedSHAPath = Path.Combine(_rebaseDir, stoppedSHAFile);
            var stoppedSHA = File.Exists(stoppedSHAPath)
                ? File.ReadAllText(stoppedSHAPath).Trim()
                : new Commands.QueryRevisionByRefName(repo.FullPath, HeadName).GetResult();

            if (!string.IsNullOrEmpty(stoppedSHA))
                StoppedAt = new Commands.QuerySingleCommit(repo.FullPath, stoppedSHA).GetResult() ?? new Models.Commit() { SHA = stoppedSHA };

            var ontoSHA = File.ReadAllText(Path.Combine(_rebaseDir, "onto")).Trim();
            Onto = new Commands.QuerySingleCommit(repo.FullPath, ontoSHA).GetResult() ?? new Models.Commit() { SHA = ontoSHA };
            BaseName = Onto.GetFriendlyName();
        }

        public override ConflictHistoryPlan CreateConflictHistoryPlan(Repository repo)
        {
            var stopped = StoppedAt?.SHA ?? string.Empty;
            if (string.IsNullOrEmpty(stopped))
                return null;

            var head = repo.CurrentBranch?.Head ?? "HEAD";
            return new ConflictHistoryPlan
            {
                SessionSeed = $"{head}\0{Onto?.SHA ?? string.Empty}\0{stopped}\0{HeadName}\0",
                MineTitle = "MINE - current HEAD",
                TheirsTitle = $"THEIRS - rebasing: {StoppedAt.GetFriendlyName()}",
                MergeBaseLeft = "HEAD",
                MergeBaseRight = stopped,
                MineTip = "HEAD",
                TheirsTip = stopped,
                TheirsIsSingleCommit = true,
            };
        }

        public override (object Mine, object Theirs) GetConflictSides(Models.Commit head)
        {
            return (Onto, StoppedAt);
        }

        public override (string Mine, string Theirs) GetExternalMergeFileSideNames()
        {
            return (
                string.IsNullOrEmpty(BaseName) ? "HEAD" : BaseName,
                string.IsNullOrEmpty(HeadName) ? StoppedAt?.GetFriendlyName() ?? "REBASE_HEAD" : HeadName);
        }

        public override string GetContinueMessageFile(Repository repo)
        {
            var file = Path.Combine(_rebaseDir, IsMergeBackend ? "message" : "final-commit");
            return File.Exists(file) ? file : string.Empty;
        }

        protected override void OnAborted()
        {
            var rebaseMergeDir = Path.Combine(_gitDir, "rebase-merge");
            if (Directory.Exists(rebaseMergeDir))
                Directory.Delete(rebaseMergeDir, true);

            var rebaseApplyDir = Path.Combine(_gitDir, "rebase-apply");
            if (Directory.Exists(rebaseApplyDir))
                Directory.Delete(rebaseApplyDir, true);

            var jobFile = Path.Combine(_gitDir, "sourcegit.interactive_rebase");
            if (File.Exists(jobFile))
                File.Delete(jobFile);
        }

        private readonly string _gitDir;
        private readonly string _rebaseDir;

        private bool IsMergeBackend => _rebaseDir.EndsWith("rebase-merge", System.StringComparison.Ordinal);
    }

    public class RevertInProgress : InProgressContext
    {
        public Models.Commit Head
        {
            get;
        }

        public RevertInProgress(Repository repo)
        {
            Name = "Revert";

            _continueCmd = new Commands.Command
            {
                WorkingDirectory = repo.FullPath,
                Context = repo.FullPath,
                Editor = Commands.Command.EditorType.None,
                Args = "-c commit.cleanup=verbatim -c commit.status=false revert --edit --continue",
            };

            _skipCmd = new Commands.Command
            {
                WorkingDirectory = repo.FullPath,
                Context = repo.FullPath,
                Args = "revert --skip",
            };

            _abortCmd = new Commands.Command
            {
                WorkingDirectory = repo.FullPath,
                Context = repo.FullPath,
                Args = "revert --abort",
            };

            var headSHA = File.ReadAllText(Path.Combine(repo.GitDir, "REVERT_HEAD")).Trim();
            Head = new Commands.QuerySingleCommit(repo.FullPath, headSHA).GetResult() ?? new Models.Commit() { SHA = headSHA };
        }

        public override (object Mine, object Theirs) GetConflictSides(Models.Commit head)
        {
            return (head, Head);
        }

        public override (string Mine, string Theirs) GetExternalMergeFileSideNames()
        {
            return ("HEAD", Head?.GetFriendlyName() ?? "REVERT_HEAD");
        }
    }

    public class MergeInProgress : InProgressContext
    {
        public string Current
        {
            get;
        }

        public Models.Commit Source
        {
            get;
        }

        public string SourceName
        {
            get;
        }

        public MergeInProgress(Repository repo)
        {
            Name = "Merge";

            _continueCmd = new Commands.Command
            {
                WorkingDirectory = repo.FullPath,
                Context = repo.FullPath,
                Editor = Commands.Command.EditorType.None,
                Args = "-c commit.cleanup=verbatim -c commit.status=false merge --continue",
            };

            _abortCmd = new Commands.Command
            {
                WorkingDirectory = repo.FullPath,
                Context = repo.FullPath,
                Args = "merge --abort",
            };

            Current = new Commands.QueryCurrentBranch(repo.FullPath).GetResult();

            var sourceSHA = File.ReadAllText(Path.Combine(repo.GitDir, "MERGE_HEAD")).Trim();
            Source = new Commands.QuerySingleCommit(repo.FullPath, sourceSHA).GetResult() ?? new Models.Commit() { SHA = sourceSHA };
            SourceName = Source.GetFriendlyName();
        }

        public override ConflictHistoryPlan CreateConflictHistoryPlan(Repository repo)
        {
            var head = repo.CurrentBranch?.Head ?? string.Empty;
            var current = string.IsNullOrEmpty(Current) ? "HEAD" : Current;
            var source = Source?.SHA ?? string.Empty;
            if (string.IsNullOrEmpty(source))
                return null;

            return new ConflictHistoryPlan
            {
                SessionSeed = $"{head}\0{current}\0{source}\0",
                MineTitle = $"MINE - current branch: {current}",
                TheirsTitle = $"THEIRS - merging: {(string.IsNullOrEmpty(SourceName) ? "MERGE_HEAD" : SourceName)}",
                MergeBaseLeft = "HEAD",
                MergeBaseRight = "MERGE_HEAD",
                MineTip = "HEAD",
                TheirsTip = "MERGE_HEAD",
            };
        }

        public override (object Mine, object Theirs) GetConflictSides(Models.Commit head)
        {
            return (head, Source);
        }

        public override (string Mine, string Theirs) GetExternalMergeFileSideNames()
        {
            return (
                string.IsNullOrEmpty(Current) ? "HEAD" : Current,
                string.IsNullOrEmpty(SourceName) ? "MERGE_HEAD" : SourceName);
        }
    }
}
