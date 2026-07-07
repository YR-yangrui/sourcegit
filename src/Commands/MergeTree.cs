using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class MergeTree : Command
    {
        public MergeTree(string repo, string source, string dest, string strategyOption)
        {
            WorkingDirectory = repo;

            var builder = new StringBuilder();
            builder.Append("merge-tree --write-tree ");
            if (!string.IsNullOrEmpty(strategyOption))
                builder.Append("-X ").Append(strategyOption).Append(' ');
            builder.Append(source).Append(' ').Append(dest);

            Args = builder.ToString();
        }

        public async Task<int> GetExitCodeAsync()
        {
            using var proc = new Process();
            proc.StartInfo = CreateGitStartInfo(false);

            var exitCode = -1;
            try
            {
                proc.Start();
                await proc.WaitForExitAsync().ConfigureAwait(false);
                exitCode = proc.ExitCode;
            }
            catch
            {
                // Ignore any exceptions and just return -1
            }

            return exitCode;
        }
    }
}
