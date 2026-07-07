using System;

using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class DiffContentHost : ObservableObject, IDisposable
    {
        public object ActiveContent
        {
            get => _activeContent;
            private set
            {
                var old = _activeContent;
                var changed = SetProperty(ref _activeContent, value);
                if (changed && !ReferenceEquals(old, value))
                {
                    DisposeContent(old);
                    OnPropertyChanged(nameof(ActiveDiffContext));
                    OnPropertyChanged(nameof(HasActiveContent));
                    OnPropertyChanged(nameof(IsWelcomeVisible));
                }
            }
        }

        public DiffContext ActiveDiffContext => _activeContent as DiffContext;

        public bool HasActiveContent => _activeContent != null;

        public bool IsWelcomeVisible => _activeContent == null && !_isLoading;

        public bool IsLoading
        {
            get => _isLoading;
            private set
            {
                if (SetProperty(ref _isLoading, value))
                    OnPropertyChanged(nameof(IsWelcomeVisible));
            }
        }

        public void ShowContent(object content)
        {
            if (_disposed)
                return;

            CancelDebouncedDiff();
            CancelPending();
            IsLoading = false;
            ActiveContent = content;
        }

        public void Clear()
        {
            CancelDebouncedDiff();
            CancelPending();
            IsLoading = false;
            ActiveContent = null;
        }

        public void ShowDiff(string repo, Models.DiffOption option, string reason = "diff.select", bool forceClearPreviousContent = false)
        {
            if (_disposed)
                return;

            if (_activeContent is DiffContext current && current.IsSameOption(option))
            {
                CancelDebouncedDiff();
                CancelPending();
                IsLoading = false;
                current.Reload(reason);
                return;
            }

            if (_pendingDiffContext != null && _pendingDiffContext.IsSameOption(option))
                return;

            if (_debouncedDiff != null && _debouncedDiff.Option.IsSame(option))
                return;

            CancelDebouncedDiff();
            CancelPending();

            var renderer = Preferences.Instance.EnableCustomDiffRenderers ? Preferences.Instance.FindCustomDiffRenderer(option.Path) : null;
            var clearPreviousContent = forceClearPreviousContent || renderer is { ClearPreviousContentOnLoad: true };
            var debounceId = ++_debounceRequestId;

            _lastDiffSelectionAt = DateTime.UtcNow;
            _debouncedDiff = new PendingDiffRequest(repo, option, reason);
            IsLoading = true;

            if (clearPreviousContent)
                ActiveContent = null;

            _selectionDebounceTimer = DispatcherTimer.RunOnce(
                () => StartDebouncedDiff(debounceId),
                SelectionDebounceDelay);
        }

        private void StartPendingDiff(string repo, Models.DiffOption option, string reason)
        {
            if (_disposed)
                return;

            if (_activeContent is DiffContext current && current.IsSameOption(option))
            {
                IsLoading = false;
                current.Reload(reason);
                return;
            }

            if (_pendingDiffContext != null && _pendingDiffContext.IsSameOption(option))
                return;

            var requestId = ++_requestId;
            var previous = _activeContent as DiffContext;

            DiffContext next = null;
            var loadedBeforeAssigned = false;
            next = new DiffContext(repo, option, previous, () =>
            {
                if (next == null)
                {
                    loadedBeforeAssigned = true;
                    return;
                }

                CommitPending(requestId);
            }, false, true);
            _pendingDiffContext = next;
            IsLoading = true;

            if (loadedBeforeAssigned)
                CommitPending(requestId);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            CancelDebouncedDiff();
            CancelPending();
            ActiveContent = null;
        }

        private void StartDebouncedDiff(long debounceId)
        {
            _selectionDebounceTimer = null;

            if (_disposed || debounceId != _debounceRequestId || _debouncedDiff == null)
                return;

            var request = _debouncedDiff;
            _debouncedDiff = null;
            StartPendingDiff(request.Repo, request.Option, request.Reason);
        }

        private void CommitPending(long requestId)
        {
            if (_disposed || requestId != _requestId || _pendingDiffContext == null)
                return;

            var elapsed = DateTime.UtcNow - _lastDiffSelectionAt;
            var delay = CommitQuietDelay - elapsed;
            if (delay > TimeSpan.Zero)
            {
                CancelPendingCommit();
                _pendingCommitTimer = DispatcherTimer.RunOnce(
                    () => CommitPendingNow(requestId),
                    delay);
                return;
            }

            CommitPendingNow(requestId);
        }

        private void CommitPendingNow(long requestId)
        {
            _pendingCommitTimer = null;

            if (_disposed || requestId != _requestId || _pendingDiffContext == null)
                return;

            var next = _pendingDiffContext;
            _pendingDiffContext = null;
            ActiveContent = next;
            IsLoading = false;
        }

        private void CancelDebouncedDiff()
        {
            ++_debounceRequestId;
            _debouncedDiff = null;
            if (_selectionDebounceTimer != null)
            {
                _selectionDebounceTimer.Dispose();
                _selectionDebounceTimer = null;
            }
        }

        private void CancelPending()
        {
            ++_requestId;
            CancelPendingCommit();
            if (_pendingDiffContext != null)
            {
                _pendingDiffContext.DisposeInBackground();
                _pendingDiffContext = null;
            }
        }

        private static void DisposeContent(object content)
        {
            if (content is DiffContext diff)
                diff.DisposeInBackground();
            else
                (content as IDisposable)?.Dispose();
        }

        private void CancelPendingCommit()
        {
            if (_pendingCommitTimer != null)
            {
                _pendingCommitTimer.Dispose();
                _pendingCommitTimer = null;
            }
        }

        private class PendingDiffRequest(string repo, Models.DiffOption option, string reason)
        {
            public string Repo { get; } = repo;
            public Models.DiffOption Option { get; } = option;
            public string Reason { get; } = reason;
        }

        private object _activeContent = null;
        private DiffContext _pendingDiffContext = null;
        private PendingDiffRequest _debouncedDiff = null;
        private IDisposable _selectionDebounceTimer = null;
        private IDisposable _pendingCommitTimer = null;
        private DateTime _lastDiffSelectionAt = DateTime.MinValue;
        private long _requestId = 0;
        private long _debounceRequestId = 0;
        private bool _isLoading = false;
        private bool _disposed = false;
        private static readonly TimeSpan SelectionDebounceDelay = TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan CommitQuietDelay = TimeSpan.FromMilliseconds(180);
    }
}
