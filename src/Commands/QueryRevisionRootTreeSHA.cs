using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class QueryRevisionRootTreeSHA : Command
    {
        public QueryRevisionRootTreeSHA(string repo, string revision)
        {
            WorkingDirectory = repo;
            Context = repo;
            CanAbortProcessOnCancel = true;
            Args = $"rev-parse {revision}^{{tree}}";
        }

        public async Task<string> GetResultAsync()
        {
            var rs = await ReadToEndAsync().ConfigureAwait(false);
            return rs.IsSuccess ? rs.StdOut.Trim() : string.Empty;
        }
    }
}
