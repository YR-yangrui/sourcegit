using System;
using System.ComponentModel;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SourceGit.Views
{
    public partial class RevisionFiles : UserControl
    {
        public RevisionFiles()
        {
            InitializeComponent();
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);

            UpdateViewModelSubscription();
            UpdateSearchBoxIndicators();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);

            ClearViewModelSubscription();

            UpdateSearchBoxIndicators();
        }

        protected override async void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);
            UpdateViewModelSubscription();
            UpdateSearchBoxIndicators();

            if (DataContext is ViewModels.CommitDetail { ActiveTabIndex: 2 } vm && IsVisible)
                await ReloadFileTreeAsync();
        }

        private void OnToggleSearch(object _, RoutedEventArgs e)
        {
            TxtSearchRevisionFiles.Focus();
            e.Handled = true;
        }

        private async void OnSearchBoxKeyDown(object _, KeyEventArgs e)
        {
            if (DataContext is not ViewModels.CommitDetail vm)
                return;

            if (e.Key == Key.Enter)
            {
                var target = SearchSuggestionBox.SelectedItem as string;
                await ApplySearchResultByEnterAsync(string.IsNullOrEmpty(target) ? vm.RevisionFileSearchFilter : target);
                e.Handled = true;
            }
            else if (e.Key == Key.Down || e.Key == Key.Up)
            {
                if (vm.RevisionFileSearchSuggestion is { Count: > 0 })
                {
                    SearchSuggestionBox.Focus(NavigationMethod.Tab);
                    SearchSuggestionBox.SelectedIndex = 0;
                }

                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                vm.CancelRevisionFileSuggestions();
                e.Handled = true;
            }
        }

        private async void OnSearchBoxTextChanged(object _, TextChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(TxtSearchRevisionFiles.Text))
                await FileTree.SetSearchResultAsync(null);
        }

        private async void OnSearchSuggestionBoxKeyDown(object _, KeyEventArgs e)
        {
            if (DataContext is not ViewModels.CommitDetail vm)
                return;

            if (e.Key == Key.Escape)
            {
                vm.CancelRevisionFileSuggestions();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && SearchSuggestionBox.SelectedItem is string content)
            {
                vm.RevisionFileSearchFilter = content;
                TxtSearchRevisionFiles.CaretIndex = content.Length;
                await ApplySearchResultByEnterAsync(vm.RevisionFileSearchFilter);
                e.Handled = true;
            }
        }

        private async void OnSearchSuggestionDoubleTapped(object sender, TappedEventArgs e)
        {
            if (DataContext is not ViewModels.CommitDetail vm)
                return;

            var content = (sender as StackPanel)?.DataContext as string;
            if (!string.IsNullOrEmpty(content))
            {
                vm.RevisionFileSearchFilter = content;
                TxtSearchRevisionFiles.CaretIndex = content.Length;
                await FileTree.SetSearchResultAsync(vm.RevisionFileSearchFilter);
            }

            e.Handled = true;
        }

        private async void OnOpenFileWithDefaultEditor(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.CommitDetail { CanOpenRevisionFileWithDefaultEditor: true } vm)
                await vm.OpenRevisionFileAsync(vm.ViewRevisionFilePath, null);

            e.Handled = true;
        }

        private void OnClearRevisionFileSearchFilter(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.CommitDetail vm)
                vm.ClearRevisionFileSearchFilter();

            e.Handled = true;
        }

        protected override async void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == IsVisibleProperty && IsVisible && FileTree != null)
                await ReloadFileTreeAsync();
        }

        private async void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModels.CommitDetail.IsRevisionFileSearchLoading) ||
                e.PropertyName == nameof(ViewModels.CommitDetail.RevisionFileSearchFilter))
                UpdateSearchBoxIndicators();

            if ((e.PropertyName == nameof(ViewModels.CommitDetail.ActiveTabIndex) ||
                 e.PropertyName == nameof(ViewModels.CommitDetail.Commit)) &&
                sender is ViewModels.CommitDetail { ActiveTabIndex: 2 } &&
                FileTree != null)
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    if (DataContext is ViewModels.CommitDetail { ActiveTabIndex: 2 } && FileTree != null)
                        await ReloadFileTreeAsync();
                });
            }
        }

        private async Task ApplySearchResultByEnterAsync(string file)
        {
            if (_isApplyingSearchResultByEnter)
                return;

            _isApplyingSearchResultByEnter = true;
            try
            {
                await FileTree.SetSearchResultAsync(file);
            }
            finally
            {
                _isApplyingSearchResultByEnter = false;
            }
        }

        private async Task ReloadFileTreeAsync()
        {
            if (DataContext is not ViewModels.CommitDetail vm)
                return;

            var hasSearchFilter = !string.IsNullOrWhiteSpace(vm.RevisionFileSearchFilter);
            vm.PreloadRevisionFileNames();

            await FileTree.ReloadAsync(!hasSearchFilter, !hasSearchFilter);

            if (hasSearchFilter)
                await FileTree.SetSearchResultAsync(vm.RevisionFileSearchFilter, true);
        }

        private void UpdateSearchBoxIndicators()
        {
            SearchRevisionFilesLoadingIcon.IsVisible = _vm?.IsRevisionFileSearchLoading == true;
            ClearRevisionFilesSearchButton.IsVisible = !string.IsNullOrEmpty(_vm?.RevisionFileSearchFilter);
        }

        private void UpdateViewModelSubscription()
        {
            var vm = DataContext as ViewModels.CommitDetail;
            if (ReferenceEquals(_vm, vm))
                return;

            ClearViewModelSubscription();

            _vm = vm;
            if (_vm != null)
                _vm.PropertyChanged += OnViewModelPropertyChanged;
        }

        private void ClearViewModelSubscription()
        {
            if (_vm != null)
            {
                _vm.PropertyChanged -= OnViewModelPropertyChanged;
                _vm = null;
            }
        }

        private ViewModels.CommitDetail _vm = null;
        private bool _isApplyingSearchResultByEnter = false;
    }
}
