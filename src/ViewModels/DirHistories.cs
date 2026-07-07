using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class DirHistories : ObservableObject, IDisposable
    {
        public string Title
        {
            get;
        }

        public bool IsLoading
        {
            get => _isLoading;
            private set => SetProperty(ref _isLoading, value);
        }

        public List<Models.HistoryQueryScope> Scopes
        {
            get => _scopes;
            private set => SetProperty(ref _scopes, value);
        }

        public Models.HistoryQueryScope SelectedScope
        {
            get => _selectedScope;
            set
            {
                if (SetProperty(ref _selectedScope, value))
                    RefreshHistory();
            }
        }

        public string StartingPointDescription
        {
            get => _startingPointDescription;
            private set => SetProperty(ref _startingPointDescription, value);
        }

        public List<Models.Commit> Commits
        {
            get => _commits;
            private set => SetProperty(ref _commits, value);
        }

        public Models.Commit SelectedCommit
        {
            get => _selectedCommit;
            set
            {
                if (_disposed)
                    return;

                if (SetProperty(ref _selectedCommit, value))
                    Detail.Commit = value;
            }
        }

        public CommitDetail Detail
        {
            get => _detail;
        }

        public DirHistories(Repository repo, string dir, string revision = null)
        {
            Title = BuildTitle(dir, revision);
            _repo = repo;
            _dir = dir;
            _openedRevision = revision;
            _detail = new CommitDetail(repo, null);
            _detail.SearchChangeFilter = dir;

            InitializeScopes(repo.Branches, repo.CurrentBranch);
        }

        public DirHistories(string repoPath, string dir)
        {
            Title = dir;
            _dir = dir;

            var gitDir = new Commands.QueryGitDir(repoPath).GetResult();
            _repo = new Repository(true, repoPath, gitDir); // Trait repository as a bare repository to disable some file operations.
            _repo.RefreshBranches();

            _detail = new CommitDetail(_repo, null);
            _detail.SearchChangeFilter = dir;

            Task.Run(async () =>
            {
                var branches = await new Commands.QueryBranches(_repo.FullPath)
                    .GetResultAsync()
                    .ConfigureAwait(false);

                Dispatcher.UIThread.Post(() =>
                {
                    if (_disposed)
                        return;

                    var current = branches.Find(x => x.IsCurrent);
                    InitializeScopes(branches, current);
                });
            });
        }

        public void NavigateToCommit(Models.Commit commit)
        {
            if (_disposed)
                return;

            _repo.NavigateToCommit(commit.SHA);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _requestId++;
            _detail?.Dispose();
        }

        public string GetCommitFullMessage(Models.Commit commit)
        {
            if (_disposed)
                return string.Empty;

            var sha = commit.SHA;
            if (_cachedCommitFullMessage.TryGetValue(sha, out var msg))
                return msg;

            msg = new Commands.QueryCommitFullMessage(_repo.FullPath, sha).GetResult();
            _cachedCommitFullMessage[sha] = msg;
            return msg;
        }

        private static string BuildTitle(string dir, string revision)
        {
            return !string.IsNullOrEmpty(revision) ? $"{dir} @ {revision}" : dir;
        }

        private void InitializeScopes(List<Models.Branch> branches, Models.Branch current)
        {
            var scopes = new List<Models.HistoryQueryScope>
            {
                Models.HistoryQueryScope.AllBranches(App.Text("HistoryScope.AllBranches"))
            };

            if (current is { IsDetachedHead: false })
                scopes.Add(Models.HistoryQueryScope.CurrentBranch(App.Text("HistoryScope.CurrentBranch"), current));
            else
                scopes.Add(Models.HistoryQueryScope.Head(App.Text("HistoryScope.Head")));

            Models.HistoryQueryScope openedRevision = null;
            if (!string.IsNullOrEmpty(_openedRevision))
            {
                openedRevision = Models.HistoryQueryScope.RevisionScope(_openedRevision);
                scopes.Add(openedRevision);
            }

            foreach (var branch in branches)
            {
                if (branch.IsLocal && !branch.IsDetachedHead)
                    scopes.Add(Models.HistoryQueryScope.BranchScope(branch));
            }

            foreach (var branch in branches)
            {
                if (!branch.IsLocal && !branch.IsDetachedHead)
                    scopes.Add(Models.HistoryQueryScope.BranchScope(branch));
            }

            Scopes = scopes;

            if (openedRevision != null)
                SelectedScope = openedRevision;
            else if (current is { IsDetachedHead: false })
                SelectedScope = scopes.Find(x => x.Kind == Models.HistoryQueryScopeKind.CurrentBranch) ?? scopes[0];
            else
                SelectedScope = scopes.Find(x => x.Kind == Models.HistoryQueryScopeKind.Head) ?? scopes[0];
        }

        private void RefreshHistory()
        {
            var scope = _selectedScope;
            if (scope == null)
                return;

            var requestId = ++_requestId;
            IsLoading = true;
            Commits = [];
            SelectedCommit = null;
            Detail.Commit = null;
            StartingPointDescription = string.Empty;
            _cachedCommitFullMessage.Clear();

            var allBranchesStartingPoint = App.Text("DirHistories.StartingPoint.AllBranches");
            Task.Run(async () =>
            {
                var startingPoint = scope.Kind == Models.HistoryQueryScopeKind.AllBranches ?
                    allBranchesStartingPoint :
                    await BuildStartingPointDescriptionAsync(scope).ConfigureAwait(false);

                Dispatcher.UIThread.Post(() =>
                {
                    if (requestId != _requestId || scope != _selectedScope || _disposed)
                        return;

                    StartingPointDescription = startingPoint;
                });

                var argsBuilder = new StringBuilder();
                argsBuilder.Append("--date-order -n 10000 ");
                if (scope.Kind == Models.HistoryQueryScopeKind.AllBranches)
                    argsBuilder.Append("--branches --remotes ");
                else if (!string.IsNullOrEmpty(scope.RevisionArg))
                    argsBuilder.Append(scope.RevisionArg.Quoted()).Append(' ');

                argsBuilder.Append("-- ").Append(_dir.Quoted());

                var commits = await new Commands.QueryCommits(_repo.FullPath, argsBuilder.ToString(), false)
                    .GetResultAsync()
                    .ConfigureAwait(false);

                Dispatcher.UIThread.Post(() =>
                {
                    if (requestId != _requestId || scope != _selectedScope || _disposed)
                        return;

                    Commits = commits;
                    IsLoading = false;

                    if (commits.Count > 0)
                        SelectedCommit = commits[0];
                });
            });
        }

        private async Task<string> BuildStartingPointDescriptionAsync(Models.HistoryQueryScope scope)
        {
            var revision = scope.Kind switch
            {
                Models.HistoryQueryScopeKind.CurrentBranch => scope.Branch?.FullName ?? "HEAD",
                Models.HistoryQueryScopeKind.Head => "HEAD",
                Models.HistoryQueryScopeKind.Revision => scope.Revision,
                Models.HistoryQueryScopeKind.Branch => scope.Branch?.FullName,
                _ => "HEAD",
            };

            if (string.IsNullOrEmpty(revision))
                revision = "HEAD";

            var commit = await new Commands.QuerySingleCommit(_repo.FullPath, revision)
                .GetResultAsync()
                .ConfigureAwait(false);

            var label = GetStartingPointLabel(scope, commit);
            if (commit == null || string.IsNullOrEmpty(commit.Subject))
                return label;

            return $"{label} - {commit.Subject}";
        }

        private static string GetStartingPointLabel(Models.HistoryQueryScope scope, Models.Commit commit)
        {
            return scope.Kind switch
            {
                Models.HistoryQueryScopeKind.CurrentBranch => scope.Branch?.FriendlyName ?? scope.Name,
                Models.HistoryQueryScopeKind.Head => commit != null ? $"HEAD - {GetShortSHA(commit.SHA)}" : "HEAD",
                Models.HistoryQueryScopeKind.Revision => scope.ShortRevision,
                Models.HistoryQueryScopeKind.Branch => scope.Branch?.FriendlyName ?? scope.Name,
                _ => scope.Name,
            };
        }

        private static string GetShortSHA(string sha)
        {
            if (string.IsNullOrEmpty(sha))
                return string.Empty;

            return sha.Length > 10 ? sha[..10] : sha;
        }

        private Repository _repo = null;
        private string _dir = null;
        private string _openedRevision = null;
        private int _requestId = 0;
        private bool _isLoading = true;
        private bool _disposed = false;
        private List<Models.HistoryQueryScope> _scopes = [];
        private Models.HistoryQueryScope _selectedScope = null;
        private string _startingPointDescription = string.Empty;
        private List<Models.Commit> _commits = [];
        private Models.Commit _selectedCommit = null;
        private CommitDetail _detail = null;
        private Dictionary<string, string> _cachedCommitFullMessage = new();
    }
}
