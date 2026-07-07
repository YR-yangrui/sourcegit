using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public partial class QueryRevisionFileObjectSHA : Command
    {
        [GeneratedRegex(@"^\d+\s+blob\s+([0-9a-f]+)\s+.*$")]
        private static partial Regex REG_FORMAT();

        public QueryRevisionFileObjectSHA(string repo, string revision, string file)
        {
            WorkingDirectory = repo;
            Context = repo;
            Args = $"ls-tree {revision} -- {file.Quoted()}";
        }

        public async Task<string> GetResultAsync()
        {
            var rs = await ReadToEndAsync().ConfigureAwait(false);
            if (rs.IsSuccess)
            {
                var match = REG_FORMAT().Match(rs.StdOut);
                if (match.Success)
                    return match.Groups[1].Value;
            }

            return null;
        }
    }
}
