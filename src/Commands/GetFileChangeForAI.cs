using System;
using System.Text;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class GetFileChangeForAI : Command
    {
        public GetFileChangeForAI(string repo, string file, string originalFile, string baseRevision = null, string revision = null)
        {
            WorkingDirectory = repo;
            Context = repo;

            var builder = new StringBuilder();
            builder.Append("diff --no-color --no-ext-diff --diff-algorithm=minimal ");
            if (!string.IsNullOrWhiteSpace(revision))
            {
                // Commit review must diff the historical revision pair instead of the current index.
                if (!string.IsNullOrWhiteSpace(baseRevision))
                    builder.Append(baseRevision.Quoted()).Append(' ');
                builder.Append(revision.Quoted()).Append(" -- ");
            }
            else
            {
                builder.Append("--cached -- ");
            }

            if (!string.IsNullOrEmpty(originalFile) && !file.Equals(originalFile, StringComparison.Ordinal))
                builder.Append(originalFile.Quoted()).Append(' ');
            builder.Append(file.Quoted());

            Args = builder.ToString();
        }

        public async Task<Result> ReadAsync()
        {
            return await ReadToEndAsync().ConfigureAwait(false);
        }
    }
}
