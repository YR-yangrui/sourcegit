using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class IsConflictResolved : Command
    {
        public IsConflictResolved(string repo, Models.Change change)
        {
            _file = Path.GetFullPath(Path.Combine(repo, change.Path));
        }

        public bool GetResult()
        {
            if (!TryGetFileSnapshot(out var snapshot))
                return false;

            if (TryGetCachedResult(snapshot, out var cached))
                return cached;

            var result = ScanFile();
            StoreCachedResult(snapshot, result);
            return result;
        }

        public async Task<bool> GetResultAsync()
        {
            if (!TryGetFileSnapshot(out var snapshot))
                return false;

            if (TryGetCachedResult(snapshot, out var cached))
                return cached;

            var result = await ScanFileAsync().ConfigureAwait(false);
            StoreCachedResult(snapshot, result);
            return result;
        }

        private bool ScanFile()
        {
            try
            {
                using var stream = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var buffer = new byte[BUFFER_SIZE];
                var prefix = new byte[MAX_MARKER_PREFIX_LENGTH];
                var atLineStart = true;
                var prefixLen = 0;

                while (true)
                {
                    var read = stream.Read(buffer, 0, buffer.Length);
                    if (read == 0)
                        return true;

                    var state = ScanBuffer(buffer.AsSpan(0, read), prefix, ref atLineStart, ref prefixLen);
                    if (state != ScanState.Text)
                        return false;
                }
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> ScanFileAsync()
        {
            try
            {
                await using var stream = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var buffer = new byte[BUFFER_SIZE];
                var prefix = new byte[MAX_MARKER_PREFIX_LENGTH];
                var atLineStart = true;
                var prefixLen = 0;

                while (true)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false);
                    if (read == 0)
                        return true;

                    var state = ScanBuffer(buffer.AsSpan(0, read), prefix, ref atLineStart, ref prefixLen);
                    if (state != ScanState.Text)
                        return false;
                }
            }
            catch
            {
                return false;
            }
        }

        private static ScanState ScanBuffer(ReadOnlySpan<byte> buffer, byte[] prefix, ref bool atLineStart, ref int prefixLen)
        {
            foreach (var b in buffer)
            {
                if (b == 0)
                    return ScanState.Binary;

                if (b is (byte)'\r' or (byte)'\n')
                {
                    atLineStart = true;
                    prefixLen = 0;
                    continue;
                }

                if (!atLineStart)
                    continue;

                if (prefixLen >= prefix.Length)
                    return IsLongPotentialConflictMarker(prefix) ? ScanState.ConflictMarker : ScanState.Text;

                prefix[prefixLen++] = b;
                if (IsFullConflictMarker(prefix, prefixLen))
                    return ScanState.ConflictMarker;

                if (!IsPotentialConflictMarkerPrefix(prefix, prefixLen))
                    atLineStart = false;
            }

            return ScanState.Text;
        }

        private bool TryGetFileSnapshot(out FileSnapshot snapshot)
        {
            try
            {
                var info = new FileInfo(_file);
                if (!info.Exists)
                {
                    RemoveCachedResult();
                    snapshot = default;
                    return false;
                }

                snapshot = new FileSnapshot(info.Length, info.LastWriteTimeUtc.Ticks);
                return true;
            }
            catch
            {
                RemoveCachedResult();
                snapshot = default;
                return false;
            }
        }

        private bool TryGetCachedResult(FileSnapshot snapshot, out bool result)
        {
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(_file, out var entry) && entry.Snapshot.Equals(snapshot))
                {
                    result = entry.ResolvedTextConflict;
                    return true;
                }
            }

            result = false;
            return false;
        }

        private void StoreCachedResult(FileSnapshot snapshot, bool result)
        {
            if (!TryGetFileSnapshot(out var latest) || !latest.Equals(snapshot))
                return;

            lock (_cacheLock)
            {
                if (_cache.Count >= MAX_CACHE_ENTRIES && !_cache.ContainsKey(_file))
                    _cache.Clear();

                _cache[_file] = new CacheEntry(snapshot, result);
            }
        }

        private void RemoveCachedResult()
        {
            lock (_cacheLock)
                _cache.Remove(_file);
        }

        private static bool IsPotentialConflictMarkerPrefix(byte[] prefix, int length)
        {
            if (length == 0)
                return true;

            var marker = prefix[0];
            return marker switch
            {
                (byte)'<' or (byte)'>' or (byte)'|' => IsRepeatedMarker(prefix, length, marker) ||
                    (length > MIN_MARKER_LENGTH &&
                        IsMarkerTerminator(prefix[length - 1]) &&
                        IsRepeatedMarker(prefix, length - 1, marker)),
                (byte)'=' => IsRepeatedMarker(prefix, length, marker),
                _ => false,
            };
        }

        private static bool IsFullConflictMarker(byte[] prefix, int length)
        {
            if (length < MIN_MARKER_LENGTH)
                return false;

            var marker = prefix[0];
            return marker switch
            {
                (byte)'=' => IsRepeatedMarker(prefix, length, marker),
                (byte)'<' or (byte)'>' or (byte)'|' => length > MIN_MARKER_LENGTH &&
                    IsMarkerTerminator(prefix[length - 1]) &&
                    IsRepeatedMarker(prefix, length - 1, marker),
                _ => false,
            };
        }

        private static bool IsLongPotentialConflictMarker(byte[] prefix)
        {
            var marker = prefix[0];
            return (marker is (byte)'<' or (byte)'>' or (byte)'|' or (byte)'=') &&
                IsRepeatedMarker(prefix, prefix.Length, marker);
        }

        private static bool IsRepeatedMarker(byte[] prefix, int length, byte marker)
        {
            for (var i = 0; i < length; i++)
            {
                if (prefix[i] != marker)
                    return false;
            }

            return true;
        }

        private static bool IsMarkerTerminator(byte value)
        {
            return value is (byte)' ' or (byte)'\t';
        }

        private enum ScanState
        {
            Text,
            Binary,
            ConflictMarker,
        }

        private readonly struct FileSnapshot(long length, long lastWriteTimeUtcTicks)
        {
            public long Length { get; } = length;
            public long LastWriteTimeUtcTicks { get; } = lastWriteTimeUtcTicks;
        }

        private readonly struct CacheEntry(FileSnapshot snapshot, bool resolvedTextConflict)
        {
            public FileSnapshot Snapshot { get; } = snapshot;
            public bool ResolvedTextConflict { get; } = resolvedTextConflict;
        }

        private const int BUFFER_SIZE = 8192;
        private const int MIN_MARKER_LENGTH = 7;
        private const int MAX_MARKER_PREFIX_LENGTH = 128;
        private const int MAX_CACHE_ENTRIES = 4096;
        private static readonly object _cacheLock = new();
        private static readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

        private readonly string _file = string.Empty;
    }
}
