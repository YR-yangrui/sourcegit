using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class MergeConflictFileHistoryCache : ObservableObject
    {
        public bool IsLoading
        {
            get => _isLoading;
            private set => SetProperty(ref _isLoading, value);
        }

        public int Revision
        {
            get => _revision;
            private set => SetProperty(ref _revision, value);
        }

        public void Reset()
        {
            if (string.IsNullOrEmpty(_sessionSeed) &&
                string.IsNullOrEmpty(_contextKey) &&
                !IsLoading &&
                _mine.Count == 0 &&
                _theirs.Count == 0)
                return;

            _generation++;
            _sessionSeed = string.Empty;
            _contextKey = string.Empty;
            _mine.Clear();
            _theirs.Clear();
            _cachedPaths.Clear();
            IsLoading = false;
            Revision++;
        }

        public void Ensure(Repository repo, ConflictHistoryPlan plan)
        {
            if (plan == null || !plan.IsValid)
            {
                Reset();
                return;
            }

            var sessionSeed = plan.SessionSeed;

            if (sessionSeed.Equals(_sessionSeed, StringComparison.Ordinal) &&
                (!string.IsNullOrEmpty(_contextKey) || IsLoading))
                return;

            if (!sessionSeed.Equals(_sessionSeed, StringComparison.Ordinal))
            {
                _generation++;
                _sessionSeed = sessionSeed;
                _contextKey = string.Empty;
                _mine.Clear();
                _theirs.Clear();
                _cachedPaths.Clear();
                IsLoading = false;
                Revision++;
            }

            var generation = _generation;
            IsLoading = true;
            Revision++;

            Task.Run(async () =>
            {
                var mine = new Dictionary<string, List<Models.FileVersion>>(StringComparer.Ordinal);
                var theirs = new Dictionary<string, List<Models.FileVersion>>(StringComparer.Ordinal);
                var contextKey = string.Empty;
                var missing = new List<string>();
                var mergeBase = string.Empty;

                try
                {
                    var snapshot = await ConflictStageSnapshot.QueryAsync(repo.FullPath, CancellationToken.None)
                        .ConfigureAwait(false);
                    if (snapshot.IsEmpty)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (generation == _generation && sessionSeed.Equals(_sessionSeed, StringComparison.Ordinal))
                                Reset();
                        });

                        return;
                    }

                    mergeBase = await new Commands.MergeBase(repo.FullPath, plan.MergeBaseLeft, plan.MergeBaseRight)
                        .GetResultAsync()
                        .ConfigureAwait(false);
                    contextKey = BuildContextKey(sessionSeed, snapshot.Hash);

                    missing.AddRange(snapshot.Paths);

                    if (!string.IsNullOrEmpty(mergeBase) && missing.Count > 0)
                    {
                        var mineTask = new Commands.QueryFileHistories(repo.FullPath, plan.BuildMineRange(mergeBase), missing)
                            .GetResultAsync();
                        var theirsTask = new Commands.QueryFileHistories(repo.FullPath, plan.BuildTheirsRange(mergeBase), missing)
                            .GetResultAsync();

                        await Task.WhenAll(mineTask, theirsTask).ConfigureAwait(false);
                        mine = mineTask.Result;
                        theirs = theirsTask.Result;
                    }
                }
                catch
                {
                    // Keep conflict resolution usable if history lookup fails.
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (generation != _generation || !sessionSeed.Equals(_sessionSeed, StringComparison.Ordinal))
                        return;

                    if (string.IsNullOrEmpty(contextKey))
                    {
                        IsLoading = false;
                        Revision++;
                        return;
                    }

                    if (!contextKey.Equals(_contextKey, StringComparison.Ordinal))
                    {
                        _contextKey = contextKey;
                        _mine.Clear();
                        _theirs.Clear();
                        _cachedPaths.Clear();
                    }

                    foreach (var path in missing)
                    {
                        _mine[path] = mine.TryGetValue(path, out var mineHistories) ? mineHistories : [];
                        _theirs[path] = theirs.TryGetValue(path, out var theirsHistories) ? theirsHistories : [];
                        _cachedPaths.Add(path);
                    }

                    IsLoading = false;
                    Revision++;
                });
            });
        }

        public List<Models.FileVersion> GetMineHistories(string path)
        {
            return _mine.TryGetValue(path, out var histories) ? histories : [];
        }

        public List<Models.FileVersion> GetTheirsHistories(string path)
        {
            return _theirs.TryGetValue(path, out var histories) ? histories : [];
        }

        private static string BuildContextKey(string sessionSeed, string snapshotHash)
        {
            var builder = new StringBuilder();
            builder.Append(sessionSeed).Append('\0');
            builder.Append(snapshotHash).Append('\0');

            return builder.ToString();
        }

        private int _generation = 0;
        private int _revision = 0;
        private bool _isLoading = false;
        private string _sessionSeed = string.Empty;
        private string _contextKey = string.Empty;
        private Dictionary<string, List<Models.FileVersion>> _mine = new(StringComparer.Ordinal);
        private Dictionary<string, List<Models.FileVersion>> _theirs = new(StringComparer.Ordinal);
        private HashSet<string> _cachedPaths = new(StringComparer.Ordinal);
    }
}
