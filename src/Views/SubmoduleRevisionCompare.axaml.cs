using System;
using System.IO;
using System.Text;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace SourceGit.Views
{
    public partial class SubmoduleRevisionCompare : ChromelessWindow
    {
        public SubmoduleRevisionCompare()
        {
            InitializeComponent();
        }

        private void OnChangeContextRequested(object sender, ContextRequestedEventArgs e)
        {
            if (DataContext is ViewModels.SubmoduleRevisionCompare vm && sender is ChangeCollectionView view)
            {
                var menu = new ContextMenu();
                var selectedFolder = view.GetSelectedSingleFolder();
                var hasSelectedFolder = selectedFolder != null;
                var selected = view.GetSelectedChangesIncludingFolders();
                var selectedPaths = view.GetSelectedPaths();
                if (selected is not { Count: > 0 })
                {
                    e.Handled = true;
                    return;
                }

                var patch = new MenuItem();
                patch.Header = App.Text("FileCM.SaveAsPatch");
                patch.Icon = this.CreateMenuIcon("Icons.Save");
                patch.Click += async (_, e) =>
                {
                    var storageProvider = this.StorageProvider;
                    if (storageProvider == null)
                        return;

                    var options = new FilePickerSaveOptions();
                    options.Title = App.Text("FileCM.SaveAsPatch");
                    options.DefaultExtension = ".patch";
                    options.FileTypeChoices = [new FilePickerFileType("Patch File") { Patterns = ["*.patch"] }];

                    try
                    {
                        var storageFile = await storageProvider.SaveFilePickerAsync(options);
                        if (storageFile != null)
                        {
                            var saveTo = storageFile.Path.LocalPath;
                            var succ = await vm.SaveChangesAsPatchAsync(selected, saveTo);
                            if (succ)
                                await new Alert().ShowAsync(this, "Save patch successfully.", false);
                        }
                    }
                    catch (Exception exception)
                    {
                        await new Alert().ShowAsync(this, $"Failed to save as patch: {exception.Message}", true);
                    }

                    e.Handled = true;
                };

                if (selected.Count == 1 && !hasSelectedFolder)
                {
                    var change = selected[0];
                    var openWithMerger = new MenuItem();
                    openWithMerger.Header = App.Text("OpenInExternalMergeTool");
                    openWithMerger.Icon = this.CreateMenuIcon("Icons.OpenWith");
                    openWithMerger.Tag = OperatingSystem.IsMacOS() ? "⌘+⇧+D" : "Ctrl+Shift+D";
                    openWithMerger.Click += (_, ev) =>
                    {
                        vm.OpenInExternalDiffTool(change);
                        ev.Handled = true;
                    };
                    menu.Items.Add(openWithMerger);

                    if (change.Index != Models.ChangeState.Deleted)
                    {
                        var full = vm.GetAbsPath(change.Path);
                        var explore = new MenuItem();
                        explore.Header = App.Text("RevealFile");
                        explore.Icon = this.CreateMenuIcon("Icons.Explore");
                        explore.IsEnabled = File.Exists(full);
                        explore.Click += (_, ev) =>
                        {
                            Native.OS.OpenInFileManager(full);
                            ev.Handled = true;
                        };
                        menu.Items.Add(explore);
                    }

                    menu.Items.Add(new MenuItem() { Header = "-" });
                    menu.Items.Add(patch);

                    var copyPath = new MenuItem();
                    copyPath.Header = App.Text("CopyPath");
                    copyPath.Icon = this.CreateMenuIcon("Icons.Copy");
                    copyPath.Tag = OperatingSystem.IsMacOS() ? "⌘+C" : "Ctrl+C";
                    copyPath.Click += async (_, ev) =>
                    {
                        await this.CopyTextAsync(change.Path);
                        ev.Handled = true;
                    };

                    var copyFullPath = new MenuItem();
                    copyFullPath.Header = App.Text("CopyFullPath");
                    copyFullPath.Icon = this.CreateMenuIcon("Icons.Copy");
                    copyFullPath.Tag = OperatingSystem.IsMacOS() ? "⌘+⇧+C" : "Ctrl+Shift+C";
                    copyFullPath.Click += async (_, ev) =>
                    {
                        await this.CopyTextAsync(vm.GetAbsPath(change.Path));
                        ev.Handled = true;
                    };

                    menu.Items.Add(new MenuItem() { Header = "-" });
                    menu.Items.Add(copyPath);
                    menu.Items.Add(copyFullPath);
                }
                else
                {
                    menu.Items.Add(patch);

                    var copyPath = new MenuItem();
                    copyPath.Header = App.Text("CopyPath");
                    copyPath.Icon = this.CreateMenuIcon("Icons.Copy");
                    copyPath.Tag = OperatingSystem.IsMacOS() ? "⌘+C" : "Ctrl+C";
                    copyPath.Click += async (_, ev) =>
                    {
                        if (selectedPaths.Count == 1)
                        {
                            await this.CopyTextAsync(selectedPaths[0]);
                        }
                        else
                        {
                            var builder = new StringBuilder();
                            foreach (var path in selectedPaths)
                                builder.AppendLine(path);

                            await this.CopyTextAsync(builder.ToString());
                        }

                        ev.Handled = true;
                    };

                    var copyFullPath = new MenuItem();
                    copyFullPath.Header = App.Text("CopyFullPath");
                    copyFullPath.Icon = this.CreateMenuIcon("Icons.Copy");
                    copyFullPath.Tag = OperatingSystem.IsMacOS() ? "⌘+⇧+C" : "Ctrl+Shift+C";
                    copyFullPath.Click += async (_, ev) =>
                    {
                        if (selectedPaths.Count == 1)
                        {
                            await this.CopyTextAsync(vm.GetAbsPath(selectedPaths[0]));
                        }
                        else
                        {
                            var builder = new StringBuilder();
                            foreach (var path in selectedPaths)
                                builder.AppendLine(vm.GetAbsPath(path));

                            await this.CopyTextAsync(builder.ToString());
                        }

                        ev.Handled = true;
                    };

                    menu.Items.Add(new MenuItem() { Header = "-" });
                    menu.Items.Add(copyPath);
                    menu.Items.Add(copyFullPath);
                }

                menu.Open(view);
            }

            e.Handled = true;
        }

        private async void OnChangeCollectionViewKeyDown(object sender, KeyEventArgs e)
        {
            if (DataContext is not ViewModels.SubmoduleRevisionCompare vm)
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
                ChangeSearchBox.Focus();
                e.Handled = true;
            }
        }
    }
}
