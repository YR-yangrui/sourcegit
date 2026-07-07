using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SourceGit.Views
{
    public partial class StructuredDiffView : UserControl
    {
        public StructuredDiffView()
        {
            InitializeComponent();
        }

        private void OnShowFormattedPrefabDiff(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.StructuredDiffContext vm)
                vm.ShowFormattedPrefabDiff();
        }

        private void OnShowPrefabHierarchy(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.StructuredDiffContext vm)
                vm.ShowPrefabHierarchy();
        }

        private void OnShowRawPrefabDiff(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.StructuredDiffContext vm)
                vm.ShowRawPrefabDiff();
        }

        private void OnToggleTableFilter(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.StructuredDiffContext vm)
                vm.ToggleTableFilter();
        }

        private void OnTogglePrefabFilter(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.StructuredDiffContext vm)
                vm.TogglePrefabFilter();
        }
    }
}
