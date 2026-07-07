using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class WorkingCopy : ObservableObject
    {
        private class ExternalMergeResult
        {
            public bool Success { get; init; } = false;
            public List<(Models.Change Change, Process Process, Commands.MergeTool.MergeFiles Files)> Processes { get; init; } = [];
        }

        public Repository Repository
        {
            get => _repo;
        }

        public bool IncludeUntracked
        {
            get => _repo.IncludeUntracked;
            set
            {
                if (_repo.IncludeUntracked != value)
                {
                    _repo.IncludeUntracked = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool HasRemotes
        {
            get => _hasRemotes;
            set => SetProperty(ref _hasRemotes, value);
        }

        public bool HasUnsolvedConflicts
        {
            get => _hasUnsolvedConflicts;
            set => SetProperty(ref _hasUnsolvedConflicts, value);
        }

        public bool CanSwitchBranchDirectly
        {
            get;
            set;
        } = true;

        public InProgressContext InProgressContext
        {
            get => _inProgressContext;
            private set => SetProperty(ref _inProgressContext, value);
        }

        public MergeConflictFileHistoryCache MergeConflictHistories
        {
            get;
        } = new();

        public bool IsStaging
        {
            get => _isStaging;
            private set => SetProperty(ref _isStaging, value);
        }

        public bool IsUnstaging
        {
            get => _isUnstaging;
            private set => SetProperty(ref _isUnstaging, value);
        }

        public bool IsCommitting
        {
            get => _isCommitting;
            private set => SetProperty(ref _isCommitting, value);
        }

        public bool EnableSignOff
        {
            get => _repo.UIStates.EnableSignOffForCommit;
            set => _repo.UIStates.EnableSignOffForCommit = value;
        }

        public bool NoVerifyOnCommit
        {
            get => _repo.UIStates.NoVerifyOnCommit;
            set => _repo.UIStates.NoVerifyOnCommit = value;
        }

        public bool UseAmend
        {
            get => _useAmend;
            set
            {
                if (SetProperty(ref _useAmend, value))
                {
                    if (value)
                    {
                        var currentBranch = _repo.CurrentBranch;
                        if (currentBranch == null)
                        {
                            _repo.SendNotification("No commits to amend!!!", true);
                            _useAmend = false;
                            OnPropertyChanged();
                            return;
                        }

                        CommitMessage = new Commands.QueryCommitFullMessage(_repo.FullPath, currentBranch.Head).GetResult();
                    }
                    else
                    {
                        CommitMessage = string.Empty;
                        ResetAuthor = false;
                    }

                    Staged = GetStagedChanges(_cached);
                    VisibleStaged = GetVisibleChanges(_staged);
                    SelectedStaged = [];
                }
            }
        }

        public bool ResetAuthor
        {
            get => _resetAuthor;
            set => SetProperty(ref _resetAuthor, value);
        }

        public string Filter
        {
            get => _filter;
            set
            {
                if (SetProperty(ref _filter, value))
                {
                    if (_isLoadingData)
                        return;

                    VisibleUnstaged = GetVisibleChanges(_unstaged);
                    VisibleStaged = GetVisibleChanges(_staged);
                    SelectedUnstaged = [];
                }
            }
        }

        public List<Models.Change> Unstaged
        {
            get => _unstaged;
            private set => SetProperty(ref _unstaged, value);
        }

        public List<Models.Change> VisibleUnstaged
        {
            get => _visibleUnstaged;
            private set => SetProperty(ref _visibleUnstaged, value);
        }

        public List<Models.Change> Staged
        {
            get => _staged;
            private set => SetProperty(ref _staged, value);
        }

        public List<Models.Change> VisibleStaged
        {
            get => _visibleStaged;
            private set => SetProperty(ref _visibleStaged, value);
        }

        public List<Models.Change> SelectedUnstaged
        {
            get => _selectedUnstaged;
            set
            {
                if (SetProperty(ref _selectedUnstaged, value))
                {
                    if (value == null || value.Count == 0)
                    {
                        if (_selectedStaged == null || _selectedStaged.Count == 0)
                            SetDetail(null, true);
                    }
                    else
                    {
                        if (_selectedStaged is { Count: > 0 })
                            SelectedStaged = [];

                        if (value.Count == 1)
                            SetDetail(value[0], true);
                        else
                            SetDetail(null, true);
                    }
                }
            }
        }

        public List<Models.Change> SelectedStaged
        {
            get => _selectedStaged;
            set
            {
                if (SetProperty(ref _selectedStaged, value))
                {
                    if (value == null || value.Count == 0)
                    {
                        if (_selectedUnstaged == null || _selectedUnstaged.Count == 0)
                            SetDetail(null, false);
                    }
                    else
                    {
                        if (_selectedUnstaged is { Count: > 0 })
                            SelectedUnstaged = [];

                        if (value.Count == 1)
                            SetDetail(value[0], false);
                        else
                            SetDetail(null, false);
                    }
                }
            }
        }

        public DiffContentHost DetailContext { get; } = new();

        public string CommitMessage
        {
            get => _commitMessage;
            set => SetProperty(ref _commitMessage, value);
        }

        public WorkingCopy(Repository repo)
        {
            _repo = repo;
        }

        public void Close()
        {
            DetailContext.Clear();
            MergeConflictHistories.Reset();
        }

        public bool SaveInProgressCommitMessage()
        {
            if (_inProgressContext == null)
                return true;

            var file = _inProgressContext.GetContinueMessageFile(_repo);
            if (string.IsNullOrEmpty(file))
            {
                if (string.IsNullOrEmpty(_commitMessage))
                    return true;

                _repo.SendNotification("Failed to save commit message: message file was not found", true);
                return false;
            }

            try
            {
                File.WriteAllText(file, Models.CommitMessageFormatter.NormalizeLineEndingsForGit(_commitMessage ?? string.Empty));
                return true;
            }
            catch (Exception e)
            {
                _repo.SendNotification($"Failed to save commit message: {e.Message}", true);
                return false;
            }
        }

        public void SetData(List<Models.Change> changes)
        {
            if (!IsChanged(_cached, changes))
            {
                HasUnsolvedConflicts = _cached.Find(x => x.IsConflicted) != null;
                UpdateInProgressState();
                EnsureMergeConflictHistoryCache();
                UpdateDetail();
                return;
            }

            var lastSelectedUnstaged = new HashSet<string>();
            var lastSelectedUnstagedPath = string.Empty;
            if (_selectedUnstaged is { Count: > 0 })
            {
                lastSelectedUnstagedPath = _selectedUnstaged[0].Path;
                foreach (var c in _selectedUnstaged)
                    lastSelectedUnstaged.Add(c.Path);
            }

            var unstaged = new List<Models.Change>();
            var visibleUnstaged = new List<Models.Change>();
            var selectedUnstaged = new List<Models.Change>();
            var filter = Models.FileSearch.Parse(_filter);
            var noFilter = filter.IsEmpty;
            var hasConflict = false;
            var canSwitchDirectly = true;
            foreach (var c in changes)
            {
                if (c.WorkTree != Models.ChangeState.None)
                {
                    unstaged.Add(c);
                    hasConflict |= c.IsConflicted;

                    if (noFilter || Models.FileSearch.Matches(c.Path, filter))
                    {
                        visibleUnstaged.Add(c);
                        if (lastSelectedUnstaged.Contains(c.Path))
                            selectedUnstaged.Add(c);
                    }
                }

                if (!canSwitchDirectly)
                    continue;

                if (c.WorkTree == Models.ChangeState.Untracked || c.Index == Models.ChangeState.Added)
                    continue;

                canSwitchDirectly = false;
            }

            var staged = GetStagedChanges(changes);
            var visibleStaged = GetVisibleChanges(staged);
            var selectedStaged = new List<Models.Change>();
            var lastSelectedStagedPath = string.Empty;
            if (_selectedStaged is { Count: > 0 })
            {
                lastSelectedStagedPath = _selectedStaged[0].Path;
                var set = new HashSet<string>();
                foreach (var c in _selectedStaged)
                    set.Add(c.Path);

                foreach (var c in visibleStaged)
                {
                    if (set.Contains(c.Path))
                        selectedStaged.Add(c);
                }
            }

            if (selectedUnstaged.Count == 0 && !string.IsNullOrEmpty(lastSelectedUnstagedPath))
            {
                var next = FindNextVisibleChange(visibleUnstaged, lastSelectedUnstagedPath);
                if (next != null)
                    selectedUnstaged.Add(next);
            }

            if (selectedStaged.Count == 0 && !string.IsNullOrEmpty(lastSelectedStagedPath))
            {
                var next = FindNextVisibleChange(visibleStaged, lastSelectedStagedPath);
                if (next != null)
                    selectedStaged.Add(next);
            }

            if (selectedUnstaged.Count == 0 && selectedStaged.Count == 0 && hasConflict)
            {
                var firstConflict = visibleUnstaged.Find(x => x.IsConflicted);
                if (firstConflict != null)
                    selectedUnstaged.Add(firstConflict);
            }

            _isLoadingData = true;
            _cached = changes;
            HasUnsolvedConflicts = hasConflict;
            CanSwitchBranchDirectly = canSwitchDirectly;
            VisibleUnstaged = visibleUnstaged;
            VisibleStaged = visibleStaged;
            Unstaged = unstaged;
            Staged = staged;
            SelectedUnstaged = selectedUnstaged;
            SelectedStaged = selectedStaged;
            _isLoadingData = false;

            UpdateInProgressState();
            EnsureMergeConflictHistoryCache();
            UpdateDetail();
        }

        private static Models.Change FindNextVisibleChange(List<Models.Change> visibleChanges, string path)
        {
            if (visibleChanges.Count == 0)
                return null;

            foreach (var change in visibleChanges)
            {
                if (Models.NumericSort.Compare(change.Path, path) > 0)
                    return change;
            }

            return visibleChanges[^1];
        }

        public async Task StageChangesAsync(List<Models.Change> changes, Models.Change next)
        {
            var canStaged = await GetCanStageChangesAsync(changes);
            var count = canStaged.Count;
            if (count == 0)
                return;

            IsStaging = true;
            _selectedUnstaged = next != null ? [next] : [];

            using var lockWatcher = _repo.LockWatcher();

            var log = _repo.CreateLog("Stage");
            var pathSpecFile = Path.GetTempFileName();
            await using (var writer = new StreamWriter(pathSpecFile))
            {
                foreach (var c in canStaged)
                    await writer.WriteLineAsync(c.Path);
            }

            await new Commands.Add(_repo.FullPath, pathSpecFile).Use(log).ExecAsync();
            File.Delete(pathSpecFile);
            log.Complete();

            _repo.MarkWorkingCopyDirtyManually();
            IsStaging = false;
        }

        public async Task UnstageChangesAsync(List<Models.Change> changes, Models.Change next)
        {
            var count = changes.Count;
            if (count == 0)
                return;

            IsUnstaging = true;
            _selectedStaged = next != null ? [next] : [];

            using var lockWatcher = _repo.LockWatcher();

            var log = _repo.CreateLog("Unstage");
            if (_useAmend)
            {
                log.AppendLine("$ git update-index --index-info ");
                await new Commands.UpdateIndexInfo(_repo.FullPath, changes).ExecAsync();
            }
            else
            {
                var pathSpecFile = Path.GetTempFileName();
                await using (var writer = new StreamWriter(pathSpecFile))
                {
                    foreach (var c in changes)
                    {
                        await writer.WriteLineAsync(c.Path);
                        if (c.Index == Models.ChangeState.Renamed)
                            await writer.WriteLineAsync(c.OriginalPath);
                    }
                }

                await new Commands.Reset(_repo.FullPath, pathSpecFile).Use(log).ExecAsync();
                File.Delete(pathSpecFile);
            }
            log.Complete();

            _repo.MarkWorkingCopyDirtyManually();
            IsUnstaging = false;
        }

        public async Task SaveChangesToPatchAsync(List<Models.Change> changes, bool isUnstaged, string saveTo)
        {
            var succ = await Commands.SaveChangesAsPatch.ProcessLocalChangesAsync(_repo.FullPath, changes, isUnstaged, saveTo);
            if (succ)
                _repo.SendNotification(App.Text("SaveAsPatchSuccess"));
        }

        public void Discard(List<Models.Change> changes)
        {
            if (_repo.CanCreatePopup())
                _repo.ShowPopup(new Discard(_repo, changes));
        }

        public void ClearFilter()
        {
            Filter = string.Empty;
        }

        public async Task UseTheirsAsync(List<Models.Change> changes)
        {
            var traceId = CreateResolveTraceId();
            TraceResolveAction(traceId, "use_theirs", "start", changes);
            using (_repo.LockWatcher())
            {
                var files = new List<string>();
                var needStage = new List<string>();
                var log = _repo.CreateLog("Use Theirs");

                foreach (var change in changes)
                {
                    if (!change.IsConflicted)
                        continue;

                    if (change.ConflictReason is Models.ConflictReason.BothDeleted or Models.ConflictReason.DeletedByThem or Models.ConflictReason.AddedByUs)
                    {
                        var fullpath = Path.Combine(_repo.FullPath, change.Path);
                        if (File.Exists(fullpath))
                            File.Delete(fullpath);

                        needStage.Add(change.Path);
                    }
                    else
                    {
                        files.Add(change.Path);
                    }
                }
                TraceResolveAction(traceId, "use_theirs", "paths", changes, files.Count, needStage.Count);

                if (files.Count > 0)
                {
                    var succ = await new Commands.CheckoutIndex(_repo.FullPath).Use(log).CheckoutStageAsync(3, files);
                    TraceResolveAction(traceId, "use_theirs", "checkout_index", changes, files.Count, needStage.Count, succ);
                    if (succ)
                        needStage.AddRange(files);
                }

                if (needStage.Count > 0)
                {
                    var pathSpecFile = Path.GetTempFileName();
                    await File.WriteAllLinesAsync(pathSpecFile, needStage);
                    var succ = await new Commands.Add(_repo.FullPath, pathSpecFile).Use(log).ExecAsync();
                    File.Delete(pathSpecFile);
                    TraceResolveAction(traceId, "use_theirs", "add", changes, files.Count, needStage.Count, succ);
                }

                log.Complete();
            }

            TraceResolveAction(traceId, "use_theirs", "manual_refresh", changes);
            _repo.MarkWorkingCopyDirtyManually();
        }

        public async Task UseMineAsync(List<Models.Change> changes)
        {
            var traceId = CreateResolveTraceId();
            TraceResolveAction(traceId, "use_mine", "start", changes);
            using (_repo.LockWatcher())
            {
                var files = new List<string>();
                var needStage = new List<string>();
                var log = _repo.CreateLog("Use Mine");

                foreach (var change in changes)
                {
                    if (!change.IsConflicted)
                        continue;

                    if (change.ConflictReason is Models.ConflictReason.BothDeleted or Models.ConflictReason.DeletedByUs or Models.ConflictReason.AddedByThem)
                    {
                        var fullpath = Path.Combine(_repo.FullPath, change.Path);
                        if (File.Exists(fullpath))
                            File.Delete(fullpath);

                        needStage.Add(change.Path);
                    }
                    else
                    {
                        files.Add(change.Path);
                    }
                }
                TraceResolveAction(traceId, "use_mine", "paths", changes, files.Count, needStage.Count);

                if (files.Count > 0)
                {
                    var succ = await new Commands.CheckoutIndex(_repo.FullPath).Use(log).CheckoutStageAsync(2, files);
                    TraceResolveAction(traceId, "use_mine", "checkout_index", changes, files.Count, needStage.Count, succ);
                    if (succ)
                        needStage.AddRange(files);
                }

                if (needStage.Count > 0)
                {
                    var pathSpecFile = Path.GetTempFileName();
                    await File.WriteAllLinesAsync(pathSpecFile, needStage);
                    var succ = await new Commands.Add(_repo.FullPath, pathSpecFile).Use(log).ExecAsync();
                    File.Delete(pathSpecFile);
                    TraceResolveAction(traceId, "use_mine", "add", changes, files.Count, needStage.Count, succ);
                }

                log.Complete();
            }

            TraceResolveAction(traceId, "use_mine", "manual_refresh", changes);
            _repo.MarkWorkingCopyDirtyManually();
        }

        public async Task ResetToConflictStateAsync(List<Models.Change> changes)
        {
            var files = new List<string>();
            var added = new HashSet<string>(StringComparer.Ordinal);
            foreach (var change in changes)
            {
                if (change.CanResetToConflictState && added.Add(change.Path))
                    files.Add(change.Path);
            }

            if (files.Count == 0)
                return;

            var succ = false;
            using (_repo.LockWatcher())
            {
                var log = _repo.CreateLog("Reset To Conflict State");
                succ = await new Commands.Checkout(_repo.FullPath).Use(log).ResetFilesToConflictStateAsync(files);
                log.Complete();
            }

            if (succ)
                _repo.MarkWorkingCopyDirtyManually();
        }

        private string CreateResolveTraceId()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        private void TraceResolveAction(
            string traceId,
            string action,
            string phase,
            List<Models.Change> changes,
            int filesCount = -1,
            int needStageCount = -1,
            bool? success = null)
        {
            if (!SourceGit.Diagnostics.DiagnosticManager.IsEnabled)
                return;

            var selectedCount = changes?.Count ?? 0;
            var conflictedCount = 0;
            var firstPath = string.Empty;
            if (changes != null)
            {
                foreach (var change in changes)
                {
                    if (string.IsNullOrEmpty(firstPath))
                        firstPath = change.Path;

                    if (change.IsConflicted)
                        conflictedCount++;
                }
            }

            var repoPath = SourceGit.Diagnostics.DiagnosticManager.GetRepositoryPath(_repo.FullPath);
            SourceGit.Diagnostics.DiagnosticManager.Info(
                "WorkingCopy.Resolve",
                $"resolve.{action}.{phase}",
                string.Empty,
                SourceGit.Diagnostics.DiagnosticManager.CreateData(
                    ("repo", SourceGit.Diagnostics.DiagnosticManager.GetRepositoryId(repoPath)),
                    ("repoPath", repoPath),
                    ("traceId", traceId),
                    ("action", action),
                    ("phase", phase),
                    ("selectedCount", selectedCount),
                    ("conflictedCount", conflictedCount),
                    ("filesCount", filesCount),
                    ("needStageCount", needStageCount),
                    ("success", success),
                    ("firstPath", firstPath)));
        }

        public async Task<bool> UseExternalMergeToolAsync(Models.Change change)
        {
            if (!_repo.CanCreatePopup())
                return false;

            await _repo.ShowAndStartPopupAsync(new ExternalMergeTool(this, change));
            return true;
        }

        public async Task<bool> UseExternalMergeToolAsync(Models.Change change, Action<string> progress)
        {
            var key = change?.Path ?? "*";
            if (!BeginExternalMerge(key, progress))
                return true;

            var releaseLockOnReturn = true;
            try
            {
                var result = await UseExternalMergeToolCoreAsync(change, progress).ConfigureAwait(false);
                if (result.Processes.Count > 0)
                {
                    releaseLockOnReturn = false;
                    WatchExternalMergeProcesses(key, result.Processes);
                }

                return result.Success;
            }
            finally
            {
                if (releaseLockOnReturn)
                    EndExternalMerge(key);
            }
        }

        private async Task<ExternalMergeResult> UseExternalMergeToolCoreAsync(Models.Change change, Action<string> progress)
        {
            progress?.Invoke("Preparing external merge tool...");

            var tool = await GetExternalMergeToolConfigAsync().ConfigureAwait(false);
            if (tool == null || !tool.CanRunDirectly)
            {
                progress?.Invoke("Starting git merge tool...");
                var fallbackSuccess = await new Commands.MergeTool(_repo.FullPath, change?.Path).OpenAsync().ConfigureAwait(false);
                return new ExternalMergeResult { Success = fallbackSuccess };
            }

            var processes = new List<(Models.Change Change, Process Process, Commands.MergeTool.MergeFiles Files)>();
            var success = false;
            if (change == null)
                success = await OpenExternalMergeToolForAllConflictsAsync(tool, progress, processes).ConfigureAwait(false);
            else
                success = await OpenExternalMergeToolForChangeAsync(change, tool, progress, processes).ConfigureAwait(false);

            return new ExternalMergeResult
            {
                Success = success,
                Processes = processes,
            };
        }

        public void UseExternalDiffTool(Models.Change change, bool isUnstaged)
        {
            new Commands.DiffTool(_repo.FullPath, new Models.DiffOption(change, isUnstaged)).Open();
        }

        public async Task ContinueMergeAsync()
        {
            if (_inProgressContext != null)
            {
                using var lockWatcher = _repo.LockWatcher();
                IsCommitting = true;

                try
                {
                    if (!SaveInProgressCommitMessage())
                        return;

                    var log = _repo.CreateLog($"Continue {_inProgressContext.Name}");
                    await _inProgressContext.ContinueAsync(log);
                    log.Complete();

                    CommitMessage = string.Empty;
                }
                finally
                {
                    IsCommitting = false;
                }
            }
            else
            {
                _repo.MarkWorkingCopyDirtyManually();
            }
        }

        public async Task SkipMergeAsync()
        {
            if (_inProgressContext != null)
            {
                using var lockWatcher = _repo.LockWatcher();
                IsCommitting = true;

                var log = _repo.CreateLog($"Skip {_inProgressContext.Name}");
                await _inProgressContext.SkipAsync(log);
                log.Complete();

                CommitMessage = string.Empty;
                IsCommitting = false;
            }
            else
            {
                _repo.MarkWorkingCopyDirtyManually();
            }
        }

        public async Task AbortMergeAsync()
        {
            if (_inProgressContext != null)
            {
                using var lockWatcher = _repo.LockWatcher();
                IsCommitting = true;

                var log = _repo.CreateLog($"Abort {_inProgressContext.Name}");
                await _inProgressContext.AbortAsync(log);
                log.Complete();

                CommitMessage = string.Empty;
                IsCommitting = false;
            }
            else
            {
                _repo.MarkWorkingCopyDirtyManually();
            }
        }

        public void ApplyCommitMessageTemplate(Models.CommitTemplate tmpl)
        {
            CommitMessage = tmpl.Apply(_repo.CurrentBranch, _staged);
        }

        public async Task ClearCommitMessageHistoryAsync()
        {
            var sure = await App.AskConfirmAsync(App.Text("WorkingCopy.ClearCommitHistories.Confirm"));
            if (sure)
                _repo.Settings.CommitMessages.Clear();
        }

        public async Task CommitAsync(bool autoStage, bool autoPush)
        {
            if (string.IsNullOrWhiteSpace(_commitMessage))
                return;

            if (!_repo.CanCreatePopup())
            {
                _repo.SendNotification("Repository has an unfinished job! Please wait!", true);
                return;
            }

            if (autoStage && HasUnsolvedConflicts)
            {
                _repo.SendNotification("Repository has unsolved conflict(s). Auto-stage and commit is disabled!", true);
                return;
            }

            if (_repo.CurrentBranch is { IsDetachedHead: true })
            {
                var msg = App.Text("WorkingCopy.ConfirmCommitWithDetachedHead");
                var sure = await App.AskConfirmAsync(msg);
                if (!sure)
                    return;
            }

            if (!string.IsNullOrEmpty(_filter) && _staged.Count > _visibleStaged.Count)
            {
                var msg = App.Text("WorkingCopy.ConfirmCommitWithFilter", _staged.Count, _visibleStaged.Count, _staged.Count - _visibleStaged.Count);
                var sure = await App.AskConfirmAsync(msg);
                if (!sure)
                    return;
            }

            if (!_useAmend)
            {
                if ((!autoStage && _staged.Count == 0) || (autoStage && _cached.Count == 0))
                {
                    var rs = await App.AskConfirmEmptyCommitAsync(_cached.Count > 0, _selectedUnstaged is { Count: > 0 });
                    if (rs == Models.ConfirmEmptyCommitResult.Cancel)
                        return;

                    if (rs == Models.ConfirmEmptyCommitResult.StageAllAndCommit)
                        autoStage = true;
                    else if (rs == Models.ConfirmEmptyCommitResult.StageSelectedAndCommit)
                        await StageChangesAsync(_selectedUnstaged, null);
                }
            }

            using var lockWatcher = _repo.LockWatcher();
            IsCommitting = true;
            try
            {
                _repo.Settings.PushCommitMessage(_commitMessage);

                if (autoStage && _unstaged.Count > 0)
                    await StageChangesAsync(_unstaged, null);

                var log = _repo.CreateLog("Commit");
                var succ = await new Commands.Commit(_repo.FullPath, _commitMessage, EnableSignOff, NoVerifyOnCommit, _useAmend, _resetAuthor)
                        .Use(log)
                        .RunAsync();

                log.Complete();

                if (succ)
                {
                    UseAmend = false;
                    CommitMessage = string.Empty;

                    if (autoPush && _repo.Remotes.Count > 0)
                    {
                        Models.Branch pushBranch = null;
                        if (_repo.CurrentBranch == null)
                        {
                            var currentBranchName = await new Commands.QueryCurrentBranch(_repo.FullPath).GetResultAsync();
                            pushBranch = new Models.Branch() { Name = currentBranchName };
                        }

                        if (_repo.CanCreatePopup())
                            await _repo.ShowAndStartPopupAsync(new Push(_repo, pushBranch));
                    }
                }

                _repo.MarkBranchesDirtyManually();
            }
            finally
            {
                IsCommitting = false;
            }
        }

        private List<Models.Change> GetVisibleChanges(List<Models.Change> changes)
        {
            var filter = Models.FileSearch.Parse(_filter);
            if (filter.IsEmpty)
                return changes;

            var visible = new List<Models.Change>();

            foreach (var c in changes)
            {
                if (Models.FileSearch.Matches(c.Path, filter))
                    visible.Add(c);
            }

            return visible;
        }

        private async Task<List<Models.Change>> GetCanStageChangesAsync(List<Models.Change> changes)
        {
            if (!HasUnsolvedConflicts)
                return changes;

            var outs = new List<Models.Change>();
            foreach (var c in changes)
            {
                if (c.IsConflicted)
                {
                    var isResolved = c.ConflictReason switch
                    {
                        Models.ConflictReason.BothAdded or Models.ConflictReason.BothModified =>
                            await new Commands.IsConflictResolved(_repo.FullPath, c).GetResultAsync(),
                        _ => false,
                    };

                    if (!isResolved)
                        continue;
                }

                outs.Add(c);
            }

            return outs;
        }

        private List<Models.Change> GetStagedChanges(List<Models.Change> cached)
        {
            if (_useAmend)
                return new Commands.QueryStagedChangesWithAmend(_repo.FullPath).GetResult();

            var rs = new List<Models.Change>();
            foreach (var c in cached)
            {
                if (c.Index != Models.ChangeState.None)
                    rs.Add(c);
            }
            return rs;
        }

        private void UpdateDetail()
        {
            if (_selectedUnstaged.Count == 1)
                SetDetail(_selectedUnstaged[0], true);
            else if (_selectedStaged.Count == 1)
                SetDetail(_selectedStaged[0], false);
            else
                SetDetail(null, false);
        }

        private void UpdateInProgressState()
        {
            var oldType = _inProgressContext != null ? _inProgressContext.GetType() : null;

            if (File.Exists(Path.Combine(_repo.GitDir, "CHERRY_PICK_HEAD")))
                InProgressContext = new CherryPickInProgress(_repo);
            else if (Directory.Exists(Path.Combine(_repo.GitDir, "rebase-merge")) || Directory.Exists(Path.Combine(_repo.GitDir, "rebase-apply")))
                InProgressContext = new RebaseInProgress(_repo);
            else if (File.Exists(Path.Combine(_repo.GitDir, "REVERT_HEAD")))
                InProgressContext = new RevertInProgress(_repo);
            else if (File.Exists(Path.Combine(_repo.GitDir, "MERGE_HEAD")))
                InProgressContext = new MergeInProgress(_repo);
            else
                InProgressContext = null;

            if (_inProgressContext != null && _inProgressContext.GetType() == oldType && !string.IsNullOrEmpty(_commitMessage))
                return;

            if (_inProgressContext is RebaseInProgress { } rebasing)
            {
                if (LoadCommitMessageFromFile(rebasing.GetContinueMessageFile(_repo)))
                    return;

                CommitMessage = new Commands.QueryCommitFullMessage(_repo.FullPath, rebasing.StoppedAt.SHA).GetResult();
                return;
            }

            if (LoadCommitMessageFromFile(_inProgressContext?.GetContinueMessageFile(_repo) ?? string.Empty))
                return;
        }

        private bool LoadCommitMessageFromFile(string file)
        {
            if (string.IsNullOrEmpty(file))
                return false;

            if (!File.Exists(file))
                return false;

            var msg = Models.CommitMessageFormatter.NormalizeLineEndingsForGit(File.ReadAllText(file));
            if (string.IsNullOrEmpty(msg))
                return false;

            CommitMessage = msg;
            return true;
        }

        private void SetDetail(Models.Change change, bool isUnstaged)
        {
            if (_isLoadingData)
                return;

            if (change == null)
            {
                DetailContext.Clear();
            }
            else if (change.IsConflicted)
            {
                DetailContext.ShowContent(new Conflict(_repo, this, change));
            }
            else
            {
                var option = new Models.DiffOption(change, isUnstaged);
                DetailContext.ShowDiff(_repo.FullPath, option, "working_copy.refresh");
            }
        }

        public void ShowResolvedConflictDiff(Models.Change change, Conflict source)
        {
            if (!ReferenceEquals(DetailContext.ActiveContent, source))
                return;

            var resolved = CreateResolvedConflictChange(change);
            DetailContext.ShowDiff(_repo.FullPath, new Models.DiffOption(resolved, true), "working_copy.resolved_conflict");
        }

        private void EnsureMergeConflictHistoryCache()
        {
            var plan = _inProgressContext?.CreateConflictHistoryPlan(_repo);
            if (plan != null)
                MergeConflictHistories.Ensure(_repo, plan);
            else
                MergeConflictHistories.Reset();
        }

        private async Task<Commands.MergeTool.ExternalToolConfig> GetExternalMergeToolConfigAsync()
        {
            var tool = Native.OS.GetDiffMergeTool(false);
            if (tool == null)
            {
                _repo.SendNotification("Invalid diff/merge tool in preference setting!", true);
                return null;
            }

            if (!string.IsNullOrEmpty(tool.Cmd))
            {
                return new Commands.MergeTool.ExternalToolConfig
                {
                    Name = "SourceGit",
                    Exec = tool.Exec,
                    Args = tool.Cmd,
                };
            }

            lock (_mergeToolConfigLock)
            {
                if (_gitMergeToolConfigTask == null)
                    _gitMergeToolConfigTask = Commands.MergeTool.ResolveGitConfiguredToolAsync(_repo.FullPath);
            }

            var gitTool = await _gitMergeToolConfigTask.ConfigureAwait(false);
            return gitTool.CanRunDirectly ? gitTool : null;
        }

        private async Task<bool> OpenExternalMergeToolForAllConflictsAsync(
            Commands.MergeTool.ExternalToolConfig tool,
            Action<string> progress,
            List<(Models.Change Change, Process Process, Commands.MergeTool.MergeFiles Files)> processes)
        {
            var conflicts = new List<Models.Change>();
            var skipped = 0;
            foreach (var change in _unstaged)
            {
                if (CanOpenWithDirectExternalMerge(change))
                    conflicts.Add(change);
                else if (change.IsConflicted)
                    skipped++;
            }

            if (conflicts.Count == 0)
                return FailExternalMerge(progress, "No mergeable text conflicts can be opened by the external merge tool.");

            if (skipped > 0)
            {
                var message = $"{skipped} conflict(s) cannot be opened by the external merge tool and were skipped.";
                progress?.Invoke(message);
                _repo.SendNotification(message);
            }

            var succ = true;
            foreach (var conflict in conflicts)
            {
                if (!await OpenExternalMergeToolForChangeAsync(conflict, tool, progress, processes).ConfigureAwait(false))
                    succ = false;
            }

            return processes.Count > 0 || succ;
        }

        private async Task<bool> OpenExternalMergeToolForChangeAsync(
            Models.Change change,
            Commands.MergeTool.ExternalToolConfig tool,
            Action<string> progress,
            List<(Models.Change Change, Process Process, Commands.MergeTool.MergeFiles Files)> processes)
        {
            if (!CanOpenWithDirectExternalMerge(change))
                return FailExternalMerge(progress, "This conflict type cannot be opened directly by the external merge tool.");

            try
            {
                progress?.Invoke("Preparing conflict files...");
                var (mineName, theirName) = GetExternalMergeFileSideNames();
                var hasBaseStage = change.ConflictReason != Models.ConflictReason.BothAdded;
                var files = await Commands.MergeConflictBlob.CreateMergeFilesAsync(
                    _repo.FullPath,
                    change.Path,
                    mineName,
                    theirName,
                    hasBaseStage).ConfigureAwait(false);

                if (files == null)
                    return FailExternalMerge(progress, "Unable to load conflict files.");

                progress?.Invoke("Starting external merge tool...");
                if (!Commands.MergeTool.TryStartExternal(_repo.FullPath, tool, files, out var proc, out var error))
                {
                    Commands.MergeConflictBlob.CleanupMergeFiles(files);
                    return FailExternalMerge(progress, error);
                }

                processes.Add((change, proc, files));
                return true;
            }
            catch (Exception e)
            {
                _repo.SendNotification(e.Message, true);
                progress?.Invoke(e.Message);
                return false;
            }
        }

        private (string Mine, string Their) GetExternalMergeFileSideNames()
        {
            return _inProgressContext?.GetExternalMergeFileSideNames() ?? ("HEAD", string.Empty);
        }

        private bool BeginExternalMerge(string key, Action<string> progress)
        {
            lock (_externalMergeLock)
            {
                if (key.Equals("*", StringComparison.Ordinal))
                {
                    if (_externalMergesInProgress.Count == 0 && _externalMergesInProgress.Add(key))
                        return true;
                }
                else if (!_externalMergesInProgress.Contains("*") && _externalMergesInProgress.Add(key))
                {
                    return true;
                }
            }

            progress?.Invoke("External merge tool is already running for this conflict.");
            return false;
        }

        private void EndExternalMerge(string key)
        {
            lock (_externalMergeLock)
                _externalMergesInProgress.Remove(key);
        }

        private void WatchExternalMergeProcesses(string key, List<(Models.Change Change, Process Process, Commands.MergeTool.MergeFiles Files)> processes)
        {
            Task.Run(async () =>
            {
                try
                {
                    var waits = new List<Task>(processes.Count);
                    foreach (var item in processes)
                        waits.Add(WaitExternalMergeProcessAsync(item.Change, item.Process, item.Files));

                    await Task.WhenAll(waits).ConfigureAwait(false);
                }
                finally
                {
                    EndExternalMerge(key);
                }
            });
        }

        private async Task WaitExternalMergeProcessAsync(Models.Change change, Process process, Commands.MergeTool.MergeFiles files)
        {
            try
            {
                using (process)
                    await process.WaitForExitAsync().ConfigureAwait(false);

                await UpdateExternalMergeResultAsync(change).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Native.OS.LogException(e);
            }
            finally
            {
                Commands.MergeConflictBlob.CleanupMergeFiles(files);
            }
        }

        private async Task UpdateExternalMergeResultAsync(Models.Change change)
        {
            if (change == null)
                return;

            var resolved = await new Commands.IsConflictResolved(_repo.FullPath, change)
                .GetResultAsync()
                .ConfigureAwait(false);

            if (resolved)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => MarkConflictAsResolved(change));
            else
                Avalonia.Threading.Dispatcher.UIThread.Post(() => CheckSelectedConflictResolved(change));
        }

        private void MarkConflictAsResolved(Models.Change change)
        {
            var updated = false;
            var changes = new List<Models.Change>(_cached.Count);
            foreach (var c in _cached)
            {
                if (!updated && c.Path.Equals(change.Path, StringComparison.Ordinal))
                {
                    if (c.IsConflicted && c.ConflictReason == change.ConflictReason)
                    {
                        changes.Add(CreateResolvedConflictChange(c));
                        updated = true;
                    }
                    else
                    {
                        changes.Add(c);
                    }
                }
                else
                {
                    changes.Add(c);
                }
            }

            if (updated)
                SetData(changes);
            else
                CheckSelectedConflictResolved(change);
        }

        private static Models.Change CreateResolvedConflictChange(Models.Change change)
        {
            return new Models.Change
            {
                Index = Models.ChangeState.None,
                WorkTree = Models.ChangeState.Modified,
                Path = change.Path,
                OriginalPath = change.OriginalPath,
                DataForAmend = change.DataForAmend,
                ConflictReason = change.ConflictReason,
                IsResolvedConflict = true,
            };
        }

        private bool FailExternalMerge(Action<string> progress, string message)
        {
            progress?.Invoke(message);
            _repo.SendNotification(message, true);
            return false;
        }

        private bool CanOpenWithDirectExternalMerge(Models.Change change)
        {
            if (change == null || !change.IsConflicted)
                return false;

            if (change.ConflictReason is not Models.ConflictReason.BothAdded and not Models.ConflictReason.BothModified)
                return false;

            return !Directory.Exists(Path.Combine(_repo.FullPath, change.Path));
        }

        private void CheckSelectedConflictResolved(Models.Change change)
        {
            if (change == null)
                return;

            if (DetailContext.ActiveContent is Conflict conflict && conflict.FilePath.Equals(change.Path, StringComparison.Ordinal))
                conflict.StartResolveCheck();
        }

        private bool IsChanged(List<Models.Change> old, List<Models.Change> cur)
        {
            if (old.Count != cur.Count)
                return true;

            for (int idx = 0; idx < old.Count; idx++)
            {
                var o = old[idx];
                var c = cur[idx];
                if (!o.Path.Equals(c.Path, StringComparison.Ordinal) ||
                    o.Index != c.Index ||
                    o.WorkTree != c.WorkTree ||
                    o.IsResolvedConflict != c.IsResolvedConflict)
                    return true;
            }

            return false;
        }

        private Repository _repo = null;
        private bool _isLoadingData = false;
        private bool _isStaging = false;
        private bool _isUnstaging = false;
        private bool _isCommitting = false;
        private bool _useAmend = false;
        private bool _resetAuthor = false;
        private bool _hasRemotes = false;
        private List<Models.Change> _cached = [];
        private List<Models.Change> _unstaged = [];
        private List<Models.Change> _visibleUnstaged = [];
        private List<Models.Change> _staged = [];
        private List<Models.Change> _visibleStaged = [];
        private List<Models.Change> _selectedUnstaged = [];
        private List<Models.Change> _selectedStaged = [];
        private string _filter = string.Empty;
        private string _commitMessage = string.Empty;

        private bool _hasUnsolvedConflicts = false;
        private InProgressContext _inProgressContext = null;
        private readonly object _mergeToolConfigLock = new();
        private Task<Commands.MergeTool.ExternalToolConfig> _gitMergeToolConfigTask = null;
        private readonly object _externalMergeLock = new();
        private HashSet<string> _externalMergesInProgress = new(StringComparer.Ordinal);
    }
}
