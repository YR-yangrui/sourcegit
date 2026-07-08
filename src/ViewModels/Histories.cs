using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class Histories : ObservableObject, IDisposable
    {
        public bool IsLoading
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value);
        }

        public bool IsAuthorColumnVisible
        {
            get => _repo.UIStates.IsAuthorColumnVisibleInHistory;
            set
            {
                if (_repo.UIStates.IsAuthorColumnVisibleInHistory != value)
                {
                    _repo.UIStates.IsAuthorColumnVisibleInHistory = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsSHAColumnVisible
        {
            get => _repo.UIStates.IsSHAColumnVisibleInHistory;
            set
            {
                if (_repo.UIStates.IsSHAColumnVisibleInHistory != value)
                {
                    _repo.UIStates.IsSHAColumnVisibleInHistory = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsAuthorTimeColumnVisible
        {
            get => _repo.UIStates.IsAuthorTimeColumnVisibleInHistory;
            set
            {
                if (_repo.UIStates.IsAuthorTimeColumnVisibleInHistory != value)
                {
                    _repo.UIStates.IsAuthorTimeColumnVisibleInHistory = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsCommitTimeColumnVisible
        {
            get => _repo.UIStates.IsCommitTimeColumnVisibleInHistory;
            set
            {
                if (_repo.UIStates.IsCommitTimeColumnVisibleInHistory != value)
                {
                    _repo.UIStates.IsCommitTimeColumnVisibleInHistory = value;
                    OnPropertyChanged();
                }
            }
        }

        public List<Models.Commit> Commits
        {
            get => _commits;
            set
            {
                MarkCurrentGitUserAuthors(value);
                GenerateGraph(value, true);
                if (SetProperty(ref _commits, value))
                    PostCommitsChanged();
            }
        }

        public Models.CommitGraph Graph
        {
            get => _graph;
            set => SetProperty(ref _graph, value);
        }

        public Models.CommitGraphHighlighting GraphHighlighting
        {
            get => _repo.UIStates.GraphHighlighting;
            set
            {
                if (_repo.UIStates.GraphHighlighting != value)
                {
                    _repo.UIStates.GraphHighlighting = value;
                    GenerateGraph(_commits);
                }
            }
        }

        public List<Models.Commit> SelectedCommits
        {
            get => _selectedCommits;
            set
            {
                var oldCount = _selectedCommits.Count;
                if (SetProperty(ref _selectedCommits, value) && oldCount + value.Count > 0)
                    PostSelectedCommitsChanged();
            }
        }

        public object DetailContext
        {
            get => _detailContext;
            set
            {
                var old = _detailContext;
                if (SetProperty(ref _detailContext, value))
                {
                    if (!ReferenceEquals(old, value))
                        (old as IDisposable)?.Dispose();

                    OnPropertyChanged(nameof(IsOpenAsStandaloneVisible));
                }
            }
        }

        public Models.Bisect Bisect
        {
            get => _bisect;
            private set => SetProperty(ref _bisect, value);
        }

        public Models.Branch CurrentBranch
        {
            get => _repo.CurrentBranch;
        }

        public AvaloniaList<Models.IssueTracker> IssueTrackers
        {
            get => _repo.IssueTrackers;
        }

        public GridLength LeftArea
        {
            get => _leftArea;
            set => SetProperty(ref _leftArea, value);
        }

        public GridLength RightArea
        {
            get => _rightArea;
            set => SetProperty(ref _rightArea, value);
        }

        public GridLength TopArea
        {
            get => _isCollapseDetails ? new GridLength(1, GridUnitType.Star) : _topArea;
            set
            {
                if (!Preferences.Instance.UseTwoColumnsLayoutInHistories && !_isCollapseDetails)
                    SetProperty(ref _topArea, value);
            }
        }

        public GridLength BottomArea
        {
            get => _isCollapseDetails ? new GridLength(28, GridUnitType.Pixel) : _bottomArea;
            set
            {
                if (!Preferences.Instance.UseTwoColumnsLayoutInHistories && !_isCollapseDetails)
                    SetProperty(ref _bottomArea, value);
            }
        }

        public double AuthorColumnWidth
        {
            get => _repo.UIStates.AuthorColumnWidth;
            set => _repo.UIStates.AuthorColumnWidth = value;
        }

        public bool IsOpenAsStandaloneVisible
        {
            get => DetailContext is CommitDetail or RevisionCompare;
        }

        public bool IsCollapseDetails
        {
            get => _isCollapseDetails;
            set
            {
                if (!Preferences.Instance.UseTwoColumnsLayoutInHistories && SetProperty(ref _isCollapseDetails, value))
                {
                    OnPropertyChanged(nameof(TopArea));
                    OnPropertyChanged(nameof(BottomArea));
                }
            }
        }

        public Histories(Repository repo)
        {
            _repo = repo;
            _commitDetailSharedData = new CommitDetailSharedData();
            RefreshCurrentGitUserIdentity();
        }

        public void Dispose()
        {
            DetailContext = null;
        }

        public void NotifyCurrentBranchChanged()
        {
            OnPropertyChanged(nameof(CurrentBranch));
        }

        public void SelectCurrentHeadIfNoSelection()
        {
            if (_selectedCommits.Count > 0 || _commits.Count == 0 || _detailContext is not Models.Null)
                return;

            var head = _commits.Find(x => x.IsCurrentHead);
            if (head == null && !string.IsNullOrEmpty(_repo.CurrentBranch?.Head))
                head = _commits.Find(x => x.SHA.Equals(_repo.CurrentBranch.Head, StringComparison.Ordinal));

            if (head != null)
                SelectedCommits = [head];
        }

        public Models.BisectState UpdateBisectInfo()
        {
            var test = Path.Combine(_repo.GitDir, "BISECT_START");
            if (!File.Exists(test))
            {
                Bisect = null;
                return Models.BisectState.None;
            }

            var head = new Commands.QueryRevisionByRefName(_repo.FullPath, "HEAD").GetResult();
            var info = new Models.Bisect();
            var markedHead = false;
            var dir = Path.Combine(_repo.GitDir, "refs", "bisect");
            if (Directory.Exists(dir))
            {
                var files = new DirectoryInfo(dir).GetFiles();
                foreach (var file in files)
                {
                    var sha = File.ReadAllText(file.FullName).Trim();
                    if (!markedHead)
                        markedHead = head.Equals(sha, StringComparison.Ordinal);

                    if (file.Name.StartsWith("bad"))
                        info.Bads.Add(sha);
                    else if (file.Name.StartsWith("good"))
                        info.Goods.Add(sha);
                    else if (file.Name.StartsWith("skip"))
                        info.Skipped.Add(sha);
                }
            }

            Bisect = info;

            if (info.Bads.Count == 0)
                return Models.BisectState.WaitingForFirstBad;

            if (markedHead)
                return Models.BisectState.WaitingForCheckoutAnother;

            if (info.Goods.Count == 0)
                return Models.BisectState.WaitingForFirstGood;

            return Models.BisectState.WaitingForMark;
        }

        public void NavigateTo(string commitSHA)
        {
            var commit = _commits.Find(x => x.SHA.StartsWith(commitSHA, StringComparison.Ordinal));
            if (commit != null)
            {
                SelectedCommits = [commit];
                return;
            }

            Task.Run(async () =>
            {
                var c = await new Commands.QuerySingleCommit(_repo.FullPath, commitSHA)
                    .GetResultAsync()
                    .ConfigureAwait(false);

                Dispatcher.UIThread.Post(() =>
                {
                    _ignoreSelectionChange = true;
                    SelectedCommits = [];

                    if (_detailContext is CommitDetail detail)
                    {
                        detail.Commit = c;
                    }
                    else
                    {
                        var commitDetail = new CommitDetail(_repo, _commitDetailSharedData);
                        commitDetail.Commit = c;
                        DetailContext = commitDetail;
                    }

                    _ignoreSelectionChange = false;
                });
            });
        }

        public async Task<Models.Commit> GetCommitAsync(string sha)
        {
            return await new Commands.QuerySingleCommit(_repo.FullPath, sha)
                .GetResultAsync()
                .ConfigureAwait(false);
        }

        public void CheckoutCommitDetached(Models.Commit c)
        {
            if (!c.IsCurrentHead && _repo.CanCreatePopup())
                _repo.ShowPopup(new CheckoutCommit(_repo, c));
        }

        public async Task<bool> CheckoutBranchByDecoratorAsync(Models.Decorator decorator)
        {
            if (decorator == null)
                return false;

            if (decorator.Type == Models.DecoratorType.CurrentBranchHead ||
                decorator.Type == Models.DecoratorType.CurrentCommitHead)
                return true;

            if (decorator.Type == Models.DecoratorType.LocalBranchHead)
            {
                var b = _repo.Branches.Find(x => x.Name == decorator.Name);
                if (b == null)
                    return false;

                await _repo.CheckoutBranchAsync(b);
                return true;
            }

            if (decorator.Type == Models.DecoratorType.RemoteBranchHead)
            {
                var rb = _repo.Branches.Find(x => x.FriendlyName == decorator.Name);
                if (rb == null)
                    return false;

                var lb = _repo.Branches.Find(x => x.IsLocal && x.Upstream == rb.FullName);
                if (lb == null || lb.Ahead.Count > 0)
                {
                    if (_repo.CanCreatePopup())
                        _repo.ShowPopup(new CreateBranch(_repo, rb));
                }
                else if (lb.Behind.Count > 0)
                {
                    if (_repo.CanCreatePopup())
                        _repo.ShowPopup(new CheckoutAndFastForward(_repo, lb, rb));
                }
                else if (!lb.IsCurrent)
                {
                    await _repo.CheckoutBranchAsync(lb);
                }

                return true;
            }

            return false;
        }

        public async Task CheckoutBranchByCommitAsync(Models.Commit commit)
        {
            if (commit.IsCurrentHead)
                return;

            Models.Branch firstRemoteBranch = null;
            foreach (var d in commit.Decorators)
            {
                if (d.Type == Models.DecoratorType.LocalBranchHead)
                {
                    var b = _repo.Branches.Find(x => x.Name == d.Name);
                    if (b == null)
                        continue;

                    await _repo.CheckoutBranchAsync(b);
                    return;
                }

                if (d.Type == Models.DecoratorType.RemoteBranchHead)
                {
                    var rb = _repo.Branches.Find(x => x.FriendlyName == d.Name);
                    if (rb == null)
                        continue;

                    var lb = _repo.Branches.Find(x => x.IsLocal && x.Upstream == rb.FullName);
                    if (lb != null && lb.Behind.Count > 0 && lb.Ahead.Count == 0)
                    {
                        if (_repo.CanCreatePopup())
                            _repo.ShowPopup(new CheckoutAndFastForward(_repo, lb, rb));
                        return;
                    }

                    firstRemoteBranch ??= rb;
                }
            }

            if (_repo.CanCreatePopup())
            {
                if (firstRemoteBranch != null)
                    _repo.ShowPopup(new CreateBranch(_repo, firstRemoteBranch));
                else if (!_repo.IsBare)
                    _repo.ShowPopup(new CheckoutCommit(_repo, commit));
            }
        }

        public async Task CherryPickAsync(Models.Commit commit)
        {
            if (_repo.CanCreatePopup())
            {
                if (commit.Parents.Count <= 1)
                {
                    _repo.ShowPopup(new CherryPick(_repo, [commit]));
                }
                else
                {
                    var parents = new List<Models.Commit>();
                    foreach (var sha in commit.Parents)
                    {
                        var parent = _commits.Find(x => x.SHA.Equals(sha, StringComparison.Ordinal));
                        if (parent == null)
                            parent = await new Commands.QuerySingleCommit(_repo.FullPath, sha).GetResultAsync();

                        if (parent != null)
                            parents.Add(parent);
                    }

                    _repo.ShowPopup(new CherryPick(_repo, commit, parents));
                }
            }
        }

        public async Task<string> GetCommitFullMessageAsync(Models.Commit commit)
        {
            return await new Commands.QueryCommitFullMessage(_repo.FullPath, commit.SHA)
                .GetResultAsync()
                .ConfigureAwait(false);
        }

        public async Task<Models.Commit> CompareWithHeadAsync(Models.Commit commit)
        {
            var head = _commits.Find(x => x.IsCurrentHead);
            if (head == null)
            {
                _repo.SearchCommitContext.Selected = null;
                head = await new Commands.QuerySingleCommit(_repo.FullPath, "HEAD").GetResultAsync();
                if (head != null)
                    DetailContext = new RevisionCompare(_repo, commit, head);

                return null;
            }

            return head;
        }

        public void CompareWithWorktree(Models.Commit commit)
        {
            DetailContext = new RevisionCompare(_repo, commit, null);
        }

        private void PostCommitsChanged()
        {
            if (_selectedCommits.Count == 0)
            {
                SelectCurrentHeadIfNoSelection();
                return;
            }

            if (_commits.Count == 0 || _selectedCommits.Count > 20)
            {
                SelectedCommits = [];
                return;
            }

            var set = new HashSet<string>();
            foreach (var c in _selectedCommits)
                set.Add(c.SHA);

            var selected = new List<Models.Commit>();
            foreach (var c in _commits)
            {
                if (set.Contains(c.SHA))
                {
                    selected.Add(c);
                    set.Remove(c.SHA);
                    if (set.Count == 0)
                        break;
                }
            }

            if (selected.Count > 0)
            {
                SelectedCommits = selected;
            }
            else
            {
                SelectedCommits = [];
                SelectCurrentHeadIfNoSelection();
            }
        }

        private void PostSelectedCommitsChanged()
        {
            if (_ignoreSelectionChange)
                return;

            using var span = StartSelectionDiagnosticSpan();
            if (_selectedCommits.Count == 0)
            {
                _repo.SearchCommitContext.Selected = null;
                DetailContext = new Models.Null();
                span.Set("result", "empty");
            }
            else if (_selectedCommits.Count == 1)
            {
                var c = _selectedCommits[0];
                if (_repo.SearchCommitContext.Selected == null || !_repo.SearchCommitContext.Selected.SHA.Equals(c.SHA, StringComparison.Ordinal))
                    _repo.SearchCommitContext.Selected = _repo.SearchCommitContext.Results?.Find(x => x.SHA.Equals(c.SHA, StringComparison.Ordinal));

                if (_detailContext is CommitDetail detail)
                {
                    detail.UseChangesTab();
                    detail.Commit = c;
                    span.Set("detailAction", "reuse_commit_detail");
                }
                else
                {
                    var newDetail = new CommitDetail(_repo, _commitDetailSharedData);
                    newDetail.UseChangesTab();
                    newDetail.Commit = c;
                    DetailContext = newDetail;
                    span.Set("detailAction", "create_commit_detail");
                }

                span.Set("result", "single_commit");
            }
            else if (_selectedCommits.Count == 2)
            {
                _repo.SearchCommitContext.Selected = null;

                if (_detailContext is RevisionCompare compare)
                {
                    compare.SetTargets(_selectedCommits[1], _selectedCommits[0]);
                    span.Set("detailAction", "reuse_revision_compare");
                }
                else
                {
                    DetailContext = new RevisionCompare(_repo, _selectedCommits[1], _selectedCommits[0]);
                    span.Set("detailAction", "create_revision_compare");
                }

                span.Set("result", "compare");
            }
            else
            {
                _repo.SearchCommitContext.Selected = null;
                DetailContext = new Models.Count(_selectedCommits.Count);
                span.Set("result", "multiple_commits");
            }

            if (_repo.UIStates.GraphHighlighting >= Models.CommitGraphHighlighting.SelectedCommitsOnly)
            {
                GenerateGraph(_commits);
                span.Set("regenerateGraph", true);
            }
        }

        private Diagnostics.DiagnosticScope StartSelectionDiagnosticSpan()
        {
            var repoPath = Diagnostics.DiagnosticManager.GetRepositoryPath(_repo?.FullPath);
            var first = _selectedCommits.Count > 0 ? _selectedCommits[0].SHA : string.Empty;
            var second = _selectedCommits.Count > 1 ? _selectedCommits[1].SHA : string.Empty;
            return Diagnostics.DiagnosticManager.StartSpan(
                "Histories",
                "selected_commits.changed",
                Diagnostics.DiagnosticManager.CreateData(
                    ("repo", Diagnostics.DiagnosticManager.GetRepositoryId(repoPath)),
                    ("repoPath", repoPath),
                    ("count", _selectedCommits.Count),
                    ("first", first),
                    ("second", second),
                    ("detailContext", _detailContext?.GetType().Name ?? string.Empty),
                    ("graphHighlighting", _repo.UIStates.GraphHighlighting.ToString())));
        }

        private void GenerateGraph(List<Models.Commit> commits, bool commitsChanged = false)
        {
            var firstParentOnly = _repo.UIStates.HistoryShowFlags.HasFlag(Models.HistoryShowFlags.FirstParentOnly);
            var highlighting = _repo.UIStates.GraphHighlighting;
            var extraHeads = new HashSet<string>();

            if (highlighting >= Models.CommitGraphHighlighting.SelectedCommitsOnly)
            {
                foreach (var c in _selectedCommits)
                    extraHeads.Add(c.SHA);
            }

            Graph = Models.CommitGraph.Generate(commits, commitsChanged, firstParentOnly, highlighting, extraHeads);
        }

        private void RefreshCurrentGitUserIdentity()
        {
            _currentGitUserName = string.Empty;
            _currentGitUserEmail = string.Empty;

            var config = new Commands.Config(_repo.FullPath).ReadAll();
            _currentGitUserName = GetConfigValue(config, "user.name");
            _currentGitUserEmail = GetConfigValue(config, "user.email");
        }

        private void MarkCurrentGitUserAuthors(List<Models.Commit> commits)
        {
            if (commits == null)
                return;

            foreach (var commit in commits)
                commit.IsCurrentGitUserAuthor = IsCurrentGitUserAuthor(commit.Author);
        }

        private bool IsCurrentGitUserAuthor(Models.User author)
        {
            if (author == null)
                return false;

            if (!string.IsNullOrWhiteSpace(_currentGitUserEmail))
                return string.Equals(author.Email, _currentGitUserEmail, StringComparison.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(_currentGitUserName))
                return string.Equals(author.Name, _currentGitUserName, StringComparison.Ordinal);

            return false;
        }

        private static string GetConfigValue(Dictionary<string, string> config, string key)
        {
            foreach (var kv in config)
            {
                if (kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                    return kv.Value?.Trim() ?? string.Empty;
            }

            return string.Empty;
        }

        private Repository _repo = null;
        private CommitDetailSharedData _commitDetailSharedData = null;
        private bool _isLoading = true;
        private List<Models.Commit> _commits = [];
        private Models.CommitGraph _graph = null;
        private List<Models.Commit> _selectedCommits = [];
        private Models.Bisect _bisect = null;
        private object _detailContext = new Models.Null();
        private bool _ignoreSelectionChange = false;

        private GridLength _leftArea = new(1, GridUnitType.Star);
        private GridLength _rightArea = new(1, GridUnitType.Star);
        private GridLength _topArea = new(1, GridUnitType.Star);
        private GridLength _bottomArea = new(1, GridUnitType.Star);
        private bool _isCollapseDetails = false;
        private string _currentGitUserName = string.Empty;
        private string _currentGitUserEmail = string.Empty;
    }
}
