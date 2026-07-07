using System.Threading.Tasks;
using Avalonia.Threading;

namespace SourceGit.ViewModels
{
    public class ExternalMergeTool : Popup
    {
        public ExternalMergeTool(WorkingCopy workingCopy, Models.Change change)
        {
            _workingCopy = workingCopy;
            _change = change;
            ProgressDescription = "Preparing external merge tool...";
        }

        public override async Task<bool> Sure()
        {
            ProgressDescription = "Preparing external merge tool...";
            return await _workingCopy.UseExternalMergeToolAsync(_change, UpdateProgress);
        }

        private void UpdateProgress(string message)
        {
            if (Dispatcher.UIThread.CheckAccess())
                ProgressDescription = message;
            else
                Dispatcher.UIThread.Post(() => ProgressDescription = message);
        }

        private readonly WorkingCopy _workingCopy = null;
        private readonly Models.Change _change = null;
    }
}
