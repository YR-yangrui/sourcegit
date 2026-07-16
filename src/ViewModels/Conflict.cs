using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;

using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class Conflict : ObservableObject
    {
        public string Marker
        {
            get => _change.ConflictMarker;
        }

        public string Description
        {
            get => _change.ConflictDesc;
        }

        public string FilePath
        {
            get => _change.Path;
        }

        public object Theirs
        {
            get;
            private set;
        }

        public object Mine
        {
            get;
            private set;
        }

        public bool IsResolved
        {
            get => _isResolved;
            private set => SetProperty(ref _isResolved, value);
        }

        public bool CanMerge
        {
            get;
            private set;
        } = false;

        public bool UseHistoryPanel
        {
            get => _useHistoryPanel;
            private set => SetProperty(ref _useHistoryPanel, value);
        }

        public bool IsLoadingHistories
        {
            get => _isLoadingHistories;
            private set
            {
                if (SetProperty(ref _isLoadingHistories, value))
                {
                    OnPropertyChanged(nameof(IsMineHistoryEmpty));
                    OnPropertyChanged(nameof(IsTheirsHistoryEmpty));
                }
            }
        }

        public string MineHistoryTitle
        {
            get => _mineHistoryTitle;
            private set => SetProperty(ref _mineHistoryTitle, value);
        }

        public string TheirsHistoryTitle
        {
            get => _theirsHistoryTitle;
            private set => SetProperty(ref _theirsHistoryTitle, value);
        }

        public List<Models.FileVersion> MineHistories
        {
            get => _mineHistories;
            private set
            {
                if (SetProperty(ref _mineHistories, value))
                    OnPropertyChanged(nameof(IsMineHistoryEmpty));
            }
        }

        public List<Models.FileVersion> TheirsHistories
        {
            get => _theirsHistories;
            private set
            {
                if (SetProperty(ref _theirsHistories, value))
                    OnPropertyChanged(nameof(IsTheirsHistoryEmpty));
            }
        }

        public bool IsMineHistoryEmpty
        {
            get => UseHistoryPanel && !IsLoadingHistories && _mineHistories.Count == 0;
        }

        public bool IsTheirsHistoryEmpty
        {
            get => UseHistoryPanel && !IsLoadingHistories && _theirsHistories.Count == 0;
        }

        public Conflict(Repository repo, WorkingCopy wc, Models.Change change)
        {
            _repo = repo;
            _wc = wc;
            _change = change;

            CanMerge = _change.ConflictReason is Models.ConflictReason.BothAdded or Models.ConflictReason.BothModified;
            if (CanMerge)
                CanMerge = !Directory.Exists(Path.Combine(repo.FullPath, change.Path)); // Cannot merge directories (submodules)

            if (CanMerge)
                IsResolved = new Commands.IsConflictResolved(repo.FullPath, change).GetResult();

            _head = new Commands.QuerySingleCommit(repo.FullPath, "HEAD").GetResult() ?? new Models.Commit() { SHA = "HEAD" };
            var context = wc.InProgressContext;
            (Mine, Theirs) = context?.GetConflictSides(_head) ?? (_head, (object)"Stash or Patch");

            var historyPlan = context?.CreateConflictHistoryPlan(repo);
            if (historyPlan != null)
            {
                UseHistoryPanel = true;
                MineHistoryTitle = historyPlan.MineTitle;
                TheirsHistoryTitle = historyPlan.TheirsTitle;

                _historyCache = wc.MergeConflictHistories;
                _historyCache.PropertyChanged += OnHistoryCachePropertyChanged;
                RefreshHistoriesFromCache();
            }

            StartResolveCheck();
        }

        public async Task UseTheirsAsync()
        {
            await _wc.UseTheirsAsync([_change]);
        }

        public async Task UseMineAsync()
        {
            await _wc.UseMineAsync([_change]);
        }

        public async Task<MergeConflictEditor> CreateOpenMergeEditorRequestAsync()
        {
            if (!CanMerge)
                return null;

            var head = _head.SHA == "HEAD"
                ? new Commands.QuerySingleCommit(_repo.FullPath, "HEAD").GetResult() ?? _head
                : _head;
            return await MergeConflictEditor.CreateAsync(_repo, head, _change.Path);
        }

        public async Task MergeExternalAsync()
        {
            if (CanMerge)
                await _wc.UseExternalMergeToolAsync(_change);
        }

        public void DetachFromHistoryCache()
        {
            if (_historyCache != null)
            {
                _historyCache.PropertyChanged -= OnHistoryCachePropertyChanged;
                _historyCache = null;
            }
        }

        private void RefreshHistoriesFromCache()
        {
            if (_historyCache == null)
                return;

            MineHistories = _historyCache.GetMineHistories(_change.Path);
            TheirsHistories = _historyCache.GetTheirsHistories(_change.Path);
            IsLoadingHistories = _historyCache.IsLoading;
        }

        private void OnHistoryCachePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MergeConflictFileHistoryCache.IsLoading) ||
                e.PropertyName == nameof(MergeConflictFileHistoryCache.Revision))
                RefreshHistoriesFromCache();
        }

        public void StartResolveCheck()
        {
            if (!CanMerge)
                return;

            Task.Run(async () =>
            {
                var resolved = false;
                try
                {
                    resolved = await new Commands.IsConflictResolved(_repo.FullPath, _change)
                        .GetResultAsync()
                        .ConfigureAwait(false);
                }
                catch
                {
                    resolved = false;
                }

                if (resolved)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        IsResolved = true;
                        _wc.ShowResolvedConflictDiff(_change, this);
                    });
                }
            });
        }

        private Repository _repo = null;
        private WorkingCopy _wc = null;
        private Models.Commit _head = null;
        private Models.Change _change = null;
        private MergeConflictFileHistoryCache _historyCache = null;
        private bool _isResolved = false;
        private bool _useHistoryPanel = false;
        private bool _isLoadingHistories = false;
        private string _mineHistoryTitle = "MINE";
        private string _theirsHistoryTitle = "THEIRS";
        private List<Models.FileVersion> _mineHistories = [];
        private List<Models.FileVersion> _theirsHistories = [];
    }
}
