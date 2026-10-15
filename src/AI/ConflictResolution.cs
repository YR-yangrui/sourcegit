using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.AI
{
    public sealed partial class ConflictResolution
    {
        public string Path { get; private init; }
        public string Original { get; private init; }
        public string Result { get; private set; }
        public string Summary { get; private set; }

        public static bool IsRebasing(string gitDir) =>
            Directory.Exists(System.IO.Path.Combine(gitDir, "rebase-merge")) ||
            Directory.Exists(System.IO.Path.Combine(gitDir, "rebase-apply"));

        public static async Task<List<Commands.MergeConflictBlob.StageEntry>> QueryEntriesAsync(string repo, CancellationToken cancellation)
        {
            var command = new Query(repo, "ls-files -u -z", cancellation);
            var result = await command.ReadAsync();
            var entries = new List<Commands.MergeConflictBlob.StageEntry>();
            // NUL records keep spaces, quotes and non-ASCII Git paths intact.
            foreach (var record in result.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var tab = record.IndexOf('\t');
                var header = record[..tab].Split(' ');
                entries.Add(new Commands.MergeConflictBlob.StageEntry
                {
                    Mode = header[0], ObjectId = header[1], Stage = int.Parse(header[2]), Path = record[(tab + 1)..],
                });
            }
            return entries;
        }

        public static async Task<ConflictResolution> GenerateAsync(string repo, string gitDir, string path, Service service, CancellationToken cancellation)
        {
            var entries = (await QueryEntriesAsync(repo, cancellation)).Where(x => x.Path == path).ToList();
            var ours = entries.Find(x => x.Stage == 2);
            var theirs = entries.Find(x => x.Stage == 3);
            // Only regular files with both sides present can be merged without a structural decision.
            if (ours == null || theirs == null || entries.Any(x => x.Mode != "100644" && x.Mode != "100755") || ours.Mode != theirs.Mode)
                throw new Exception(App.Text("AIConflict.Unsupported"));

            var fullPath = System.IO.Path.Combine(repo, path);
            var originalBytes = await File.ReadAllBytesAsync(fullPath, cancellation);
            var original = Decode(originalBytes, out var encoding);
            var blocks = BLOCKS().Matches(original);
            if (blocks.Count == 0)
                throw new Exception(App.Text("AIConflict.Unsupported"));

            var resolution = new ConflictResolution
            {
                Path = path, Original = original, _originalBytes = originalBytes, _encoding = encoding, _entries = entries,
                _head = await new Query(repo, "rev-parse HEAD", cancellation).ReadAsync(),
            };
            var prompt = new StringBuilder();
            prompt.Append("File: ").AppendLine(path);
            prompt.AppendLine(IsRebasing(gitDir)
                ? "Operation: rebase. MINE/stage 2 is the upstream target plus already replayed commits; THEIRS/stage 3 is the local commit being replayed."
                : "MINE/stage 2 is the current target; THEIRS/stage 3 is the incoming change. BASE/stage 1 is their common ancestor.");

            // Read immutable blobs by their captured object IDs, not mutable index references.
            foreach (var entry in entries)
            {
                var bytes = await Commands.QueryFileContent.RunSpecAsBytesAsync(repo, entry.ObjectId);
                cancellation.ThrowIfCancellationRequested();
                if (bytes == null)
                    throw new Exception(App.Text("AIConflict.Changed"));
                var content = Decode(bytes, out _);
                prompt.Append("Stage ").Append(entry.Stage).AppendLine(":").AppendLine(content);
            }
            prompt.AppendLine("Working file (preserve existing manual edits outside conflict blocks):").AppendLine(original);
            // Number blocks explicitly so each returned replacement has a fixed destination.
            for (var i = 0; i < blocks.Count; i++)
                prompt.Append("Block ").Append(i + 1).AppendLine(":").AppendLine(blocks[i].Value);

            if (prompt.Length > 180000)
                throw new Exception(App.Text("AIConflict.TooLarge"));

            var response = await new Agent(service).ResolveConflictAsync(prompt.ToString(), cancellation);
            resolution.SetResult(response, blocks);
            return resolution;
        }

        private void SetResult(string response, MatchCollection blocks)
        {
            // Parse a small explicit contract; malformed, refused or truncated output cannot reach disk.
            using var json = JsonDocument.Parse(response);
            var root = json.RootElement;
            Summary = root.GetProperty("summary").GetString() ?? string.Empty;
            if (!root.GetProperty("resolved").GetBoolean())
                throw new Exception(string.IsNullOrWhiteSpace(Summary) ? App.Text("AIConflict.Uncertain") : Summary);
            var replacements = root.GetProperty("resolutions");
            if (replacements.GetArrayLength() != blocks.Count)
                throw new Exception(App.Text("AIConflict.InvalidResult"));

            var builder = new StringBuilder();
            var offset = 0;
            var index = 0;
            // Splice only conflict regions, keeping all unrelated working-copy text exactly as read.
            foreach (var replacement in replacements.EnumerateArray())
            {
                var block = blocks[index++];
                var text = replacement.GetString() ?? throw new Exception(App.Text("AIConflict.InvalidResult"));
                if (MARKERS().IsMatch(text) || text.Contains('\0'))
                    throw new Exception(App.Text("AIConflict.InvalidResult"));
                var newline = block.Value.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
                text = text.ReplaceLineEndings(newline);
                if (text.Length > 0 && block.Value.EndsWith('\n') && !text.EndsWith('\n'))
                    text += newline;
                builder.Append(Original, offset, block.Index - offset).Append(text);
                offset = block.Index + block.Length;
            }
            builder.Append(Original, offset, Original.Length - offset);
            Result = builder.ToString();
            if (MARKERS().IsMatch(Result))
                throw new Exception(App.Text("AIConflict.InvalidResult"));
        }

        public async Task ApplyAsync(string repo, Models.ICommandLog log, CancellationToken cancellation)
        {
            var path = System.IO.Path.Combine(repo, Path);
            var entries = (await QueryEntriesAsync(repo, cancellation)).Where(x => x.Path == Path).ToList();
            var head = await new Query(repo, "rev-parse HEAD", cancellation).ReadAsync();
            var bytes = await File.ReadAllBytesAsync(path, cancellation);
            // AI and preview can take minutes: reject stale results instead of overwriting manual edits.
            if (head != _head || !bytes.AsSpan().SequenceEqual(_originalBytes) || entries.Count != _entries.Count ||
                entries.Where((x, i) => x.Stage != _entries[i].Stage || x.ObjectId != _entries[i].ObjectId || x.Mode != _entries[i].Mode).Any())
                throw new Exception(App.Text("AIConflict.Changed"));

            cancellation.ThrowIfCancellationRequested();
            // Do not interrupt a write/add halfway through; cancellation is honored at the next boundary.
            await File.WriteAllTextAsync(path, Result, _encoding);
            var command = new Commands.Command
            {
                WorkingDirectory = repo, Context = repo, Log = log, RaiseError = false,
                Args = $"--literal-pathspecs add -- {Path.Quoted()}",
            };
            if (!await command.ExecAsync())
                throw new Exception(App.Text("AIConflict.StageFailed"));
        }

        private static string Decode(byte[] bytes, out Encoding encoding)
        {
            if (bytes.Length > 1024 * 1024)
                throw new Exception(App.Text("AIConflict.TooLarge"));
            var text = Models.TextEncoding.Decode(bytes);
            // Reuse supported decoding, but require an exact round trip before writing in that encoding.
            encoding = new UTF8Encoding(false, true);
            if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
                encoding = new UTF8Encoding(true, true);
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }))
                encoding = new UnicodeEncoding(false, true, true);
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
                encoding = new UnicodeEncoding(true, true, true);
            else if (!encoding.GetBytes(text).AsSpan().SequenceEqual(bytes))
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                encoding = Encoding.GetEncoding("GB18030", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            }
            var roundtrip = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
            if (text.Contains('\0') || !roundtrip.AsSpan().SequenceEqual(bytes))
                throw new Exception(App.Text("AIConflict.Unsupported"));
            return text;
        }

        // Match complete standard/diff3/zdiff3 blocks, including custom marker sizes, across lines.
        [GeneratedRegex(@"^<{7,}[^\r\n]*\r?\n.*?^={7,}\r?\n.*?^>{7,}[^\r\n]*(?:\r?\n|$)", RegexOptions.Multiline | RegexOptions.Singleline)]
        private static partial Regex BLOCKS();

        [GeneratedRegex(@"^(?:<{7,}|>{7,}|\|{7,}|={7,})(?:\s|$)", RegexOptions.Multiline)]
        private static partial Regex MARKERS();

        private sealed class Query : Commands.Command
        {
            public Query(string repo, string args, CancellationToken cancellation)
            {
                WorkingDirectory = repo;
                Context = repo;
                Args = args;
                CancellationToken = cancellation;
                CanAbortProcessOnCancel = true;
            }

            public async Task<string> ReadAsync()
            {
                var result = await ReadToEndAsync();
                CancellationToken.ThrowIfCancellationRequested();
                if (!result.IsSuccess)
                    throw new Exception(result.StdErr);
                return result.StdOut;
            }
        }

        private byte[] _originalBytes;
        private Encoding _encoding;
        private List<Commands.MergeConflictBlob.StageEntry> _entries;
        private string _head;
    }
}
