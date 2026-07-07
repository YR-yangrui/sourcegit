using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class DiffContext : ObservableObject, IDisposable
    {
        public string Title
        {
            get;
        }

        public string RelativePath => _option.Path;

        public string FullPath => Native.OS.GetAbsPath(_repo, _option.Path);

        public string FileModeChange
        {
            get => _fileModeChange;
            private set => SetProperty(ref _fileModeChange, value);
        }

        public bool IsTextDiff
        {
            get => _isTextDiff;
            private set => SetProperty(ref _isTextDiff, value);
        }

        public bool IsIgnoreWhitespaceVisible
        {
            get => _isIgnoreWhitespaceVisible;
            private set => SetProperty(ref _isIgnoreWhitespaceVisible, value);
        }

        public bool CanRefreshCustomDiff
        {
            get => _canRefreshCustomDiff;
            private set => SetProperty(ref _canRefreshCustomDiff, value);
        }

        public bool IsRefreshingCustomDiff
        {
            get => _isRefreshingCustomDiff;
            private set => SetProperty(ref _isRefreshingCustomDiff, value);
        }

        public bool IsRetainedHtmlDiffLoading
        {
            get => _isRetainedHtmlDiffLoading;
            private set => SetProperty(ref _isRetainedHtmlDiffLoading, value);
        }

        public bool IsRetainedContentLoading
        {
            get => _isRetainedContentLoading;
            private set => SetProperty(ref _isRetainedContentLoading, value);
        }

        public object Content
        {
            get => _content;
            private set => SetProperty(ref _content, value);
        }

        public int UnifiedLines
        {
            get => _unifiedLines;
            private set => SetProperty(ref _unifiedLines, value);
        }

        public DiffContext(string repo, Models.DiffOption option, DiffContext previous = null, Action onContentLoaded = null, bool keepPreviousContent = true, bool cancelPreviousInBackground = false)
        {
            _repo = repo;
            _option = option;
            _onContentLoaded = onContentLoaded;
            RefreshCustomDiff = new App.Command(_ => RefreshCustomDiffImpl());

            if (previous != null)
            {
                _unifiedLines = previous._unifiedLines;
                if (cancelPreviousInBackground)
                    previous.CancelContentLoadInBackground(true, $"superseded_by:{_traceId}");
                else
                    previous.CancelContentLoad(true, $"superseded_by:{_traceId}");

                if (keepPreviousContent && ShouldKeepPreviousContent(previous))
                    TakePreviousContent(previous);
            }

            if (_onContentLoaded != null && _content is Models.DiffLoading)
                _content = null;

            if (string.IsNullOrEmpty(_option.OrgPath) || _option.OrgPath == "/dev/null")
                Title = _option.Path;
            else
                Title = $"{_option.OrgPath} → {_option.Path}";

            LogDiffEvent("context.create",
                ("hasPrevious", previous != null),
                ("previousTraceId", previous?._traceId ?? string.Empty),
                ("previousContentType", GetContentType(previous?._content)),
                ("previousHtmlSource", GetHtmlSource(previous?._content)));

            LoadContent("initial");
        }

        public void IncrUnified()
        {
            UnifiedLines = _unifiedLines + 1;
            LoadContent("unified.increase");
        }

        public void DecrUnified()
        {
            UnifiedLines = Math.Max(4, _unifiedLines - 1);
            LoadContent("unified.decrease");
        }

        public void OpenExternalMergeTool()
        {
            new Commands.DiffTool(_repo, _option).Open();
        }

        public bool IsSameOption(Models.DiffOption option)
        {
            return _option.IsSame(option);
        }

        public void Reload(string reason)
        {
            LoadContent(reason);
        }

        public App.Command RefreshCustomDiff
        {
            get;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            CancelContentLoad(true, "dispose");
            ReleaseCustomDiffCacheLease();
        }

        public void DisposeInBackground()
        {
            if (_disposed)
                return;

            _disposed = true;
            CancelContentLoadInBackground(true, "dispose_background");
            ReleaseCustomDiffCacheLeaseInBackground();
        }

        private void RefreshCustomDiffImpl()
        {
            if (_canRefreshCustomDiff || !string.IsNullOrEmpty(_customRendererKey) || !string.IsNullOrEmpty(_loadingCustomRendererKey))
            {
                IsRefreshingCustomDiff = true;
                LoadContent("custom_renderer.refresh", true);
            }
        }

        public void CheckSettings()
        {
            var pref = Preferences.Instance;
            var renderer = pref.EnableCustomDiffRenderers ? pref.FindCustomDiffRenderer(_option.Path) : null;
            var rendererKey = renderer?.Identity ?? string.Empty;
            var activeRendererKey = !string.IsNullOrEmpty(_customRendererKey) ? _customRendererKey : _loadingCustomRendererKey;
            if (!string.IsNullOrEmpty(activeRendererKey) || !string.IsNullOrEmpty(rendererKey))
            {
                var reload = !activeRendererKey.Equals(rendererKey, StringComparison.Ordinal);
                LogDiffEvent("check_settings.custom_renderer",
                    ("rendererName", renderer?.Name ?? string.Empty),
                    ("hasCompletedRenderer", !string.IsNullOrEmpty(_customRendererKey)),
                    ("hasLoadingRenderer", !string.IsNullOrEmpty(_loadingCustomRendererKey)),
                    ("hasCurrentRenderer", !string.IsNullOrEmpty(rendererKey)),
                    ("reload", reload));

                if (reload)
                    LoadContent("settings.custom_renderer");

                return;
            }

            if (Content is TextDiffContext ctx)
            {
                if (_info == null ||
                    (pref.UseFullTextDiff && _info.UnifiedLines != _entireFileLines) ||
                    (!pref.UseFullTextDiff && _info.UnifiedLines == _entireFileLines) ||
                    (pref.IgnoreWhitespaceChangesInDiff != _info.IgnoreWhitespace) ||
                    (pref.IgnoreCRAtEOLInDiff != _info.IgnoreCRAtEOL))
                {
                    LogDiffEvent("check_settings.text_reload",
                        ("useFullTextDiff", pref.UseFullTextDiff),
                        ("ignoreWhitespace", pref.IgnoreWhitespaceChangesInDiff),
                        ("ignoreCRAtEOL", pref.IgnoreCRAtEOLInDiff),
                        ("infoUnifiedLines", _info?.UnifiedLines ?? -1),
                        ("infoIgnoreWhitespace", _info?.IgnoreWhitespace ?? false),
                        ("infoIgnoreCRAtEOL", _info?.IgnoreCRAtEOL ?? false));

                    LoadContent("settings.text");
                    return;
                }

                if (ctx.IsSideBySide() != pref.UseSideBySideDiff)
                {
                    LogDiffEvent("check_settings.switch_text_mode",
                        ("useSideBySide", pref.UseSideBySideDiff));

                    SetContent(ctx.SwitchMode());
                }
            }
            else if (Content is Models.NoOrEOLChange)
            {
                if (_info == null ||
                    (pref.UseFullTextDiff && _info.UnifiedLines != _entireFileLines) ||
                    (!pref.UseFullTextDiff && _info.UnifiedLines == _entireFileLines) ||
                    pref.IgnoreWhitespaceChangesInDiff != _info.IgnoreWhitespace ||
                    pref.IgnoreCRAtEOLInDiff != _info.IgnoreCRAtEOL)
                {
                    LogDiffEvent("check_settings.no_or_eol_reload",
                        ("useFullTextDiff", pref.UseFullTextDiff),
                        ("ignoreWhitespace", pref.IgnoreWhitespaceChangesInDiff),
                        ("ignoreCRAtEOL", pref.IgnoreCRAtEOLInDiff),
                        ("infoUnifiedLines", _info?.UnifiedLines ?? -1),
                        ("infoIgnoreWhitespace", _info?.IgnoreWhitespace ?? false),
                        ("infoIgnoreCRAtEOL", _info?.IgnoreCRAtEOL ?? false));

                    LoadContent("settings.no_or_eol");
                }
            }
        }

        private void LoadContent(string reason, bool forceCustomRenderer = false)
        {
            var requestId = ++_contentRequestId;
            LogDiffEvent("load.start",
                ("reason", reason),
                ("requestId", requestId));
            if (_option.Path.EndsWith('/'))
            {
                CancelContentLoad(false, "directory_path");
                SetContent(null);
                _info = null;
                _customRendererKey = string.Empty;
                _loadingCustomRendererKey = string.Empty;
                CanRefreshCustomDiff = false;
                IsTextDiff = false;
                LogDiffEvent("load.directory_path",
                    ("requestId", requestId));
                _onContentLoaded?.Invoke();
                return;
            }

            var cancellation = BeginContentLoad();
            var cancellationToken = cancellation.Token;
            _loadingCustomRendererKey = GetCurrentCustomRendererKey();
            var previousTextContext = _content as TextDiffContext;

            Task.Run(async () =>
            {
                try
                {
                    var pref = Preferences.Instance;
                    var numLines = pref.UseFullTextDiff ? _entireFileLines : _unifiedLines;
                    var ignoreWhitespace = pref.IgnoreWhitespaceChangesInDiff;
                    var ignoreCRAtEOL = pref.IgnoreCRAtEOLInDiff;
                    var renderer = pref.EnableCustomDiffRenderers ? pref.FindCustomDiffRenderer(_option.Path) : null;
                    var rendererKey = renderer?.Identity ?? string.Empty;

                    if (renderer != null)
                    {
                        LogDiffEvent("load.custom_renderer.start",
                            ("requestId", requestId),
                            ("rendererName", renderer.Name),
                            ("rendererExecutable", renderer.Executable));

                        var renderCommand = new Commands.RenderCustomDiff(_repo, _option, renderer, Title);
                        Commands.RenderCustomDiff.PreparedInput preparedInput = null;

                        try
                        {
                            var metadataCommand = new Commands.Diff(_repo, _option, 0, ignoreWhitespace, ignoreCRAtEOL)
                            {
                                CancellationToken = cancellationToken,
                                CanAbortProcessOnCancel = true,
                                DisableLazyFetchOnAbort = false,
                            };
                            var metadata = await metadataCommand.ReadAsync().ConfigureAwait(false);
                            cancellationToken.ThrowIfCancellationRequested();

                            preparedInput = await renderCommand.PrepareInputAsync(cancellationToken).ConfigureAwait(false);
                            cancellationToken.ThrowIfCancellationRequested();

                            var inputFingerprint = preparedInput?.Fingerprint ?? string.Empty;
                            var hasInputFingerprint = !string.IsNullOrEmpty(inputFingerprint);
                            var metadataInfo = new Info(_option, 0, ignoreWhitespace, ignoreCRAtEOL, metadata, inputFingerprint);
                            var cacheKey = BuildCustomDiffCacheKey(_repo, _option, Title, rendererKey, metadataInfo);

                            if (!forceCustomRenderer && hasInputFingerprint && _customDiffCache.TryAcquire(cacheKey, out var cacheLease))
                            {
                                var cached = cacheLease.Entry;
                                if (!IsCachedCustomDiffContentAvailable(cached))
                                {
                                    cacheLease.Dispose();
                                    _customDiffCache.Remove(cacheKey);
                                }
                                else
                                {
                                    preparedInput?.Dispose();
                                    preparedInput = null;

                                    Dispatcher.UIThread.Post(() =>
                                    {
                                        var canceled = cancellationToken.IsCancellationRequested;
                                        var staleRequest = requestId != _contentRequestId;
                                        var rendererChanged = !rendererKey.Equals(GetCurrentCustomRendererKey(), StringComparison.Ordinal);
                                        if (canceled || staleRequest || rendererChanged)
                                        {
                                            cacheLease.Dispose();
                                            LogDiffEvent("load.custom_renderer.skip_cache_hit_stale",
                                                ("requestId", requestId),
                                                ("canceled", canceled),
                                                ("staleRequest", staleRequest),
                                                ("rendererChanged", rendererChanged),
                                                ("resultType", GetContentType(cached.Content)),
                                                ("htmlSource", GetHtmlSource(cached.Content)));
                                            return;
                                        }

                                        _info = metadataInfo;
                                        _customRendererKey = rendererKey;
                                        _loadingCustomRendererKey = string.Empty;
                                        FileModeChange = cached.FileModeChange;
                                        IsTextDiff = false;
                                        IsIgnoreWhitespaceVisible = false;
                                        CanRefreshCustomDiff = true;
                                        SetContent(cached.Content, cacheLease);

                                        LogDiffEvent("load.custom_renderer.cache_hit",
                                            ("requestId", requestId),
                                            ("rendererName", renderer.Name),
                                            ("resultType", GetContentType(cached.Content)),
                                            ("htmlSource", GetHtmlSource(cached.Content)),
                                            ("fileModeChange", FileModeChange));
                                    });

                                    return;
                                }
                            }

                            if (Models.CustomDiffCache.ShouldSkipSameContent(
                                forceCustomRenderer,
                                _content is Models.HtmlDiff,
                                _info != null,
                                _customRendererKey.Equals(rendererKey, StringComparison.Ordinal),
                                hasInputFingerprint && _info != null && metadataInfo.IsSame(_info),
                                IsCustomDiffContentAvailable(_content)))
                            {
                                preparedInput?.Dispose();
                                preparedInput = null;

                                Dispatcher.UIThread.Post(() =>
                                {
                                    var canceled = cancellationToken.IsCancellationRequested;
                                    var staleRequest = requestId != _contentRequestId;
                                    var rendererChanged = !rendererKey.Equals(GetCurrentCustomRendererKey(), StringComparison.Ordinal);
                                    if (canceled || staleRequest || rendererChanged)
                                    {
                                        LogDiffEvent("load.custom_renderer.skip_same_stale",
                                            ("requestId", requestId),
                                            ("canceled", canceled),
                                            ("staleRequest", staleRequest),
                                            ("rendererChanged", rendererChanged));
                                        return;
                                    }

                                    _info = metadataInfo;
                                    _loadingCustomRendererKey = string.Empty;
                                    FileModeChange = metadataInfo.FileModeChange;
                                    CanRefreshCustomDiff = true;
                                    ClearRetainedLoading();

                                    LogDiffEvent("load.custom_renderer.skip_same",
                                        ("requestId", requestId),
                                        ("rendererName", renderer.Name),
                                        ("htmlSource", GetHtmlSource(_content)),
                                        ("fileModeChange", FileModeChange));
                                });

                                return;
                            }

                            var rs = await renderCommand.RunAsync(cancellationToken, preparedInput)
                                .ConfigureAwait(false);
                            preparedInput = null;

                            Dispatcher.UIThread.Post(() =>
                            {
                                var canceled = cancellationToken.IsCancellationRequested;
                                var staleRequest = requestId != _contentRequestId;
                                var rendererChanged = !rendererKey.Equals(GetCurrentCustomRendererKey(), StringComparison.Ordinal);
                                if (canceled || staleRequest || rendererChanged)
                                {
                                    _ = Task.Run(() => Models.CustomDiffCache.ReleaseTemporaryContent(rs));
                                    LogDiffEvent("load.custom_renderer.skip_ui",
                                        ("requestId", requestId),
                                        ("canceled", canceled),
                                        ("staleRequest", staleRequest),
                                        ("rendererChanged", rendererChanged),
                                        ("resultType", GetContentType(rs)),
                                        ("htmlSource", GetHtmlSource(rs)));
                                    return;
                                }

                                _info = metadataInfo;
                                _customRendererKey = rendererKey;
                                _loadingCustomRendererKey = string.Empty;
                                FileModeChange = metadataInfo.FileModeChange;
                                IsTextDiff = false;
                                IsIgnoreWhitespaceVisible = false;
                                CanRefreshCustomDiff = true;

                                Models.CustomDiffCacheLease cacheLease = null;
                                if (hasInputFingerprint && IsCacheableCustomDiffContent(rs))
                                {
                                    cacheLease = _customDiffCache.PutAndAcquire(cacheKey, new Models.CustomDiffCacheEntry
                                    {
                                        Content = rs,
                                        FileModeChange = metadataInfo.FileModeChange,
                                    });
                                }

                                SetContent(rs, cacheLease);

                                LogDiffEvent("load.custom_renderer.content_set",
                                    ("requestId", requestId),
                                    ("rendererName", renderer.Name),
                                    ("resultType", GetContentType(rs)),
                                    ("htmlSource", GetHtmlSource(rs)),
                                    ("fileModeChange", FileModeChange));
                            });

                            return;
                        }
                        finally
                        {
                            preparedInput?.Dispose();
                        }
                    }

                    var diffCommand = new Commands.Diff(_repo, _option, numLines, ignoreWhitespace, ignoreCRAtEOL)
                    {
                        CancellationToken = cancellationToken,
                        CanAbortProcessOnCancel = true,
                        DisableLazyFetchOnAbort = false,
                    };
                    var latest = await diffCommand.ReadAsync().ConfigureAwait(false);

                    await ApplyDiffResultAsync(latest, numLines, ignoreWhitespace, ignoreCRAtEOL, requestId, cancellationToken, previousTextContext).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    LogDiffEvent("load.canceled",
                        ("requestId", requestId));
                }
                finally
                {
                    ClearContentLoad(cancellation);

                    cancellation.Dispose();

                    LogDiffEvent("load.finished",
                        ("requestId", requestId),
                        ("isCurrentRequest", requestId == _contentRequestId),
                        ("contentType", GetContentType(_content)),
                        ("htmlSource", GetHtmlSource(_content)));

                    if (_onContentLoaded != null && requestId == _contentRequestId)
                        Dispatcher.UIThread.Post(_onContentLoaded);

                    Dispatcher.UIThread.Post(() =>
                    {
                        if (requestId == _contentRequestId)
                            IsRefreshingCustomDiff = false;
                    });
                }
            });
        }

        private CancellationTokenSource BeginContentLoad()
        {
            CancellationTokenSource previous = null;
            CancellationTokenSource cancellation = null;
            lock (_contentCancellationLock)
            {
                if (_contentCancellation != null)
                    LogDiffEvent("load.cancel",
                        ("reason", "begin_new_load"),
                        ("invalidatePendingCallbacks", false),
                        ("hasCancellation", true));

                previous = _contentCancellation;
                cancellation = new CancellationTokenSource();
                _contentCancellation = cancellation;
            }

            CancelDetachedContentLoadInBackground(previous, "begin_new_load");
            return cancellation;
        }

        private void CancelContentLoad(bool invalidatePendingCallbacks, string reason)
        {
            var cancellation = DetachContentLoad(invalidatePendingCallbacks, reason);
            CancelDetachedContentLoad(cancellation);
        }

        private void CancelContentLoadInBackground(bool invalidatePendingCallbacks, string reason)
        {
            var cancellation = DetachContentLoad(invalidatePendingCallbacks, reason);
            CancelDetachedContentLoadInBackground(cancellation, reason);
        }

        private CancellationTokenSource DetachContentLoad(bool invalidatePendingCallbacks, string reason)
        {
            lock (_contentCancellationLock)
            {
                if (invalidatePendingCallbacks)
                    _contentRequestId++;

                LogDiffEvent("load.cancel",
                    ("reason", reason),
                    ("invalidatePendingCallbacks", invalidatePendingCallbacks),
                    ("hasCancellation", _contentCancellation != null));

                var cancellation = _contentCancellation;
                _contentCancellation = null;
                return cancellation;
            }
        }

        private void CancelDetachedContentLoad(CancellationTokenSource cancellation)
        {
            if (cancellation == null)
                return;

            try
            {
                cancellation.Cancel();
            }
            catch
            {
                // Ignore cancellation races with already disposed sources.
            }
        }

        private void CancelDetachedContentLoadInBackground(CancellationTokenSource cancellation, string reason)
        {
            if (cancellation == null)
                return;

            _ = Task.Run(() =>
            {
                using var span = Diagnostics.DiagnosticManager.StartSpan(
                    "Diff.Context",
                    "cancel.background",
                    Diagnostics.DiagnosticManager.CreateData(
                        ("repo", Diagnostics.DiagnosticManager.GetRepositoryId(Diagnostics.DiagnosticManager.GetRepositoryPath(_repo))),
                        ("path", _option.Path),
                        ("traceId", _traceId),
                        ("reason", reason)));

                CancelDetachedContentLoad(cancellation);
            });
        }

        private void ClearContentLoad(CancellationTokenSource cancellation)
        {
            lock (_contentCancellationLock)
            {
                if (ReferenceEquals(_contentCancellation, cancellation))
                    _contentCancellation = null;
            }
        }

        private async Task ApplyDiffResultAsync(Models.DiffResult latest, int numLines, bool ignoreWhitespace, bool ignoreCRAtEOL, int requestId, CancellationToken cancellationToken, TextDiffContext previousTextContext)
        {
            if (latest == null)
                return;

            await Task.Run(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var info = new Info(_option, numLines, ignoreWhitespace, ignoreCRAtEOL, latest);

                object rs = null;
                if (Models.StructuredDiffBuilder.CanHandle(_option.Path))
                {
                    var structured = await Models.StructuredDiffBuilder.BuildAsync(_repo, _option).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (structured != null)
                        rs = new StructuredDiffContext(structured, _option, latest.TextDiff);
                }

                if (rs == null)
                {
                    if (latest.TextDiff != null)
                    {
                        var count = latest.TextDiff.Lines.Count;
                        var isSubmodule = false;
                        if (count <= 3)
                        {
                            var submoduleDiff = new Models.SubmoduleDiff();
                            var submoduleRoot = $"{_repo}/{_option.Path}".Replace('\\', '/').TrimEnd('/');
                            isSubmodule = true;
                            for (int i = 1; i < count; i++)
                            {
                                var line = latest.TextDiff.Lines[i];
                                if (!line.Content.StartsWith("Subproject commit ", StringComparison.Ordinal))
                                {
                                    isSubmodule = false;
                                    break;
                                }

                                var sha = line.Content.Substring(18);
                                if (line.Type == Models.TextDiffLineType.Added)
                                    submoduleDiff.New = await QuerySubmoduleRevisionAsync(submoduleRoot, sha).ConfigureAwait(false);
                                else if (line.Type == Models.TextDiffLineType.Deleted)
                                    submoduleDiff.Old = await QuerySubmoduleRevisionAsync(submoduleRoot, sha).ConfigureAwait(false);
                            }

                            if (isSubmodule)
                            {
                                submoduleDiff.FullPath = submoduleRoot;
                                rs = submoduleDiff;
                            }
                        }

                        if (!isSubmodule)
                            rs = latest.TextDiff;
                    }
                    else if (latest.IsBinary)
                    {
                        var oldPath = string.IsNullOrEmpty(_option.OrgPath) ? _option.Path : _option.OrgPath;
                        var imgDecoder = ImageSource.GetDecoder(_option.Path);

                        if (imgDecoder != Models.ImageDecoder.None)
                        {
                            var imgDiff = new Models.ImageDiff();

                            if (_option.Revisions.Count == 2)
                            {
                                var oldImage = await ImageSource.FromRevisionAsync(_repo, _option.Revisions[0], oldPath, imgDecoder).ConfigureAwait(false);
                                var newImage = await ImageSource.FromRevisionAsync(_repo, _option.Revisions[1], _option.Path, imgDecoder).ConfigureAwait(false);
                                imgDiff.Old = oldImage.Bitmap;
                                imgDiff.OldFileSize = oldImage.Size;
                                imgDiff.New = newImage.Bitmap;
                                imgDiff.NewFileSize = newImage.Size;
                            }
                            else
                            {
                                if (!oldPath.Equals("/dev/null", StringComparison.Ordinal))
                                {
                                    var oldImage = await ImageSource.FromRevisionAsync(_repo, "HEAD", oldPath, imgDecoder).ConfigureAwait(false);
                                    imgDiff.Old = oldImage.Bitmap;
                                    imgDiff.OldFileSize = oldImage.Size;
                                }

                                var fullPath = Path.Combine(_repo, _option.Path);
                                if (File.Exists(fullPath))
                                {
                                    var newImage = await ImageSource.FromFileAsync(fullPath, imgDecoder).ConfigureAwait(false);
                                    imgDiff.New = newImage.Bitmap;
                                    imgDiff.NewFileSize = newImage.Size;
                                }
                            }

                            rs = imgDiff;
                        }
                        else
                        {
                            var binaryDiff = new Models.BinaryDiff();
                            if (_option.Revisions.Count == 2)
                            {
                                binaryDiff.OldSize = await new Commands.QueryFileSize(_repo, oldPath, _option.Revisions[0]).GetResultAsync().ConfigureAwait(false);
                                binaryDiff.NewSize = await new Commands.QueryFileSize(_repo, _option.Path, _option.Revisions[1]).GetResultAsync().ConfigureAwait(false);
                            }
                            else
                            {
                                var fullPath = Path.Combine(_repo, _option.Path);
                                binaryDiff.OldSize = await new Commands.QueryFileSize(_repo, oldPath, "HEAD").GetResultAsync().ConfigureAwait(false);
                                binaryDiff.NewSize = File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0;
                            }
                            rs = binaryDiff;
                        }
                    }
                    else if (latest.IsLFS)
                    {
                        var imgDecoder = ImageSource.GetDecoder(_option.Path);
                        if (imgDecoder != Models.ImageDecoder.None)
                            rs = new LFSImageDiff(_repo, latest.LFSDiff, imgDecoder);
                        else
                            rs = latest.LFSDiff;
                    }
                    else if (IsEmptyFileHash(latest.OldHash) || IsEmptyFileHash(latest.NewHash))
                    {
                        rs = new Models.EmptyFile();
                    }
                    else
                    {
                        rs = new Models.NoOrEOLChange();
                    }
                }

                if (rs is Models.TextDiff textDiff)
                {
                    rs = Preferences.Instance.UseSideBySideDiff ?
                        new TwoSideTextDiff(_option, textDiff, previousTextContext) :
                        new CombinedTextDiff(_option, textDiff, previousTextContext);
                }

                Dispatcher.UIThread.Post(() =>
                {
                    var canceled = cancellationToken.IsCancellationRequested;
                    var staleRequest = requestId != _contentRequestId;
                    var hasCustomRenderer = !string.IsNullOrEmpty(GetCurrentCustomRendererKey());
                    if (canceled || staleRequest || hasCustomRenderer)
                    {
                        LogDiffEvent("load.text.skip_ui",
                            ("requestId", requestId),
                            ("canceled", canceled),
                            ("staleRequest", staleRequest),
                            ("hasCustomRenderer", hasCustomRenderer),
                            ("resultType", GetContentType(rs)));
                        return;
                    }

                    if (string.IsNullOrEmpty(_customRendererKey) && _info != null && info.IsSame(_info))
                    {
                        ClearRetainedLoading();
                        LogDiffEvent("load.text.skip_same",
                            ("requestId", requestId),
                            ("resultType", GetContentType(rs)));
                        return;
                    }

                    _info = info;
                    _customRendererKey = string.Empty;
                    _loadingCustomRendererKey = string.Empty;
                    CanRefreshCustomDiff = false;
                    FileModeChange = latest.FileModeChange;

                    if (rs is TextDiffContext)
                    {
                        IsTextDiff = true;
                        IsIgnoreWhitespaceVisible = true;
                        SetContent(rs);
                    }
                    else
                    {
                        IsTextDiff = false;
                        IsIgnoreWhitespaceVisible = (rs is Models.NoOrEOLChange);
                        SetContent(rs);
                    }

                    LogDiffEvent("load.text.content_set",
                        ("requestId", requestId),
                        ("resultType", GetContentType(rs)),
                        ("fileModeChange", FileModeChange));
                });
            }).ConfigureAwait(false);
        }

        private string GetCurrentCustomRendererKey()
        {
            var pref = Preferences.Instance;
            var renderer = pref.EnableCustomDiffRenderers ? pref.FindCustomDiffRenderer(_option.Path) : null;
            return renderer?.Identity ?? string.Empty;
        }

        private void SetContent(object content, Models.CustomDiffCacheLease cacheLease = null)
        {
            var oldLease = _customDiffCacheLease;
            _customDiffCacheLease = cacheLease;
            ClearRetainedLoading();
            Content = content;
            oldLease?.Dispose();
        }

        private void ClearRetainedLoading()
        {
            IsRetainedHtmlDiffLoading = false;
            IsRetainedContentLoading = false;
        }

        private bool ShouldKeepPreviousContent(DiffContext previous)
        {
            if (previous._content is null or Models.DiffLoading)
                return false;

            var pref = Preferences.Instance;
            var renderer = pref.EnableCustomDiffRenderers ? pref.FindCustomDiffRenderer(_option.Path) : null;
            var previousRendererKey = previous.GetActiveCustomRendererKey();

            if (renderer == null)
                return string.IsNullOrEmpty(previousRendererKey) && !IsCustomDiffContent(previous._content);

            return !renderer.ClearPreviousContentOnLoad &&
                previousRendererKey.Equals(renderer.Identity, StringComparison.Ordinal) &&
                IsCustomDiffContent(previous._content);
        }

        private void TakePreviousContent(DiffContext previous)
        {
            _content = previous._content;
            _customDiffCacheLease = previous._customDiffCacheLease;
            previous._customDiffCacheLease = null;
            _fileModeChange = previous._fileModeChange;
            _isTextDiff = previous._isTextDiff;
            _isIgnoreWhitespaceVisible = previous._isIgnoreWhitespaceVisible;
            _canRefreshCustomDiff = previous._canRefreshCustomDiff;
            IsRetainedHtmlDiffLoading = _content is Models.HtmlDiff;
            IsRetainedContentLoading = _onContentLoaded == null && _content is not Models.HtmlDiff;
        }

        private string GetActiveCustomRendererKey()
        {
            return !string.IsNullOrEmpty(_customRendererKey) ? _customRendererKey : _loadingCustomRendererKey;
        }

        private static bool IsCustomDiffContent(object content)
        {
            return content is Models.HtmlDiff or Models.CustomDiffEmpty or Models.CustomDiffError;
        }

        private void ReleaseCustomDiffCacheLease()
        {
            var lease = DetachCustomDiffCacheLease();
            lease?.Dispose();
        }

        private void ReleaseCustomDiffCacheLeaseInBackground()
        {
            var lease = DetachCustomDiffCacheLease();
            if (lease == null)
                return;

            _ = Task.Run(() =>
            {
                try
                {
                    lease.Dispose();
                }
                catch (Exception e)
                {
                    Native.OS.LogException(e);
                }
            });
        }

        private Models.CustomDiffCacheLease DetachCustomDiffCacheLease()
        {
            var lease = _customDiffCacheLease;
            _customDiffCacheLease = null;
            return lease;
        }

        private void LogDiffEvent(string name, params (string Key, object Value)[] values)
        {
            if (!Diagnostics.DiagnosticManager.IsEnabled)
                return;

            var repoPath = Diagnostics.DiagnosticManager.GetRepositoryPath(_repo);
            var data = new List<(string Key, object Value)>
            {
                ("repo", Diagnostics.DiagnosticManager.GetRepositoryId(repoPath)),
                ("repoPath", repoPath),
                ("traceId", _traceId),
                ("path", _option.Path),
                ("title", Title ?? string.Empty),
                ("currentRequestId", _contentRequestId),
                ("contentType", GetContentType(_content)),
                ("currentHtmlSource", GetHtmlSource(_content)),
            };

            if (values != null)
                data.AddRange(values);

            Diagnostics.DiagnosticManager.Info(
                "Diff.Context",
                $"diff.{name}",
                string.Empty,
                Diagnostics.DiagnosticManager.CreateData(data.ToArray()));
        }

        private static string GetContentType(object content)
        {
            return content switch
            {
                null => string.Empty,
                Models.HtmlDiff => nameof(Models.HtmlDiff),
                TextDiffContext => nameof(TextDiffContext),
                _ => content.GetType().Name,
            };
        }

        private static string GetHtmlSource(object content)
        {
            return content is Models.HtmlDiff html ? html.Source?.LocalPath ?? html.Source?.ToString() ?? string.Empty : string.Empty;
        }

        private static string BuildCustomDiffCacheKey(string repo, Models.DiffOption option, string title, string rendererKey, Info info)
        {
            return string.Join('\n',
                repo,
                rendererKey,
                option.Context,
                option.CustomDiffMode,
                option.IsLocalChange ? "local" : "revision",
                option.IsUnstaged ? "unstaged" : "staged",
                title,
                info.Argument,
                info.IgnoreWhitespace ? "ignore-ws" : "keep-ws",
                info.IgnoreCRAtEOL ? "ignore-cr-at-eol" : "keep-cr-at-eol",
                info.OldHash,
                info.NewHash,
                info.FileModeChange,
                info.InputFingerprint);
        }

        private static bool IsCacheableCustomDiffContent(object content)
        {
            return content is Models.HtmlDiff or Models.CustomDiffEmpty;
        }

        private static bool IsCachedCustomDiffContentAvailable(Models.CustomDiffCacheEntry entry)
        {
            return IsCustomDiffContentAvailable(entry.Content);
        }

        private static bool IsCustomDiffContentAvailable(object content)
        {
            if (content is Models.HtmlDiff { Source: { IsFile: true } source })
                return File.Exists(source.LocalPath);

            if (content is Models.HtmlDiff html)
                return html.Source != null;

            return content is Models.CustomDiffEmpty;
        }

        private async Task<Models.RevisionSubmodule> QuerySubmoduleRevisionAsync(string repo, string sha)
        {
            if (!File.Exists(Path.Combine(repo, ".git")))
                return new Models.RevisionSubmodule() { Commit = new Models.Commit() { SHA = sha } };

            var uncommittedChangesCount = 0;
            if (sha.EndsWith("-dirty", StringComparison.Ordinal))
            {
                sha = sha.Substring(0, sha.Length - 6);
                uncommittedChangesCount = await new Commands.CountLocalChanges(repo, true).GetResultAsync().ConfigureAwait(false);
            }

            var commit = await new Commands.QuerySingleCommit(repo, sha).GetResultAsync().ConfigureAwait(false);
            if (commit == null)
                return new Models.RevisionSubmodule() { Commit = new Models.Commit() { SHA = sha } };

            var body = await new Commands.QueryCommitFullMessage(repo, sha).GetResultAsync().ConfigureAwait(false);
            return new Models.RevisionSubmodule()
            {
                Commit = commit,
                FullMessage = new Models.CommitFullMessage { Message = body },
                UncommittedChanges = uncommittedChangesCount
            };
        }

        private bool IsEmptyFileHash(string hash)
        {
            if (string.IsNullOrEmpty(hash))
                return false;

            if (hash.Length == 40)
                return hash.Equals(Models.EmptyFile.SHA1, StringComparison.Ordinal);

            if (hash.Length == 64)
                return hash.Equals(Models.EmptyFile.SHA256, StringComparison.Ordinal);

            return false;
        }

        private class Info
        {
            public string Argument { get; }
            public int UnifiedLines { get; }
            public bool IgnoreWhitespace { get; }
            public bool IgnoreCRAtEOL { get; }
            public string OldHash { get; }
            public string NewHash { get; }
            public string FileModeChange { get; }
            public string InputFingerprint { get; }

            public Info(Models.DiffOption option, int unifiedLines, bool ignoreWhitespace, bool ignoreCRAtEOL, Models.DiffResult result, string inputFingerprint = "")
            {
                Argument = option.ToString();
                UnifiedLines = unifiedLines;
                IgnoreWhitespace = ignoreWhitespace;
                IgnoreCRAtEOL = ignoreCRAtEOL;
                OldHash = result?.OldHash ?? string.Empty;
                NewHash = result?.NewHash ?? string.Empty;
                FileModeChange = result?.FileModeChange ?? string.Empty;
                InputFingerprint = inputFingerprint;
            }

            public bool IsSame(Info other)
            {
                return Argument.Equals(other.Argument, StringComparison.Ordinal) &&
                    UnifiedLines == other.UnifiedLines &&
                    IgnoreWhitespace == other.IgnoreWhitespace &&
                    IgnoreCRAtEOL == other.IgnoreCRAtEOL &&
                    OldHash.Equals(other.OldHash, StringComparison.Ordinal) &&
                    NewHash.Equals(other.NewHash, StringComparison.Ordinal) &&
                    FileModeChange.Equals(other.FileModeChange, StringComparison.Ordinal) &&
                    InputFingerprint.Equals(other.InputFingerprint, StringComparison.Ordinal);
            }
        }

        private static readonly Models.CustomDiffCache _customDiffCache = Models.CustomDiffCache.Shared;
        private readonly int _entireFileLines = 999999999;
        private readonly string _repo;
        private readonly Models.DiffOption _option = null;
        private readonly string _traceId = Guid.NewGuid().ToString("N").Substring(0, 8);
        private string _fileModeChange = string.Empty;
        private int _unifiedLines = 4;
        private bool _isTextDiff = false;
        private bool _isIgnoreWhitespaceVisible = false;
        private bool _canRefreshCustomDiff = false;
        private bool _isRefreshingCustomDiff = false;
        private bool _isRetainedHtmlDiffLoading = false;
        private bool _isRetainedContentLoading = false;
        private bool _disposed = false;
        private object _content = new Models.DiffLoading();
        private Info _info = null;
        private string _customRendererKey = string.Empty;
        private string _loadingCustomRendererKey = string.Empty;
        private Models.CustomDiffCacheLease _customDiffCacheLease = null;
        private CancellationTokenSource _contentCancellation = null;
        private readonly object _contentCancellationLock = new object();
        private int _contentRequestId = 0;
        private readonly Action _onContentLoaded = null;
    }
}
