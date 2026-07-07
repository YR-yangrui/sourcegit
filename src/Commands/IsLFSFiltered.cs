using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class IsLFSFiltered : Command
    {
        public IsLFSFiltered(string repo, string path)
        {
            WorkingDirectory = repo;
            Context = repo;
            Args = $"check-attr -z filter {path.Quoted()}";
            RaiseError = false;
        }

        public IsLFSFiltered(string repo, string sha, string path)
        {
            WorkingDirectory = repo;
            Context = repo;
            Args = IsIndexRevision(sha) ?
                $"check-attr --cached -z filter -- {path.Quoted()}" :
                $"check-attr --source {sha} -z filter {path.Quoted()}";
            RaiseError = false;
        }

        public bool GetResult()
        {
            return Parse(ReadToEnd());
        }

        public async Task<bool> GetResultAsync()
        {
            var rs = await ReadToEndAsync().ConfigureAwait(false);
            return Parse(rs);
        }

        private bool Parse(Result rs)
        {
            return rs.IsSuccess && rs.StdOut.Contains("filter\0lfs");
        }

        private static bool IsIndexRevision(string revision)
        {
            return revision is { Length: 2 } &&
                revision[0] == ':' &&
                revision[1] >= '0' &&
                revision[1] <= '3';
        }
    }
}
