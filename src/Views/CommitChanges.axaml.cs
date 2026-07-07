using System;
using System.Text;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace SourceGit.Views
{
    public partial class CommitChanges : UserControl
    {
        public CommitChanges()
        {
            InitializeComponent();
        }

        private void OnChangeContextRequested(object sender, ContextRequestedEventArgs e)
        {
            e.Handled = true;

            if (sender is not ChangeCollectionView view)
                return;

            var detailView = this.FindAncestorOfType<CommitDetail>();
            if (detailView == null)
                return;

            if (view.GetSelectedSingleFolder() is { } node)
            {
                var changes = view.GetSelectedChangesInSingleFolder();
                detailView.CreateChangeContextMenuByFolder(node, changes)?.Open(view);
            }
            else if (view.GetSelectedChangesIncludingFolders() is not { Count: > 0 } changes)
            {
                return;
            }
            else if (changes.Count > 1)
            {
                detailView.CreateMultipleChangesContextMenu(changes, view.GetSelectedPaths())?.Open(view);
            }
            else
            {
                detailView.CreateChangeContextMenu(changes[0])?.Open(view);
            }
        }

        private async void OnChangeCollectionViewKeyDown(object sender, KeyEventArgs e)
        {
            if (DataContext is not ViewModels.CommitDetail vm)
                return;

            if (sender is not ChangeCollectionView view)
                return;

            var cmdKey = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
            if (e.Key == Key.C && e.KeyModifiers.HasFlag(cmdKey))
            {
                var selectedPaths = view.GetSelectedPaths();
                if (selectedPaths.Count == 0)
                    return;

                var builder = new StringBuilder();
                var copyAbsPath = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                if (selectedPaths.Count == 1)
                {
                    builder.Append(copyAbsPath ? vm.GetAbsPath(selectedPaths[0]) : selectedPaths[0]);
                }
                else
                {
                    foreach (var path in selectedPaths)
                        builder.AppendLine(copyAbsPath ? vm.GetAbsPath(path) : path);
                }

                await this.CopyTextAsync(builder.ToString());
                e.Handled = true;
            }
            else if (e.Key == Key.F && e.KeyModifiers == cmdKey)
            {
                CommitChangeSearchBox.Focus();
                e.Handled = true;
            }
        }
    }
}
