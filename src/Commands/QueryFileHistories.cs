using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class QueryFileHistories : Command
    {
        private const int MAX_PATH_ARGS_LENGTH = 12000;

        public QueryFileHistories(string repo, string range, IReadOnlyList<string> paths)
        {
            WorkingDirectory = repo;
            Context = repo;
            RaiseError = false;
            _range = range;
            _paths = paths;

            foreach (var path in paths)
                _pathSet.Add(path);
        }

        public async Task<Dictionary<string, List<Models.FileVersion>>> GetResultAsync()
        {
            var histories = new Dictionary<string, List<Models.FileVersion>>(StringComparer.Ordinal);
            foreach (var path in _paths)
                histories[path] = [];

            foreach (var batch in MakeBatches())
            {
                Args = BuildArgs(batch);
                var rs = await ReadToEndAsync().ConfigureAwait(false);
                if (rs.IsSuccess)
                    Parse(rs.StdOut, histories);
            }

            return histories;
        }

        private List<List<string>> MakeBatches()
        {
            var batches = new List<List<string>>();
            var current = new List<string>();
            var currentLen = 0;

            foreach (var path in _paths)
            {
                var quoted = path.Quoted();
                var nextLen = quoted.Length + 1;
                if (current.Count > 0 && currentLen + nextLen > MAX_PATH_ARGS_LENGTH)
                {
                    batches.Add(current);
                    current = [];
                    currentLen = 0;
                }

                current.Add(path);
                currentLen += nextLen;
            }

            if (current.Count > 0)
                batches.Add(current);

            return batches;
        }

        private string BuildArgs(List<string> paths)
        {
            var builder = new StringBuilder();
            builder.Append("log --no-show-signature --date-order -n 10000 --decorate=no --format=\"@%H%x00%P%x00%aN±%aE%x00%at%x00%s\" --name-status ");
            builder.Append(_range);
            builder.Append(" --");

            foreach (var path in paths)
                builder.Append(' ').Append(path.Quoted());

            return builder.ToString();
        }

        private void Parse(string output, Dictionary<string, List<Models.FileVersion>> histories)
        {
            Models.FileVersion current = null;
            var touched = new Dictionary<string, Models.Change>(StringComparer.Ordinal);

            void Flush()
            {
                if (current == null || touched.Count == 0)
                    return;

                foreach (var pair in touched)
                {
                    if (!histories.TryGetValue(pair.Key, out var list))
                        continue;

                    list.Add(new Models.FileVersion()
                    {
                        SHA = current.SHA,
                        HasParent = current.HasParent,
                        Author = current.Author,
                        AuthorTime = current.AuthorTime,
                        Subject = current.Subject,
                        Change = pair.Value,
                    });
                }

                touched.Clear();
            }

            var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.StartsWith('@'))
                {
                    Flush();

                    var parts = line.Split('\0');
                    if (parts.Length != 5)
                    {
                        current = null;
                        continue;
                    }

                    current = new Models.FileVersion()
                    {
                        SHA = parts[0].Substring(1),
                        HasParent = !string.IsNullOrEmpty(parts[1]),
                        Author = Models.User.FindOrAdd(parts[2]),
                        AuthorTime = ulong.Parse(parts[3]),
                        Subject = parts[4],
                    };
                    continue;
                }

                if (current == null)
                    continue;

                var fields = line.Split('\t');
                if (fields.Length < 2)
                    continue;

                var status = fields[0];
                if (status.StartsWith('R') || status.StartsWith('C'))
                {
                    if (fields.Length < 3)
                        continue;

                    var oldPath = fields[1];
                    var newPath = fields[2];
                    var key = _pathSet.Contains(newPath) ? newPath : (_pathSet.Contains(oldPath) ? oldPath : null);
                    if (key == null)
                        continue;

                    var change = new Models.Change() { Path = $"{oldPath}\t{newPath}" };
                    change.Set(status[0] == 'R' ? Models.ChangeState.Renamed : Models.ChangeState.Copied);
                    touched[key] = change;
                }
                else
                {
                    var path = fields[1];
                    if (!_pathSet.Contains(path))
                        continue;

                    var change = new Models.Change() { Path = path };
                    switch (status[0])
                    {
                        case 'M':
                            change.Set(Models.ChangeState.Modified);
                            break;
                        case 'A':
                            change.Set(Models.ChangeState.Added);
                            break;
                        case 'D':
                            change.Set(Models.ChangeState.Deleted);
                            break;
                        case 'T':
                            change.Set(Models.ChangeState.TypeChanged);
                            break;
                        default:
                            continue;
                    }

                    touched[path] = change;
                }
            }

            Flush();
        }

        private readonly string _range = string.Empty;
        private readonly IReadOnlyList<string> _paths = [];
        private readonly HashSet<string> _pathSet = new(StringComparer.Ordinal);
    }
}
