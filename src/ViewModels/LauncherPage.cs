using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class LauncherPage : ObservableObject
    {
        public RepositoryNode Node
        {
            get => _node;
            set => SetProperty(ref _node, value);
        }

        public object Data
        {
            get => _data;
            set
            {
                if (ReferenceEquals(_data, value))
                    return;

                if (_data is Repository oldRepo)
                    oldRepo.PropertyChanged -= OnRepositoryPropertyChanged;

                if (SetProperty(ref _data, value))
                {
                    if (_data is Repository repo)
                        repo.PropertyChanged += OnRepositoryPropertyChanged;

                    OnPropertyChanged(nameof(IsRefreshing));
                    OnPropertyChanged(nameof(IsRemoteSyncing));
                    OnPropertyChanged(nameof(IsDirtyStateVisible));
                }
            }
        }

        public Models.DirtyState DirtyState
        {
            get => _dirtyState;
            private set => SetProperty(ref _dirtyState, value);
        }

        public bool IsRefreshing
        {
            get => _data is Repository { IsRefreshing: true };
        }

        public bool IsRemoteSyncing
        {
            get => _data is Repository { IsRemoteSyncing: true };
        }

        public bool IsDirtyStateVisible
        {
            get => _dirtyState != Models.DirtyState.None && !IsRefreshing;
        }

        public Popup Popup
        {
            get => _popup;
            set => SetProperty(ref _popup, value);
        }

        public AvaloniaList<Models.Notification> Notifications
        {
            get;
            set;
        } = new AvaloniaList<Models.Notification>();

        public LauncherPage()
        {
            _node = new RepositoryNode() { Id = Guid.NewGuid().ToString() };
            _data = Welcome.Instance;

            // New welcome page will clear the search filter before.
            Welcome.Instance.ClearSearchFilter();
        }

        public LauncherPage(RepositoryNode node, Repository repo)
        {
            _node = node;
            _data = repo;
            repo.PropertyChanged += OnRepositoryPropertyChanged;
        }

        public void ClearNotifications()
        {
            Notifications.Clear();
        }

        public void ChangeDirtyState(Models.DirtyState flag, bool remove)
        {
            var state = _dirtyState;
            if (remove)
            {
                if (state.HasFlag(flag))
                    state -= flag;
            }
            else
            {
                state |= flag;
            }

            DirtyState = state;
            OnPropertyChanged(nameof(IsDirtyStateVisible));
        }

        public bool CanCreatePopup()
        {
            return _popup is not { InProgress: true };
        }

        public async Task ProcessPopupAsync()
        {
            if (_popup is { InProgress: false } dump)
            {
                if (!dump.Check())
                    return;

                dump.InProgress = true;

                try
                {
                    var finished = await dump.Sure();
                    if (finished)
                    {
                        dump.Cleanup();
                        Popup = null;
                    }
                }
                catch (Exception e)
                {
                    Native.OS.LogException(e);
                }

                dump.InProgress = false;
            }
        }

        public void CancelPopup()
        {
            if (_popup == null || _popup.InProgress)
                return;

            _popup?.Cleanup();
            Popup = null;
        }

        private void OnRepositoryPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Repository.IsRefreshing))
            {
                OnPropertyChanged(nameof(IsRefreshing));
                OnPropertyChanged(nameof(IsDirtyStateVisible));
            }
            else if (e.PropertyName == nameof(Repository.IsRemoteSyncing))
            {
                OnPropertyChanged(nameof(IsRemoteSyncing));
            }
        }

        private RepositoryNode _node = null;
        private object _data = null;
        private Models.DirtyState _dirtyState = Models.DirtyState.None;
        private Popup _popup = null;
    }
}
