using Avalonia.Collections;

namespace SourceGit.Models
{
    public class UnstagedChangeCategory
    {
        public string Name
        {
            get;
            set;
        } = string.Empty;

        public AvaloniaList<string> Paths
        {
            get;
            set;
        } = [];

        public bool IsUncategorized => string.IsNullOrEmpty(Name);

        public string DisplayName => IsUncategorized ? App.Text("WorkingCopy.Unstaged.Uncategorized") : Name;
    }
}
