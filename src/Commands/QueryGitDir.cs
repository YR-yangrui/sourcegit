using System.IO;

namespace SourceGit.Commands
{
    public class QueryGitDir : Command
    {
        public QueryGitDir(string workDir)
        {
            WorkingDirectory = workDir;
            Args = "rev-parse --git-dir";
        }

        public string GetResult()
        {
            return Parse(ReadToEnd());
        }

        public static string GetRepositoryGitDir(string repo)
        {
            var fullpath = Path.Combine(repo, ".git");
            try
            {
                if (Directory.Exists(fullpath))
                {
                    if (Directory.Exists(Path.Combine(fullpath, "refs")) &&
                        Directory.Exists(Path.Combine(fullpath, "objects")) &&
                        File.Exists(Path.Combine(fullpath, "HEAD")))
                        return fullpath;

                    return null;
                }

                if (File.Exists(fullpath))
                {
                    var redirect = File.ReadAllText(fullpath).Trim();
                    if (redirect.StartsWith("gitdir: ", System.StringComparison.Ordinal))
                        redirect = redirect.Substring(8);

                    if (!Path.IsPathRooted(redirect))
                        redirect = Path.GetFullPath(Path.Combine(repo, redirect));

                    if (Directory.Exists(redirect))
                        return redirect;

                    return null;
                }
            }
            catch (IOException)
            {
                // Fall back to git when direct .git probing is blocked by IO races.
            }
            catch (System.UnauthorizedAccessException)
            {
                // Fall back to git when direct .git probing is blocked by permissions.
            }

            return new QueryGitDir(repo) { RaiseError = false }.GetResult();
        }

        private string Parse(Result rs)
        {
            if (!rs.IsSuccess)
                return null;

            var stdout = rs.StdOut.Trim();
            if (string.IsNullOrEmpty(stdout))
                return null;

            return Path.IsPathRooted(stdout) ? stdout : Path.GetFullPath(Path.Combine(WorkingDirectory, stdout));
        }
    }
}
