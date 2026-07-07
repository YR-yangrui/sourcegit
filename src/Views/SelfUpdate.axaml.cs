using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.TextMate;

namespace SourceGit.Views
{
    public class UpdateInfoView : TextEditor
    {
        protected override Type StyleKeyOverride => typeof(TextEditor);

        public UpdateInfoView() : base(new TextArea(), new TextDocument())
        {
            IsReadOnly = true;
            ShowLineNumbers = false;
            WordWrap = true;
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

            TextArea.TextView.Margin = new Thickness(4, 0);
            TextArea.TextView.Options.EnableHyperlinks = false;
            TextArea.TextView.Options.EnableEmailHyperlinks = false;
        }

        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);

            if (_textMate == null)
            {
                _textMate = Models.TextMateHelper.CreateForEditor(this);
                Models.TextMateHelper.SetGrammarByFileName(_textMate, "README.md");
            }
        }

        protected override void OnUnloaded(RoutedEventArgs e)
        {
            base.OnUnloaded(e);

            if (_propertyChanged != null)
            {
                _propertyChanged.PropertyChanged -= OnPropertyChanged;
                _propertyChanged = null;
            }

            if (_textMate != null)
            {
                _textMate.Dispose();
                _textMate = null;
            }

            GC.Collect();
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);

            if (_propertyChanged != null)
                _propertyChanged.PropertyChanged -= OnPropertyChanged;

            _propertyChanged = DataContext as INotifyPropertyChanged;
            if (_propertyChanged != null)
                _propertyChanged.PropertyChanged += OnPropertyChanged;

            RefreshText();
        }

        private void OnPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName) ||
                e.PropertyName == nameof(ViewModels.SelfUpdate.ChangelogPageText))
                RefreshText();
        }

        private void RefreshText()
        {
            if (DataContext is ViewModels.SelfUpdate vm)
                Text = vm.ChangelogPageText;
            else if (DataContext is Models.UpdateAvailable update)
                Text = update.Body;
            else
                Text = string.Empty;
        }

        private INotifyPropertyChanged _propertyChanged = null;
        private TextMate.Installation _textMate = null;
    }

    public partial class SelfUpdate : ChromelessWindow
    {
        public SelfUpdate()
        {
            CloseOnESC = true;
            InitializeComponent();
        }

        private void CloseWindow(object _1, RoutedEventArgs _2)
        {
            Close();
        }

        private async void InstallUpdate(object sender, RoutedEventArgs e)
        {
            if (_isInstallingUpdate)
            {
                e.Handled = true;
                return;
            }

            if (sender is Button { DataContext: Models.UpdateAvailable update })
            {
                _isInstallingUpdate = true;
                CloseOnESC = false;

                try
                {
                    if (DataContext is ViewModels.SelfUpdate vm)
                    {
                        vm.IsInstallingUpdate = true;
                        vm.UpdateInstallProgress(new Models.UpdateInstallProgress("SelfUpdate.InstallStage.Preparing"));

                        var progress = new Progress<Models.UpdateInstallProgress>(vm.UpdateInstallProgress);
                        await Models.UpdateInstaller.DownloadAndInstallAsync(update, progress);
                    }
                    else
                    {
                        await Models.UpdateInstaller.DownloadAndInstallAsync(update);
                    }

                    Close();
                }
                catch (Exception ex)
                {
                    if (DataContext is ViewModels.SelfUpdate vm)
                    {
                        vm.IsInstallingUpdate = false;
                        vm.ResetInstallProgress();
                        vm.Data = new Models.SelfUpdateFailed(ex);
                    }

                    _isInstallingUpdate = false;
                    CloseOnESC = true;
                }
            }

            e.Handled = true;
        }

        private void GotoDownload(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: Models.UpdateAvailable update } &&
                !string.IsNullOrWhiteSpace(update.ReleasePageUrl))
                Native.OS.OpenBrowser(update.ReleasePageUrl);

            e.Handled = true;
        }

        private void IgnoreThisVersion(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: Models.UpdateAvailable update })
                ViewModels.Preferences.Instance.IgnoreUpdate(update.Channel, update.TagName);

            Close();
            e.Handled = true;
        }

        private void GoPreviousChangelogPage(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.SelfUpdate vm)
                vm.GoPreviousChangelogPage();

            e.Handled = true;
        }

        private void GoNextChangelogPage(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.SelfUpdate vm)
                vm.GoNextChangelogPage();

            e.Handled = true;
        }

        private void OnChangelogPageNumberLostFocus(object sender, RoutedEventArgs e)
        {
            CommitChangelogPageNumber(sender as TextBox);
            e.Handled = true;
        }

        private void OnChangelogPageNumberKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox textBox)
                return;

            if (e.Key == Key.Enter)
            {
                CommitChangelogPageNumber(textBox);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                ResetChangelogPageNumber(textBox);
                e.Handled = true;
            }
        }

        private void CommitChangelogPageNumber(TextBox textBox)
        {
            if (textBox == null || DataContext is not ViewModels.SelfUpdate vm)
                return;

            var text = textBox.Text?.Trim() ?? string.Empty;
            if (TryParseChangelogPageNumber(text, out var pageNumber))
                vm.GoToChangelogPage(pageNumber);

            textBox.Text = vm.ChangelogPageNumberText;
        }

        private static bool TryParseChangelogPageNumber(string text, out int pageNumber)
        {
            if (int.TryParse(text, out pageNumber))
                return true;

            if (string.IsNullOrEmpty(text))
                return false;

            var start = text[0] == '+' || text[0] == '-' ? 1 : 0;
            if (start == text.Length)
                return false;

            for (var i = start; i < text.Length; i++)
            {
                if (!char.IsDigit(text[i]))
                    return false;
            }

            pageNumber = text[0] == '-' ? int.MinValue : int.MaxValue;
            return true;
        }

        private void ResetChangelogPageNumber(TextBox textBox)
        {
            if (DataContext is ViewModels.SelfUpdate vm)
                textBox.Text = vm.ChangelogPageNumberText;
        }

        private bool _isInstallingUpdate = false;
    }
}
