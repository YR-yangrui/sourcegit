using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class SearchCommitContext : ObservableObject
    {
        public int Method
        {
            get => _method;
            set
            {
                if (SetProperty(ref _method, value))
                {
                    OnPropertyChanged(nameof(IsScopeSelectorVisible));
                    UpdateSuggestions();
                    StartSearch();
                }
            }
        }

        public string Filter
        {
            get => _filter;
            set
            {
                if (SetProperty(ref _filter, value))
                    UpdateSuggestions();
            }
        }

        public bool IsScopeSelectorVisible => _method != (int)Models.CommitSearchMethod.BySHA;

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
                {
                    _worktreeFiles = null;
                    _worktreeFilesRevision = null;

                    if (_repo.IsSearchingCommits)
                    {
                        UpdateSuggestions();
                        StartSearch();
                    }
                }
            }
        }

        public List<string> Suggestions
        {
            get => _suggestions;
            private set => SetProperty(ref _suggestions, value);
        }

        public bool IsQuerying
        {
            get => _isQuerying;
            private set => SetProperty(ref _isQuerying, value);
        }

        public List<Models.Commit> Results
        {
            get => _results;
            private set => SetProperty(ref _results, value);
        }

        public Models.Commit Selected
        {
            get => _selected;
            set
            {
                if (SetProperty(ref _selected, value) && value != null)
                    _repo.NavigateToCommit(value.SHA);
            }
        }

        public SearchCommitContext(Repository repo)
        {
            _repo = repo;
            RefreshScopes();
        }

        public void ClearFilter()
        {
            Filter = string.Empty;
            Selected = null;
            Results = null;
        }

        public void ClearSuggestions()
        {
            Suggestions = null;
        }

        public void StartSearch()
        {
            Results = null;
            Selected = null;
            Suggestions = null;

            if (!_repo.IsSearchingCommits || string.IsNullOrEmpty(_filter))
                return;

            IsQuerying = true;

            if (_cancellation is { IsCancellationRequested: false })
                _cancellation.Cancel();

            _cancellation = new();
            var token = _cancellation.Token;

            Task.Run(async () =>
            {
                var result = new List<Models.Commit>();
                var method = (Models.CommitSearchMethod)_method;
                var repoPath = _repo.FullPath;
                var scope = _selectedScope ?? (_scopes.Count > 0 ? _scopes[0] : Models.HistoryQueryScope.AllBranches("All branches"));

                if (method == Models.CommitSearchMethod.BySHA)
                {
                    var isCommitSHA = await new Commands.IsCommitSHA(repoPath, _filter)
                        .GetResultAsync()
                        .ConfigureAwait(false);

                    if (isCommitSHA)
                    {
                        var commit = await new Commands.QuerySingleCommit(repoPath, _filter)
                            .GetResultAsync()
                            .ConfigureAwait(false);

                        if (commit != null)
                        {
                            commit.IsMerged = await new Commands.IsAncestor(repoPath, commit.SHA, "HEAD")
                                .GetResultAsync()
                                .ConfigureAwait(false);

                            result.Add(commit);
                        }
                    }
                }
                else if (IsScopeCurrentHead(scope))
                {
                    result = await new Commands.QueryCommits(repoPath, _filter, method, scope)
                        .GetResultAsync()
                        .ConfigureAwait(false);

                    foreach (var c in result)
                        c.IsMerged = true;
                }
                else
                {
                    result = await new Commands.QueryCommits(repoPath, _filter, method, scope)
                        .GetResultAsync()
                        .ConfigureAwait(false);

                    if (result.Count > 0)
                    {
                        var set = await new Commands.QueryCurrentBranchCommitHashes(repoPath, result[^1].CommitterTime)
                            .GetResultAsync()
                            .ConfigureAwait(false);

                        foreach (var c in result)
                            c.IsMerged = set.Contains(c.SHA);
                    }
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (token.IsCancellationRequested)
                        return;

                    IsQuerying = false;
                    if (_repo.IsSearchingCommits)
                        Results = result;
                });
            }, token);
        }

        public void RefreshScopes()
        {
            var scopes = new List<Models.HistoryQueryScope>
            {
                Models.HistoryQueryScope.AllBranches(App.Text("HistoryScope.AllBranches"))
            };

            if (_repo.CurrentBranch is { IsDetachedHead: false } current)
                scopes.Add(Models.HistoryQueryScope.CurrentBranch(App.Text("HistoryScope.CurrentBranch"), current));
            else
                scopes.Add(Models.HistoryQueryScope.Head(App.Text("HistoryScope.Head")));

            foreach (var branch in _repo.Branches)
            {
                if (branch.IsLocal && !branch.IsDetachedHead)
                    scopes.Add(Models.HistoryQueryScope.BranchScope(branch));
            }

            foreach (var branch in _repo.Branches)
            {
                if (!branch.IsLocal && !branch.IsDetachedHead)
                    scopes.Add(Models.HistoryQueryScope.BranchScope(branch));
            }

            var selected = FindEquivalentScope(scopes, _selectedScope) ?? scopes[0];
            Scopes = scopes;
            SelectedScope = selected;
        }

        public void EndSearch()
        {
            if (_cancellation is { IsCancellationRequested: false })
                _cancellation.Cancel();

            _worktreeFiles = null;
            IsQuerying = false;
            Suggestions = null;
            Results = null;
            GC.Collect();
        }

        private void UpdateSuggestions()
        {
            if (_method != (int)Models.CommitSearchMethod.ByPath)
            {
                Suggestions = null;
                return;
            }

            var revision = GetSuggestionRevision();
            if (_requestingWorktreeFiles && _requestingWorktreeFilesRevision == revision)
            {
                Suggestions = null;
                return;
            }

            if (_worktreeFilesRevision != revision)
                _worktreeFiles = null;

            if (_worktreeFiles == null)
            {
                _requestingWorktreeFiles = true;
                _requestingWorktreeFilesRevision = revision;

                Task.Run(async () =>
                {
                    var files = await new Commands.QueryRevisionFileNames(_repo.FullPath, revision)
                        .GetResultAsync()
                        .ConfigureAwait(false);

                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_requestingWorktreeFilesRevision == revision)
                        {
                            _requestingWorktreeFiles = false;
                            _requestingWorktreeFilesRevision = null;
                        }

                        if (_repo.IsSearchingCommits && revision == GetSuggestionRevision())
                        {
                            _worktreeFiles = files;
                            _worktreeFilesRevision = revision;
                            UpdateSuggestions();
                        }
                    });
                });

                return;
            }

            if (_worktreeFiles.Count == 0 || _filter.Length < 3)
            {
                Suggestions = null;
                return;
            }

            var pattern = Models.FileSearch.Parse(_filter);
            Suggestions = Models.FileSearch.FilterAndSort(_worktreeFiles, pattern, 100, true);
        }

        private string GetSuggestionRevision()
        {
            var scope = _selectedScope;
            if (scope is { Kind: Models.HistoryQueryScopeKind.Branch or Models.HistoryQueryScopeKind.Revision } &&
                !string.IsNullOrEmpty(scope.RevisionArg))
                return scope.RevisionArg;

            return "HEAD";
        }

        private Models.HistoryQueryScope FindEquivalentScope(List<Models.HistoryQueryScope> scopes, Models.HistoryQueryScope selected)
        {
            if (selected == null)
                return null;

            foreach (var scope in scopes)
            {
                if (scope.Kind != selected.Kind)
                    continue;

                if (scope.Kind == Models.HistoryQueryScopeKind.Branch)
                {
                    if (scope.Branch?.FullName == selected.Branch?.FullName)
                        return scope;
                }
                else if (scope.Kind == Models.HistoryQueryScopeKind.Revision)
                {
                    if (scope.Revision == selected.Revision)
                        return scope;
                }
                else
                {
                    return scope;
                }
            }

            return null;
        }

        private bool IsScopeCurrentHead(Models.HistoryQueryScope scope)
        {
            if (scope == null || scope.IsCurrentHead)
                return true;

            if (scope.Kind == Models.HistoryQueryScopeKind.Branch &&
                _repo.CurrentBranch?.FullName == scope.Branch?.FullName)
                return true;

            return false;
        }

        private Repository _repo = null;
        private CancellationTokenSource _cancellation = null;
        private int _method = (int)Models.CommitSearchMethod.ByMessage;
        private string _filter = string.Empty;
        private List<Models.HistoryQueryScope> _scopes = [];
        private Models.HistoryQueryScope _selectedScope = null;
        private List<string> _suggestions = null;
        private bool _isQuerying = false;
        private List<Models.Commit> _results = null;
        private Models.Commit _selected = null;
        private bool _requestingWorktreeFiles = false;
        private string _requestingWorktreeFilesRevision = null;
        private string _worktreeFilesRevision = null;
        private List<string> _worktreeFiles = null;
    }
}
