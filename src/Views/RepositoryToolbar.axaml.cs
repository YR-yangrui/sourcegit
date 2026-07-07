using System.Collections.Generic;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace SourceGit.Views
{
    public partial class RepositoryToolbar : UserControl
    {
        public RepositoryToolbar()
        {
            InitializeComponent();
        }

        private void OpenWithExternalTools(object sender, RoutedEventArgs ev)
        {
            if (sender is Button button && DataContext is ViewModels.Repository repo)
            {
                var fullpath = repo.FullPath;
                var menu = new ContextMenu();
                menu.Placement = PlacementMode.BottomEdgeAlignedLeft;

                RenderOptions.SetBitmapInterpolationMode(menu, BitmapInterpolationMode.HighQuality);
                RenderOptions.SetEdgeMode(menu, EdgeMode.Antialias);

                var visitableRemotes = repo.Remotes.FindAll(x => x.CanGetVisitURL());
                if (visitableRemotes.Count > 0)
                {
                    foreach (var remote in visitableRemotes)
                    {
                        var dupRemote = remote;

                        var item = new MenuItem();
                        item.Header = App.Text("Repository.Visit", dupRemote.Name);
                        item.Icon = this.CreateMenuIcon("Icons.Remotes");
                        item.Click += (_, e) =>
                        {
                            if (dupRemote.TryGetVisitURL(out var visit))
                                Native.OS.OpenBrowser(visit);

                            e.Handled = true;
                        };

                        menu.Items.Add(item);
                    }
                }

                var tools = Native.OS.ExternalTools;
                if (tools.Count > 0)
                {
                    if (menu.Items.Count > 0)
                        menu.Items.Add(new MenuItem() { Header = "-" });

                    MenuItem CreateOpenAsFolderMenuItem(Models.ExternalTool tool, string path)
                    {
                        var openAsFolder = new MenuItem();
                        openAsFolder.Header = App.Text("Repository.OpenAsFolder");
                        openAsFolder.Click += (_, e) =>
                        {
                            tool.Launch(path.Quoted());
                            e.Handled = true;
                        };
                        return openAsFolder;
                    }

                    foreach (var tool in tools)
                    {
                        var dupTool = tool;

                        var item = new MenuItem();
                        item.Header = App.Text("Repository.OpenIn", dupTool.Name);
                        item.Icon = new Image { Width = 16, Height = 16, Source = dupTool.IconImage };

                        if (dupTool.HasLaunchOptions)
                        {
                            var optionsLoaded = false;
                            item.SubmenuOpened += (_, _) =>
                            {
                                if (optionsLoaded)
                                    return;

                                optionsLoaded = true;

                                var options = dupTool.MakeLaunchOptions(fullpath);
                                if (options is { Count: > 0 })
                                {
                                    item.Items.Clear();
                                    foreach (var opt in options)
                                    {
                                        var subItem = new MenuItem();
                                        subItem.Header = opt.Title;
                                        subItem.Click += (_, e) =>
                                        {
                                            dupTool.Launch(opt.Args);
                                            e.Handled = true;
                                        };

                                        item.Items.Add(subItem);
                                    }

                                    item.Items.Add(new MenuItem() { Header = "-" });
                                }

                                item.Items.Add(CreateOpenAsFolderMenuItem(dupTool, fullpath));
                            };

                            item.Items.Add(CreateOpenAsFolderMenuItem(dupTool, fullpath));
                        }
                        else
                        {
                            item.Click += (_, e) =>
                            {
                                dupTool.Launch(fullpath.Quoted());
                                e.Handled = true;
                            };
                        }

                        menu.Items.Add(item);
                    }
                }

                menu.Open(button);
                ev.Handled = true;
            }
        }

        private void OpenFileBrowser(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.Repository repo)
            {
                Native.OS.OpenInFileManager(repo.FullPath);
                e.Handled = true;
            }
        }

        private void OpenTerminal(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.Repository repo)
            {
                Native.OS.OpenTerminal(repo.FullPath);
                e.Handled = true;
            }
        }

        private async void OpenStatistics(object _, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.Repository repo)
            {
                await this.ShowDialogAsync(new ViewModels.Statistics(repo.FullPath));
                e.Handled = true;
            }
        }

        private async void OpenConfigure(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.Repository repo)
            {
                await this.ShowDialogAsync(new ViewModels.RepositoryConfigure(repo));
                e.Handled = true;
            }
        }

        private async void Fetch(object sender, TappedEventArgs e)
        {
            if (DataContext is ViewModels.Repository repo)
            {
                await repo.FetchAsync(e.KeyModifiers is KeyModifiers.Control);
                e.Handled = true;
            }
        }

        private async void FetchDirectlyByHotKey(object sender, RoutedEventArgs e)
        {
            if (App.GetLauncher() is { CommandPalette: { } } launcher)
                return;

            if (DataContext is ViewModels.Repository repo)
            {
                await repo.FetchAsync(true);
                e.Handled = true;
            }
        }

        private async void Pull(object sender, TappedEventArgs e)
        {
            if (DataContext is ViewModels.Repository repo)
            {
                await repo.PullAsync(e.KeyModifiers is KeyModifiers.Control);
                e.Handled = true;
            }
        }

        private async void PullDirectlyByHotKey(object sender, RoutedEventArgs e)
        {
            if (App.GetLauncher() is { CommandPalette: { } } launcher)
                return;

            if (DataContext is ViewModels.Repository repo)
            {
                await repo.PullAsync(true);
                e.Handled = true;
            }
        }

        private async void Push(object sender, TappedEventArgs e)
        {
            if (DataContext is ViewModels.Repository repo)
            {
                await repo.PushAsync(e.KeyModifiers is KeyModifiers.Control);
                e.Handled = true;
            }
        }

        private async void PushDirectlyByHotKey(object sender, RoutedEventArgs e)
        {
            if (App.GetLauncher() is { CommandPalette: { } } launcher)
                return;

            if (DataContext is ViewModels.Repository repo)
            {
                await repo.PushAsync(true);
                e.Handled = true;
            }
        }

        private async void StashAll(object _, TappedEventArgs e)
        {
            if (DataContext is ViewModels.Repository repo)
            {
                await repo.StashAllAsync(e.KeyModifiers is KeyModifiers.Control);
                e.Handled = true;
            }
        }

        private List<MenuItem> CreateGitFlowMenuItems(ViewModels.Repository repo)
        {
            var items = new List<MenuItem>();

            if (repo.IsGitFlowEnabled())
            {
                var startFeature = new MenuItem();
                startFeature.Header = App.Text("GitFlow.StartFeature");
                startFeature.Icon = this.CreateMenuIcon("Icons.GitFlow.Feature");
                startFeature.Click += (_, e) =>
                {
                    if (repo.CanCreatePopup())
                        repo.ShowPopup(new ViewModels.GitFlowStart(repo, Models.GitFlowBranchType.Feature));
                    e.Handled = true;
                };

                var startRelease = new MenuItem();
                startRelease.Header = App.Text("GitFlow.StartRelease");
                startRelease.Icon = this.CreateMenuIcon("Icons.GitFlow.Release");
                startRelease.Click += (_, e) =>
                {
                    if (repo.CanCreatePopup())
                        repo.ShowPopup(new ViewModels.GitFlowStart(repo, Models.GitFlowBranchType.Release));
                    e.Handled = true;
                };

                var startHotfix = new MenuItem();
                startHotfix.Header = App.Text("GitFlow.StartHotfix");
                startHotfix.Icon = this.CreateMenuIcon("Icons.GitFlow.Hotfix");
                startHotfix.Click += (_, e) =>
                {
                    if (repo.CanCreatePopup())
                        repo.ShowPopup(new ViewModels.GitFlowStart(repo, Models.GitFlowBranchType.Hotfix));
                    e.Handled = true;
                };

                items.Add(startFeature);
                items.Add(startRelease);
                items.Add(startHotfix);
            }
            else
            {
                var init = new MenuItem();
                init.Header = App.Text("GitFlow.Init");
                init.Icon = this.CreateMenuIcon("Icons.Init");
                init.Click += (_, e) =>
                {
                    if (repo.CurrentBranch == null)
                        repo.SendNotification("Git flow init failed: No branch found!!!", true);
                    else if (repo.CanCreatePopup())
                        repo.ShowPopup(new ViewModels.InitGitFlow(repo));

                    e.Handled = true;
                };
                items.Add(init);
            }

            return items;
        }

        private MenuItem CreateGitFlowMenuItem(ViewModels.Repository repo)
        {
            var item = new MenuItem();
            item.Header = App.Text("GitFlow");
            item.Icon = this.CreateMenuIcon("Icons.GitFlow");

            foreach (var subItem in CreateGitFlowMenuItems(repo))
                item.Items.Add(subItem);

            return item;
        }

        private void OpenGitFlowMenu(object sender, RoutedEventArgs ev)
        {
            if (DataContext is ViewModels.Repository repo && sender is Control control)
            {
                var menu = new ContextMenu();
                menu.Placement = PlacementMode.BottomEdgeAlignedLeft;

                foreach (var item in CreateGitFlowMenuItems(repo))
                    menu.Items.Add(item);

                menu.Open(control);
            }

            ev.Handled = true;
        }

        private List<MenuItem> CreateGitLFSMenuItems(ViewModels.Repository repo)
        {
            var items = new List<MenuItem>();

            if (repo.IsLFSEnabled())
            {
                var addPattern = new MenuItem();
                addPattern.Header = App.Text("GitLFS.AddTrackPattern");
                addPattern.Icon = this.CreateMenuIcon("Icons.File.Add");
                addPattern.Click += (_, e) =>
                {
                    if (repo.CanCreatePopup())
                        repo.ShowPopup(new ViewModels.LFSTrackCustomPattern(repo));

                    e.Handled = true;
                };
                items.Add(addPattern);
                items.Add(new MenuItem() { Header = "-" });

                var fetch = new MenuItem();
                fetch.Header = App.Text("GitLFS.Fetch");
                fetch.Icon = this.CreateMenuIcon("Icons.Fetch");
                fetch.IsEnabled = repo.Remotes.Count > 0;
                fetch.Click += async (_, e) =>
                {
                    if (repo.CanCreatePopup())
                    {
                        if (repo.Remotes.Count == 1)
                            await repo.ShowAndStartPopupAsync(new ViewModels.LFSFetch(repo));
                        else
                            repo.ShowPopup(new ViewModels.LFSFetch(repo));
                    }

                    e.Handled = true;
                };
                items.Add(fetch);

                var pull = new MenuItem();
                pull.Header = App.Text("GitLFS.Pull");
                pull.Icon = this.CreateMenuIcon("Icons.Pull");
                pull.IsEnabled = repo.Remotes.Count > 0;
                pull.Click += async (_, e) =>
                {
                    if (repo.CanCreatePopup())
                    {
                        if (repo.Remotes.Count == 1)
                            await repo.ShowAndStartPopupAsync(new ViewModels.LFSPull(repo));
                        else
                            repo.ShowPopup(new ViewModels.LFSPull(repo));
                    }

                    e.Handled = true;
                };
                items.Add(pull);

                var push = new MenuItem();
                push.Header = App.Text("GitLFS.Push");
                push.Icon = this.CreateMenuIcon("Icons.Push");
                push.IsEnabled = repo.Remotes.Count > 0;
                push.Click += async (_, e) =>
                {
                    if (repo.CanCreatePopup())
                    {
                        if (repo.Remotes.Count == 1)
                            await repo.ShowAndStartPopupAsync(new ViewModels.LFSPush(repo));
                        else
                            repo.ShowPopup(new ViewModels.LFSPush(repo));
                    }

                    e.Handled = true;
                };
                items.Add(push);

                var prune = new MenuItem();
                prune.Header = App.Text("GitLFS.Prune");
                prune.Icon = this.CreateMenuIcon("Icons.Clean");
                prune.Click += async (_, e) =>
                {
                    if (repo.CanCreatePopup())
                        await repo.ShowAndStartPopupAsync(new ViewModels.LFSPrune(repo));

                    e.Handled = true;
                };
                items.Add(new MenuItem() { Header = "-" });
                items.Add(prune);

                var locks = new MenuItem();
                locks.Header = App.Text("GitLFS.Locks");
                locks.Icon = this.CreateMenuIcon("Icons.Lock");
                locks.IsEnabled = repo.Remotes.Count > 0;
                if (repo.Remotes.Count == 1)
                {
                    locks.Click += async (_, e) =>
                    {
                        await this.ShowDialogAsync(new ViewModels.LFSLocks(repo, repo.Remotes[0].Name));
                        e.Handled = true;
                    };
                }
                else
                {
                    foreach (var remote in repo.Remotes)
                    {
                        var remoteName = remote.Name;
                        var lockRemote = new MenuItem();
                        lockRemote.Header = remoteName;
                        lockRemote.Click += async (_, e) =>
                        {
                            await this.ShowDialogAsync(new ViewModels.LFSLocks(repo, remoteName));
                            e.Handled = true;
                        };
                        locks.Items.Add(lockRemote);
                    }
                }

                items.Add(new MenuItem() { Header = "-" });
                items.Add(locks);
            }
            else
            {
                var install = new MenuItem();
                install.Header = App.Text("GitLFS.Install");
                install.Icon = this.CreateMenuIcon("Icons.Init");
                install.Click += async (_, e) =>
                {
                    await repo.InstallLFSAsync();
                    e.Handled = true;
                };
                items.Add(install);
            }

            return items;
        }

        private MenuItem CreateGitLFSMenuItem(ViewModels.Repository repo)
        {
            var item = new MenuItem();
            item.Header = App.Text("GitLFS");
            item.Icon = this.CreateMenuIcon("Icons.LFS");

            foreach (var subItem in CreateGitLFSMenuItems(repo))
                item.Items.Add(subItem);

            return item;
        }

        private void OpenGitLFSMenu(object sender, RoutedEventArgs ev)
        {
            if (DataContext is ViewModels.Repository repo && sender is Control control)
            {
                var menu = new ContextMenu();
                menu.Placement = PlacementMode.BottomEdgeAlignedLeft;

                foreach (var item in CreateGitLFSMenuItems(repo))
                    menu.Items.Add(item);

                menu.Open(control);
            }

            ev.Handled = true;
        }

        private void OpenMoreToolsMenu(object sender, RoutedEventArgs ev)
        {
            if (DataContext is ViewModels.Repository repo && sender is Control control)
            {
                var menu = new ContextMenu();
                menu.Placement = PlacementMode.BottomEdgeAlignedLeft;

                if (!repo.IsBare)
                {
                    menu.Items.Add(CreateGitFlowMenuItem(repo));
                    menu.Items.Add(CreateGitLFSMenuItem(repo));
                    menu.Items.Add(new MenuItem() { Header = "-" });

                    var bisect = new MenuItem();
                    bisect.Header = App.Text("Bisect");
                    bisect.Icon = this.CreateMenuIcon("Icons.Bisect");
                    bisect.Click += async (_, e) =>
                    {
                        await StartBisectAsync(repo);
                        e.Handled = true;
                    };
                    menu.Items.Add(bisect);
                }

                var cleanup = new MenuItem();
                cleanup.Header = App.Text("Repository.Clean");
                cleanup.Icon = this.CreateMenuIcon("Icons.Clean");
                cleanup.Click += async (_, e) =>
                {
                    await repo.CleanupAsync();
                    e.Handled = true;
                };
                menu.Items.Add(cleanup);

                menu.Open(control);
            }

            ev.Handled = true;
        }

        private async Task StartBisectAsync(ViewModels.Repository repo)
        {
            if (repo.InProgressContext != null || !repo.CanCreatePopup())
                return;

            if (repo.LocalChangesCount > 0)
                repo.SendNotification("You have un-committed local changes. Please discard or stash them first.", true);
            else if (repo.IsBisectCommandRunning || repo.BisectState != Models.BisectState.None)
                repo.SendNotification("Bisect is running! Please abort it before starting a new one.", true);
            else
                await repo.ExecBisectCommandAsync("start");
        }

        private async void StartBisect(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.Repository repo)
                await StartBisectAsync(repo);

            e.Handled = true;
        }

        private async void Cleanup(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.Repository repo)
            {
                await repo.CleanupAsync();
                e.Handled = true;
            }
        }

        private void OpenCustomActionMenu(object sender, RoutedEventArgs ev)
        {
            if (DataContext is ViewModels.Repository repo && sender is Control control)
            {
                var menu = new ContextMenu();
                menu.Placement = PlacementMode.BottomEdgeAlignedLeft;

                var actions = repo.GetCustomActions(Models.CustomActionScope.Repository);
                if (actions.Count > 0)
                {
                    foreach (var action in actions)
                    {
                        var (dup, label) = action;
                        var item = new MenuItem();
                        item.Icon = this.CreateMenuIcon("Icons.Action");
                        item.Header = label;
                        item.Click += async (_, e) =>
                        {
                            await repo.ExecCustomActionAsync(dup, null);
                            e.Handled = true;
                        };

                        menu.Items.Add(item);
                    }
                }
                else
                {
                    menu.Items.Add(new MenuItem() { Header = App.Text("Repository.CustomActions.Empty") });
                }

                menu.Open(control);
            }

            ev.Handled = true;
        }

        private async void OpenGitLogs(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.Repository repo)
            {
                await this.ShowDialogAsync(new ViewModels.ViewLogs(repo));
                e.Handled = true;
            }
        }

        private void NavigateToHead(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.Repository { CurrentBranch: not null } repo)
            {
                var repoView = TopLevel.GetTopLevel(this)?.FindDescendantOfType<Repository>();
                repoView?.LocalBranchTree?.Select(repo.CurrentBranch);

                repo.NavigateToCommit(repo.CurrentBranch.Head);
                e.Handled = true;
            }
        }
    }
}
