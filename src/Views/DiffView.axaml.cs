using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace SourceGit.Views
{
    public partial class DiffView : UserControl
    {
        public DiffView()
        {
            InitializeComponent();
        }

        public void ToggleHotkeyBindings(bool enabled)
        {
            var isMacOS = OperatingSystem.IsMacOS();
            if (enabled)
            {
                BtnGotoFirstChange.HotKey = KeyGesture.Parse(isMacOS ? "Cmd+Alt+Home" : "Ctrl+Alt+Home");
                BtnGotoPrevChange.HotKey = KeyGesture.Parse(isMacOS ? "Cmd+Alt+Up" : "Ctrl+Alt+Up");
                BtnGotoNextChange.HotKey = KeyGesture.Parse(isMacOS ? "Cmd+Alt+Down" : "Ctrl+Alt+Down");
                BtnGotoLastChange.HotKey = KeyGesture.Parse(isMacOS ? "Cmd+Alt+End" : "Ctrl+Alt+End");
                BtnOpenExternalMergeTool.HotKey = KeyGesture.Parse(isMacOS ? "Cmd+Shift+D" : "Ctrl+Shift+D");
            }
            else
            {
                BtnGotoFirstChange.HotKey = null;
                BtnGotoPrevChange.HotKey = null;
                BtnGotoNextChange.HotKey = null;
                BtnGotoLastChange.HotKey = null;
                BtnOpenExternalMergeTool.HotKey = null;
            }
        }

        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);

            if (DataContext is ViewModels.DiffContext vm)
                vm.CheckSettings();

            ToggleHotkeyBindings(IsEffectivelyVisible);
        }

        private void OnGotoFirstChange(object _, RoutedEventArgs e)
        {
            this.FindDescendantOfType<ThemedTextDiffPresenter>()?.GotoChange(ViewModels.BlockNavigationDirection.First);
            e.Handled = true;
        }

        private void OnGotoPrevChange(object _, RoutedEventArgs e)
        {
            this.FindDescendantOfType<ThemedTextDiffPresenter>()?.GotoChange(ViewModels.BlockNavigationDirection.Prev);
            e.Handled = true;
        }

        private void OnGotoNextChange(object _, RoutedEventArgs e)
        {
            this.FindDescendantOfType<ThemedTextDiffPresenter>()?.GotoChange(ViewModels.BlockNavigationDirection.Next);
            e.Handled = true;
        }

        private void OnGotoLastChange(object _, RoutedEventArgs e)
        {
            this.FindDescendantOfType<ThemedTextDiffPresenter>()?.GotoChange(ViewModels.BlockNavigationDirection.Last);
            e.Handled = true;
        }

        private void OnToggleButtonPropertyChanged(object sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == ToggleButton.IsCheckedProperty && DataContext is ViewModels.DiffContext vm)
                vm.CheckSettings();
        }

        private void OnTitlePointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (sender is SelectableTextBlock title && e.ClickCount >= 3)
            {
                title.SelectAll();
                e.Handled = true;
            }
        }

        private void OnTitleContextRequested(object sender, ContextRequestedEventArgs e)
        {
            if (sender is not SelectableTextBlock title || DataContext is not ViewModels.DiffContext vm)
                return;

            var copy = new MenuItem();
            copy.Header = App.Text("Copy");
            copy.Icon = this.CreateMenuIcon("Icons.Copy");
            copy.IsEnabled = !string.IsNullOrEmpty(title.SelectedText) || !string.IsNullOrEmpty(vm.Title);
            copy.Click += async (_, ev) =>
            {
                var selected = title.SelectedText;
                await this.CopyTextAsync(string.IsNullOrEmpty(selected) ? vm.Title : selected);
                ev.Handled = true;
            };

            var copyPath = new MenuItem();
            copyPath.Header = App.Text("CopyPath");
            copyPath.Icon = this.CreateMenuIcon("Icons.Copy");
            copyPath.Click += async (_, ev) =>
            {
                await this.CopyTextAsync(vm.RelativePath);
                ev.Handled = true;
            };

            var copyFullPath = new MenuItem();
            copyFullPath.Header = App.Text("CopyFullPath");
            copyFullPath.Icon = this.CreateMenuIcon("Icons.Copy");
            copyFullPath.Click += async (_, ev) =>
            {
                await this.CopyTextAsync(vm.FullPath);
                ev.Handled = true;
            };

            var menu = new ContextMenu();
            menu.Items.Add(copy);
            menu.Items.Add(new MenuItem() { Header = "-" });
            menu.Items.Add(copyPath);
            menu.Items.Add(copyFullPath);
            menu.Open(title);
            e.Handled = true;
        }

        private void OnOpenSubmoduleRevisionCompare(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: Models.SubmoduleDiff diff } && diff.CanOpenDetails)
            {
                var vm = new ViewModels.SubmoduleRevisionCompare(diff);
                this.ShowWindow(vm);
            }
        }
    }
}
