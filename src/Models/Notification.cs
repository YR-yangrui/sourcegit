using System;

namespace SourceGit.Models
{
    public class Notification
    {
        public static event Action<Notification> Raised;

        public string Group { get; set; }
        public string Message { get; set; }
        public bool IsError { get; set; }

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
