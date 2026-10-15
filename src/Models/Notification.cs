using System;
using System.IO;
using System.Text.RegularExpressions;

namespace SourceGit.Models
{
    public class Notification
    {
        public static event Action<Notification> Raised;

        public string Group { get; set; }
        public string Message { get; set; }
        public bool IsError { get; set; }

        public string IndexLockPath
        {
            get
            {
                if (!IsError || string.IsNullOrEmpty(Message))
                    return null;

                // Use the exact path from Git's existing-index-lock error so worktrees and
                // submodules do not accidentally target the parent repository's index.
                var match = Regex.Match(Message, @"(?m)^fatal: Unable to create '([^\r\n]+[/\\]index\.lock)': File exists\.?\r?$");
                return match.Success && Path.IsPathFullyQualified(match.Groups[1].Value) ? match.Groups[1].Value : null;
            }
        }

        public bool CanRemoveIndexLock => IndexLockPath != null;

        public static void Send(string group, string message, bool isError = false)
        {
            if (isError)
            {
                var repoPath = SourceGit.Diagnostics.DiagnosticManager.GetRepositoryPath(group);
                SourceGit.Diagnostics.DiagnosticManager.Warning(
                    "Notification",
                    "notification.error",
                    SourceGit.Diagnostics.DiagnosticManager.Redact(message),
                    SourceGit.Diagnostics.DiagnosticManager.CreateData(
                        ("repo", SourceGit.Diagnostics.DiagnosticManager.GetRepositoryId(repoPath)),
                        ("repoPath", repoPath)));
            }

            Raised?.Invoke(new Notification
            {
                Group = group,
                Message = message,
                IsError = isError
            });
        }
    }
}
