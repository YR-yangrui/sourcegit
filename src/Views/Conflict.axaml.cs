using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace SourceGit.Views
{
    public partial class Conflict : UserControl
    {
        public Conflict()
        {
            InitializeComponent();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            if (DataContext is ViewModels.Conflict vm)
                vm.DetachFromHistoryCache();

            base.OnDetachedFromVisualTree(e);
        }

        private void OnPressedSHA(object sender, PointerPressedEventArgs e)
        {
            var repoView = this.FindAncestorOfType<Repository>();
            if (repoView is { DataContext: ViewModels.Repository repo } && sender is TextBlock text)
            {
                if (text.DataContext is Models.Commit commit)
                    repo.NavigateToCommit(commit.SHA);
                else if (text.DataContext is Models.FileVersion version)
                    repo.NavigateToCommit(version.SHA);
                else
                    repo.NavigateToCommit(text.Text);
            }

            e.Handled = true;
        }

        private async void OnUseTheirs(object _, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.Conflict vm)
                await vm.UseTheirsAsync();

            e.Handled = true;
        }

        private async void OnUseMine(object _, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.Conflict vm)
                await vm.UseMineAsync();

            e.Handled = true;
        }

        private async void OnMerge(object _, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.Conflict vm)
            {
                var request = await vm.CreateOpenMergeEditorRequestAsync();
                this.ShowWindow(request);
            }

            e.Handled = true;
        }

        private async void OnMergeExternal(object _, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.Conflict vm)
                await vm.MergeExternalAsync();

            e.Handled = true;
        }
    }
}
