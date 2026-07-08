using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class CommitDetailSharedData
    {
        public int ActiveTabIndex
        {
            get;
            set;
        }

        public CommitDetailSharedData()
        {
            ActiveTabIndex = Preferences.Instance.ShowChangesInCommitDetailByDefault ? 1 : 0;
        }
    }

    public partial class CommitDetail : ObservableObject, IDisposable
    {
        public Repository Repository
        {
            get => _repo;
        }

        public int ActiveTabIndex
        {
            get => _sharedData.ActiveTabIndex;
            set
            {
                if (value != _sharedData.ActiveTabIndex)
                {
                    _sharedData.ActiveTabIndex = value;
                    OnPropertyChanged(nameof(ActiveTabIndex));

                    if (value == 1 && !DiffHost.HasActiveContent && _selectedChanges is { Count: 1 })
                        RequestDiffContext(_selectedChanges[0]);
                }
            }
        }

        public Models.Commit Commit
        {
            get => _commit;
            set
            {
                if (_disposed)
                    return;

                if (_commit != null && value != null && _commit.SHA.Equals(value.SHA, StringComparison.Ordinal))
                    return;

                if (SetProperty(ref _commit, value))
                    Refresh();
            }
        }

        public Models.CommitFullMessage FullMessage
        {
            get => _fullMessage;
            private set => SetProperty(ref _fullMessage, value);
        }

        public Models.CommitSignInfo SignInfo
        {
            get => _signInfo;
            private set => SetProperty(ref _signInfo, value);
        }

        public List<Models.CommitLink> WebLinks
        {
            get;
            private set;
        }

        public List<string> Children
        {
            get => _children;
            private set => SetProperty(ref _children, value);
        }

        public List<Models.Change> Changes
        {
            get => _changes;
            set => SetProperty(ref _changes, value);
        }

        public List<Models.Change> VisibleChanges
        {
            get => _visibleChanges;
            set => SetProperty(ref _visibleChanges, value);
        }

        public List<Models.Change> SelectedChanges
        {
            get => _selectedChanges;
            set
            {
                using var span = StartSelectedChangesDiagnosticSpan(value);
                if (_disposed)
                {
                    span.Set("result", "disposed");
                    return;
                }

                if (_isCommitDetailLoading && value is { Count: > 0 })
                {
                    span.Set("result", "skip_loading");
                    return;
                }

                if (SetProperty(ref _selectedChanges, value))
                {
                    if (ActiveTabIndex != 1 || value is not { Count: 1 })
                    {
                        DiffHost.Clear();
                        span.Set("result", "clear_diff");
                    }
                    else
                    {
                        RequestDiffContext(value[0]);
                        span.Set("result", "request_diff");
                    }
                }
                else
                {
                    span.Set("result", "unchanged");
                }
            }
        }

        public DiffContentHost DiffHost { get; } = new();

        public string SearchChangeFilter
        {
            get => _searchChangeFilter;
            set
            {
                if (SetProperty(ref _searchChangeFilter, value))
                    RefreshVisibleChanges();
            }
        }

        public string ViewRevisionFilePath
        {
            get => _viewRevisionFilePath;
            private set => SetProperty(ref _viewRevisionFilePath, value);
        }

        public object ViewRevisionFileContent
        {
            get => _viewRevisionFileContent;
            private set => SetProperty(ref _viewRevisionFileContent, value);
        }

        public bool IsRevisionFileContentLoading
        {
            get => _isRevisionFileContentLoading;
            private set => SetProperty(ref _isRevisionFileContentLoading, value);
        }

        public string RevisionFileSearchFilter
        {
            get => _revisionFileSearchFilter;
            set
            {
                if (SetProperty(ref _revisionFileSearchFilter, value))
                    RefreshRevisionSearchSuggestion();
            }
        }

        public List<string> RevisionFileSearchSuggestion
        {
            get => _revisionFileSearchSuggestion;
            private set => SetProperty(ref _revisionFileSearchSuggestion, value);
        }

        public bool IsRevisionFileIndexLoading
        {
            get => _isRevisionFileIndexLoading;
            private set => SetProperty(ref _isRevisionFileIndexLoading, value);
        }

        public bool IsRevisionFileSearchLoading
        {
            get => _isRevisionFileSearchLoading;
            private set => SetProperty(ref _isRevisionFileSearchLoading, value);
        }

        public bool IsCommitDetailLoading
        {
            get => _isCommitDetailLoading;
            private set => SetProperty(ref _isCommitDetailLoading, value);
        }

        public bool CanOpenRevisionFileWithDefaultEditor
        {
            get => _canOpenRevisionFileWithDefaultEditor;
            private set => SetProperty(ref _canOpenRevisionFileWithDefaultEditor, value);
        }

        public Vector ScrollOffset
        {
            get => _scrollOffset;
            set => SetProperty(ref _scrollOffset, value);
        }

        public Vector RevisionFileContentScrollOffset
        {
            get => _revisionFileContentScrollOffset;
            set => SetProperty(ref _revisionFileContentScrollOffset, value);
        }

        public CommitDetail(Repository repo, CommitDetailSharedData sharedData)
        {
            _repo = repo;
            _sharedData = sharedData ?? new CommitDetailSharedData();
            WebLinks = Models.CommitLink.Get(repo.Remotes);
        }

        public void UseChangesTab()
        {
            ActiveTabIndex = 1;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _requestId++;
            if (_cancellationSource is { IsCancellationRequested: false })
                _cancellationSource.Cancel();

            DiffHost.Dispose();
        }

        public CommitDetail Clone()
        {
            var cloned = new CommitDetail(_repo, null);
            cloned.ActiveTabIndex = ActiveTabIndex;
            cloned.Commit = _commit;
            return cloned;
        }

        public void NavigateTo(string commitSHA)
        {
            _repo?.NavigateToCommit(commitSHA);
        }

        public async Task<List<Models.Decorator>> GetRefsContainsThisCommitAsync()
        {
            return await new Commands.QueryRefsContainsCommit(_repo.FullPath, _commit.SHA)
                .GetResultAsync()
                .ConfigureAwait(false);
        }

        public void ClearSearchChangeFilter()
        {
            SearchChangeFilter = string.Empty;
        }

        public void ClearRevisionFileSearchFilter()
        {
            RevisionFileSearchFilter = string.Empty;
        }

        public void CancelRevisionFileSuggestions()
        {
            RevisionFileSearchSuggestion = null;
        }

        public void PreloadRevisionFileNames()
        {
            if (_commit == null || _revisionFiles != null || _isRevisionFileNamesLoading)
                return;

            RequestRevisionFileNames();
        }

        public async Task<string> GetRevisionRootTreeSHAAsync()
        {
            var commit = _commit;
            if (commit == null)
                return string.Empty;

            var cmd = new Commands.QueryRevisionRootTreeSHA(_repo.FullPath, commit.SHA)
            {
                CancellationToken = _cancellationSource?.Token ?? CancellationToken.None,
            };

            return await cmd.GetResultAsync().ConfigureAwait(false);
        }

        public async Task<List<Models.Object>> GetRevisionTreeChildrenAsync(string treeSHA, string parentPath)
        {
            if (string.IsNullOrEmpty(treeSHA))
                return [];

            if (RevisionFileCache.TryGetTree(treeSHA, out var cached))
                return CreateRevisionTreeObjects(cached, parentPath);

            IsRevisionFileIndexLoading = true;
            try
            {
                var token = _cancellationSource?.Token ?? CancellationToken.None;
                var entries = await RevisionFileCache
                    .GetOrAddTreeAsync(treeSHA, async () =>
                    {
                        var cmd = new Commands.QueryRevisionObjects(_repo.FullPath, treeSHA, null) { CancellationToken = token };
                        var objects = await cmd.GetResultAsync().ConfigureAwait(false);
                        return token.IsCancellationRequested ? null : objects;
                    });

                if (token.IsCancellationRequested || entries == null)
                    return [];

                return CreateRevisionTreeObjects(entries, parentPath);
            }
            finally
            {
                IsRevisionFileIndexLoading = false;
            }
        }

        public async Task<List<Models.Object>> GetRevisionObjectsAsync(string path)
        {
            var commit = _commit;
            if (commit == null)
                return [];

            var cmd = new Commands.QueryRevisionObjects(_repo.FullPath, commit.SHA, path)
            {
                CancellationToken = _cancellationSource?.Token ?? CancellationToken.None,
            };

            return await cmd.GetResultAsync().ConfigureAwait(false);
        }

        public async Task<List<string>> GetRevisionFileNamesAsync()
        {
            var commit = _commit;
            if (commit == null)
                return [];

            return await GetRevisionFileNamesAsync(commit.SHA, _cancellationSource?.Token ?? CancellationToken.None)
                .ConfigureAwait(false);
        }

        private async Task<List<string>> GetRevisionFileNamesAsync(string commitSHA, CancellationToken token)
        {
            if (string.IsNullOrEmpty(commitSHA))
                return [];

            if (RevisionFileCache.TryGetFileNames(commitSHA, out var cached))
                return cached;

            var files = await RevisionFileCache
                .GetOrAddFileNamesAsync(commitSHA, async () =>
                {
                    var cmd = new Commands.QueryRevisionFileNames(_repo.FullPath, commitSHA) { CancellationToken = token };
                    var names = await cmd.GetResultAsync().ConfigureAwait(false);
                    return token.IsCancellationRequested ? null : names;
                });

            return token.IsCancellationRequested || files == null ? [] : files;
        }

        public async Task<Models.Commit> GetCommitAsync(string sha)
        {
            return await new Commands.QuerySingleCommit(_repo.FullPath, sha)
                .GetResultAsync()
                .ConfigureAwait(false);
        }

        public string GetAbsPath(string path)
        {
            return Native.OS.GetAbsPath(_repo.FullPath, path);
        }

        public void OpenChangeInMergeTool(Models.Change c)
        {
            new Commands.DiffTool(_repo.FullPath, new Models.DiffOption(_commit, c)).Open();
        }

        public async Task SaveChangesAsPatchAsync(List<Models.Change> changes, string saveTo)
        {
            if (_commit == null)
                return;

            var succ = await Commands.SaveChangesAsPatch.ProcessRevisionCompareChangesAsync(
                _repo.FullPath,
                changes,
                _commit.FirstParentToCompare,
                _commit.SHA,
                saveTo);

            if (succ)
                _repo.SendNotification(App.Text("SaveAsPatchSuccess"));
        }

        public async Task ResetToThisRevisionAsync(string path)
        {
            var c = _changes?.Find(x => x.Path.Equals(path, StringComparison.Ordinal));
            if (c != null)
            {
                await ResetToThisRevisionAsync(c);
                return;
            }

            var log = _repo.CreateLog($"Reset File to '{_commit.SHA}'");
            await new Commands.Checkout(_repo.FullPath).Use(log).FileWithRevisionAsync(path, _commit.SHA);
            log.Complete();
        }

        public async Task ResetToThisRevisionAsync(Models.Change change)
        {
            var log = _repo.CreateLog($"Reset File to '{_commit.SHA}'");

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
                    .FileWithRevisionAsync(change.Path, _commit.SHA);
            }
            else
            {
                await new Commands.Checkout(_repo.FullPath)
                    .Use(log)
                    .FileWithRevisionAsync(change.Path, _commit.SHA);
            }

            log.Complete();
        }

        public async Task ResetToParentRevisionAsync(Models.Change change)
        {
            var log = _repo.CreateLog($"Reset File to '{_commit.SHA}~1'");

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
                    .FileWithRevisionAsync(change.OriginalPath, $"{_commit.SHA}~1");
            }
            else
            {
                await new Commands.Checkout(_repo.FullPath)
                    .Use(log)
                    .FileWithRevisionAsync(change.Path, $"{_commit.SHA}~1");
            }

            log.Complete();
        }

        public async Task ResetMultipleToThisRevisionAsync(List<Models.Change> changes)
        {
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
                    var old = Native.OS.GetAbsPath(_repo.FullPath, c.OriginalPath);
                    if (File.Exists(old))
                        removes.Add(c.OriginalPath);

                    checkouts.Add(c.Path);
                }
                else
                {
                    checkouts.Add(c.Path);
                }
            }

            var log = _repo.CreateLog($"Reset Files to '{_commit.SHA}'");

            if (removes.Count > 0)
                await new Commands.Remove(_repo.FullPath, removes)
                    .Use(log)
                    .ExecAsync();

            if (checkouts.Count > 0)
                await new Commands.Checkout(_repo.FullPath)
                    .Use(log)
                    .MultipleFilesWithRevisionAsync(checkouts, _commit.SHA);

            log.Complete();
        }

        public async Task ResetMultipleToParentRevisionAsync(List<Models.Change> changes)
        {
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
                    var renamed = Native.OS.GetAbsPath(_repo.FullPath, c.Path);
                    if (File.Exists(renamed))
                        removes.Add(c.Path);

                    checkouts.Add(c.OriginalPath);
                }
                else
                {
                    checkouts.Add(c.Path);
                }
            }

            var log = _repo.CreateLog($"Reset Files to '{_commit.SHA}~1'");

            if (removes.Count > 0)
                await new Commands.Remove(_repo.FullPath, removes)
                    .Use(log)
                    .ExecAsync();

            if (checkouts.Count > 0)
                await new Commands.Checkout(_repo.FullPath)
                    .Use(log)
                    .MultipleFilesWithRevisionAsync(checkouts, $"{_commit.SHA}~1");

            log.Complete();
        }

        public async Task ViewRevisionFileAsync(Models.Object file)
        {
            var requestId = _requestId;
            var commitSHA = _commit?.SHA;
            var obj = file ?? new Models.Object() { Path = string.Empty, Type = Models.ObjectType.None };

            if (obj.Type == Models.ObjectType.None)
            {
                _viewRevisionFileObject = null;
                ViewRevisionFilePath = string.Empty;
                ViewRevisionFileContent = null;
                CanOpenRevisionFileWithDefaultEditor = false;
                IsRevisionFileContentLoading = false;
                RevisionFileContentScrollOffset = Vector.Zero;
                return;
            }

            var oldObj = _viewRevisionFileObject;
            if (IsSameRevisionFileObject(oldObj, obj))
            {
                _viewRevisionFileObject = CloneRevisionFileObject(obj);
                ViewRevisionFilePath = obj.Path;
                CanOpenRevisionFileWithDefaultEditor = obj.Type == Models.ObjectType.Blob;
                IsRevisionFileContentLoading = false;
                return;
            }

            RevisionFileContentScrollOffset = Vector.Zero;

            ViewRevisionFilePath = obj.Path;

            switch (obj.Type)
            {
                case Models.ObjectType.Blob:
                    if (string.IsNullOrEmpty(commitSHA))
                        return;

                    CanOpenRevisionFileWithDefaultEditor = true;
                    IsRevisionFileContentLoading = true;
                    try
                    {
                        await SetViewingBlobAsync(obj, commitSHA, requestId);
                    }
                    finally
                    {
                        if (IsCurrentRevisionFileRequest(commitSHA, obj.Path, requestId))
                        {
                            _viewRevisionFileObject = CloneRevisionFileObject(obj);
                            IsRevisionFileContentLoading = false;
                        }
                    }
                    break;
                case Models.ObjectType.Commit:
                    if (string.IsNullOrEmpty(commitSHA))
                        return;

                    CanOpenRevisionFileWithDefaultEditor = false;
                    IsRevisionFileContentLoading = true;
                    try
                    {
                        await SetViewingCommitAsync(obj, commitSHA, requestId);
                    }
                    finally
                    {
                        if (IsCurrentRevisionFileRequest(commitSHA, obj.Path, requestId))
                        {
                            _viewRevisionFileObject = CloneRevisionFileObject(obj);
                            IsRevisionFileContentLoading = false;
                        }
                    }
                    break;
                default:
                    _viewRevisionFileObject = null;
                    CanOpenRevisionFileWithDefaultEditor = false;
                    IsRevisionFileContentLoading = false;
                    ViewRevisionFileContent = null;
                    RevisionFileContentScrollOffset = Vector.Zero;
                    break;
            }
        }

        public async Task OpenRevisionFileAsync(string file, Models.ExternalTool tool, string revisionBlobSHA = null)
        {
            var fullPath = Native.OS.GetAbsPath(_repo.FullPath, file);
            var openPath = await Commands.SaveRevisionFile
                .GetWorktreePathIfRevisionMatchesDiskAsync(_repo.FullPath, _commit.SHA, file, revisionBlobSHA)
                .ConfigureAwait(false);

            if (string.IsNullOrEmpty(openPath))
            {
                var fileName = Path.GetFileNameWithoutExtension(fullPath) ?? "";
                var fileExt = Path.GetExtension(fullPath) ?? "";
                openPath = Path.Combine(Path.GetTempPath(), $"{fileName}~{_commit.SHA.AsSpan(0, 10)}{fileExt}");

                await Commands.SaveRevisionFile
                    .RunAsync(_repo.FullPath, _commit.SHA, file, openPath)
                    .ConfigureAwait(false);
            }

            if (tool == null)
                Native.OS.OpenWithDefaultEditor(openPath);
            else
                tool.Launch(openPath.Quoted());
        }

        public async Task SaveRevisionFileAsync(Models.Object file, string saveTo)
        {
            await Commands.SaveRevisionFile
                .RunAsync(_repo.FullPath, _commit.SHA, file.Path, saveTo)
                .ConfigureAwait(false);
        }

        private static List<Models.Object> CreateRevisionTreeObjects(List<RevisionFileCache.TreeEntry> entries, string parentPath)
        {
            var objects = new List<Models.Object>();
            if (entries == null)
                return objects;

            foreach (var entry in entries)
                objects.Add(entry.ToObject(parentPath));

            return objects;
        }

        private static bool IsSameRevisionFileObject(Models.Object left, Models.Object right)
        {
            return left != null &&
                right != null &&
                left.Type == right.Type &&
                string.Equals(left.SHA, right.SHA, StringComparison.Ordinal) &&
                string.Equals(left.Path, right.Path, StringComparison.Ordinal);
        }

        private static Models.Object CloneRevisionFileObject(Models.Object source)
        {
            if (source == null)
                return null;

            return new Models.Object
            {
                SHA = source.SHA,
                Type = source.Type,
                Path = source.Path,
            };
        }

        private void Refresh()
        {
            var requestId = ++_requestId;
            using var span = StartCommitDiagnosticSpan("refresh.schedule", _commit, requestId);
            var keepRevisionFilePreview =
                _commit != null &&
                _viewRevisionFileObject != null &&
                !string.IsNullOrEmpty(_viewRevisionFilePath);

            if (_cancellationSource is { IsCancellationRequested: false })
            {
                using var cancelSpan = StartCommitDiagnosticSpan("cancel_previous", _commit, requestId);
                _cancellationSource.Cancel();
                cancelSpan.Set("result", "canceled");
            }

            IsRevisionFileIndexLoading = false;
            IsRevisionFileSearchLoading = false;
            _isRevisionFileNamesLoading = false;
            IsCommitDetailLoading = _commit != null;
            span.Set("keepRevisionFilePreview", keepRevisionFilePreview);
            span.Set("activeTab", ActiveTabIndex);

            SelectedChanges = null;
            DiffHost.Clear();
            SignInfo = null;
            Children = null;

            if (keepRevisionFilePreview)
            {
                IsRevisionFileContentLoading = true;
                CanOpenRevisionFileWithDefaultEditor = false;
            }
            else
            {
                _viewRevisionFileObject = null;
                ViewRevisionFileContent = null;
                ViewRevisionFilePath = string.Empty;
                IsRevisionFileContentLoading = false;
                CanOpenRevisionFileWithDefaultEditor = false;
                RevisionFileContentScrollOffset = Vector.Zero;
            }

            RevisionFileSearchSuggestion = null;
            _revisionFiles = null;
            ScrollOffset = Vector.Zero;

            if (_commit == null)
            {
                IsCommitDetailLoading = false;
                FullMessage = null;
                SignInfo = null;
                Changes = [];
                VisibleChanges = [];
                Children = null;
                _viewRevisionFileObject = null;
                ViewRevisionFileContent = null;
                ViewRevisionFilePath = string.Empty;
                IsRevisionFileContentLoading = false;
                CanOpenRevisionFileWithDefaultEditor = false;
                RevisionFileContentScrollOffset = Vector.Zero;
                span.Set("result", "clear");
                return;
            }

            _cancellationSource = new CancellationTokenSource();
            var token = _cancellationSource.Token;
            var commit = _commit;
            var commitSHA = commit.SHA;
            span.Set("commit", commitSHA);
            span.Set("firstParent", commit.FirstParentToCompare ?? string.Empty);
            span.Set("result", "scheduled");

            Task.Run(async () =>
            {
                using var querySpan = StartCommitDiagnosticSpan("query.full_message", commit, requestId);
                var message = await new Commands.QueryCommitFullMessage(_repo.FullPath, commitSHA)
                    .GetResultAsync()
                    .ConfigureAwait(false);
                var inlines = await ParseInlinesInMessageAsync(message, token).ConfigureAwait(false);
                querySpan.Set("messageLength", message?.Length ?? 0);

                if (token.IsCancellationRequested || requestId != _requestId)
                {
                    querySpan.MarkCanceled(token.IsCancellationRequested);
                    querySpan.Set("result", "stale_before_post");
                    return;
                }

                Dispatcher.UIThread.Post(() =>
                {
                    using var applySpan = StartCommitDiagnosticSpan("apply.full_message", commit, requestId);
                    if (token.IsCancellationRequested || requestId != _requestId)
                    {
                        applySpan.MarkCanceled(token.IsCancellationRequested);
                        applySpan.Set("result", "stale");
                        return;
                    }

                    FullMessage = new Models.CommitFullMessage
                    {
                        Message = message,
                        Inlines = inlines
                    };
                    applySpan.Set("messageLength", message?.Length ?? 0);
                    applySpan.Set("result", "applied");
                });
                querySpan.Set("result", "posted");
            }, token);

            Task.Run(async () =>
            {
                using var querySpan = StartCommitDiagnosticSpan("query.sign_info", commit, requestId);
                var signInfo = await new Commands.QueryCommitSignInfo(_repo.FullPath, commitSHA, !_repo.HasAllowedSignersFile)
                    .GetResultAsync()
                    .ConfigureAwait(false);
                querySpan.Set("hasSignInfo", signInfo != null);

                if (token.IsCancellationRequested || requestId != _requestId)
                {
                    querySpan.MarkCanceled(token.IsCancellationRequested);
                    querySpan.Set("result", "stale_before_post");
                    return;
                }

                Dispatcher.UIThread.Post(() =>
                {
                    using var applySpan = StartCommitDiagnosticSpan("apply.sign_info", commit, requestId);
                    if (!token.IsCancellationRequested && requestId == _requestId)
                    {
                        SignInfo = signInfo;
                        applySpan.Set("result", "applied");
                    }
                    else
                    {
                        applySpan.MarkCanceled(token.IsCancellationRequested);
                        applySpan.Set("result", "stale");
                    }
                });
                querySpan.Set("result", "posted");
            }, token);

            if (Preferences.Instance.ShowChildren)
            {
                Task.Run(async () =>
                {
                    using var querySpan = StartCommitDiagnosticSpan("query.children", commit, requestId);
                    var max = Preferences.Instance.MaxHistoryCommits;
                    var cmd = new Commands.QueryCommitChildren(_repo.FullPath, commitSHA, max) { CancellationToken = token };
                    var children = await cmd.GetResultAsync().ConfigureAwait(false);
                    querySpan.Set("childrenCount", children?.Count ?? 0);
                    if (!token.IsCancellationRequested && requestId == _requestId)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            using var applySpan = StartCommitDiagnosticSpan("apply.children", commit, requestId);
                            if (!token.IsCancellationRequested && requestId == _requestId)
                            {
                                Children = children;
                                applySpan.Set("childrenCount", children?.Count ?? 0);
                                applySpan.Set("result", "applied");
                            }
                            else
                            {
                                applySpan.MarkCanceled(token.IsCancellationRequested);
                                applySpan.Set("result", "stale");
                            }
                        });
                        querySpan.Set("result", "posted");
                    }
                    else
                    {
                        querySpan.MarkCanceled(token.IsCancellationRequested);
                        querySpan.Set("result", "stale_before_post");
                    }
                }, token);
            }

            Task.Run(async () =>
            {
                using var querySpan = StartCommitDiagnosticSpan("query.changes", commit, requestId);
                var cmd = new Commands.CompareRevisions(_repo.FullPath, commit.FirstParentToCompare, commitSHA) { CancellationToken = token };
                var changes = await cmd.ReadAsync().ConfigureAwait(false);
                var visible = GetVisibleChanges(changes);
                querySpan.Set("changesCount", changes.Count);
                querySpan.Set("visibleCount", visible.Count);

                if (token.IsCancellationRequested || requestId != _requestId)
                {
                    querySpan.MarkCanceled(token.IsCancellationRequested);
                    querySpan.Set("result", "stale_before_post");
                    return;
                }

                Dispatcher.UIThread.Post(() =>
                {
                    using var applySpan = StartCommitDiagnosticSpan("apply.changes", commit, requestId);
                    if (token.IsCancellationRequested || requestId != _requestId)
                    {
                        applySpan.MarkCanceled(token.IsCancellationRequested);
                        applySpan.Set("result", "stale");
                        return;
                    }

                    Changes = changes;
                    VisibleChanges = visible;
                    IsCommitDetailLoading = false;
                    applySpan.Set("changesCount", changes.Count);
                    applySpan.Set("visibleCount", visible.Count);

                    if (visible.Count == 0)
                    {
                        SelectedChanges = null;
                        applySpan.Set("autoSelect", false);
                    }
                    else
                    {
                        SelectedChanges = [visible[0]];
                        applySpan.Set("autoSelect", true);
                        applySpan.Set("autoSelectPath", visible[0].Path);
                    }

                    applySpan.Set("result", "applied");
                });
                querySpan.Set("result", "posted");
            }, token);
        }

        private async Task<Models.InlineElementCollector> ParseInlinesInMessageAsync(string message, CancellationToken token)
        {
            var inlines = new Models.InlineElementCollector();
            if (_repo.IssueTrackers is { Count: > 0 } rules)
            {
                foreach (var rule in rules)
                    rule.Matches(inlines, message);
            }

            var urlMatches = REG_URL_FORMAT().Matches(message);
            foreach (Match match in urlMatches)
            {
                var start = match.Index;
                var len = match.Length;
                if (inlines.Intersect(start, len) != null)
                    continue;

                var url = message.Substring(start, len);
                if (Uri.IsWellFormedUriString(url, UriKind.Absolute))
                    inlines.Add(new Models.InlineElement(Models.InlineElementType.Link, start, len, url));
            }

            var checkedShas = new Dictionary<string, bool>();
            foreach (Match match in REG_SHA_FORMAT().Matches(message))
            {
                if (token.IsCancellationRequested)
                    break;

                var start = match.Index;
                var len = match.Length;
                if (inlines.Intersect(start, len) != null)
                    continue;

                var sha = match.Groups[1].Value;
                if (!checkedShas.TryGetValue(sha, out var isCommitSHA))
                {
                    if (checkedShas.Count >= 64)
                        continue;

                    isCommitSHA = await new Commands.IsCommitSHA(_repo.FullPath, sha).GetResultAsync().ConfigureAwait(false);
                    checkedShas.Add(sha, isCommitSHA);
                }

                if (isCommitSHA)
                    inlines.Add(new Models.InlineElement(Models.InlineElementType.CommitSHA, start, len, sha));
            }

            var inlineCodeMatches = REG_INLINECODE_FORMAT().Matches(message);
            foreach (Match match in inlineCodeMatches)
            {
                var start = match.Index;
                var len = match.Length;
                if (inlines.Intersect(start, len) != null)
                    continue;

                inlines.Add(new Models.InlineElement(Models.InlineElementType.Code, start + 1, len - 2, string.Empty));
            }

            inlines.Sort();
            return inlines;
        }

        private List<Models.Change> GetVisibleChanges(List<Models.Change> changes)
        {
            var filter = Models.FileSearch.Parse(_searchChangeFilter);
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

        private void RequestDiffContext(Models.Change selected)
        {
            using var span = StartDiffRequestDiagnosticSpan(selected);
            if (_disposed)
            {
                span.Set("result", "disposed");
                return;
            }

            if (_commit == null || selected == null)
            {
                DiffHost.Clear();
                span.Set("result", "clear");
                return;
            }

            var commit = _commit;
            var option = new Models.DiffOption(commit, selected);
            if (_commit != null && _commit.SHA.Equals(commit.SHA, StringComparison.Ordinal))
            {
                DiffHost.ShowDiff(_repo.FullPath, option, "commit_detail.select");
                span.Set("result", "requested");
            }
            else
            {
                span.Set("result", "stale_commit");
            }
        }

        private Diagnostics.DiagnosticScope StartCommitDiagnosticSpan(string name, Models.Commit commit, long requestId)
        {
            var repoPath = Diagnostics.DiagnosticManager.GetRepositoryPath(_repo?.FullPath);
            return Diagnostics.DiagnosticManager.StartSpan(
                "CommitDetail",
                name,
                Diagnostics.DiagnosticManager.CreateData(
                    ("repo", Diagnostics.DiagnosticManager.GetRepositoryId(repoPath)),
                    ("repoPath", repoPath),
                    ("commit", commit?.SHA ?? string.Empty),
                    ("requestId", requestId),
                    ("currentRequestId", _requestId),
                    ("disposed", _disposed),
                    ("activeTab", ActiveTabIndex)));
        }

        private Diagnostics.DiagnosticScope StartSelectedChangesDiagnosticSpan(List<Models.Change> changes)
        {
            var repoPath = Diagnostics.DiagnosticManager.GetRepositoryPath(_repo?.FullPath);
            var first = changes is { Count: > 0 } ? changes[0] : null;
            return Diagnostics.DiagnosticManager.StartSpan(
                "CommitDetail",
                "selected_changes.set",
                Diagnostics.DiagnosticManager.CreateData(
                    ("repo", Diagnostics.DiagnosticManager.GetRepositoryId(repoPath)),
                    ("repoPath", repoPath),
                    ("commit", _commit?.SHA ?? string.Empty),
                    ("count", changes?.Count ?? 0),
                    ("firstPath", first?.Path ?? string.Empty),
                    ("activeTab", ActiveTabIndex),
                    ("isLoading", _isCommitDetailLoading),
                    ("disposed", _disposed)));
        }

        private Diagnostics.DiagnosticScope StartDiffRequestDiagnosticSpan(Models.Change selected)
        {
            var repoPath = Diagnostics.DiagnosticManager.GetRepositoryPath(_repo?.FullPath);
            return Diagnostics.DiagnosticManager.StartSpan(
                "CommitDetail",
                "diff.request",
                Diagnostics.DiagnosticManager.CreateData(
                    ("repo", Diagnostics.DiagnosticManager.GetRepositoryId(repoPath)),
                    ("repoPath", repoPath),
                    ("commit", _commit?.SHA ?? string.Empty),
                    ("path", selected?.Path ?? string.Empty),
                    ("activeTab", ActiveTabIndex),
                    ("isLoading", _isCommitDetailLoading)));
        }

        private void RefreshVisibleChanges()
        {
            var filter = Models.FileSearch.Parse(_searchChangeFilter);
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

        private void RefreshRevisionSearchSuggestion()
        {
            var filter = _revisionFileSearchFilter;
            if (string.IsNullOrWhiteSpace(filter))
            {
                RevisionFileSearchSuggestion = null;
                IsRevisionFileSearchLoading = false;
                return;
            }

            if (_revisionFiles != null)
            {
                IsRevisionFileSearchLoading = false;
                CalcRevisionFileSearchSuggestion(filter);
                return;
            }

            RevisionFileSearchSuggestion = null;
            IsRevisionFileSearchLoading = true;
            if (_isRevisionFileNamesLoading)
                return;

            RequestRevisionFileNames();
        }

        private void RequestRevisionFileNames()
        {
            var commit = _commit;
            if (commit == null)
                return;

            var requestId = _requestId;
            var commitSHA = commit.SHA;
            var token = _cancellationSource?.Token ?? CancellationToken.None;
            _isRevisionFileNamesLoading = true;

            Task.Run(async () =>
            {
                List<string> files = [];
                try
                {
                    files = await GetRevisionFileNamesAsync(commitSHA, token).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore stale search failures.
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (requestId != _requestId ||
                        _commit == null ||
                        !_commit.SHA.Equals(commitSHA, StringComparison.Ordinal))
                    {
                        return;
                    }

                    _revisionFiles = files;
                    _isRevisionFileNamesLoading = false;
                    if (!string.IsNullOrWhiteSpace(_revisionFileSearchFilter))
                    {
                        CalcRevisionFileSearchSuggestion(_revisionFileSearchFilter);
                        IsRevisionFileSearchLoading = false;
                    }
                });
            });
        }

        private void CalcRevisionFileSearchSuggestion(string filter)
        {
            using var span = Diagnostics.DiagnosticManager.StartSpan(
                "RevisionFiles",
                "search_suggestions",
                Diagnostics.DiagnosticManager.CreateData(
                    ("repo", Diagnostics.DiagnosticManager.GetRepositoryId(_repo.FullPath)),
                    ("filterLength", filter?.Length ?? 0),
                    ("fileCount", _revisionFiles?.Count ?? 0)));

            var pattern = Models.FileSearch.Parse(filter);
            var suggestion = Models.FileSearch.FilterAndSort(_revisionFiles, pattern, 100, true);
            span.Set("suggestionCount", suggestion.Count);
            RevisionFileSearchSuggestion = suggestion;
        }

        private async Task SetViewingBlobAsync(Models.Object file, string commitSHA, long requestId)
        {
            var isBinary = await new Commands.IsBinary(_repo.FullPath, commitSHA, file.Path).GetResultAsync();
            if (isBinary)
            {
                var imgDecoder = ImageSource.GetDecoder(file.Path);
                if (imgDecoder != Models.ImageDecoder.None)
                {
                    var source = await ImageSource.FromRevisionAsync(_repo.FullPath, commitSHA, file.Path, imgDecoder);
                    if (IsCurrentRevisionFileRequest(commitSHA, file.Path, requestId))
                        ViewRevisionFileContent = new Models.RevisionImageFile(file.Path, source.Bitmap, source.Size);
                }
                else
                {
                    var size = await new Commands.QueryFileSize(_repo.FullPath, file.Path, commitSHA).GetResultAsync();
                    if (IsCurrentRevisionFileRequest(commitSHA, file.Path, requestId))
                        ViewRevisionFileContent = new Models.RevisionBinaryFile() { Size = size };
                }

                return;
            }

            var contentStream = await Commands.QueryFileContent.RunAsync(_repo.FullPath, commitSHA, file.Path);
            var content = await new StreamReader(contentStream).ReadToEndAsync();
            var lfs = Models.LFSObject.Parse(content);
            if (lfs != null)
            {
                var imgDecoder = ImageSource.GetDecoder(file.Path);
                if (IsCurrentRevisionFileRequest(commitSHA, file.Path, requestId))
                {
                    if (imgDecoder != Models.ImageDecoder.None)
                        ViewRevisionFileContent = new RevisionLFSImage(_repo.FullPath, file.Path, lfs, imgDecoder);
                    else
                        ViewRevisionFileContent = new Models.RevisionLFSObject() { Object = lfs };
                }
            }
            else if (IsCurrentRevisionFileRequest(commitSHA, file.Path, requestId))
            {
                ViewRevisionFileContent = new Models.RevisionTextFile() { FileName = file.Path, Content = content };
            }
        }

        private async Task SetViewingCommitAsync(Models.Object file, string commitSHA, long requestId)
        {
            var submoduleRoot = Path.Combine(_repo.FullPath, file.Path).Replace('\\', '/').Trim('/');
            var commit = await new Commands.QuerySingleCommit(submoduleRoot, file.SHA).GetResultAsync();
            if (!IsCurrentRevisionFileRequest(commitSHA, file.Path, requestId))
                return;

            if (commit == null)
            {
                ViewRevisionFileContent = new Models.RevisionSubmodule()
                {
                    Commit = new Models.Commit() { SHA = file.SHA },
                    FullMessage = new Models.CommitFullMessage()
                };
            }
            else
            {
                var message = await new Commands.QueryCommitFullMessage(submoduleRoot, file.SHA).GetResultAsync();
                if (!IsCurrentRevisionFileRequest(commitSHA, file.Path, requestId))
                    return;

                ViewRevisionFileContent = new Models.RevisionSubmodule()
                {
                    Commit = commit,
                    FullMessage = new Models.CommitFullMessage { Message = message }
                };
            }
        }

        private bool IsCurrentRevisionFileRequest(string commitSHA, string path, long requestId)
        {
            return !string.IsNullOrEmpty(commitSHA) &&
                requestId == _requestId &&
                _commit != null &&
                _commit.SHA.Equals(commitSHA, StringComparison.Ordinal) &&
                _viewRevisionFilePath.Equals(path ?? string.Empty, StringComparison.Ordinal);
        }

        [GeneratedRegex(@"\b(https?://|ftp://)[\w\d\._/\-~%@()+:?&=#!]*[\w\d/]")]
        private static partial Regex REG_URL_FORMAT();

        [GeneratedRegex(@"\b([0-9a-fA-F]{6,64})\b")]
        private static partial Regex REG_SHA_FORMAT();

        [GeneratedRegex(@"`.*?`")]
        private static partial Regex REG_INLINECODE_FORMAT();

        private Repository _repo = null;
        private CommitDetailSharedData _sharedData = null;
        private Models.Commit _commit = null;
        private Models.CommitFullMessage _fullMessage = null;
        private Models.CommitSignInfo _signInfo = null;
        private List<string> _children = null;
        private List<Models.Change> _changes = [];
        private List<Models.Change> _visibleChanges = [];
        private List<Models.Change> _selectedChanges = null;
        private string _searchChangeFilter = string.Empty;
        private string _viewRevisionFilePath = string.Empty;
        private object _viewRevisionFileContent = null;
        private bool _isRevisionFileContentLoading = false;
        private bool _disposed = false;
        private CancellationTokenSource _cancellationSource = null;
        private long _requestId = 0;
        private List<string> _revisionFiles = null;
        private string _revisionFileSearchFilter = string.Empty;
        private List<string> _revisionFileSearchSuggestion = null;
        private bool _isRevisionFileNamesLoading = false;
        private bool _isRevisionFileIndexLoading = false;
        private bool _isRevisionFileSearchLoading = false;
        private bool _isCommitDetailLoading = false;
        private bool _canOpenRevisionFileWithDefaultEditor = false;
        private Vector _scrollOffset = Vector.Zero;
        private Vector _revisionFileContentScrollOffset = Vector.Zero;
        private Models.Object _viewRevisionFileObject = null;
    }
}
