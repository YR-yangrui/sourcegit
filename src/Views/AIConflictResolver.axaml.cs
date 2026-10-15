using System;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SourceGit.Views
{
    public partial class AIConflictResolver : ChromelessWindow
    {
        public AIConflictResolver()
        {
            InitializeComponent();
        }

        protected override async void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            if (DataContext is ViewModels.AIConflictResolver { AutoStart: true })
                await RunAsync();
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            if (DataContext is ViewModels.AIConflictResolver { IsRunning: true } vm)
            {
                // Keep the owner disabled until the active write/Git command has finished.
                e.Cancel = true;
                _closeRequested = true;
                vm.Cancel();
            }
            base.OnClosing(e);
        }

        private async Task RunAsync()
        {
            if (DataContext is not ViewModels.AIConflictResolver vm || vm.IsRunning)
                return;
            await vm.RunAsync();
            if (_closeRequested || vm.Succeeded)
                Close(vm.Succeeded);
        }

        private async void OnGenerate(object sender, RoutedEventArgs e) => await RunAsync();
        private void OnApply(object sender, RoutedEventArgs e) => (DataContext as ViewModels.AIConflictResolver)?.Apply();
        private void OnStop(object sender, RoutedEventArgs e) => Close(false);

        private bool _closeRequested;
    }
}
