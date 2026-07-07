using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Collections;
using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class Repository : ObservableObject, Models.IRepository
    {
        [Flags]
        private enum RefreshDirtyFlags
        {
            None = 0,
            Branches = 1,
            Worktrees = 1 << 1,
            Tags = 1 << 2,
            Commits = 1 << 3,
            Submodules = 1 << 4,
            Stashes = 1 << 5,
        }

        public bool IsBare
        {
            get;
        }

        public string FullPath
        {
            get;
        }

        public string GitDir
        {
            get;
        }

        public Models.RepositorySettings Settings
        {
            get => _settings;
        }

        public Models.RepositoryUIStates UIStates
        {
            get => _uiStates;
        }

        public Models.GitFlow GitFlow
        {
            get;
            set;
        } = new();

        public Models.FilterMode HistoryFilterMode
        {
            get => _historyFilterMode;
            private set => SetProperty(ref _historyFilterMode, value);
        }

        public bool HasAllowedSignersFile
        {
            get => _hasAllowedSignersFile;
        }

        public int SelectedViewIndex
        {
            get => _selectedViewIndex;
            set
            {
                if (SetProperty(ref _selectedViewIndex, value))
                {
                    OnPropertyChanged(nameof(IsHistoriesVisible));
                    OnPropertyChanged(nameof(IsWorkingCopyVisible));
                    OnPropertyChanged(nameof(IsStashesVisible));

                    if (value == 1)
                        _histories?.SelectCurrentHeadIfNoSelection();
                }
            }
        }

        public Histories Histories
        {
            get => _histories;
        }

        public WorkingCopy WorkingCopy
        {
            get => _workingCopy;
        }

        public StashesPage StashesPage
        {
            get => _stashesPage;
        }

        public bool IsHistoriesVisible
        {
            get => SelectedViewIndex == 1;
        }

        public bool IsWorkingCopyVisible
        {
            get => SelectedViewIndex == 0;
        }

        public bool IsStashesVisible
        {
            get => SelectedViewIndex == 2;
        }

        public bool EnableTopoOrderInHistory
        {
            get => _uiStates.EnableTopoOrderInHistory;
            set
            {
                if (value != _uiStates.EnableTopoOrderInHistory)
                {
                    _uiStates.EnableTopoOrderInHistory = value;
                    RefreshCommits();
                }
            }
        }

        public Models.HistoryShowFlags HistoryShowFlags
        {
            get => _uiStates.HistoryShowFlags;
            private set
            {
                if (value != _uiStates.HistoryShowFlags)
                {
                    _uiStates.HistoryShowFlags = value;
                    RefreshCommits();
                }
            }
        }

        public string Filter
        {
            get => _filter;
            set
            {
                if (SetProperty(ref _filter, value))
                {
                    var builder = BuildBranchTree(_branches, _remotes);
                    LocalBranchTrees = builder.Locals;
                    RemoteBranchTrees = builder.Remotes;
                    VisibleTags = BuildVisibleTags();
                    VisibleSubmodules = BuildVisibleSubmodules();
                }
            }
        }

        public List<Models.Remote> Remotes
        {
            get => _remotes;
            private set => SetProperty(ref _remotes, value);
        }

        public List<Models.Branch> Branches
        {
            get => _branches;
            private set => SetProperty(ref _branches, value);
        }

        public Models.Branch CurrentBranch
        {
            get => _currentBranch;
            private set
            {
                var oldHead = _currentBranch?.Head;
                if (SetProperty(ref _currentBranch, value))
                {
                    _histories?.NotifyCurrentBranchChanged();
                    if (value != null && !value.Head.Equals(oldHead, StringComparison.Ordinal) && _workingCopy is { UseAmend: true })
                        _workingCopy.UseAmend = false;
                }
            }
        }

        public List<BranchTreeNode> LocalBranchTrees
        {
            get => _localBranchTrees;
            private set => SetProperty(ref _localBranchTrees, value);
        }

        public List<BranchTreeNode> RemoteBranchTrees
        {
            get => _remoteBranchTrees;
            private set => SetProperty(ref _remoteBranchTrees, value);
        }

        public List<Worktree> Worktrees
        {
            get => _worktrees;
            private set => SetProperty(ref _worktrees, value);
        }

        public List<Models.Tag> Tags
        {
            get => _tags;
            private set => SetProperty(ref _tags, value);
        }

        public bool ShowTagsAsTree
        {
            get => _uiStates.ShowTagsAsTree;
            set
            {
                if (value != _uiStates.ShowTagsAsTree)
                {
                    _uiStates.ShowTagsAsTree = value;
                    VisibleTags = BuildVisibleTags();
                    OnPropertyChanged();
                }
            }
        }

        public object VisibleTags
        {
            get => _visibleTags;
            private set => SetProperty(ref _visibleTags, value);
        }

        public List<Models.Submodule> Submodules
        {
            get => _submodules;
            private set => SetProperty(ref _submodules, value);
        }

        public bool ShowSubmodulesAsTree
        {
            get => _uiStates.ShowSubmodulesAsTree;
            set
            {
                if (value != _uiStates.ShowSubmodulesAsTree)
                {
                    _uiStates.ShowSubmodulesAsTree = value;
                    VisibleSubmodules = BuildVisibleSubmodules();
                    OnPropertyChanged();
                }
            }
        }

        public object VisibleSubmodules
        {
            get => _visibleSubmodules;
            private set => SetProperty(ref _visibleSubmodules, value);
        }

        public int LocalChangesCount
        {
            get => _localChangesCount;
            private set => SetProperty(ref _localChangesCount, value);
        }

        public int StashesCount
        {
            get => _stashesCount;
            private set => SetProperty(ref _stashesCount, value);
        }

        public int LocalBranchesCount
        {
            get => _localBranchesCount;
            private set => SetProperty(ref _localBranchesCount, value);
        }

        public bool IncludeUntracked
        {
            get => _uiStates.IncludeUntrackedInLocalChanges;
            set
            {
                if (value != _uiStates.IncludeUntrackedInLocalChanges)
                {
                    _uiStates.IncludeUntrackedInLocalChanges = value;
                    _uiStates.Save();
                    OnPropertyChanged();
                    RefreshSubmodules();
                    RefreshWorkingCopyChanges();
                }
            }
        }

        public bool IsSearchingCommits
        {
            get => _isSearchingCommits;
            set
            {
                if (SetProperty(ref _isSearchingCommits, value))
                {
                    if (value)
                        SelectedViewIndex = 1;
                    else
                        _searchCommitContext.EndSearch();
                }
            }
        }

        public SearchCommitContext SearchCommitContext
        {
            get => _searchCommitContext;
        }

        public bool IsLocalBranchGroupExpanded
        {
            get => _uiStates.IsLocalBranchesExpandedInSideBar;
            set
            {
                if (value != _uiStates.IsLocalBranchesExpandedInSideBar)
                {
                    _uiStates.IsLocalBranchesExpandedInSideBar = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsRemoteGroupExpanded
        {
            get => _uiStates.IsRemotesExpandedInSideBar;
            set
            {
                if (value != _uiStates.IsRemotesExpandedInSideBar)
                {
                    _uiStates.IsRemotesExpandedInSideBar = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsStashGroupExpanded
        {
            get => _uiStates.IsStashesExpandedInSideBar;
            set
            {
                if (value != _uiStates.IsStashesExpandedInSideBar)
                {
                    _uiStates.IsStashesExpandedInSideBar = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsStashListVisible));
                }
            }
        }

        public bool IsStashListVisible
        {
            get => !IsBare && IsStashGroupExpanded;
        }

        public bool IsTagGroupExpanded
        {
            get => _uiStates.IsTagsExpandedInSideBar;
            set
            {
                if (value != _uiStates.IsTagsExpandedInSideBar)
                {
                    _uiStates.IsTagsExpandedInSideBar = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsSubmoduleGroupExpanded
        {
            get => _uiStates.IsSubmodulesExpandedInSideBar;
            set
            {
                if (value != _uiStates.IsSubmodulesExpandedInSideBar)
                {
                    _uiStates.IsSubmodulesExpandedInSideBar = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsWorktreeGroupExpanded
        {
            get => _uiStates.IsWorktreeExpandedInSideBar;
            set
            {
                if (value != _uiStates.IsWorktreeExpandedInSideBar)
                {
                    _uiStates.IsWorktreeExpandedInSideBar = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsSortingLocalBranchByName
        {
            get => _uiStates.LocalBranchSortMode == Models.BranchSortMode.Name;
            set
            {
                _uiStates.LocalBranchSortMode = value ? Models.BranchSortMode.Name : Models.BranchSortMode.CommitterDate;
                OnPropertyChanged();

                var builder = BuildBranchTree(_branches, _remotes);
                LocalBranchTrees = builder.Locals;
                RemoteBranchTrees = builder.Remotes;
            }
        }

        public bool IsSortingRemoteBranchByName
        {
            get => _uiStates.RemoteBranchSortMode == Models.BranchSortMode.Name;
            set
            {
                _uiStates.RemoteBranchSortMode = value ? Models.BranchSortMode.Name : Models.BranchSortMode.CommitterDate;
                OnPropertyChanged();

                var builder = BuildBranchTree(_branches, _remotes);
                LocalBranchTrees = builder.Locals;
                RemoteBranchTrees = builder.Remotes;
            }
        }

        public bool IsSortingTagsByName
        {
            get => _uiStates.TagSortMode == Models.TagSortMode.Name;
            set
            {
                _uiStates.TagSortMode = value ? Models.TagSortMode.Name : Models.TagSortMode.CreatorDate;
                OnPropertyChanged();
                VisibleTags = BuildVisibleTags();
            }
        }

        public InProgressContext InProgressContext
        {
            get => _workingCopy?.InProgressContext;
        }

        public Models.BisectState BisectState
        {
            get => _bisectState;
            private set => SetProperty(ref _bisectState, value);
        }

        public bool IsBisectCommandRunning
        {
            get => _isBisectCommandRunning;
            private set => SetProperty(ref _isBisectCommandRunning, value);
        }

        public bool IsAutoFetching
        {
            get => _isAutoFetching;
            private set => SetProperty(ref _isAutoFetching, value);
        }

        public bool IsRemoteSyncing
        {
            get => Interlocked.CompareExchange(ref _runningRemoteSyncs, 0, 0) > 0;
        }

        public bool IsActive
        {
            get => Interlocked.CompareExchange(ref _isActive, 0, 0) != 0;
        }

        public bool IsForeground
        {
            get => App.GetLauncher()?.IsWindowActive ?? true;
        }

        public bool IsRefreshing
        {
            get => _isRefreshing;
            private set => SetProperty(ref _isRefreshing, value);
        }

        public AvaloniaList<Models.IssueTracker> IssueTrackers
        {
            get;
        } = [];

        public AvaloniaList<CommandLog> Logs
        {
            get;
        } = [];

        public Repository(bool isBare, string path, string gitDir)
        {
            IsBare = isBare;
            FullPath = path.Replace('\\', '/').TrimEnd('/');
            GitDir = gitDir.Replace('\\', '/').TrimEnd('/');

            var commonDirFile = Path.Combine(GitDir, "commondir");
            var isWorktree = GitDir.IndexOf("/worktrees/", StringComparison.Ordinal) > 0 &&
                          File.Exists(commonDirFile);

            if (isWorktree)
            {
                var commonDir = File.ReadAllText(commonDirFile).Trim();
                if (Path.IsPathRooted(commonDir))
                    commonDir = new DirectoryInfo(commonDir).FullName;
                else
                    commonDir = new DirectoryInfo(Path.Combine(GitDir, commonDir)).FullName;

                _gitCommonDir = commonDir.Replace('\\', '/').TrimEnd('/');
            }
            else
            {
                _gitCommonDir = GitDir;
            }

            _settings = Models.RepositorySettings.Get(_gitCommonDir);
            Commands.GitRuntimeConfig.Register(FullPath, GitDir, _gitCommonDir, _settings);
            _uiStates = Models.RepositoryUIStates.Load(GitDir);
        }

        public void Open(int autoFetchIndex = 0, int autoFetchCount = 1)
        {
            using var span = StartLifecycleDiagnosticSpan("open");
            span.Set("autoFetchIndex", autoFetchIndex);
            span.Set("autoFetchCount", autoFetchCount);

            Interlocked.Exchange(ref _isClosed, 0);

            try
            {
                _watcher = new Models.Watcher(this, FullPath, _gitCommonDir);
                span.Set("watcherStarted", true);
            }
            catch (Exception ex)
            {
                span.Set("watcherStarted", false);
                span.Set("watcherError", ex.Message);
                SendNotification($"Failed to start watcher for repository: '{FullPath}'. You may need to press 'F5' to refresh repository manually!\n\nReason: {ex.Message}", true);
            }

            _historyFilterMode = _uiStates.GetHistoryFilterMode();
            _histories = new Histories(this);
            _workingCopy = new WorkingCopy(this) { CommitMessage = _uiStates.LastCommitMessage };
            _stashesPage = new StashesPage(this);
            _searchCommitContext = new SearchCommitContext(this);
            _selectedViewIndex = !IsBare && Preferences.Instance.ShowLocalChangesByDefault ? 0 : 1;
            _nextAutoFetchTime = DateTime.Now
                .Add(GetAutoFetchInterval())
                .Add(GetInitialAutoFetchDelay(autoFetchIndex, autoFetchCount));
            ScheduleNextMultiPackIndexWriteCheck(DateTime.Now);
            _autoFetchTimer = new Timer(AutoFetchByTimer, null, 5000, 5000);
            QueueMultiPackIndexWriteCheck();

            span.Set("selectedViewIndex", _selectedViewIndex);
            span.Set("historyFilterMode", _historyFilterMode.ToString());
        }

        public void Activate()
        {
            using var span = StartLifecycleDiagnosticSpan("activate");
            Interlocked.Exchange(ref _isActive, 1);

            if (Interlocked.CompareExchange(ref _isLoaded, 0, 0) == 0)
            {
                span.Set("action", "refresh_all");
                RefreshAll();
                return;
            }

            span.Set("action", "refresh_dirty");
            RefreshDirty();
        }

        public void Deactivate()
        {
            Interlocked.Exchange(ref _isActive, 0);
        }

        public void Close()
        {
            Interlocked.Exchange(ref _isClosed, 1);
            Deactivate();

            var commitMessage = _workingCopy?.CommitMessage ?? string.Empty;
            _workingCopy?.SaveInProgressCommitMessage();

            _uiStates.LastCommitMessage = commitMessage;
            _uiStates.Save();

            if (_cancellationRefreshBranches is { IsCancellationRequested: false })
                _cancellationRefreshBranches.Cancel();
            if (_cancellationRefreshTags is { IsCancellationRequested: false })
                _cancellationRefreshTags.Cancel();
            if (_cancellationRefreshWorkingCopyChanges is { IsCancellationRequested: false })
                _cancellationRefreshWorkingCopyChanges.Cancel();
            if (_cancellationRefreshCommits is { IsCancellationRequested: false })
                _cancellationRefreshCommits.Cancel();
            if (_cancellationRefreshStashes is { IsCancellationRequested: false })
                _cancellationRefreshStashes.Cancel();

            _workingCopy?.Close();
            _histories?.Dispose();
            _stashesPage?.Dispose();
            _watcher?.Dispose();
            _autoFetchTimer.Dispose();
            DisposeWorkingCopyDirtyRefreshTimer();
            Commands.GitRuntimeConfig.Unregister(FullPath, GitDir, _gitCommonDir);
        }

        public void SendNotification(string message, bool isError = false)
        {
            Models.Notification.Send(FullPath, message, isError);
        }

        public bool CanCreatePopup()
        {
            var page = GetOwnerPage();
            if (page == null)
                return false;

            return page.CanCreatePopup();
        }

        public bool IsFetching
        {
            get
            {
                lock (_fetchStateLock)
                    return _isFetching != 0;
            }
        }

        public bool BeginFetch()
        {
            lock (_fetchStateLock)
            {
                if (_isFetching != 0)
                    return false;

                _isFetching = 1;
                _fetchCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            BeginRemoteSync();
            OnPropertyChanged(nameof(IsFetching));
            return true;
        }

        public void EndFetch()
        {
            TaskCompletionSource<bool> completion;
            lock (_fetchStateLock)
            {
                if (_isFetching == 0)
                    return;

                _isFetching = 0;
                completion = _fetchCompletion;
                _fetchCompletion = null;
            }

            OnPropertyChanged(nameof(IsFetching));
            completion?.TrySetResult(true);
            EndRemoteSync();
        }

        public Task WaitForFetchCompletionAsync()
        {
            lock (_fetchStateLock)
                return _fetchCompletion?.Task ?? Task.CompletedTask;
        }

        public void BeginRemoteSync()
        {
            if (Interlocked.Increment(ref _runningRemoteSyncs) == 1)
                OnPropertyChanged(nameof(IsRemoteSyncing));
        }

        public void EndRemoteSync()
        {
            if (Interlocked.Decrement(ref _runningRemoteSyncs) > 0)
                return;

            Interlocked.Exchange(ref _runningRemoteSyncs, 0);
            OnPropertyChanged(nameof(IsRemoteSyncing));
        }

        public void ShowPopup(Popup popup)
        {
            var page = GetOwnerPage();
            if (page != null)
                page.Popup = popup;
        }

        public async Task ShowAndStartPopupAsync(Popup popup)
        {
            var page = GetOwnerPage();
            page.Popup = popup;

            if (popup.CanStartDirectly())
                await page.ProcessPopupAsync();
        }

        public bool IsGitFlowEnabled()
        {
            return GitFlow is { IsValid: true } &&
                _branches.Find(x => x.IsLocal && x.Name.Equals(GitFlow.Master, StringComparison.Ordinal)) != null &&
                _branches.Find(x => x.IsLocal && x.Name.Equals(GitFlow.Develop, StringComparison.Ordinal)) != null;
        }

        public Models.GitFlowBranchType GetGitFlowType(Models.Branch b)
        {
            if (!IsGitFlowEnabled())
                return Models.GitFlowBranchType.None;

            var name = b.Name;
            if (name.StartsWith(GitFlow.FeaturePrefix, StringComparison.Ordinal))
                return Models.GitFlowBranchType.Feature;
            if (name.StartsWith(GitFlow.ReleasePrefix, StringComparison.Ordinal))
                return Models.GitFlowBranchType.Release;
            if (name.StartsWith(GitFlow.HotfixPrefix, StringComparison.Ordinal))
                return Models.GitFlowBranchType.Hotfix;
            return Models.GitFlowBranchType.None;
        }

        public bool IsLFSEnabled()
        {
            var path = Path.Combine(FullPath, ".git", "hooks", "pre-push");
            if (!File.Exists(path))
                return false;

            try
            {
                var content = File.ReadAllText(path);
                return content.Contains("git lfs pre-push");
            }
            catch
            {
                return false;
            }
        }

        public async Task InstallLFSAsync()
        {
            var log = CreateLog("Install LFS");
            var succ = await new Commands.LFS(FullPath).Use(log).InstallAsync();
            if (succ)
                SendNotification("LFS enabled successfully!");

            log.Complete();
        }

        public async Task<bool> TrackLFSFileAsync(string pattern, bool isFilenameMode)
        {
            var log = CreateLog("Track LFS");
            var succ = await new Commands.LFS(FullPath)
                .Use(log)
                .TrackAsync(pattern, isFilenameMode);

            if (succ)
                SendNotification($"Tracking successfully! Pattern: {pattern}");

            log.Complete();
            return succ;
        }

        public async Task<bool> LockLFSFileAsync(string remote, string path)
        {
            var log = CreateLog("Lock LFS File");
            var succ = await new Commands.LFS(FullPath)
                .Use(log)
                .LockAsync(remote, path);

            if (succ)
                SendNotification($"Lock file successfully! File: {path}");

            log.Complete();
            return succ;
        }

        public async Task<bool> UnlockLFSFileAsync(string remote, string path, bool force, bool notify)
        {
            var log = CreateLog("Unlock LFS File");
            var succ = await new Commands.LFS(FullPath)
                .Use(log)
                .UnlockAsync(remote, path, force);

            if (succ && notify)
                SendNotification($"Unlock file successfully! File: {path}");

            log.Complete();
            return succ;
        }

        public CommandLog CreateLog(string name)
        {
            var log = new CommandLog(name);
            Logs.Insert(0, log);
            return log;
        }

        public void RefreshAll()
        {
            MarkNextRefreshBatchReason("all");
            using var span = StartRefreshDiagnosticSpan("all.schedule");

            Interlocked.Exchange(ref _isLoaded, 1);
            Interlocked.Exchange(ref _dirtyRefreshFlags, 0);
            span.Set("includeUntracked", _uiStates.IncludeUntrackedInLocalChanges);
            span.Set("selectedViewIndex", _selectedViewIndex);

            var refSnapshotTask = StartRefSnapshotTask();
            RefreshCommits(refSnapshotTask);
            RefreshBranches(refSnapshotTask);
            RefreshTags(refSnapshotTask);
            RefreshSubmodules();
            RefreshWorktrees();
            RefreshWorkingCopyChanges();
            RefreshStashes();

            Task.Run(async () =>
            {
                BeginRefreshTask();
                using var metadataSpan = StartRefreshDiagnosticSpan("metadata");
                var metadataSuccess = false;
                try
                {
                    var issuetrackers = new List<Models.IssueTracker>();
                    await new Commands.IssueTracker(FullPath, true).ReadAllAsync(issuetrackers, true).ConfigureAwait(false);
                    await new Commands.IssueTracker(FullPath, false).ReadAllAsync(issuetrackers, false).ConfigureAwait(false);
                    metadataSpan.Set("issueTrackerCount", issuetrackers.Count);
                    Dispatcher.UIThread.Post(() =>
                    {
                        IssueTrackers.Clear();
                        IssueTrackers.AddRange(issuetrackers);
                    });

                    var config = await new Commands.Config(FullPath).ReadAllAsync().ConfigureAwait(false);
                    metadataSpan.Set("configCount", config.Count);
                    _hasAllowedSignersFile = config.TryGetValue("gpg.ssh.allowedsignersfile", out var allowedSignersFile) && !string.IsNullOrEmpty(allowedSignersFile);

                    if (config.TryGetValue("gitflow.branch.master", out var masterName))
                        GitFlow.Master = masterName;
                    if (config.TryGetValue("gitflow.branch.develop", out var developName))
                        GitFlow.Develop = developName;
                    if (config.TryGetValue("gitflow.prefix.feature", out var featurePrefix))
                        GitFlow.FeaturePrefix = featurePrefix;
                    if (config.TryGetValue("gitflow.prefix.release", out var releasePrefix))
                        GitFlow.ReleasePrefix = releasePrefix;
                    if (config.TryGetValue("gitflow.prefix.hotfix", out var hotfixPrefix))
                        GitFlow.HotfixPrefix = hotfixPrefix;

                    metadataSuccess = true;
                }
                catch (Exception e)
                {
                    metadataSpan.Set("success", false);
                    metadataSpan.Set("error", e.Message);
                    throw;
                }
                finally
                {
                    if (metadataSuccess)
                        metadataSpan.Set("success", true);

                    EndRefreshTask();
                }
            });
        }

        private Task<Commands.QueryRefSnapshot.Snapshot> StartRefSnapshotTask()
        {
            return Task.Run(async () => await new Commands.QueryRefSnapshot(FullPath).GetResultAsync().ConfigureAwait(false));
        }

        public async Task FetchAsync(bool autoStart)
        {
            if (IsFetching || !CanCreatePopup())
                return;

            if (_remotes.Count == 0)
            {
                SendNotification("No remotes added to this repository!!!", true);
                return;
            }

            if (autoStart)
                await ShowAndStartPopupAsync(new Fetch(this));
            else
                ShowPopup(new Fetch(this));
        }

        public async Task PullAsync(bool autoStart)
        {
            if (IsBare || !CanCreatePopup())
                return;

            if (_remotes.Count == 0)
            {
                SendNotification("No remotes added to this repository!!!", true);
                return;
            }

            if (_currentBranch == null)
            {
                SendNotification("Can NOT find current branch!!!", true);
                return;
            }

            var pull = new Pull(this, null);
            if (autoStart && pull.SelectedBranch != null)
                await ShowAndStartPopupAsync(pull);
            else
                ShowPopup(pull);
        }

        public async Task PushAsync(bool autoStart)
        {
            if (!CanCreatePopup())
                return;

            if (_remotes.Count == 0)
            {
                SendNotification("No remotes added to this repository!!!", true);
                return;
            }

            if (_currentBranch == null)
            {
                SendNotification("Can NOT find current branch!!!", true);
                return;
            }

            if (autoStart)
                await ShowAndStartPopupAsync(new Push(this, null));
            else
                ShowPopup(new Push(this, null));
        }

        public void ApplyPatch()
        {
            if (CanCreatePopup())
                ShowPopup(new Apply(this));
        }

        public async Task ExecCustomActionAsync(Models.CustomAction action, object scopeTarget)
        {
            if (!CanCreatePopup())
                return;

            var popup = new ExecuteCustomAction(this, action, scopeTarget);
            if (action.Controls.Count == 0)
                await ShowAndStartPopupAsync(popup);
            else
                ShowPopup(popup);
        }

        public async Task CleanupAsync()
        {
            if (CanCreatePopup())
                await ShowAndStartPopupAsync(new Cleanup(this));
        }

        public void ClearFilter()
        {
            Filter = string.Empty;
        }

        public IDisposable LockWatcher()
        {
            return _watcher?.Lock();
        }

        public void RefreshAfterCreateBranch(Models.Branch created, bool checkout)
        {
            _watcher?.MarkBranchUpdated();
            _watcher?.MarkWorkingCopyUpdated();

            _branches.RemoveAll(b => b.IsLocal && b.FriendlyName.Equals(created.FriendlyName, StringComparison.Ordinal));
            _branches.Add(created);

            if (checkout)
            {
                if (_currentBranch.IsDetachedHead)
                {
                    _branches.Remove(_currentBranch);
                }
                else
                {
                    _currentBranch.IsCurrent = false;
                    _currentBranch.WorktreePath = null;
                }

                created.IsCurrent = true;
                created.WorktreePath = FullPath;

                var folderEndIdx = created.FullName.LastIndexOf('/');
                if (folderEndIdx > 10)
                    _uiStates.ExpandedBranchNodesInSideBar.Add(created.FullName.Substring(0, folderEndIdx));

                if (_historyFilterMode == Models.FilterMode.Included)
                    SetBranchFilterMode(created, Models.FilterMode.Included, false, false);

                CurrentBranch = created;
            }

            List<Models.Branch> locals = [];
            foreach (var b in _branches)
            {
                if (b.IsLocal)
                    locals.Add(b);
            }

            var builder = BuildBranchTree(locals, [], false);
            LocalBranchTrees = builder.Locals;

            RefreshCommits();
            RefreshWorkingCopyChanges();
            RefreshWorktrees();
        }

        public void RefreshAfterCheckoutBranch(Models.Branch checkouted)
        {
            _watcher?.MarkBranchUpdated();
            _watcher?.MarkWorkingCopyUpdated();

            if (_currentBranch.IsDetachedHead)
            {
                _branches.Remove(_currentBranch);
            }
            else
            {
                _currentBranch.IsCurrent = false;
                _currentBranch.WorktreePath = null;
            }

            checkouted.IsCurrent = true;
            checkouted.WorktreePath = FullPath;
            if (_historyFilterMode == Models.FilterMode.Included)
                SetBranchFilterMode(checkouted, Models.FilterMode.Included, false, false);

            List<Models.Branch> locals = [];
            foreach (var b in _branches)
            {
                if (b.IsLocal)
                    locals.Add(b);
            }

            var builder = BuildBranchTree(locals, [], false);
            LocalBranchTrees = builder.Locals;
            CurrentBranch = checkouted;

            RefreshCommits();
            RefreshWorkingCopyChanges();
            RefreshWorktrees();
        }

        public void RefreshAfterRenameBranch(Models.Branch b, string newName)
        {
            _watcher?.MarkBranchUpdated();

            var newFullName = $"refs/heads/{newName}";
            _uiStates.RenameBranchFilter(b.FullName, newFullName);

            b.Name = newName;
            b.FullName = newFullName;

            List<Models.Branch> locals = [];
            foreach (var branch in _branches)
            {
                if (branch.IsLocal)
                    locals.Add(branch);
            }

            var builder = BuildBranchTree(locals, [], false);
            LocalBranchTrees = builder.Locals;

            RefreshCommits();
            RefreshWorktrees();
        }

        public void MarkBranchesDirtyManually()
        {
            _watcher?.MarkBranchUpdated();
            RefreshBranches();
            RefreshCommits();
            RefreshWorkingCopyChanges();
            RefreshWorktrees();
        }

        public void MarkTagsDirtyManually()
        {
            _watcher?.MarkTagUpdated();
            RefreshTags();
            RefreshCommits();
        }

        public void MarkWorkingCopyDirtyManually()
        {
            LogWorkingCopyRefreshEvent("manual_dirty", ("isActive", IsActive));
            _watcher?.MarkWorkingCopyUpdated();
            RefreshWorkingCopyChanges();
        }

        public void MarkWorkingCopyDirty()
        {
            if (!IsBare)
                Interlocked.Exchange(ref _isWorkingCopyDirty, 1);
        }

        public void MarkBranchesDirty()
        {
            MarkRefreshDirty(RefreshDirtyFlags.Branches);
        }

        public void MarkWorktreesDirty()
        {
            MarkRefreshDirty(RefreshDirtyFlags.Worktrees);
        }

        public void MarkTagsDirty()
        {
            MarkRefreshDirty(RefreshDirtyFlags.Tags);
        }

        public void MarkCommitsDirty()
        {
            MarkRefreshDirty(RefreshDirtyFlags.Commits);
        }

        public void MarkSubmodulesDirty()
        {
            MarkRefreshDirty(RefreshDirtyFlags.Submodules);
        }

        public void MarkStashesDirty()
        {
            MarkRefreshDirty(RefreshDirtyFlags.Stashes);
        }

        public void RefreshWorkingCopyChangesIfDirty()
        {
            if (Interlocked.CompareExchange(ref _isWorkingCopyDirty, 0, 0) != 0)
            {
                if (!IsActive || !IsForeground)
                    return;

                var forceNextDirty = Interlocked.CompareExchange(ref _forceNextWorkingCopyDirtyRefresh, 0, 0) != 0;
                if (!forceNextDirty && !CanRefreshWorkingCopyChangesByDirty(out var delay))
                {
                    ScheduleWorkingCopyDirtyRefresh(delay);
                    LogWorkingCopyRefreshEvent("throttled", ("delayMs", Math.Ceiling(delay.TotalMilliseconds)));
                    return;
                }

                UnscheduleWorkingCopyDirtyRefresh();
                if (RefreshWorkingCopyChanges() && forceNextDirty)
                    Interlocked.Exchange(ref _forceNextWorkingCopyDirtyRefresh, 0);
            }
        }

        public void RefreshWorkingCopyChangesOnForegroundActivated()
        {
            Interlocked.Exchange(ref _forceNextWorkingCopyDirtyRefresh, 1);
            RefreshWorkingCopyChangesIfDirty();
        }

        public void MarkStashesDirtyManually()
        {
            _watcher?.MarkStashUpdated();
            RefreshStashes();
        }

        public void MarkSubmodulesDirtyManually()
        {
            _watcher?.MarkSubmodulesUpdated();
            RefreshSubmodules();
        }

        public void MarkFetched()
        {
            ScheduleNextAutoFetchFromNow();
            QueueMultiPackIndexWriteCheck();
        }

        public void NavigateToCommit(string sha, bool isDelayMode = false)
        {
            if (isDelayMode)
            {
                _navigateToCommitDelayed = sha;
            }
            else
            {
                SelectedViewIndex = 1;
                _histories?.NavigateTo(sha);
            }
        }

        public void SetCommitMessage(string message)
        {
            if (_workingCopy is not null)
                _workingCopy.CommitMessage = message;
        }

        public void ClearCommitMessage()
        {
            if (_workingCopy is not null)
                _workingCopy.CommitMessage = string.Empty;
        }

        public Models.Commit GetSelectedCommitInHistory()
        {
            return (_histories?.DetailContext as CommitDetail)?.Commit;
        }

        public void ClearHistoryFilters()
        {
            _uiStates.HistoryFilters.Clear();
            HistoryFilterMode = Models.FilterMode.None;

            ResetBranchTreeFilterMode(LocalBranchTrees);
            ResetBranchTreeFilterMode(RemoteBranchTrees);
            ResetTagFilterMode();
            RefreshCommits();
        }

        public void RemoveHistoryFilter(Models.HistoryFilter filter)
        {
            if (_uiStates.HistoryFilters.Remove(filter))
            {
                HistoryFilterMode = _uiStates.GetHistoryFilterMode();
                RefreshHistoryFilters(true);
            }
        }

        public void UpdateBranchNodeIsExpanded(BranchTreeNode node)
        {
            if (_uiStates == null || !string.IsNullOrWhiteSpace(_filter))
                return;

            if (node.IsExpanded)
            {
                if (!_uiStates.ExpandedBranchNodesInSideBar.Contains(node.Path))
                    _uiStates.ExpandedBranchNodesInSideBar.Add(node.Path);
            }
            else
            {
                _uiStates.ExpandedBranchNodesInSideBar.Remove(node.Path);
            }
        }

        public void SetTagFilterMode(Models.Tag tag, Models.FilterMode mode)
        {
            var changed = _uiStates.UpdateHistoryFilters(tag.Name, Models.FilterType.Tag, mode);
            if (changed)
                RefreshHistoryFilters(true);
        }

        public void SetBranchFilterMode(Models.Branch branch, Models.FilterMode mode, bool clearExists, bool refresh)
        {
            var node = FindBranchNode(branch.IsLocal ? _localBranchTrees : _remoteBranchTrees, branch.FullName);
            if (node != null)
                SetBranchFilterMode(node, mode, clearExists, refresh);
        }

        public void SetBranchFilterMode(BranchTreeNode node, Models.FilterMode mode, bool clearExists, bool refresh)
        {
            var isLocal = node.Path.StartsWith("refs/heads/", StringComparison.Ordinal);
            var tree = isLocal ? _localBranchTrees : _remoteBranchTrees;

            if (clearExists)
            {
                _uiStates.HistoryFilters.Clear();
                HistoryFilterMode = Models.FilterMode.None;
            }

            if (node.Backend is Models.Branch branch)
            {
                var type = isLocal ? Models.FilterType.LocalBranch : Models.FilterType.RemoteBranch;
                var changed = _uiStates.UpdateHistoryFilters(node.Path, type, mode);
                if (!changed)
                    return;

                if (isLocal && !string.IsNullOrEmpty(branch.Upstream) && !branch.IsUpstreamGone)
                    _uiStates.UpdateHistoryFilters(branch.Upstream, Models.FilterType.RemoteBranch, mode);
            }
            else
            {
                var type = isLocal ? Models.FilterType.LocalBranchFolder : Models.FilterType.RemoteBranchFolder;
                var changed = _uiStates.UpdateHistoryFilters(node.Path, type, mode);
                if (!changed)
                    return;

                _uiStates.RemoveBranchFiltersByPrefix(node.Path);
            }

            var parentType = isLocal ? Models.FilterType.LocalBranchFolder : Models.FilterType.RemoteBranchFolder;
            var cur = node;
            do
            {
                var lastSepIdx = cur.Path.LastIndexOf('/');
                if (lastSepIdx <= 0)
                    break;

                var parentPath = cur.Path.Substring(0, lastSepIdx);
                var parent = FindBranchNode(tree, parentPath);
                if (parent == null)
                    break;

                _uiStates.UpdateHistoryFilters(parent.Path, parentType, Models.FilterMode.None);
                cur = parent;
            } while (true);

            RefreshHistoryFilters(refresh);
        }

        public async Task StashAllAsync(bool autoStart)
        {
            if (!CanCreatePopup())
                return;

            var popup = new StashChanges(this, null);
            if (autoStart)
                await ShowAndStartPopupAsync(popup);
            else
                ShowPopup(popup);
        }

        public async Task SkipMergeAsync()
        {
            if (_workingCopy != null)
                await _workingCopy.SkipMergeAsync();
        }

        public async Task AbortMergeAsync()
        {
            if (_workingCopy != null)
                await _workingCopy.AbortMergeAsync();
        }

        public List<(Models.CustomAction, CustomActionContextMenuLabel)> GetCustomActions(Models.CustomActionScope scope)
        {
            var actions = new List<(Models.CustomAction, CustomActionContextMenuLabel)>();

            foreach (var act in Preferences.Instance.CustomActions)
            {
                if (act.Scope == scope)
                    actions.Add((act, new CustomActionContextMenuLabel(act.Name, true)));
            }

            foreach (var act in _settings.CustomActions)
            {
                if (act.Scope == scope)
                    actions.Add((act, new CustomActionContextMenuLabel(act.Name, false)));
            }

            return actions;
        }

        public async Task ExecBisectCommandAsync(string subcmd)
        {
            using var lockWatcher = _watcher?.Lock();
            IsBisectCommandRunning = true;

            var log = CreateLog($"Bisect({subcmd})");

            var succ = await new Commands.Bisect(FullPath, subcmd).Use(log).ExecAsync();
            log.Complete();

            var head = await new Commands.QueryRevisionByRefName(FullPath, "HEAD").GetResultAsync();
            if (!succ)
                SendNotification(log.Content.Substring(log.Content.IndexOf('\n')).Trim(), true);
            else if (log.Content.Contains("is the first bad commit"))
                SendNotification(log.Content.Substring(log.Content.IndexOf('\n')).Trim());

            MarkBranchesDirtyManually();
            NavigateToCommit(head, true);
            IsBisectCommandRunning = false;
        }

        public bool MayHaveSubmodules()
        {
            var modulesFile = Path.Combine(FullPath, ".gitmodules");
            var info = new FileInfo(modulesFile);
            return info.Exists && info.Length > 20;
        }

        public void RefreshBranches()
        {
            RefreshBranches(StartRefSnapshotTask());
        }

        private void RefreshBranches(Task<Commands.QueryRefSnapshot.Snapshot> refSnapshotTask)
        {
            if (_cancellationRefreshBranches is { IsCancellationRequested: false })
                _cancellationRefreshBranches.Cancel();

            _cancellationRefreshBranches = new CancellationTokenSource();
            var token = _cancellationRefreshBranches.Token;

            BeginRefreshTask();
            Task.Run(async () =>
            {
                using var span = StartRefreshDiagnosticSpan("branches");
                try
                {
                    var remotesTask = new Commands.QueryRemotes(FullPath).GetResultAsync();
                    var branchesTask = QueryBranchesWithCacheAsync(refSnapshotTask, token, span);

                    var remotes = await remotesTask.ConfigureAwait(false);
                    span.Set("remoteCount", remotes.Count);
                    Dispatcher.UIThread.Invoke(() =>
                    {
                        if (token.IsCancellationRequested)
                            return;

                        Remotes = remotes;

                        if (_workingCopy != null)
                            _workingCopy.HasRemotes = remotes.Count > 0;
                    });

                    var branches = await branchesTask.ConfigureAwait(false);
                    var builder = BuildBranchTree(branches, remotes);
                    span.Set("branchCount", branches.Count);

                    Dispatcher.UIThread.Invoke(() =>
                    {
                        if (token.IsCancellationRequested)
                            return;

                        Branches = branches;
                        CurrentBranch = branches.Find(x => x.IsCurrent);
                        _searchCommitContext?.RefreshScopes();
                        LocalBranchTrees = builder.Locals;
                        RemoteBranchTrees = builder.Remotes;

                        var localBranchesCount = 0;
                        foreach (var b in branches)
                        {
                            if (b.IsLocal && !b.IsDetachedHead)
                                localBranchesCount++;
                        }
                        LocalBranchesCount = localBranchesCount;

                        var hasPendingPullOrPush = CurrentBranch?.IsTrackStatusVisible ?? false;
                        GetOwnerPage()?.ChangeDirtyState(Models.DirtyState.HasPendingPullOrPush, !hasPendingPullOrPush);
                    });
                }
                finally
                {
                    span.MarkCanceled(token.IsCancellationRequested);
                    EndRefreshTask();
                }
            });
        }

        public void RefreshWorktrees()
        {
            BeginRefreshTask();
            Task.Run(async () =>
            {
                using var span = StartRefreshDiagnosticSpan("worktrees");
                try
                {
                    var worktrees = await new Commands.Worktree(FullPath).ReadAllAsync().ConfigureAwait(false);
                    var cleaned = Worktree.Build(FullPath, worktrees);
                    span.Set("worktreeCount", cleaned.Count);
                    Dispatcher.UIThread.Invoke(() => Worktrees = cleaned);
                }
                finally
                {
                    EndRefreshTask();
                }
            });
        }

        public void RefreshTags()
        {
            RefreshTags(StartRefSnapshotTask());
        }

        private void RefreshTags(Task<Commands.QueryRefSnapshot.Snapshot> refSnapshotTask)
        {
            if (_cancellationRefreshTags is { IsCancellationRequested: false })
                _cancellationRefreshTags.Cancel();

            _cancellationRefreshTags = new CancellationTokenSource();
            var token = _cancellationRefreshTags.Token;

            BeginRefreshTask();
            Task.Run(async () =>
            {
                using var span = StartRefreshDiagnosticSpan("tags");
                try
                {
                    var tags = await QueryTagsWithCacheAsync(refSnapshotTask, token, span).ConfigureAwait(false);
                    span.Set("tagCount", tags.Count);
                    Dispatcher.UIThread.Invoke(() =>
                    {
                        if (token.IsCancellationRequested)
                            return;

                        Tags = tags;
                        VisibleTags = BuildVisibleTags();
                    });
                }
                finally
                {
                    span.MarkCanceled(token.IsCancellationRequested);
                    EndRefreshTask();
                }
            });
        }

        public void RefreshCommits()
        {
            RefreshCommits(StartRefSnapshotTask());
        }

        private void RefreshCommits(Task<Commands.QueryRefSnapshot.Snapshot> refSnapshotTask)
        {
            if (_cancellationRefreshCommits is { IsCancellationRequested: false })
                _cancellationRefreshCommits.Cancel();

            _cancellationRefreshCommits = new CancellationTokenSource();
            var token = _cancellationRefreshCommits.Token;

            BeginRefreshTask();
            Task.Run(async () =>
            {
                using var span = StartRefreshDiagnosticSpan("commits");
                try
                {
                    await Dispatcher.UIThread.InvokeAsync(() => _histories.IsLoading = true);

                    var builder = new StringBuilder();
                    builder
                        .Append('-').Append(Preferences.Instance.MaxHistoryCommits).Append(' ')
                        .Append(_uiStates.BuildHistoryParams(GitDir));

                    var historyArgs = builder.ToString();
                    var commits = await QueryCommitsWithCacheAsync(refSnapshotTask, historyArgs, token, span).ConfigureAwait(false);
                    span.Set("commitCount", commits.Count);

                    Dispatcher.UIThread.Invoke(() =>
                    {
                        if (token.IsCancellationRequested)
                            return;

                        if (_histories != null)
                        {
                            _histories.IsLoading = false;
                            _histories.Commits = commits;
                            BisectState = _histories.UpdateBisectInfo();

                            if (!string.IsNullOrEmpty(_navigateToCommitDelayed))
                                NavigateToCommit(_navigateToCommitDelayed);
                        }

                        _navigateToCommitDelayed = string.Empty;
                    });
                }
                finally
                {
                    span.MarkCanceled(token.IsCancellationRequested);
                    EndRefreshTask();
                }
            });
        }

        public void RefreshSubmodules()
        {
            if (!MayHaveSubmodules())
            {
                if (_submodules.Count > 0)
                {
                    Dispatcher.UIThread.Invoke(() =>
                    {
                        Submodules = [];
                        VisibleSubmodules = BuildVisibleSubmodules();
                    });
                }

                return;
            }

            BeginRefreshTask();
            Task.Run(async () =>
            {
                using var span = StartRefreshDiagnosticSpan("submodules");
                try
                {
                    var submodules = await new Commands.QuerySubmodules(FullPath, _uiStates.IncludeUntrackedInLocalChanges).GetResultAsync().ConfigureAwait(false);
                    span.Set("submoduleCount", submodules.Count);

                    Dispatcher.UIThread.Invoke(() =>
                    {
                        bool hasChanged = _submodules.Count != submodules.Count;
                        if (!hasChanged)
                        {
                            var old = new Dictionary<string, Models.Submodule>();
                            foreach (var module in _submodules)
                                old.Add(module.Path, module);

                            foreach (var module in submodules)
                            {
                                if (!old.TryGetValue(module.Path, out var exist))
                                {
                                    hasChanged = true;
                                    break;
                                }

                                hasChanged = !exist.SHA.Equals(module.SHA, StringComparison.Ordinal) ||
                                             !exist.Branch.Equals(module.Branch, StringComparison.Ordinal) ||
                                             !exist.URL.Equals(module.URL, StringComparison.Ordinal) ||
                                             exist.Status != module.Status;

                                if (hasChanged)
                                    break;
                            }
                        }

                        if (hasChanged)
                        {
                            Submodules = submodules;
                            VisibleSubmodules = BuildVisibleSubmodules();
                        }
                    });
                }
                finally
                {
                    EndRefreshTask();
                }
            });
        }

        public bool RefreshWorkingCopyChanges()
        {
            if (IsBare)
                return false;

            if (Interlocked.Exchange(ref _isRefreshingWorkingCopy, 1) != 0)
            {
                Interlocked.Exchange(ref _isWorkingCopyDirty, 1);
                LogWorkingCopyRefreshEvent("busy_mark_dirty", ("isActive", IsActive));
                return false;
            }

            UnscheduleWorkingCopyDirtyRefresh();

            if (_cancellationRefreshWorkingCopyChanges is { IsCancellationRequested: false })
                _cancellationRefreshWorkingCopyChanges.Cancel();

            _cancellationRefreshWorkingCopyChanges = new CancellationTokenSource();
            var token = _cancellationRefreshWorkingCopyChanges.Token;
            var noOptionalLocks = Interlocked.Add(ref _queryLocalChangesTimes, 1) > 1;
            Interlocked.Exchange(ref _isWorkingCopyDirty, 0);
            LogWorkingCopyRefreshEvent(
                "start",
                ("includeUntracked", _uiStates.IncludeUntrackedInLocalChanges),
                ("noOptionalLocks", noOptionalLocks));

            BeginRefreshTask();
            Task.Run(async () =>
            {
                using var span = StartRefreshDiagnosticSpan("working_copy");
                try
                {
                    var changes = await new Commands.QueryLocalChanges(FullPath, _uiStates.IncludeUntrackedInLocalChanges, noOptionalLocks)
                        .GetResultAsync()
                        .ConfigureAwait(false);

                    changes.Sort((l, r) => Models.NumericSort.Compare(l.Path, r.Path));
                    span.Set("changeCount", changes.Count);
                    span.Set("includeUntracked", _uiStates.IncludeUntrackedInLocalChanges);
                    span.Set("noOptionalLocks", noOptionalLocks);

                    Dispatcher.UIThread.Invoke(() =>
                    {
                        if (token.IsCancellationRequested)
                        {
                            LogWorkingCopyRefreshEvent("apply_canceled", ("changeCount", changes.Count));
                            return;
                        }

                        LogWorkingCopyRefreshEvent("apply", ("changeCount", changes.Count));
                        _workingCopy.SetData(changes);
                        LocalChangesCount = changes.Count;
                        OnPropertyChanged(nameof(InProgressContext));
                        GetOwnerPage()?.ChangeDirtyState(Models.DirtyState.HasLocalChanges, changes.Count == 0);
                    });
                }
                finally
                {
                    span.MarkCanceled(token.IsCancellationRequested);
                    Interlocked.Exchange(ref _lastWorkingCopyRefreshFinishedTicks, DateTime.UtcNow.Ticks);
                    Interlocked.Exchange(ref _isRefreshingWorkingCopy, 0);
                    EndRefreshTask();
                    RefreshWorkingCopyChangesIfDirty();
                }
            });

            return true;
        }

        public void RefreshStashes()
        {
            if (IsBare)
                return;

            if (_cancellationRefreshStashes is { IsCancellationRequested: false })
                _cancellationRefreshStashes.Cancel();

            _cancellationRefreshStashes = new CancellationTokenSource();
            var token = _cancellationRefreshStashes.Token;

            BeginRefreshTask();
            Task.Run(async () =>
            {
                using var span = StartRefreshDiagnosticSpan("stashes");
                try
                {
                    var stashes = await new Commands.QueryStashes(FullPath).GetResultAsync().ConfigureAwait(false);
                    span.Set("stashCount", stashes.Count);
                    Dispatcher.UIThread.Invoke(() =>
                    {
                        if (token.IsCancellationRequested)
                            return;

                        if (_stashesPage != null)
                            _stashesPage.Stashes = stashes;

                        StashesCount = stashes.Count;
                    });
                }
                finally
                {
                    span.MarkCanceled(token.IsCancellationRequested);
                    EndRefreshTask();
                }
            });
        }

        public void ToggleHistoryShowFlag(Models.HistoryShowFlags flag)
        {
            if (_uiStates.HistoryShowFlags.HasFlag(flag))
                HistoryShowFlags -= flag;
            else
                HistoryShowFlags |= flag;
        }

        public void CreateNewBranch()
        {
            if (_currentBranch == null)
            {
                SendNotification("Git cannot create a branch before your first commit.", true);
                return;
            }

            if (CanCreatePopup())
                ShowPopup(new CreateBranch(this, _currentBranch));
        }

        public async Task CheckoutBranchAsync(Models.Branch branch)
        {
            if (branch.IsLocal)
            {
                var worktree = _worktrees.Find(x => x.IsAttachedTo(branch));
                if (worktree != null)
                {
                    OpenWorktree(worktree);
                    return;
                }
            }

            if (IsBare)
                return;

            if (!CanCreatePopup())
                return;

            if (branch.IsLocal)
            {
                if (_workingCopy is { CanSwitchBranchDirectly: true })
                    await ShowAndStartPopupAsync(new Checkout(this, branch));
                else
                    ShowPopup(new Checkout(this, branch));
            }
            else
            {
                foreach (var b in _branches)
                {
                    if (b.IsLocal &&
                        b.Upstream.Equals(branch.FullName, StringComparison.Ordinal) &&
                        b.Ahead.Count == 0)
                    {
                        if (b.Behind.Count > 0)
                            ShowPopup(new CheckoutAndFastForward(this, b, branch));
                        else if (!b.IsCurrent)
                            await CheckoutBranchAsync(b);

                        return;
                    }
                }

                ShowPopup(new CreateBranch(this, branch));
            }
        }

        public async Task CheckoutTagAsync(Models.Tag tag)
        {
            var c = await new Commands.QuerySingleCommit(FullPath, tag.SHA).GetResultAsync();
            if (c != null && _histories != null)
                await _histories.CheckoutBranchByCommitAsync(c);
        }

        public void DeleteBranch(Models.Branch branch)
        {
            if (CanCreatePopup())
                ShowPopup(new DeleteBranch(this, branch));
        }

        public void DeleteMultipleBranches(List<Models.Branch> branches, bool isLocal)
        {
            if (CanCreatePopup())
                ShowPopup(new DeleteMultipleBranches(this, branches, isLocal));
        }

        public void MergeMultipleBranches(List<Models.Branch> branches)
        {
            if (CanCreatePopup())
                ShowPopup(new MergeMultiple(this, branches));
        }

        public void CreateNewTag()
        {
            if (_currentBranch == null)
            {
                SendNotification("Git cannot create a tag before your first commit.", true);
                return;
            }

            if (CanCreatePopup())
                ShowPopup(new CreateTag(this, _currentBranch));
        }

        public void DeleteTag(Models.Tag tag)
        {
            if (CanCreatePopup())
                ShowPopup(new DeleteTag(this, tag));
        }

        public void AddRemote()
        {
            if (CanCreatePopup())
                ShowPopup(new AddRemote(this));
        }

        public void DeleteRemote(Models.Remote remote)
        {
            if (CanCreatePopup())
                ShowPopup(new DeleteRemote(this, remote));
        }

        public async Task ToggleAutoFetchOnRemoteAsync(Models.Remote remote)
        {
            var val = remote.DisableAutoFetch ? "false" : "true";
            var succ = await new Commands.Config(FullPath).SetAsync($"remote.{remote.Name.Quoted()}.disableautofetch", val);
            if (succ)
                remote.DisableAutoFetch = !remote.DisableAutoFetch;
        }

        public void AddSubmodule()
        {
            if (CanCreatePopup())
                ShowPopup(new AddSubmodule(this));
        }

        public void UpdateSubmodules()
        {
            if (CanCreatePopup())
                ShowPopup(new UpdateSubmodules(this, null));
        }

        public async Task AutoUpdateSubmodulesAsync(Models.ICommandLog log)
        {
            var submodules = await new Commands.QueryUpdatableSubmodules(FullPath, false).GetResultAsync();
            if (submodules.Count == 0)
                return;

            do
            {
                if (_settings.AskBeforeAutoUpdatingSubmodules)
                {
                    var builder = new StringBuilder();
                    builder.Append("\n\n");
                    foreach (var s in submodules)
                        builder.Append("- ").Append(s).Append('\n');
                    builder.Append("\n");

                    var msg = App.Text("Checkout.WarnUpdatingSubmodules", builder.ToString());
                    var shouldContinue = await App.AskConfirmAsync(msg, Models.ConfirmButtonType.YesNo);
                    if (!shouldContinue)
                        break;
                }

                await new Commands.Submodule(FullPath)
                    .Use(log)
                    .UpdateAsync(submodules, false, _settings.EnableRecursiveWhenAutoUpdatingSubmodules, false);
            } while (false);
        }

        public void OpenSubmodule(string submodule)
        {
            var selfPage = GetOwnerPage();
            if (selfPage == null)
                return;

            var root = Path.GetFullPath(Path.Combine(FullPath, submodule));
            var normalizedPath = root.Replace('\\', '/').TrimEnd('/');
            App.GetLauncher().OpenRepositoryInTab(normalizedPath, null);
        }

        public void AddWorktree()
        {
            if (CanCreatePopup())
                ShowPopup(new AddWorktree(this));
        }

        public async Task PruneWorktreesAsync()
        {
            if (CanCreatePopup())
                await ShowAndStartPopupAsync(new PruneWorktrees(this));
        }

        public void OpenWorktree(Worktree worktree)
        {
            if (worktree.IsCurrent)
                return;

            var normalizedPath = worktree.FullPath.Replace('\\', '/').TrimEnd('/');
            App.GetLauncher().OpenRepositoryInTab(normalizedPath, null);
        }

        public async Task LockWorktreeAsync(Worktree worktree)
        {
            using var lockWatcher = _watcher?.Lock();
            var log = CreateLog("Lock Worktree");
            var succ = await new Commands.Worktree(FullPath).Use(log).LockAsync(worktree.FullPath);
            if (succ)
                worktree.IsLocked = true;
            log.Complete();
        }

        public async Task UnlockWorktreeAsync(Worktree worktree)
        {
            using var lockWatcher = _watcher?.Lock();
            var log = CreateLog("Unlock Worktree");
            var succ = await new Commands.Worktree(FullPath).Use(log).UnlockAsync(worktree.FullPath);
            if (succ)
                worktree.IsLocked = false;
            log.Complete();
        }

        public List<AI.Service> GetPreferredOpenAIServices()
        {
            var services = Preferences.Instance.OpenAIServices;
            if (services == null || services.Count == 0)
                return [];

            if (services.Count == 1)
                return [services[0]];

            var preferred = _settings.PreferredOpenAIService;
            var all = new List<AI.Service>();
            foreach (var service in services)
            {
                if (service.Name.Equals(preferred, StringComparison.Ordinal))
                    return [service];

                all.Add(service);
            }

            return all;
        }

        public void DiscardAllChanges()
        {
            if (CanCreatePopup())
                ShowPopup(new Discard(this));
        }

        public void ClearStashes()
        {
            if (CanCreatePopup())
                ShowPopup(new ClearStashes(this));
        }

        public async Task<bool> SaveCommitAsPatchAsync(Models.Commit commit, string folder, int index = 0)
        {
            var ignoredChars = new HashSet<char> { '/', '\\', ':', ',', '*', '?', '\"', '<', '>', '|', '`', '$', '^', '%', '[', ']', '+', '-' };
            var builder = new StringBuilder();
            builder.Append(index.ToString("D4"));
            builder.Append('-');

            var chars = commit.Subject.ToCharArray();
            var len = 0;
            foreach (var c in chars)
            {
                if (!ignoredChars.Contains(c))
                {
                    if (c == ' ' || c == '\t')
                        builder.Append('-');
                    else
                        builder.Append(c);

                    len++;

                    if (len >= 48)
                        break;
                }
            }
            builder.Append(".patch");

            var saveTo = Path.Combine(folder, builder.ToString());
            var log = CreateLog("Save Commit as Patch");
            var succ = await new Commands.FormatPatch(FullPath, commit.SHA, saveTo).Use(log).ExecAsync();
            log.Complete();
            return succ;
        }

        private void MarkRefreshDirty(RefreshDirtyFlags flags)
        {
            int oldValue;
            int newValue;
            do
            {
                oldValue = Interlocked.CompareExchange(ref _dirtyRefreshFlags, 0, 0);
                newValue = oldValue | (int)flags;
            } while (Interlocked.CompareExchange(ref _dirtyRefreshFlags, newValue, oldValue) != oldValue);
        }

        private void RefreshDirty()
        {
            if (!IsActive)
                return;

            if (Interlocked.CompareExchange(ref _isLoaded, 0, 0) == 0)
            {
                RefreshAll();
                return;
            }

            var dirty = (RefreshDirtyFlags)Interlocked.Exchange(ref _dirtyRefreshFlags, 0);
            if (dirty.HasFlag(RefreshDirtyFlags.Branches))
                RefreshBranches();
            if (dirty.HasFlag(RefreshDirtyFlags.Worktrees))
                RefreshWorktrees();
            if (dirty.HasFlag(RefreshDirtyFlags.Tags))
                RefreshTags();
            if (dirty.HasFlag(RefreshDirtyFlags.Commits))
                RefreshCommits();
            if (dirty.HasFlag(RefreshDirtyFlags.Submodules))
                RefreshSubmodules();
            if (dirty.HasFlag(RefreshDirtyFlags.Stashes))
                RefreshStashes();

            RefreshWorkingCopyChangesIfDirty();
        }

        private bool CanRefreshWorkingCopyChangesByDirty(out TimeSpan delay)
        {
            delay = TimeSpan.Zero;

            var lastTicks = Interlocked.Read(ref _lastWorkingCopyRefreshFinishedTicks);
            if (lastTicks <= 0)
                return true;

            var elapsed = DateTime.UtcNow - new DateTime(lastTicks, DateTimeKind.Utc);
            if (elapsed < TimeSpan.Zero || elapsed >= WORKING_COPY_DIRTY_REFRESH_INTERVAL)
                return true;

            delay = WORKING_COPY_DIRTY_REFRESH_INTERVAL - elapsed;
            return false;
        }

        private void ScheduleWorkingCopyDirtyRefresh(TimeSpan delay)
        {
            if (delay < TimeSpan.Zero)
                delay = TimeSpan.Zero;

            Interlocked.Increment(ref _workingCopyDirtyRefreshTimerVersion);

            lock (_workingCopyDirtyRefreshTimerLock)
            {
                if (_workingCopyDirtyRefreshTimer == null)
                    _workingCopyDirtyRefreshTimer = new Timer(OnWorkingCopyDirtyRefreshTimer, null, delay, Timeout.InfiniteTimeSpan);
                else
                    _workingCopyDirtyRefreshTimer.Change(delay, Timeout.InfiniteTimeSpan);
            }
        }

        private void UnscheduleWorkingCopyDirtyRefresh()
        {
            Interlocked.Increment(ref _workingCopyDirtyRefreshTimerVersion);

            lock (_workingCopyDirtyRefreshTimerLock)
                _workingCopyDirtyRefreshTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        private void DisposeWorkingCopyDirtyRefreshTimer()
        {
            lock (_workingCopyDirtyRefreshTimerLock)
            {
                _workingCopyDirtyRefreshTimer?.Dispose();
                _workingCopyDirtyRefreshTimer = null;
            }
        }

        private void OnWorkingCopyDirtyRefreshTimer(object sender)
        {
            var version = Interlocked.Read(ref _workingCopyDirtyRefreshTimerVersion);

            try
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (version != Interlocked.Read(ref _workingCopyDirtyRefreshTimerVersion))
                        return;

                    if (Interlocked.CompareExchange(ref _isClosed, 0, 0) != 0)
                        return;

                    if (IsActive && IsForeground)
                        RefreshWorkingCopyChangesIfDirty();
                });
            }
            catch
            {
                // Ignore dispatcher shutdown races.
            }
        }

        private void BeginRefreshTask()
        {
            var running = Interlocked.Increment(ref _runningRefreshTasks);
            StartOrUpdateRefreshBatchDiagnosticSpan();

            if (running == 1)
                UpdateIsRefreshing();
        }

        private void MarkNextRefreshBatchReason(string reason)
        {
            if (string.IsNullOrEmpty(reason))
                return;

            lock (_refreshBatchLock)
            {
                if (_refreshBatchSpan != null)
                    _refreshBatchSpan.Set("reason", reason);
                else
                    _nextRefreshBatchReason = reason;
            }
        }

        private void StartOrUpdateRefreshBatchDiagnosticSpan()
        {
            lock (_refreshBatchLock)
            {
                if (_refreshBatchSpan == null)
                {
                    var repoPath = SourceGit.Diagnostics.DiagnosticManager.GetRepositoryPath(FullPath);
                    var reason = string.IsNullOrEmpty(_nextRefreshBatchReason) ? "unspecified" : _nextRefreshBatchReason;
                    _nextRefreshBatchReason = string.Empty;
                    _refreshBatchTaskCount = 0;
                    _refreshBatchSpan = SourceGit.Diagnostics.DiagnosticManager.StartSpan(
                        "Repository.Refresh",
                        "refresh.batch",
                        SourceGit.Diagnostics.DiagnosticManager.CreateData(
                            ("repo", SourceGit.Diagnostics.DiagnosticManager.GetRepositoryId(repoPath)),
                            ("repoPath", repoPath),
                            ("reason", reason),
                            ("batch", ++_refreshBatchId)));
                }

                _refreshBatchTaskCount++;
            }
        }

        private void FinishRefreshBatchDiagnosticSpan()
        {
            SourceGit.Diagnostics.DiagnosticScope span = null;

            lock (_refreshBatchLock)
            {
                if (_refreshBatchSpan == null)
                    return;

                span = _refreshBatchSpan;
                _refreshBatchSpan = null;
                span.Set("taskCount", _refreshBatchTaskCount);
            }

            span.Dispose();
        }

        private SourceGit.Diagnostics.DiagnosticScope StartRefreshDiagnosticSpan(string name)
        {
            var repoPath = SourceGit.Diagnostics.DiagnosticManager.GetRepositoryPath(FullPath);
            return SourceGit.Diagnostics.DiagnosticManager.StartSpan(
                "Repository.Refresh",
                $"refresh.{name}",
                SourceGit.Diagnostics.DiagnosticManager.CreateData(
                    ("repo", SourceGit.Diagnostics.DiagnosticManager.GetRepositoryId(repoPath)),
                    ("repoPath", repoPath),
                    ("name", name)));
        }

        private SourceGit.Diagnostics.DiagnosticScope StartLifecycleDiagnosticSpan(string name)
        {
            var repoPath = SourceGit.Diagnostics.DiagnosticManager.GetRepositoryPath(FullPath);
            return SourceGit.Diagnostics.DiagnosticManager.StartSpan(
                "Repository.Lifecycle",
                $"repository.{name}",
                SourceGit.Diagnostics.DiagnosticManager.CreateData(
                    ("repo", SourceGit.Diagnostics.DiagnosticManager.GetRepositoryId(repoPath)),
                    ("repoPath", repoPath),
                    ("name", name)));
        }

        private void LogWorkingCopyRefreshEvent(string name, params (string Key, object Value)[] values)
        {
            if (!SourceGit.Diagnostics.DiagnosticManager.IsEnabled)
                return;

            var repoPath = SourceGit.Diagnostics.DiagnosticManager.GetRepositoryPath(FullPath);
            var data = new List<(string Key, object Value)>
            {
                ("repo", SourceGit.Diagnostics.DiagnosticManager.GetRepositoryId(repoPath)),
                ("repoPath", repoPath),
            };

            if (values != null)
                data.AddRange(values);

            SourceGit.Diagnostics.DiagnosticManager.Info(
                "Repository.Refresh",
                $"working_copy.{name}",
                string.Empty,
                SourceGit.Diagnostics.DiagnosticManager.CreateData(data.ToArray()));
        }

        private void EndRefreshTask()
        {
            if (Interlocked.Decrement(ref _runningRefreshTasks) <= 0)
            {
                Interlocked.Exchange(ref _runningRefreshTasks, 0);
                FinishRefreshBatchDiagnosticSpan();
                UpdateIsRefreshing();
            }
        }

        private void UpdateIsRefreshing()
        {
            if (Dispatcher.UIThread.CheckAccess())
                IsRefreshing = Interlocked.CompareExchange(ref _runningRefreshTasks, 0, 0) > 0;
            else
                Dispatcher.UIThread.Post(UpdateIsRefreshing);
        }

        private LauncherPage GetOwnerPage()
        {
            var launcher = App.GetLauncher();
            if (launcher == null)
                return null;

            foreach (var page in launcher.Pages)
            {
                if (page.Node.Id.Equals(FullPath))
                    return page;
            }

            return null;
        }

        private async Task<List<Models.Branch>> QueryBranchesWithCacheAsync(
            Task<Commands.QueryRefSnapshot.Snapshot> refSnapshotTask,
            CancellationToken token,
            SourceGit.Diagnostics.DiagnosticScope span)
        {
            var snapshot = await refSnapshotTask.ConfigureAwait(false);
            if (token.IsCancellationRequested)
                return [];

            if (!snapshot.IsSuccess)
            {
                span.Set("branchCacheFallback", true);
                return await new Commands.QueryBranches(FullPath).GetResultAsync().ConfigureAwait(false);
            }

            var rows = new List<Commands.QueryRefSnapshot.RefRow>();
            var rowByRef = new Dictionary<string, Commands.QueryRefSnapshot.RefRow>(StringComparer.Ordinal);
            foreach (var row in snapshot.Rows)
            {
                if (!row.IsBranch || row.IsRemoteHEAD)
                    continue;

                rows.Add(row);
                rowByRef[row.FullName] = row;
            }

            var keys = new Dictionary<string, string>(StringComparer.Ordinal);
            var branchesByRef = new Dictionary<string, Models.Branch>(StringComparer.Ordinal);
            var cacheableBranchRefs = new HashSet<string>(StringComparer.Ordinal);
            var missRefs = new List<string>();
            var hit = 0;
            var deleted = 0;

            lock (_refreshCacheLock)
            {
                foreach (var oldRef in _branchCache.Keys)
                {
                    if (!rowByRef.ContainsKey(oldRef))
                        deleted++;
                }

                foreach (var row in rows)
                {
                    var key = BuildBranchCacheKey(row);
                    keys[row.FullName] = key;

                    if (_branchCache.TryGetValue(row.FullName, out var cached) &&
                        cached.Key.Equals(key, StringComparison.Ordinal))
                    {
                        branchesByRef[row.FullName] = CloneBranch(cached.Branch);
                        cacheableBranchRefs.Add(row.FullName);
                        hit++;
                    }
                    else
                    {
                        missRefs.Add(row.FullName);
                    }
                }
            }

            if (missRefs.Count > 0)
            {
                var detailsResult = await new Commands.QueryBranches(FullPath)
                    .GetDetailsWithStatusByRefsAsync(missRefs)
                    .ConfigureAwait(false);
                var details = detailsResult.Branches;
                span.Set("branchCache.detailsSuccess", detailsResult.IsSuccess);

                foreach (var refName in missRefs)
                {
                    if (details.TryGetValue(refName, out var detail))
                    {
                        branchesByRef[refName] = detail;
                        cacheableBranchRefs.Add(refName);
                    }
                    else if (rowByRef.TryGetValue(refName, out var row))
                    {
                        branchesByRef[refName] = Commands.QueryBranches.CreateBranch(
                            row.FullName,
                            0,
                            row.ObjectName,
                            row.IsCurrent,
                            row.Upstream,
                            row.WorktreePath);
                    }
                }
            }

            var branches = new List<Models.Branch>(rows.Count + 1);
            var remotes = new Dictionary<string, Models.Branch>(StringComparer.Ordinal);
            var hasCurrent = false;
            foreach (var row in rows)
            {
                if (!branchesByRef.TryGetValue(row.FullName, out var branch) || branch == null)
                    continue;

                branches.Add(branch);
                if (branch.IsCurrent)
                    hasCurrent = true;
                if (!branch.IsLocal)
                    remotes[branch.FullName] = branch;
            }

            if (!hasCurrent)
            {
                var head = await ResolveHeadSHAAsync(snapshot, span).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(head))
                    branches.Insert(0, Commands.QueryBranches.CreateDetachedHeadBranch(head));
            }

            foreach (var branch in branches)
            {
                if (!branch.IsLocal || branch.IsDetachedHead || string.IsNullOrEmpty(branch.Upstream))
                    continue;

                if (remotes.TryGetValue(branch.Upstream, out var upstream))
                {
                    branch.IsUpstreamGone = false;
                    await new Commands.QueryTrackStatus(FullPath).GetResultAsync(branch, upstream).ConfigureAwait(false);
                }
                else
                {
                    branch.Ahead.Clear();
                    branch.Behind.Clear();
                    branch.IsUpstreamGone = true;
                }
            }

            if (!token.IsCancellationRequested)
            {
                var nextCache = new Dictionary<string, BranchCacheEntry>(StringComparer.Ordinal);
                foreach (var row in rows)
                {
                    if (cacheableBranchRefs.Contains(row.FullName) &&
                        branchesByRef.TryGetValue(row.FullName, out var branch) &&
                        branch != null)
                    {
                        nextCache[row.FullName] = new BranchCacheEntry()
                        {
                            Key = keys[row.FullName],
                            Branch = CloneBranch(branch),
                        };
                    }
                }

                lock (_refreshCacheLock)
                    _branchCache = nextCache;
            }

            span.Set("branchCache.hit", hit);
            span.Set("branchCache.miss", missRefs.Count);
            span.Set("branchCache.deleted", deleted);
            return branches;
        }

        private async Task<List<Models.Tag>> QueryTagsWithCacheAsync(
            Task<Commands.QueryRefSnapshot.Snapshot> refSnapshotTask,
            CancellationToken token,
            SourceGit.Diagnostics.DiagnosticScope span)
        {
            var snapshot = await refSnapshotTask.ConfigureAwait(false);
            if (token.IsCancellationRequested)
                return [];

            if (!snapshot.IsSuccess)
            {
                span.Set("tagCacheFallback", true);
                return await new Commands.QueryTags(FullPath).GetResultAsync().ConfigureAwait(false);
            }

            var rows = new List<Commands.QueryRefSnapshot.RefRow>();
            var rowByRef = new Dictionary<string, Commands.QueryRefSnapshot.RefRow>(StringComparer.Ordinal);
            foreach (var row in snapshot.Rows)
            {
                if (!row.IsTag)
                    continue;

                rows.Add(row);
                rowByRef[row.FullName] = row;
            }

            var keys = new Dictionary<string, string>(StringComparer.Ordinal);
            var tagsByRef = new Dictionary<string, Models.Tag>(StringComparer.Ordinal);
            var missRefs = new List<string>();
            var hit = 0;
            var deleted = 0;

            lock (_refreshCacheLock)
            {
                foreach (var oldRef in _tagCache.Keys)
                {
                    if (!rowByRef.ContainsKey(oldRef))
                        deleted++;
                }

                foreach (var row in rows)
                {
                    var key = BuildTagCacheKey(row);
                    keys[row.FullName] = key;

                    if (_tagCache.TryGetValue(row.FullName, out var cached) &&
                        cached.Key.Equals(key, StringComparison.Ordinal))
                    {
                        tagsByRef[row.FullName] = CloneTag(cached.Tag);
                        hit++;
                    }
                    else
                    {
                        missRefs.Add(row.FullName);
                    }
                }
            }

            if (missRefs.Count > 0)
            {
                var details = await new Commands.QueryTags(FullPath)
                    .GetDetailsByRefsAsync(missRefs)
                    .ConfigureAwait(false);

                foreach (var refName in missRefs)
                {
                    if (details.TryGetValue(refName, out var tag))
                        tagsByRef[refName] = tag;
                }
            }

            var tags = new List<Models.Tag>(rows.Count);
            foreach (var row in rows)
            {
                if (tagsByRef.TryGetValue(row.FullName, out var tag) && tag != null)
                    tags.Add(tag);
            }

            if (!token.IsCancellationRequested)
            {
                var nextCache = new Dictionary<string, TagCacheEntry>(StringComparer.Ordinal);
                foreach (var row in rows)
                {
                    if (tagsByRef.TryGetValue(row.FullName, out var tag) && tag != null)
                    {
                        nextCache[row.FullName] = new TagCacheEntry()
                        {
                            Key = keys[row.FullName],
                            Tag = CloneTag(tag),
                        };
                    }
                }

                lock (_refreshCacheLock)
                    _tagCache = nextCache;
            }

            span.Set("tagCache.hit", hit);
            span.Set("tagCache.miss", missRefs.Count);
            span.Set("tagCache.deleted", deleted);
            return tags;
        }

        private async Task<List<Models.Commit>> QueryCommitsWithCacheAsync(
            Task<Commands.QueryRefSnapshot.Snapshot> refSnapshotTask,
            string historyArgs,
            CancellationToken token,
            SourceGit.Diagnostics.DiagnosticScope span)
        {
            if (historyArgs.Contains("--reflog", StringComparison.Ordinal))
            {
                span.Set("historyCache.disabled", "reflog");
                return await new Commands.QueryCommits(FullPath, historyArgs).GetResultAsync().ConfigureAwait(false);
            }

            var snapshot = await refSnapshotTask.ConfigureAwait(false);
            if (token.IsCancellationRequested)
                return [];

            if (!snapshot.IsSuccess)
            {
                span.Set("historyCacheFallback", true);
                return await new Commands.QueryCommits(FullPath, historyArgs).GetResultAsync().ConfigureAwait(false);
            }

            var head = await ResolveHeadSHAAsync(snapshot, span).ConfigureAwait(false);
            if (string.IsNullOrEmpty(head))
            {
                span.Set("historyCacheFallback", true);
                span.Set("historyCacheFallbackReason", "head");
                return await new Commands.QueryCommits(FullPath, historyArgs).GetResultAsync().ConfigureAwait(false);
            }

            var cacheKey = $"{historyArgs}\0{head}\0{snapshot.RefsFingerprint}";
            lock (_refreshCacheLock)
            {
                if (_lastHistoryCacheKey.Equals(cacheKey, StringComparison.Ordinal) && _lastHistoryCommits != null)
                {
                    span.Set("historyCache.hit", true);
                    span.Set("historyCache.commitCount", _lastHistoryCommits.Count);
                    return CloneCommits(_lastHistoryCommits);
                }
            }

            span.Set("historyCache.hit", false);
            var result = await new Commands.QueryCommits(FullPath, historyArgs).GetResultWithStatusAsync().ConfigureAwait(false);
            span.Set("historyCache.querySuccess", result.IsSuccess);
            if (result.IsSuccess && !token.IsCancellationRequested)
            {
                lock (_refreshCacheLock)
                {
                    _lastHistoryCacheKey = cacheKey;
                    _lastHistoryCommits = CloneCommits(result.Commits);
                }
            }

            return result.Commits;
        }

        private static string BuildBranchCacheKey(Commands.QueryRefSnapshot.RefRow row)
        {
            return $"{row.FullName}\0{row.ObjectName}\0{(row.IsCurrent ? "*" : string.Empty)}\0{row.Upstream}\0{row.WorktreePath}";
        }

        private static string BuildTagCacheKey(Commands.QueryRefSnapshot.RefRow row)
        {
            return $"{row.FullName}\0{row.ObjectName}";
        }

        private static Models.Branch CloneBranch(Models.Branch source)
        {
            return new Models.Branch()
            {
                Name = source.Name,
                FullName = source.FullName,
                CommitterDate = source.CommitterDate,
                Head = source.Head,
                IsLocal = source.IsLocal,
                IsCurrent = source.IsCurrent,
                IsDetachedHead = source.IsDetachedHead,
                Upstream = source.Upstream,
                Ahead = new List<string>(source.Ahead),
                Behind = new List<string>(source.Behind),
                Remote = source.Remote,
                IsUpstreamGone = source.IsUpstreamGone,
                WorktreePath = source.WorktreePath,
            };
        }

        private static Models.Tag CloneTag(Models.Tag source)
        {
            return new Models.Tag()
            {
                Name = source.Name,
                IsAnnotated = source.IsAnnotated,
                SHA = source.SHA,
                Creator = source.Creator,
                CreatorDate = source.CreatorDate,
                Message = source.Message,
            };
        }

        private static List<Models.Commit> CloneCommits(List<Models.Commit> source)
        {
            var commits = new List<Models.Commit>(source.Count);
            foreach (var commit in source)
                commits.Add(CloneCommit(commit));

            return commits;
        }

        private static Models.Commit CloneCommit(Models.Commit source)
        {
            var commit = new Models.Commit()
            {
                SHA = source.SHA,
                Author = source.Author,
                AuthorTime = source.AuthorTime,
                Committer = source.Committer,
                CommitterTime = source.CommitterTime,
                Subject = source.Subject,
                Parents = new List<string>(source.Parents),
                Decorators = new List<Models.Decorator>(source.Decorators.Count),
                IsMerged = source.IsMerged,
            };

            foreach (var decorator in source.Decorators)
            {
                commit.Decorators.Add(new Models.Decorator()
                {
                    Type = decorator.Type,
                    Name = decorator.Name,
                });
            }

            return commit;
        }

        private async Task<string> ResolveHeadSHAAsync(
            Commands.QueryRefSnapshot.Snapshot snapshot,
            SourceGit.Diagnostics.DiagnosticScope span)
        {
            if (!string.IsNullOrEmpty(snapshot.HeadSHA))
            {
                span.Set("refSnapshot.headSource", "snapshot");
                return snapshot.HeadSHA;
            }

            if (TryReadHeadSHA(out var head))
            {
                span.Set("refSnapshot.headSource", "file");
                return head;
            }

            var gitHead = await new Commands.QueryRevisionByRefName(FullPath, "HEAD")
                .GetResultAsync()
                .ConfigureAwait(false);
            span.Set("refSnapshot.headSource", string.IsNullOrEmpty(gitHead) ? "none" : "git");
            return gitHead;
        }

        private bool TryReadHeadSHA(out string sha)
        {
            sha = string.Empty;

            try
            {
                var headFile = Path.Combine(GitDir, "HEAD");
                if (!File.Exists(headFile))
                    return false;

                var content = File.ReadAllText(headFile).Trim();
                if (IsObjectId(content))
                {
                    sha = content;
                    return true;
                }

                if (content.StartsWith("ref:", StringComparison.Ordinal))
                    return TryReadRefSHA(content.Substring(4).Trim(), out sha);
            }
            catch
            {
                // Fall back to git rev-parse.
            }

            return false;
        }

        private bool TryReadRefSHA(string refName, out string sha)
        {
            sha = string.Empty;
            var current = refName;
            for (var depth = 0; depth < 8; depth++)
            {
                if (TryReadLooseRefSHA(GitDir, current, out sha) ||
                    (!_gitCommonDir.Equals(GitDir, StringComparison.Ordinal) && TryReadLooseRefSHA(_gitCommonDir, current, out sha)) ||
                    TryReadPackedRefSHA(current, out sha))
                    return true;

                if (TryReadLooseSymbolicRef(GitDir, current, out var next) ||
                    (!_gitCommonDir.Equals(GitDir, StringComparison.Ordinal) && TryReadLooseSymbolicRef(_gitCommonDir, current, out next)))
                {
                    current = next;
                    continue;
                }

                return false;
            }

            return false;
        }

        private static bool TryReadLooseRefSHA(string dir, string refName, out string sha)
        {
            sha = string.Empty;
            try
            {
                var path = Path.Combine(dir, refName);
                if (!File.Exists(path))
                    return false;

                var content = File.ReadAllText(path).Trim();
                if (!IsObjectId(content))
                    return false;

                sha = content;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadLooseSymbolicRef(string dir, string refName, out string nextRef)
        {
            nextRef = string.Empty;
            try
            {
                var path = Path.Combine(dir, refName);
                if (!File.Exists(path))
                    return false;

                var content = File.ReadAllText(path).Trim();
                if (!content.StartsWith("ref:", StringComparison.Ordinal))
                    return false;

                nextRef = content.Substring(4).Trim();
                return !string.IsNullOrEmpty(nextRef);
            }
            catch
            {
                return false;
            }
        }

        private bool TryReadPackedRefSHA(string refName, out string sha)
        {
            sha = string.Empty;
            try
            {
                var packedRefs = Path.Combine(_gitCommonDir, "packed-refs");
                if (!File.Exists(packedRefs))
                    return false;

                foreach (var line in File.ReadLines(packedRefs))
                {
                    if (line.Length == 0 || line[0] == '#' || line[0] == '^')
                        continue;

                    var sep = line.IndexOf(' ');
                    if (sep <= 0 || sep + 1 >= line.Length)
                        continue;

                    if (!line.AsSpan(sep + 1).Equals(refName.AsSpan(), StringComparison.Ordinal))
                        continue;

                    var value = line.Substring(0, sep);
                    if (!IsObjectId(value))
                        return false;

                    sha = value;
                    return true;
                }
            }
            catch
            {
                // Fall back to git rev-parse.
            }

            return false;
        }

        private static bool IsObjectId(string value)
        {
            if (value.Length != 40 && value.Length != 64)
                return false;

            foreach (var c in value)
            {
                if (!((c >= '0' && c <= '9') ||
                    (c >= 'a' && c <= 'f') ||
                    (c >= 'A' && c <= 'F')))
                    return false;
            }

            return true;
        }

        private BranchTreeNode.Builder BuildBranchTree(List<Models.Branch> branches, List<Models.Remote> remotes, bool validateExpandedNodes = true)
        {
            var builder = new BranchTreeNode.Builder(_uiStates.LocalBranchSortMode, _uiStates.RemoteBranchSortMode);
            if (string.IsNullOrEmpty(_filter))
            {
                builder.SetExpandedNodes(_uiStates.ExpandedBranchNodesInSideBar);
                builder.Run(branches, remotes, false);

                if (validateExpandedNodes)
                {
                    foreach (var invalid in builder.InvalidExpandedNodes)
                        _uiStates.ExpandedBranchNodesInSideBar.Remove(invalid);
                }
            }
            else
            {
                var visibles = new List<Models.Branch>();
                foreach (var b in branches)
                {
                    if (b.FullName.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                        visibles.Add(b);
                }

                builder.Run(visibles, remotes, true);
            }

            var filterMap = _uiStates.GetHistoryFiltersMap();
            UpdateBranchTreeFilterMode(builder.Locals, filterMap);
            UpdateBranchTreeFilterMode(builder.Remotes, filterMap);
            return builder;
        }

        private object BuildVisibleTags()
        {
            switch (_uiStates.TagSortMode)
            {
                case Models.TagSortMode.CreatorDate:
                    _tags.Sort((l, r) => r.CreatorDate.CompareTo(l.CreatorDate));
                    break;
                default:
                    _tags.Sort((l, r) => Models.NumericSort.Compare(l.Name, r.Name));
                    break;
            }

            var visible = new List<Models.Tag>();
            if (string.IsNullOrEmpty(_filter))
            {
                visible.AddRange(_tags);
            }
            else
            {
                foreach (var t in _tags)
                {
                    if (t.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                        visible.Add(t);
                }
            }

            var filterMap = _uiStates.GetHistoryFiltersMap();
            UpdateTagFilterMode(filterMap);

            if (_uiStates.ShowTagsAsTree)
            {
                var tree = TagCollectionAsTree.Build(visible, _visibleTags as TagCollectionAsTree);
                foreach (var node in tree.Tree)
                    node.UpdateFilterMode(filterMap);
                return tree;
            }
            else
            {
                var list = new TagCollectionAsList(visible);
                foreach (var item in list.TagItems)
                    item.FilterMode = filterMap.GetValueOrDefault(item.Tag.Name, Models.FilterMode.None);
                return list;
            }
        }

        private object BuildVisibleSubmodules()
        {
            var visible = new List<Models.Submodule>();
            var filter = Models.FileSearch.Parse(_filter);
            if (filter.IsEmpty)
            {
                visible.AddRange(_submodules);
            }
            else
            {
                foreach (var s in _submodules)
                {
                    if (Models.FileSearch.Matches(s.Path, filter))
                        visible.Add(s);
                }
            }

            if (_uiStates.ShowSubmodulesAsTree)
                return SubmoduleCollectionAsTree.Build(visible, _visibleSubmodules as SubmoduleCollectionAsTree);
            else
                return new SubmoduleCollectionAsList() { Submodules = visible };
        }

        private void RefreshHistoryFilters(bool refresh)
        {
            HistoryFilterMode = _uiStates.GetHistoryFilterMode();
            if (!refresh)
                return;

            var map = _uiStates.GetHistoryFiltersMap();
            UpdateBranchTreeFilterMode(LocalBranchTrees, map);
            UpdateBranchTreeFilterMode(RemoteBranchTrees, map);
            UpdateTagFilterMode(map);
            RefreshCommits();
        }

        private void UpdateBranchTreeFilterMode(List<BranchTreeNode> nodes, Dictionary<string, Models.FilterMode> map)
        {
            foreach (var node in nodes)
            {
                node.FilterMode = map.GetValueOrDefault(node.Path, Models.FilterMode.None);

                if (!node.IsBranch)
                    UpdateBranchTreeFilterMode(node.Children, map);
            }
        }

        private void UpdateTagFilterMode(Dictionary<string, Models.FilterMode> map)
        {
            if (VisibleTags is TagCollectionAsTree tree)
            {
                foreach (var node in tree.Tree)
                    node.UpdateFilterMode(map);
            }
            else if (VisibleTags is TagCollectionAsList list)
            {
                foreach (var item in list.TagItems)
                    item.FilterMode = map.GetValueOrDefault(item.Tag.Name, Models.FilterMode.None);
            }
        }

        private void ResetBranchTreeFilterMode(List<BranchTreeNode> nodes)
        {
            foreach (var node in nodes)
            {
                node.FilterMode = Models.FilterMode.None;
                if (!node.IsBranch)
                    ResetBranchTreeFilterMode(node.Children);
            }
        }

        private void ResetTagFilterMode()
        {
            if (VisibleTags is TagCollectionAsTree tree)
            {
                var filters = new Dictionary<string, Models.FilterMode>();
                foreach (var node in tree.Tree)
                    node.UpdateFilterMode(filters);
            }
            else if (VisibleTags is TagCollectionAsList list)
            {
                foreach (var item in list.TagItems)
                    item.FilterMode = Models.FilterMode.None;
            }
        }

        private BranchTreeNode FindBranchNode(List<BranchTreeNode> nodes, string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;

            foreach (var node in nodes)
            {
                if (node.Path.Equals(path, StringComparison.Ordinal))
                    return node;

                if (path.StartsWith(node.Path, StringComparison.Ordinal))
                {
                    var founded = FindBranchNode(node.Children, path);
                    if (founded != null)
                        return founded;
                }
            }

            return null;
        }

        private void AutoFetchByTimer(object sender)
        {
            try
            {
                Dispatcher.UIThread.Invoke(AutoFetchOnUIThread);
            }
            catch
            {
                // Ignore exception.
            }

            QueueMultiPackIndexWriteCheck();
        }

        private static TimeSpan GetInitialAutoFetchDelay(int autoFetchIndex, int autoFetchCount)
        {
            if (autoFetchCount <= 1)
                return TimeSpan.Zero;

            var interval = GetAutoFetchInterval();
            var index = Math.Clamp(autoFetchIndex, 0, autoFetchCount - 1);
            return TimeSpan.FromTicks(interval.Ticks * index / autoFetchCount);
        }

        private static TimeSpan GetAutoFetchInterval()
        {
            return TimeSpan.FromMinutes(Math.Max(1, Preferences.Instance.AutoFetchInterval));
        }

        private void ScheduleNextAutoFetchFromNow()
        {
            _nextAutoFetchTime = DateTime.Now.Add(GetAutoFetchInterval());
        }

        private void AdvanceAutoFetchSchedule(DateTime now)
        {
            var interval = GetAutoFetchInterval();
            if (_nextAutoFetchTime == DateTime.MinValue)
            {
                _nextAutoFetchTime = now.Add(interval);
                return;
            }

            do
            {
                _nextAutoFetchTime = _nextAutoFetchTime.Add(interval);
            } while (_nextAutoFetchTime <= now);
        }

        private bool IsAutoFetchTurn(DateTime now)
        {
            var launcher = App.GetLauncher();
            if (launcher == null)
                return true;

            var selfIndex = -1;
            var pages = launcher.Pages;
            for (var i = 0; i < pages.Count; i++)
            {
                if (ReferenceEquals(pages[i].Data, this))
                {
                    selfIndex = i;
                    break;
                }
            }

            if (selfIndex < 0)
                return true;

            for (var i = 0; i < pages.Count; i++)
            {
                if (i == selfIndex || pages[i].Data is not Repository repo)
                    continue;

                if (repo._nextAutoFetchTime > now)
                    continue;

                var diff = repo._nextAutoFetchTime.CompareTo(_nextAutoFetchTime);
                if (diff < 0 || (diff == 0 && i < selfIndex))
                    return false;
            }

            return true;
        }

        private async Task AutoFetchOnUIThread()
        {
            if (IsFetching || Interlocked.CompareExchange(ref _isClosed, 0, 0) != 0)
                return;

            CommandLog log = null;
            var acquired = false;
            var beganFetch = false;

            try
            {
                var now = DateTime.Now;
                if (_nextAutoFetchTime > now)
                    return;

                if (!IsAutoFetchTurn(now))
                    return;

                await _autoFetchLock.WaitAsync();
                acquired = true;

                now = DateTime.Now;
                if (_nextAutoFetchTime > now)
                    return;

                if (!IsAutoFetchTurn(now))
                    return;

                if (!Preferences.Instance.EnableAutoFetch || !CanCreatePopup())
                {
                    AdvanceAutoFetchSchedule(now);
                    return;
                }

                var lockFile = Path.Combine(GitDir, "index.lock");
                if (File.Exists(lockFile) || Interlocked.CompareExchange(ref _isClosed, 0, 0) != 0)
                {
                    AdvanceAutoFetchSchedule(now);
                    return;
                }

                var remotes = _remotes;
                if (remotes.Count == 0)
                {
                    remotes = await new Commands.QueryRemotes(FullPath).GetResultAsync();
                    if (Interlocked.CompareExchange(ref _isClosed, 0, 0) != 0)
                        return;

                    Remotes = remotes;

                    if (_workingCopy != null)
                        _workingCopy.HasRemotes = remotes.Count > 0;
                }

                var fetchRemotes = new List<string>();
                foreach (var r in remotes)
                {
                    if (!r.DisableAutoFetch)
                        fetchRemotes.Add(r.Name);
                }

                if (fetchRemotes.Count == 0)
                {
                    AdvanceAutoFetchSchedule(DateTime.Now);
                    return;
                }

                if (!BeginFetch())
                {
                    ScheduleNextAutoFetchFromNow();
                    return;
                }

                beganFetch = true;
                IsAutoFetching = true;
                log = CreateLog("Auto-Fetch");

                foreach (var remote in fetchRemotes)
                    await new Commands.Fetch(FullPath, remote).Use(log).RunAsync();

                AdvanceAutoFetchSchedule(DateTime.Now);
            }
            catch
            {
                AdvanceAutoFetchSchedule(DateTime.Now);
                // Ignore all exceptions.
            }
            finally
            {
                log?.Complete();
                IsAutoFetching = false;

                if (beganFetch)
                    EndFetch();

                if (acquired)
                    _autoFetchLock.Release();
            }
        }

        private bool IsMainWorktree()
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return GitDir.Equals(_gitCommonDir, comparison);
        }

        private void ScheduleNextMultiPackIndexWriteCheck(DateTime now)
        {
            if (!IsMainWorktree())
            {
                _nextMultiPackIndexWriteCheckTime = DateTime.MaxValue;
                return;
            }

            var lastWriteTime = Settings.LastMultiPackIndexWriteTime;
            if (lastWriteTime < DateTime.UnixEpoch || lastWriteTime > now)
            {
                _nextMultiPackIndexWriteCheckTime = now;
                return;
            }

            var next = lastWriteTime.Add(MULTI_PACK_INDEX_WRITE_INTERVAL);
            _nextMultiPackIndexWriteCheckTime = next <= now ? now : next;
        }

        private void QueueMultiPackIndexWriteCheck()
        {
            if (!IsMainWorktree() || DateTime.Now < _nextMultiPackIndexWriteCheckTime)
                return;

            try
            {
                Dispatcher.UIThread.Post(() => _ = TryRunMultiPackIndexWriteAsync());
            }
            catch
            {
                // Ignore dispatcher shutdown races.
            }
        }

        private async Task TryRunMultiPackIndexWriteAsync()
        {
            var now = DateTime.Now;
            if (!IsMainWorktree() ||
                now < _nextMultiPackIndexWriteCheckTime ||
                Interlocked.CompareExchange(ref _isClosed, 0, 0) != 0)
                return;

            if (Interlocked.Exchange(ref _isWritingMultiPackIndex, 1) != 0)
                return;

            if (!_runningMultiPackIndexWrites.TryAdd(_gitCommonDir, 0))
            {
                Interlocked.Exchange(ref _isWritingMultiPackIndex, 0);
                return;
            }

            CommandLog log = null;
            try
            {
                log = CreateLog("Maintenance (multi-pack-index)");
                var succ = await new Commands.MultiPackIndexWrite(FullPath)
                    .Use(log)
                    .ExecAsync();

                if (succ)
                {
                    Settings.LastMultiPackIndexWriteTime = DateTime.Now;
                    Settings.Save();
                    ScheduleNextMultiPackIndexWriteCheck(Settings.LastMultiPackIndexWriteTime);
                }
                else
                {
                    _nextMultiPackIndexWriteCheckTime = DateTime.Now.Add(MULTI_PACK_INDEX_WRITE_FAILURE_RETRY_INTERVAL);
                }
            }
            catch
            {
                _nextMultiPackIndexWriteCheckTime = DateTime.Now.Add(MULTI_PACK_INDEX_WRITE_FAILURE_RETRY_INTERVAL);
            }
            finally
            {
                log?.Complete();
                _runningMultiPackIndexWrites.TryRemove(_gitCommonDir, out _);
                Interlocked.Exchange(ref _isWritingMultiPackIndex, 0);
            }
        }

        private readonly string _gitCommonDir = null;
        private Models.RepositorySettings _settings = null;
        private Models.RepositoryUIStates _uiStates = null;
        private Models.FilterMode _historyFilterMode = Models.FilterMode.None;
        private bool _hasAllowedSignersFile = false;
        private ulong _queryLocalChangesTimes = 0;

        private Models.Watcher _watcher = null;
        private Histories _histories = null;
        private WorkingCopy _workingCopy = null;
        private StashesPage _stashesPage = null;
        private int _selectedViewIndex = 0;
        private int _isClosed = 1;

        private int _localBranchesCount = 0;
        private int _localChangesCount = 0;
        private int _stashesCount = 0;

        private bool _isSearchingCommits = false;
        private SearchCommitContext _searchCommitContext = null;

        private string _filter = string.Empty;
        private List<Models.Remote> _remotes = [];
        private List<Models.Branch> _branches = [];
        private Models.Branch _currentBranch = null;
        private List<BranchTreeNode> _localBranchTrees = [];
        private List<BranchTreeNode> _remoteBranchTrees = [];
        private List<Worktree> _worktrees = [];
        private List<Models.Tag> _tags = [];
        private object _visibleTags = null;
        private List<Models.Submodule> _submodules = [];
        private object _visibleSubmodules = null;
        private string _navigateToCommitDelayed = string.Empty;

        private bool _isAutoFetching = false;
        private int _runningRemoteSyncs = 0;
        private int _isFetching = 0;
        private readonly object _fetchStateLock = new();
        private TaskCompletionSource<bool> _fetchCompletion = null;
        private int _isActive = 0;
        private int _isLoaded = 0;
        private int _dirtyRefreshFlags = 0;
        private bool _isRefreshing = false;
        private int _runningRefreshTasks = 0;
        private int _isRefreshingWorkingCopy = 0;
        private int _isWorkingCopyDirty = 0;
        private int _forceNextWorkingCopyDirtyRefresh = 0;
        private long _lastWorkingCopyRefreshFinishedTicks = 0;
        private readonly object _refreshBatchLock = new();
        private SourceGit.Diagnostics.DiagnosticScope _refreshBatchSpan = null;
        private string _nextRefreshBatchReason = string.Empty;
        private int _refreshBatchTaskCount = 0;
        private int _refreshBatchId = 0;
        private readonly object _refreshCacheLock = new();
        private Dictionary<string, BranchCacheEntry> _branchCache = new(StringComparer.Ordinal);
        private Dictionary<string, TagCacheEntry> _tagCache = new(StringComparer.Ordinal);
        private string _lastHistoryCacheKey = string.Empty;
        private List<Models.Commit> _lastHistoryCommits = null;
        private Timer _autoFetchTimer = null;
        private Timer _workingCopyDirtyRefreshTimer = null;
        private long _workingCopyDirtyRefreshTimerVersion = 0;
        private readonly object _workingCopyDirtyRefreshTimerLock = new();
        private DateTime _nextAutoFetchTime = DateTime.MinValue;
        private DateTime _nextMultiPackIndexWriteCheckTime = DateTime.MaxValue;
        private int _isWritingMultiPackIndex = 0;
        private static readonly SemaphoreSlim _autoFetchLock = new SemaphoreSlim(1, 1);
        private static readonly TimeSpan WORKING_COPY_DIRTY_REFRESH_INTERVAL = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan MULTI_PACK_INDEX_WRITE_INTERVAL = TimeSpan.FromHours(24);
        private static readonly TimeSpan MULTI_PACK_INDEX_WRITE_FAILURE_RETRY_INTERVAL = TimeSpan.FromHours(1);
        private static readonly ConcurrentDictionary<string, byte> _runningMultiPackIndexWrites = new(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        private Models.BisectState _bisectState = Models.BisectState.None;
        private bool _isBisectCommandRunning = false;

        private CancellationTokenSource _cancellationRefreshBranches = null;
        private CancellationTokenSource _cancellationRefreshTags = null;
        private CancellationTokenSource _cancellationRefreshWorkingCopyChanges = null;
        private CancellationTokenSource _cancellationRefreshCommits = null;
        private CancellationTokenSource _cancellationRefreshStashes = null;

        private sealed class BranchCacheEntry
        {
            public string Key { get; set; } = string.Empty;
            public Models.Branch Branch { get; set; } = null;
        }

        private sealed class TagCacheEntry
        {
            public string Key { get; set; } = string.Empty;
            public Models.Tag Tag { get; set; } = null;
        }
    }
}
