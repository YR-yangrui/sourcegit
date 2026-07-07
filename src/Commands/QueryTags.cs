using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class QueryTags : Command
    {
        public QueryTags(string repo)
        {
            _boundary = $"----- BOUNDARY OF TAGS {Guid.NewGuid()} -----";

            Context = repo;
            WorkingDirectory = repo;
            Args = $"tag -l --format=\"{_boundary}%(refname)%00%(objecttype)%00%(objectname)%00%(*objectname)%00%(taggername)±%(taggeremail)%00%(creatordate:unix)%00%(contents:subject)%0a%0a%(contents:body)\"";
        }

        public async Task<List<Models.Tag>> GetResultAsync()
        {
            var tags = new List<Models.Tag>();
            var rs = await ReadToEndAsync().ConfigureAwait(false);
            if (!rs.IsSuccess)
                return tags;

            var records = rs.StdOut.Split(_boundary, StringSplitOptions.RemoveEmptyEntries);
            foreach (var record in records)
            {
                if (TryParseRecord(record, out _, out var tag))
                    tags.Add(tag);
            }

            return tags;
        }

        public async Task<Dictionary<string, Models.Tag>> GetDetailsByRefsAsync(IReadOnlyList<string> refs)
        {
            var tags = new Dictionary<string, Models.Tag>(StringComparer.Ordinal);
            if (refs == null || refs.Count == 0)
                return tags;

            var chunks = BuildRefChunks(refs);
            Args = $"for-each-ref --format=\"{_boundary}%(refname)%00%(objecttype)%00%(objectname)%00%(*objectname)%00%(taggername)±%(taggeremail)%00%(creatordate:unix)%00%(contents:subject)%0a%0a%(contents:body)\" <{refs.Count} refs>";
            using var span = StartGitDiagnosticSpan("query_tag_details");
            var success = true;
            var commandCount = 0;

            foreach (var chunk in chunks)
            {
                Args = $"for-each-ref --format=\"{_boundary}%(refname)%00%(objecttype)%00%(objectname)%00%(*objectname)%00%(taggername)±%(taggeremail)%00%(creatordate:unix)%00%(contents:subject)%0a%0a%(contents:body)\" {chunk}";
                commandCount++;

                var rs = await ReadToEndAsync().ConfigureAwait(false);
                if (!rs.IsSuccess)
                {
                    success = false;
                    continue;
                }

                var records = rs.StdOut.Split(_boundary, StringSplitOptions.RemoveEmptyEntries);
                foreach (var record in records)
                {
                    if (TryParseRecord(record, out var refName, out var tag))
                        tags[refName] = tag;
                }
            }

            span.Set("success", success);
            span.Set("requestedRefCount", refs.Count);
            span.Set("commandCount", commandCount);
            span.Set("tagCount", tags.Count);
            return tags;
        }

        private static bool TryParseRecord(string record, out string refName, out Models.Tag tag)
        {
            refName = string.Empty;
            tag = null;

            var subs = record.Split('\0');
            if (subs.Length != 7 || !subs[0].StartsWith("refs/tags/", StringComparison.Ordinal))
                return false;

            refName = subs[0];
            var name = subs[0].Substring(10);
            var message = subs[6].Trim();
            if (!string.IsNullOrEmpty(message) && message.Equals(name, StringComparison.Ordinal))
                message = null;

            ulong.TryParse(subs[5], out var creatorDate);

            tag = new Models.Tag()
            {
                Name = name,
                IsAnnotated = subs[1].Equals("tag", StringComparison.Ordinal),
                SHA = string.IsNullOrEmpty(subs[3]) ? subs[2] : subs[3],
                Creator = Models.User.FindOrAdd(subs[4]),
                CreatorDate = creatorDate,
                Message = message,
            };
            return true;
        }

        private static List<string> BuildRefChunks(IReadOnlyList<string> refs)
        {
            if (refs.Count > 128)
                return ["refs/tags"];

            var chunks = new List<string>();
            var builder = new StringBuilder();
            foreach (var r in refs)
            {
                var quoted = r.Quoted();
                if (builder.Length > 0 && builder.Length + quoted.Length + 1 > 24000)
                {
                    chunks.Add(builder.ToString());
                    builder.Clear();
                }

                if (builder.Length > 0)
                    builder.Append(' ');
                builder.Append(quoted);
            }

            if (builder.Length > 0)
                chunks.Add(builder.ToString());

            return chunks;
        }

        private readonly string _boundary;
    }
}
