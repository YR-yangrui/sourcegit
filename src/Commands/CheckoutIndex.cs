using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class CheckoutIndex : Command
    {
        public CheckoutIndex(string repo)
        {
            WorkingDirectory = repo;
            Context = repo;
        }

        public async Task<bool> CheckoutStageAsync(int stage, IReadOnlyList<string> files)
        {
            if (files.Count == 0)
                return true;

            Args = $"checkout-index -f --stage={stage} -z --stdin";
            using var span = StartGitDiagnosticSpan("exec");
            Log?.AppendLine($"$ git {Args} ({files.Count} path(s))\n");

            var start = CreateGitStartInfo(true);
            start.RedirectStandardInput = true;
            start.StandardInputEncoding = new UTF8Encoding(false);

            using var proc = new Process() { StartInfo = start };
            try
            {
                proc.Start();
            }
            catch (Exception e)
            {
                span.Set("startError", e.Message);
                span.Set("success", false);
                if (RaiseError)
                    RaiseException(e.Message);

                Log?.AppendLine(string.Empty);
                return false;
            }

            var stdout = proc.StandardOutput.ReadToEndAsync(CancellationToken);
            var stderr = proc.StandardError.ReadToEndAsync(CancellationToken);

            await using (var writer = proc.StandardInput)
            {
                foreach (var file in files)
                {
                    await writer.WriteAsync(file).ConfigureAwait(false);
                    await writer.WriteAsync('\0').ConfigureAwait(false);
                }
            }

            try
            {
                await proc.WaitForExitAsync(CancellationToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                span.Set("waitError", e.Message);
            }

            var output = await stdout.ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);
            AppendOutput(output);
            AppendOutput(error);

            span.Set("exitCode", proc.ExitCode);
            span.Set("canceled", CancellationToken.IsCancellationRequested);
            span.Set("success", !CancellationToken.IsCancellationRequested && proc.ExitCode == 0);
            Log?.AppendLine(string.Empty);

            if (CancellationToken.IsCancellationRequested)
                return false;

            if (proc.ExitCode == 0)
                return true;

            if (RaiseError)
            {
                var message = string.IsNullOrWhiteSpace(error) ? output : error;
                if (!string.IsNullOrWhiteSpace(message))
                    RaiseException(message.Trim());
            }

            return false;
        }

        private void AppendOutput(string content)
        {
            if (string.IsNullOrEmpty(content) || Log == null)
                return;

            var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            foreach (var line in lines)
            {
                if (line.Length > 0)
                    Log.AppendLine(line);
            }
        }
    }
}
