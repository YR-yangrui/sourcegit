using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class QueryTrackStatus : Command
    {
        public QueryTrackStatus(string repo)
        {
            WorkingDirectory = repo;
            Context = repo;
        }

        public async Task GetResultAsync(Models.Branch local, Models.Branch remote)
        {
            local.Ahead.Clear();
            local.Behind.Clear();

            if (local.Head.Equals(remote.Head, StringComparison.Ordinal))
                return;

            var key = $"{local.Head}\0{remote.Head}";
            Args = $"rev-list --left-right {local.Head}...{remote.Head}";
            using var span = StartGitDiagnosticSpan("query_track_status");
            span.Set("local", local.FullName);
            span.Set("remote", remote.FullName);
            span.Set("localHead", local.Head);
            span.Set("remoteHead", remote.Head);

            if (_cache.TryGetValue(key, out var cached))
            {
                local.Ahead.AddRange(cached.Ahead);
                local.Behind.AddRange(cached.Behind);
                span.Set("success", true);
                span.Set("cacheHit", true);
                span.Set("trackStatusCache.hit", true);
                span.Set("ahead", local.Ahead.Count);
                span.Set("behind", local.Behind.Count);
                return;
            }

            var rs = await ReadToEndAsync().ConfigureAwait(false);
            if (!rs.IsSuccess)
            {
                span.Set("success", false);
                span.Set("cacheHit", false);
                span.Set("trackStatusCache.hit", false);
                return;
            }

            var ahead = new List<string>();
            var behind = new List<string>();
            var lines = rs.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line[0] == '>')
                    behind.Add(line.Substring(1));
                else
                    ahead.Add(line.Substring(1));
            }

            if (_cache.Count > 16384)
                _cache.Clear();

            var status = new TrackStatus(ahead.ToArray(), behind.ToArray());
            _cache[key] = status;
            local.Ahead.AddRange(status.Ahead);
            local.Behind.AddRange(status.Behind);

            span.Set("success", true);
            span.Set("cacheHit", false);
            span.Set("trackStatusCache.hit", false);
            span.Set("ahead", local.Ahead.Count);
            span.Set("behind", local.Behind.Count);
        }

        private sealed class TrackStatus(string[] ahead, string[] behind)
        {
            public string[] Ahead { get; } = ahead;
            public string[] Behind { get; } = behind;
        }

        private static readonly ConcurrentDictionary<string, TrackStatus> _cache = new();
    }
}
