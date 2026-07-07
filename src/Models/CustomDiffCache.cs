using System;
using System.Collections.Generic;
using System.IO;

namespace SourceGit.Models
{
    public class CustomDiffCacheEntry
    {
        public object Content { get; init; } = null;
        public string FileModeChange { get; init; } = string.Empty;
    }

    public sealed class CustomDiffCacheLease : IDisposable
    {
        internal CustomDiffCacheLease(CustomDiffCache owner, CustomDiffCacheItem item)
        {
            _owner = owner;
            _item = item;
        }

        public CustomDiffCacheEntry Entry => _item?.Entry;

        public void Dispose()
        {
            var owner = _owner;
            var item = _item;
            if (owner == null || item == null)
                return;

            _owner = null;
            _item = null;
            owner.Release(item);
        }

        private CustomDiffCache _owner;
        private CustomDiffCacheItem _item;
    }

    public class CustomDiffCache
    {
        public const int DefaultCapacity = 32;

        public static CustomDiffCache Shared { get; } = new CustomDiffCache();

        public CustomDiffCache(int capacity = DefaultCapacity)
        {
            _capacity = Math.Max(1, capacity);
        }

        public bool TryGet(string key, out CustomDiffCacheEntry entry)
        {
            lock (_lock)
            {
                if (!_entries.TryGetValue(key, out var node))
                {
                    entry = null;
                    return false;
                }

                _lru.Remove(node);
                _lru.AddFirst(node);
                entry = node.Value.Entry;
                return true;
            }
        }

        public bool TryAcquire(string key, out CustomDiffCacheLease lease)
        {
            lock (_lock)
            {
                if (!_entries.TryGetValue(key, out var node))
                {
                    lease = null;
                    return false;
                }

                _lru.Remove(node);
                _lru.AddFirst(node);
                lease = CreateLease(node.Value);
                return true;
            }
        }

        public void Put(string key, CustomDiffCacheEntry entry)
        {
            PutCore(key, entry, false)?.Dispose();
        }

        public CustomDiffCacheLease PutAndAcquire(string key, CustomDiffCacheEntry entry)
        {
            return PutCore(key, entry, true);
        }

        public void Remove(string key)
        {
            var entriesToRelease = new List<CustomDiffCacheEntry>();
            lock (_lock)
            {
                if (!_entries.TryGetValue(key, out var node))
                    return;

                RemoveNode(node, entriesToRelease);
            }

            ReleaseEntries(entriesToRelease);
        }

        public static bool ShouldSkipSameContent(bool forceRefresh, bool hasHtmlContent, bool hasInfo, bool rendererMatches, bool metadataMatches, bool contentAvailable)
        {
            return !forceRefresh && hasHtmlContent && hasInfo && rendererMatches && metadataMatches && contentAvailable;
        }

        public static void ReleaseTemporaryContent(object content)
        {
            if (content is not HtmlDiff html || string.IsNullOrEmpty(html.TempDirectory))
                return;

            var tempRoot = Path.Combine(Path.GetTempPath(), "sourcegit-custom-diff");
            if (!IsPathUnderDirectory(html.TempDirectory, tempRoot))
                return;

            try
            {
                if (Directory.Exists(html.TempDirectory))
                    Directory.Delete(html.TempDirectory, true);
            }
            catch
            {
                // The embedded WebView may still hold the file briefly. Stale temp cleanup handles it later.
            }
        }

        internal void Release(CustomDiffCacheItem item)
        {
            CustomDiffCacheEntry entryToRelease = null;
            lock (_lock)
            {
                item.RefCount = Math.Max(0, item.RefCount - 1);
                if (item.Removed && item.RefCount == 0)
                    entryToRelease = item.Entry;
            }

            if (entryToRelease != null)
                ReleaseTemporaryContent(entryToRelease.Content);
        }

        private CustomDiffCacheLease PutCore(string key, CustomDiffCacheEntry entry, bool acquire)
        {
            if (entry == null)
                return null;

            var entriesToRelease = new List<CustomDiffCacheEntry>();
            CustomDiffCacheLease lease = null;
            lock (_lock)
            {
                if (_entries.TryGetValue(key, out var existing))
                    RemoveNode(existing, entriesToRelease);

                var item = new CustomDiffCacheItem(key, entry);
                var node = new LinkedListNode<CustomDiffCacheItem>(item);
                _entries.Add(key, node);
                _lru.AddFirst(node);
                if (acquire)
                    lease = CreateLease(item);

                while (_entries.Count > _capacity)
                {
                    var last = _lru.Last;
                    if (last == null)
                        break;

                    RemoveNode(last, entriesToRelease);
                }
            }

            ReleaseEntries(entriesToRelease);
            return lease;
        }

        private CustomDiffCacheLease CreateLease(CustomDiffCacheItem item)
        {
            item.RefCount++;
            return new CustomDiffCacheLease(this, item);
        }

        private void RemoveNode(LinkedListNode<CustomDiffCacheItem> node, List<CustomDiffCacheEntry> entriesToRelease)
        {
            _entries.Remove(node.Value.Key);
            _lru.Remove(node);
            node.Value.Removed = true;
            if (node.Value.RefCount == 0)
                entriesToRelease.Add(node.Value.Entry);
        }

        private static void ReleaseEntries(List<CustomDiffCacheEntry> entries)
        {
            foreach (var entry in entries)
                ReleaseTemporaryContent(entry.Content);
        }

        private static bool IsPathUnderDirectory(string path, string directory)
        {
            var fullPath = Path.GetFullPath(path);
            var fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
        }

        private readonly int _capacity;
        private readonly object _lock = new object();
        private readonly Dictionary<string, LinkedListNode<CustomDiffCacheItem>> _entries = new Dictionary<string, LinkedListNode<CustomDiffCacheItem>>();
        private readonly LinkedList<CustomDiffCacheItem> _lru = new LinkedList<CustomDiffCacheItem>();
    }

    internal class CustomDiffCacheItem(string key, CustomDiffCacheEntry entry)
    {
        public string Key { get; } = key;
        public CustomDiffCacheEntry Entry { get; } = entry;
        public int RefCount { get; set; } = 0;
        public bool Removed { get; set; } = false;
    }
}
