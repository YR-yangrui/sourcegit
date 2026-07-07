using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class RevisionCompare : ObservableObject, IDisposable
    {
        public bool IsLoading
        {
            get => _isLoading;
            private set => SetProperty(ref _isLoading, value);
        }

        public object StartPoint
        {
            get => _startPoint;
            private set => SetProperty(ref _startPoint, value);
        }

        public object EndPoint
        {
            get => _endPoint;
            private set => SetProperty(ref _endPoint, value);
        }

        public string LeftSideDesc
        {
            get => GetDesc(StartPoint);
        }

        public string RightSideDesc
        {
            get => GetDesc(EndPoint);
        }

        public bool CanResetToLeft
        {
            get => !_repo.IsBare && _startPoint != null;
        }

        public bool CanResetToRight
        {
            get => !_repo.IsBare && _endPoint != null;
        }

        public bool CanSaveAsPatch
        {
            get => _startPoint != null && _endPoint != null;
        }

        public int TotalChanges
        {
            get => _totalChanges;
            private set => SetProperty(ref _totalChanges, value);
        }

        public List<Models.Change> VisibleChanges
        {
            get => _visibleChanges;
            private set => SetProperty(ref _visibleChanges, value);
        }

        public List<Models.Change> SelectedChanges
        {
            get => _selectedChanges;
            set
            {
                if (_disposed)
                    return;

                if (SetProperty(ref _selectedChanges, value))
                {
                    if (value is { Count: 1 })
                    {
                        var option = new Models.DiffOption(GetSHA(_startPoint), GetSHA(_endPoint), value[0]);
                        DiffHost.ShowDiff(_repo.FullPath, option, "revision_compare.select");
                    }
                    else
                    {
                        DiffHost.Clear();
                    }
                }
            }
        }

        public string SearchFilter
        {
            get => _searchFilter;
            set
            {
                if (SetProperty(ref _searchFilter, value))
                    RefreshVisible();
            }
        }

        public DiffContentHost DiffHost { get; } = new();

        public RevisionCompare(Repository repo, Models.Commit startPoint, Models.Commit endPoint)
        {
            _repo = repo;
            _startPoint = (object)startPoint ?? new Models.Null();
            _endPoint = (object)endPoint ?? new Models.Null();
            Refresh();
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _refreshRequestId++;
            DiffHost.Dispose();
        }

        public RevisionCompare Clone()
        {
            return new RevisionCompare(_repo, _startPoint as Models.Commit, _endPoint as Models.Commit);
        }

        public void SetTargets(Models.Commit l, Models.Commit r)
        {
            if (_startPoint is Models.Commit s &&
                _endPoint is Models.Commit e &&
                l != null &&
                r != null &&
                s.SHA.Equals(l.SHA, StringComparison.Ordinal) &&
                e.SHA.Equals(r.SHA, StringComparison.Ordinal))
                return;

            StartPoint = l == null ? new Models.Null() : l;
            EndPoint = r == null ? new Models.Null() : r;
            OnPropertyChanged(nameof(LeftSideDesc));
            OnPropertyChanged(nameof(RightSideDesc));
            OnPropertyChanged(nameof(CanResetToLeft));
            OnPropertyChanged(nameof(CanResetToRight));
            OnPropertyChanged(nameof(CanSaveAsPatch));
            Refresh();
        }

        public void OpenChangeWithExternalDiffTool(Models.Change change)
        {
            var opt = new Models.DiffOption(GetSHA(_startPoint), GetSHA(_endPoint), change);
            new Commands.DiffTool(_repo.FullPath, opt).Open();
        }

        public void NavigateTo(string commitSHA)
        {
            _repo?.NavigateToCommit(commitSHA);
        }

        public void Swap()
        {
            (StartPoint, EndPoint) = (_endPoint, _startPoint);
            VisibleChanges = [];
            SelectedChanges = [];
            IsLoading = true;
            Refresh();
        }

        public string GetAbsPath(string path)
        {
            return Native.OS.GetAbsPath(_repo.FullPath, path);
        }

        public async Task ResetToLeftAsync(Models.Change change)
        {
            var sha = GetSHA(_startPoint);
            var log = _repo.CreateLog($"Reset File to '{GetDesc(_startPoint)}'");

            if (change.Index == Models.ChangeState.Added)
            {
                var fullpath = Native.OS.GetAbsPath(_repo.FullPath, change.Path);
                if (File.Exists(fullpath))
                    await new Commands.Remove(_repo.FullPath, [change.Path])
                        .Use(log)
                        .ExecAsync();
            }
            else if (change.Index == Models.ChangeState.Renamed)
            {
                var renamed = Native.OS.GetAbsPath(_repo.FullPath, change.Path);
                if (File.Exists(renamed))
                    await new Commands.Remove(_repo.FullPath, [change.Path])
                        .Use(log)
                        .ExecAsync();

                await new Commands.Checkout(_repo.FullPath)
                    .Use(log)
                    .FileWithRevisionAsync(change.OriginalPath, sha);
            }
            else
            {
                await new Commands.Checkout(_repo.FullPath)
                    .Use(log)
                    .FileWithRevisionAsync(change.Path, sha);
            }

            log.Complete();
        }

        public async Task ResetToRightAsync(Models.Change change)
        {
            var sha = GetSHA(_endPoint);
            var log = _repo.CreateLog($"Reset File to '{GetDesc(_endPoint)}'");

            if (change.Index == Models.ChangeState.Deleted)
            {
                var fullpath = Native.OS.GetAbsPath(_repo.FullPath, change.Path);
                if (File.Exists(fullpath))
                    await new Commands.Remove(_repo.FullPath, [change.Path])
                        .Use(log)
                        .ExecAsync();
            }
            else if (change.Index == Models.ChangeState.Renamed)
            {
                var old = Native.OS.GetAbsPath(_repo.FullPath, change.OriginalPath);
                if (File.Exists(old))
                    await new Commands.Remove(_repo.FullPath, [change.OriginalPath])
                        .Use(log)
                        .ExecAsync();

                await new Commands.Checkout(_repo.FullPath)
                    .Use(log)
                    .FileWithRevisionAsync(change.Path, sha);
            }
            else
            {
                await new Commands.Checkout(_repo.FullPath)
                    .Use(log)
                    .FileWithRevisionAsync(change.Path, sha);
            }

            log.Complete();
        }

        public async Task ResetMultipleToLeftAsync(List<Models.Change> changes)
        {
            var sha = GetSHA(_startPoint);
            var checkouts = new List<string>();
            var removes = new List<string>();

            foreach (var c in changes)
            {
                if (c.Index == Models.ChangeState.Added)
                {
                    var fullpath = Native.OS.GetAbsPath(_repo.FullPath, c.Path);
                    if (File.Exists(fullpath))
                        removes.Add(c.Path);
                }
                else if (c.Index == Models.ChangeState.Renamed)
                {
                    var old = Native.OS.GetAbsPath(_repo.FullPath, c.Path);
                    if (File.Exists(old))
                        removes.Add(c.Path);

                    checkouts.Add(c.OriginalPath);
                }
                else
                {
                    checkouts.Add(c.Path);
                }
            }

            var log = _repo.CreateLog($"Reset Files to '{GetDesc(_startPoint)}'");

            if (removes.Count > 0)
                await new Commands.Remove(_repo.FullPath, removes)
                    .Use(log)
                    .ExecAsync();

            if (checkouts.Count > 0)
                await new Commands.Checkout(_repo.FullPath)
                    .Use(log)
                    .MultipleFilesWithRevisionAsync(checkouts, sha);

            log.Complete();
        }

        public async Task ResetMultipleToRightAsync(List<Models.Change> changes)
        {
            var sha = GetSHA(_endPoint);
            var checkouts = new List<string>();
            var removes = new List<string>();

            foreach (var c in changes)
            {
                if (c.Index == Models.ChangeState.Deleted)
                {
                    var fullpath = Native.OS.GetAbsPath(_repo.FullPath, c.Path);
                    if (File.Exists(fullpath))
                        removes.Add(c.Path);
                }
                else if (c.Index == Models.ChangeState.Renamed)
                {
                    var renamed = Native.OS.GetAbsPath(_repo.FullPath, c.OriginalPath);
                    if (File.Exists(renamed))
                        removes.Add(c.OriginalPath);

                    checkouts.Add(c.Path);
                }
                else
                {
                    checkouts.Add(c.Path);
                }
            }

            var log = _repo.CreateLog($"Reset Files to '{GetDesc(_endPoint)}'");

            if (removes.Count > 0)
                await new Commands.Remove(_repo.FullPath, removes)
                    .Use(log)
                    .ExecAsync();

            if (checkouts.Count > 0)
                await new Commands.Checkout(_repo.FullPath)
                    .Use(log)
                    .MultipleFilesWithRevisionAsync(checkouts, sha);

            log.Complete();
        }

        public async Task SaveChangesAsPatchAsync(List<Models.Change> changes, string saveTo)
        {
            var targets = changes ?? _changes;
            if (targets == null || targets.Count == 0)
                return;

            var succ = await Commands.SaveChangesAsPatch.ProcessRevisionCompareChangesAsync(_repo.FullPath, targets, GetSHA(_startPoint), GetSHA(_endPoint), saveTo);
            if (succ)
                _repo.SendNotification(App.Text("SaveAsPatchSuccess"));
        }

        public void ClearSearchFilter()
        {
            SearchFilter = string.Empty;
        }

        private void RefreshVisible()
        {
            if (_disposed)
                return;

            if (_changes == null)
                return;

            var filter = Models.FileSearch.Parse(_searchFilter);
            if (filter.IsEmpty)
            {
                VisibleChanges = _changes;
            }
            else
            {
                var visible = new List<Models.Change>();
                foreach (var c in _changes)
                {
                    if (Models.FileSearch.Matches(c.Path, filter))
                        visible.Add(c);
                }

                VisibleChanges = visible;
            }
        }

        private void Refresh()
        {
            var requestId = ++_refreshRequestId;
            var start = GetSHA(_startPoint);
            var end = GetSHA(_endPoint);

            IsLoading = true;
            _changes = null;
            TotalChanges = 0;
            VisibleChanges = [];
            SelectedChanges = [];

            Task.Run(async () =>
            {
                var changes = await new Commands.CompareRevisions(_repo.FullPath, start, end)
                    .ReadAsync()
                    .ConfigureAwait(false);

                var visible = changes;
                var filter = Models.FileSearch.Parse(_searchFilter);
                if (!filter.IsEmpty)
                {
                    visible = [];
                    foreach (var c in changes)
                    {
                        if (Models.FileSearch.Matches(c.Path, filter))
                            visible.Add(c);
                    }
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (_disposed ||
                        requestId != _refreshRequestId ||
                        !start.Equals(GetSHA(_startPoint), StringComparison.Ordinal) ||
                        !end.Equals(GetSHA(_endPoint), StringComparison.Ordinal))
                        return;

                    _changes = changes;
                    TotalChanges = _changes.Count;
                    VisibleChanges = visible;
                    IsLoading = false;

                    if (VisibleChanges.Count > 0)
                        SelectedChanges = [VisibleChanges[0]];
                    else
                        SelectedChanges = [];
                });
            });
        }

        private string GetSHA(object obj)
        {
            return obj is Models.Commit commit ? commit.SHA : string.Empty;
        }

        private string GetDesc(object obj)
        {
            return obj is Models.Commit commit ? commit.GetFriendlyName() : App.Text("Worktree");
        }

        private Repository _repo;
        private bool _isLoading = true;
        private object _startPoint = null;
        private object _endPoint = null;
        private int _totalChanges = 0;
        private List<Models.Change> _changes = null;
        private List<Models.Change> _visibleChanges = null;
        private List<Models.Change> _selectedChanges = null;
        private string _searchFilter = string.Empty;
        private long _refreshRequestId = 0;
        private bool _disposed = false;
    }
}
