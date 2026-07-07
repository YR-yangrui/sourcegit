using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class FileHistoriesRevisionFile(string path, object content = null, bool canOpenWithDefaultEditor = false, string blobSHA = null)
    {
        public string Path { get; set; } = path;
        public object Content { get; set; } = content;
        public bool CanOpenWithDefaultEditor { get; set; } = canOpenWithDefaultEditor;
        public string BlobSHA { get; set; } = blobSHA;
    }

    public class FileHistoriesSingleRevisionViewMode
    {
        public bool IsDiff
        {
            get;
            set;
        } = true;
    }

    public class FileHistoriesSingleRevision : ObservableObject, IDisposable
    {
        public bool IsDiffMode
        {
            get => _viewMode.IsDiff;
            set
            {
                if (_disposed)
                    return;

                if (_viewMode.IsDiff != value)
                {
                    _viewMode.IsDiff = value;
                    RefreshViewContent();
                }
            }
        }

        public object ViewContent
        {
            get => _viewContent;
            set
            {
                var old = _viewContent;
                if (SetProperty(ref _viewContent, value) && !ReferenceEquals(old, value))
                    (old as DiffContext)?.Dispose();
            }
        }

        public DiffContentHost DiffHost { get; } = new();

        public FileHistoriesSingleRevision(string repo, Models.FileVersion revision, FileHistoriesSingleRevisionViewMode viewMode)
        {
            _repo = repo;
            _file = revision.Path;
            _revision = revision;
            _viewMode = viewMode;
            _viewContent = null;

            RefreshViewContent();
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            DiffHost.Dispose();
            ViewContent = null;
        }

        public void SetRevision(Models.FileVersion revision)
        {
            if (_disposed)
                return;

            _file = revision.Path;
            _revision = revision;
            RefreshViewContent();
        }

        public async Task<bool> ResetToSelectedRevisionAsync()
        {
            if (_disposed)
                return false;

            return await new Commands.Checkout(_repo)
                .FileWithRevisionAsync(_file, $"{_revision.SHA}")
                .ConfigureAwait(false);
        }

        public async Task OpenWithDefaultEditorAsync()
        {
            if (_disposed)
                return;

            if (_viewContent is not FileHistoriesRevisionFile { CanOpenWithDefaultEditor: true } revisionFile)
                return;

            var fullPath = Native.OS.GetAbsPath(_repo, _file);
            var openPath = await Commands.SaveRevisionFile
                .GetWorktreePathIfRevisionMatchesDiskAsync(_repo, _revision.SHA, _file, revisionFile.BlobSHA)
                .ConfigureAwait(false);

            if (string.IsNullOrEmpty(openPath))
            {
                var fileName = Path.GetFileNameWithoutExtension(fullPath) ?? "";
                var fileExt = Path.GetExtension(fullPath) ?? "";
                openPath = Path.Combine(Path.GetTempPath(), $"{fileName}~{_revision.SHA.AsSpan(0, 10)}{fileExt}");

                await Commands.SaveRevisionFile
                    .RunAsync(_repo, _revision.SHA, _file, openPath)
                    .ConfigureAwait(false);
            }

            Native.OS.OpenWithDefaultEditor(openPath);
        }

        private void RefreshViewContent()
        {
            if (_disposed)
                return;

            if (_viewMode.IsDiff)
            {
                ViewContent = DiffHost;
                DiffHost.ShowDiff(_repo, new(_revision), "file_history.single_revision");
                return;
            }

            DiffHost.Clear();
            Task.Run(async () =>
            {
                var objs = await new Commands.QueryRevisionObjects(_repo, _revision.SHA, _file)
                    .GetResultAsync()
                    .ConfigureAwait(false);

                if (objs.Count == 0)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!_disposed)
                            ViewContent = new FileHistoriesRevisionFile(_file);
                    });
                    return;
                }

                var revisionContent = await GetRevisionFileContentAsync(objs[0]).ConfigureAwait(false);
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_disposed)
                        ViewContent = revisionContent;
                });
            });
        }

        private async Task<object> GetRevisionFileContentAsync(Models.Object obj)
        {
            if (obj.Type == Models.ObjectType.Blob)
            {
                var isBinary = await new Commands.IsBinary(_repo, _revision.SHA, _file).GetResultAsync().ConfigureAwait(false);
                if (isBinary)
                {
                    var imgDecoder = ImageSource.GetDecoder(_file);
                    if (imgDecoder != Models.ImageDecoder.None)
                    {
                        var source = await ImageSource.FromRevisionAsync(_repo, _revision.SHA, _file, imgDecoder).ConfigureAwait(false);
                        var image = new Models.RevisionImageFile(_file, source.Bitmap, source.Size);
                        return new FileHistoriesRevisionFile(_file, image, true, obj.SHA);
                    }

                    var size = await new Commands.QueryFileSize(_repo, _file, _revision.SHA).GetResultAsync().ConfigureAwait(false);
                    var binaryFile = new Models.RevisionBinaryFile() { Size = size };
                    return new FileHistoriesRevisionFile(_file, binaryFile, true, obj.SHA);
                }

                var contentStream = await Commands.QueryFileContent.RunAsync(_repo, _revision.SHA, _file).ConfigureAwait(false);
                var content = await new StreamReader(contentStream).ReadToEndAsync();
                var lfs = Models.LFSObject.Parse(content);
                if (lfs != null)
                {
                    var imgDecoder = ImageSource.GetDecoder(_file);
                    if (imgDecoder != Models.ImageDecoder.None)
                    {
                        var combined = new RevisionLFSImage(_repo, _file, lfs, imgDecoder);
                        return new FileHistoriesRevisionFile(_file, combined, true, obj.SHA);
                    }

                    var rlfs = new Models.RevisionLFSObject() { Object = lfs };
                    return new FileHistoriesRevisionFile(_file, rlfs, true, obj.SHA);
                }

                var txt = new Models.RevisionTextFile() { FileName = obj.Path, Content = content };
                return new FileHistoriesRevisionFile(_file, txt, true, obj.SHA);
            }

            if (obj.Type == Models.ObjectType.Commit)
            {
                var submoduleRoot = Path.Combine(_repo, _file);
                var commit = await new Commands.QuerySingleCommit(submoduleRoot, obj.SHA).GetResultAsync().ConfigureAwait(false);
                var message = commit != null ? await new Commands.QueryCommitFullMessage(submoduleRoot, obj.SHA).GetResultAsync().ConfigureAwait(false) : null;
                var module = new Models.RevisionSubmodule()
                {
                    Commit = commit ?? new Models.Commit() { SHA = obj.SHA },
                    FullMessage = new Models.CommitFullMessage { Message = message }
                };

                return new FileHistoriesRevisionFile(_file, module);
            }

            return new FileHistoriesRevisionFile(_file);
        }

        private string _repo = null;
        private string _file = null;
        private Models.FileVersion _revision = null;
        private FileHistoriesSingleRevisionViewMode _viewMode = null;
        private object _viewContent = null;
        private bool _disposed = false;
    }

    public class FileHistoriesCompareRevisions : ObservableObject, IDisposable
    {
        public Models.FileVersion StartPoint
        {
            get => _startPoint;
            set => SetProperty(ref _startPoint, value);
        }

        public Models.FileVersion EndPoint
        {
            get => _endPoint;
            set => SetProperty(ref _endPoint, value);
        }

        public DiffContentHost ViewContent { get; } = new();

        public FileHistoriesCompareRevisions(string repo, Models.FileVersion start, Models.FileVersion end)
        {
            _repo = repo;
            _startPoint = start;
            _endPoint = end;
            ViewContent.ShowDiff(_repo, new(start, end), "file_history.compare");
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            ViewContent.Dispose();
        }

        public void Swap()
        {
            if (_disposed)
                return;

            (StartPoint, EndPoint) = (_endPoint, _startPoint);
            ViewContent.ShowDiff(_repo, new(_startPoint, _endPoint), "file_history.compare_swap");
        }

        public async Task<bool> SaveAsPatch(string saveTo)
        {
            if (_disposed)
                return false;

            return await Commands.SaveChangesAsPatch
                .ProcessRevisionCompareChangesAsync(_repo, _changes, _startPoint.SHA, _endPoint.SHA, saveTo)
                .ConfigureAwait(false);
        }

        private string _repo = null;
        private Models.FileVersion _startPoint = null;
        private Models.FileVersion _endPoint = null;
        private List<Models.Change> _changes = [];
        private bool _disposed = false;
    }

    public class FileHistories : ObservableObject, IDisposable
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

        public List<Models.FileVersion> Revisions
        {
            get => _revisions;
            set => SetProperty(ref _revisions, value);
        }

        public List<Models.FileVersion> SelectedRevisions
        {
            get => _selectedRevisions;
            set
            {
                if (_disposed)
                    return;

                if (SetProperty(ref _selectedRevisions, value))
                    RefreshViewContent();
            }
        }

        public object ViewContent
        {
            get => _viewContent;
            private set
            {
                var old = _viewContent;
                if (SetProperty(ref _viewContent, value) && !ReferenceEquals(old, value))
                    (old as IDisposable)?.Dispose();
            }
        }

        public FileHistories(Repository repo, string file, string commit = null)
        {
            Title = BuildTitle(file, commit);
            _repoPath = repo.FullPath;
            _file = file;
            _openedRevision = commit;

            InitializeScopes(repo.Branches, repo.CurrentBranch);
        }

        public FileHistories(string repo, string file, string commit = null)
        {
            Title = BuildTitle(file, commit);
            _repoPath = repo;
            _file = file;
            _openedRevision = commit;

            Task.Run(async () =>
            {
                var branches = await new Commands.QueryBranches(_repoPath)
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

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _requestId++;
            ViewContent = null;
        }

        public void NavigateToCommit(Models.FileVersion revision)
        {
            if (_disposed)
                return;

            var launcher = App.GetLauncher();
            if (launcher != null)
            {
                foreach (var page in launcher.Pages)
                {
                    if (page.Data is Repository repo && repo.FullPath.Equals(_repoPath, StringComparison.Ordinal))
                    {
                        repo.NavigateToCommit(revision.SHA);
                        break;
                    }
                }
            }
        }

        public string GetCommitFullMessage(Models.FileVersion revision)
        {
            if (_disposed)
                return string.Empty;

            var sha = revision.SHA;
            if (_fullCommitMessages.TryGetValue(sha, out var msg))
                return msg;

            msg = new Commands.QueryCommitFullMessage(_repoPath, sha).GetResult();
            _fullCommitMessages[sha] = msg;
            return msg;
        }

        private static string BuildTitle(string file, string commit)
        {
            return !string.IsNullOrEmpty(commit) ? $"{file} @ {commit}" : file;
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
            Revisions = null;
            SelectedRevisions = [];
            ViewContent = null;
            StartingPointDescription = string.Empty;
            _fullCommitMessages.Clear();

            var allBranchesStartingPoint = App.Text("FileHistory.StartingPoint.AllBranches");
            Task.Run(async () =>
            {
                var startingPoint = scope.Kind == Models.HistoryQueryScopeKind.AllBranches ?
                    allBranchesStartingPoint :
                    await BuildStartingPointDescriptionAsync(scope).ConfigureAwait(false);

                Dispatcher.UIThread.Post(() =>
                {
                    if (requestId != _requestId || scope != _selectedScope)
                        return;

                    StartingPointDescription = startingPoint;
                });

                var revisions = await new Commands.QueryFileHistory(_repoPath, _file, scope)
                    .GetResultAsync()
                    .ConfigureAwait(false);

                Dispatcher.UIThread.Post(() =>
                {
                    if (requestId != _requestId || scope != _selectedScope)
                        return;

                    IsLoading = false;
                    Revisions = revisions;
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

            var commit = await new Commands.QuerySingleCommit(_repoPath, revision)
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

        private void RefreshViewContent()
        {
            if (_disposed)
                return;

            var count = _selectedRevisions?.Count ?? 0;
            if (count == 0)
            {
                ViewContent = null;
            }
            else if (count == 1)
            {
                if (_viewContent is FileHistoriesSingleRevision single)
                    single.SetRevision(_selectedRevisions[0]);
                else
                    ViewContent = new FileHistoriesSingleRevision(_repoPath, _selectedRevisions[0], _viewMode);
            }
            else if (count == 2)
            {
                ViewContent = new FileHistoriesCompareRevisions(_repoPath, _selectedRevisions[0], _selectedRevisions[1]);
            }
            else
            {
                ViewContent = _selectedRevisions.Count;
            }
        }

        private readonly string _repoPath = null;
        private readonly string _file = null;
        private readonly string _openedRevision = null;
        private int _requestId = 0;
        private bool _isLoading = true;
        private List<Models.HistoryQueryScope> _scopes = [];
        private Models.HistoryQueryScope _selectedScope = null;
        private string _startingPointDescription = string.Empty;
        private FileHistoriesSingleRevisionViewMode _viewMode = new();
        private List<Models.FileVersion> _revisions = null;
        private List<Models.FileVersion> _selectedRevisions = [];
        private Dictionary<string, string> _fullCommitMessages = new();
        private object _viewContent = null;
        private bool _disposed = false;
    }
}
