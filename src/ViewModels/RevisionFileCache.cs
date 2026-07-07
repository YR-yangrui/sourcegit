using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public static class RevisionFileCache
    {
        public sealed class TreeEntry
        {
            public string SHA { get; set; } = string.Empty;
            public Models.ObjectType Type { get; set; } = Models.ObjectType.None;
            public string Name { get; set; } = string.Empty;

            public Models.Object ToObject(string parentPath)
            {
                return new Models.Object
                {
                    SHA = SHA,
                    Type = Type,
                    Path = string.IsNullOrEmpty(parentPath) ? Name : $"{parentPath}/{Name}",
                };
            }
        }

        public static bool TryGetTree(string treeSHA, out List<TreeEntry> entries)
        {
            return _trees.TryGet(treeSHA, out entries);
        }

        public static Task<List<TreeEntry>> GetOrAddTreeAsync(string treeSHA, Func<Task<List<Models.Object>>> loader)
        {
            return _trees.GetOrAddAsync(treeSHA, async () =>
            {
                var objects = await loader().ConfigureAwait(false);
                if (objects == null)
                    return null;

                var entries = new List<TreeEntry>();
                foreach (var obj in objects)
                {
                    if (obj == null || string.IsNullOrEmpty(obj.Path))
                        continue;

                    entries.Add(new TreeEntry
                    {
                        SHA = obj.SHA,
                        Type = obj.Type,
                        Name = obj.Path,
                    });
                }

                SortEntries(entries);
                return entries;
            });
        }

        public static bool TryGetFileNames(string commitSHA, out List<string> files)
        {
            return _fileNames.TryGet(commitSHA, out files);
        }

        public static Task<List<string>> GetOrAddFileNamesAsync(string commitSHA, Func<Task<List<string>>> loader)
        {
            return _fileNames.GetOrAddAsync(commitSHA, async () =>
            {
                var files = await loader().ConfigureAwait(false);
                return files;
            });
        }

        private static void SortEntries(List<TreeEntry> entries)
        {
            entries.Sort((l, r) =>
            {
                var lFolder = l.Type == Models.ObjectType.Tree;
                var rFolder = r.Type == Models.ObjectType.Tree;
                if (lFolder == rFolder)
                    return Models.NumericSort.Compare(l.Name, r.Name);

                return lFolder ? -1 : 1;
            });
        }

        private static readonly LruAsyncCache<List<TreeEntry>> _trees = new LruAsyncCache<List<TreeEntry>>(1024);
        private static readonly LruAsyncCache<List<string>> _fileNames = new LruAsyncCache<List<string>>(32);

        private sealed class LruAsyncCache<T>
        {
            public LruAsyncCache(int capacity)
            {
                _capacity = capacity;
            }

            public bool TryGet(string key, out T value)
            {
                lock (_lock)
                {
                    if (_map.TryGetValue(key, out var node))
                    {
                        _order.Remove(node);
                        _order.AddFirst(node);
                        value = node.Value.Value;
                        return true;
                    }
                }

                value = default;
                return false;
            }

            public Task<T> GetOrAddAsync(string key, Func<Task<T>> loader)
            {
                lock (_lock)
                {
                    if (_map.TryGetValue(key, out var node))
                    {
                        _order.Remove(node);
                        _order.AddFirst(node);
                        return Task.FromResult(node.Value.Value);
                    }

                    if (_pending.TryGetValue(key, out var pending))
                        return pending.Task;

                    var source = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _pending.Add(key, source);
                    _ = Task.Run(() => LoadAsync(key, loader, source));
                    return source.Task;
                }
            }

            private async Task LoadAsync(string key, Func<Task<T>> loader, TaskCompletionSource<T> source)
            {
                try
                {
                    var value = await loader().ConfigureAwait(false);
                    if (value != null)
                        Add(key, value);

                    source.TrySetResult(value);
                }
                catch (Exception e)
                {
                    source.TrySetException(e);
                }
                finally
                {
                    lock (_lock)
                        _pending.Remove(key);
                }
            }

            private void Add(string key, T value)
            {
                lock (_lock)
                {
                    if (_map.TryGetValue(key, out var existing))
                    {
                        existing.Value = (key, value);
                        _order.Remove(existing);
                        _order.AddFirst(existing);
                        return;
                    }

                    var node = new LinkedListNode<(string Key, T Value)>((key, value));
                    _order.AddFirst(node);
                    _map.Add(key, node);

                    while (_map.Count > _capacity)
                    {
                        var last = _order.Last;
                        if (last == null)
                            break;

                        _order.RemoveLast();
                        _map.Remove(last.Value.Key);
                    }
                }
            }

            private readonly int _capacity;
            private readonly object _lock = new object();
            private readonly Dictionary<string, LinkedListNode<(string Key, T Value)>> _map = new Dictionary<string, LinkedListNode<(string Key, T Value)>>();
            private readonly Dictionary<string, TaskCompletionSource<T>> _pending = new Dictionary<string, TaskCompletionSource<T>>();
            private readonly LinkedList<(string Key, T Value)> _order = new LinkedList<(string Key, T Value)>();
        }
    }
}
