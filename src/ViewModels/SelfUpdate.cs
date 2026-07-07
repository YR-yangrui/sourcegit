using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class SelfUpdate : ObservableObject
    {
        public object Data
        {
            get => _data;
            set
            {
                if (SetProperty(ref _data, value))
                {
                    _changelogPageIndex = 0;
                    RaiseChangelogPropertiesChanged();
                }
            }
        }

        public bool IsInstallingUpdate
        {
            get => _isInstallingUpdate;
            set => SetProperty(ref _isInstallingUpdate, value);
        }

        public string InstallProgressDescription
        {
            get => _installProgressDescription;
            private set => SetProperty(ref _installProgressDescription, value);
        }

        public bool IsInstallProgressIndeterminate => !_installProgressPercentage.HasValue;

        public bool HasInstallProgressValue => _installProgressPercentage.HasValue;

        public double InstallProgressValue => _installProgressPercentage ?? 0;

        public string InstallProgressText => _installProgressPercentage.HasValue ? $"{_installProgressPercentage.Value:0}%" : string.Empty;

        public string ChangelogPageText
        {
            get
            {
                if (Data is Models.UpdateAvailable update && GetCurrentChangelogPage(update) is { } page)
                    return page.Body;

                return string.Empty;
            }
        }

        public string ChangelogPageTagName
        {
            get
            {
                if (Data is Models.UpdateAvailable update && GetCurrentChangelogPage(update) is { } page)
                    return page.TagName;

                return string.Empty;
            }
        }

        public string ChangelogPageReleaseDateStr
        {
            get
            {
                if (Data is Models.UpdateAvailable update && GetCurrentChangelogPage(update) is { } page)
                    return page.ReleaseDateStr;

                return string.Empty;
            }
        }

        public int ChangelogPageCount
        {
            get
            {
                if (Data is Models.UpdateAvailable update)
                    return update.ChangelogPages.Count;

                return 0;
            }
        }

        public int ChangelogPageNumber
        {
            get
            {
                var count = ChangelogPageCount;
                if (count == 0)
                    return 0;

                return count - _changelogPageIndex;
            }
        }

        public string ChangelogPageNumberText
        {
            get
            {
                var number = ChangelogPageNumber;
                return number > 0 ? number.ToString() : string.Empty;
            }
        }

        public bool CanGoPreviousChangelogPage
        {
            get
            {
                return Data is Models.UpdateAvailable update &&
                    _changelogPageIndex + 1 < update.ChangelogPages.Count;
            }
        }

        public bool CanGoNextChangelogPage => _changelogPageIndex > 0;

        public void GoPreviousChangelogPage()
        {
            if (Data is Models.UpdateAvailable update && _changelogPageIndex + 1 < update.ChangelogPages.Count)
            {
                _changelogPageIndex++;
                RaiseChangelogPropertiesChanged();
            }
        }

        public void GoNextChangelogPage()
        {
            if (_changelogPageIndex > 0)
            {
                _changelogPageIndex--;
                RaiseChangelogPropertiesChanged();
            }
        }

        public void GoToChangelogPage(int pageNumber)
        {
            if (Data is not Models.UpdateAvailable update || update.ChangelogPages.Count == 0)
                return;

            var clamped = Math.Max(1, Math.Min(pageNumber, update.ChangelogPages.Count));
            var index = update.ChangelogPages.Count - clamped;
            if (_changelogPageIndex == index)
            {
                OnPropertyChanged(nameof(ChangelogPageNumber));
                OnPropertyChanged(nameof(ChangelogPageNumberText));
                return;
            }

            _changelogPageIndex = index;
            RaiseChangelogPropertiesChanged();
        }

        public void ResetInstallProgress()
        {
            InstallProgressDescription = string.Empty;
            SetInstallProgressPercentage(null);
        }

        public void UpdateInstallProgress(Models.UpdateInstallProgress progress)
        {
            if (progress == null)
                return;

            InstallProgressDescription = App.Text(progress.DescriptionKey);
            SetInstallProgressPercentage(progress.Percentage);
        }

        private void SetInstallProgressPercentage(double? percentage)
        {
            if (_installProgressPercentage == percentage)
                return;

            _installProgressPercentage = percentage;
            OnPropertyChanged(nameof(IsInstallProgressIndeterminate));
            OnPropertyChanged(nameof(HasInstallProgressValue));
            OnPropertyChanged(nameof(InstallProgressValue));
            OnPropertyChanged(nameof(InstallProgressText));
        }

        private void RaiseChangelogPropertiesChanged()
        {
            OnPropertyChanged(nameof(ChangelogPageText));
            OnPropertyChanged(nameof(ChangelogPageTagName));
            OnPropertyChanged(nameof(ChangelogPageReleaseDateStr));
            OnPropertyChanged(nameof(ChangelogPageCount));
            OnPropertyChanged(nameof(ChangelogPageNumber));
            OnPropertyChanged(nameof(ChangelogPageNumberText));
            OnPropertyChanged(nameof(CanGoPreviousChangelogPage));
            OnPropertyChanged(nameof(CanGoNextChangelogPage));
        }

        private Models.UpdateChangelogPage GetCurrentChangelogPage(Models.UpdateAvailable update)
        {
            if (update.ChangelogPages.Count == 0)
                return null;

            if (_changelogPageIndex >= update.ChangelogPages.Count)
                _changelogPageIndex = 0;

            return update.ChangelogPages[_changelogPageIndex];
        }

        private object _data = null;
        private bool _isInstallingUpdate = false;
        private int _changelogPageIndex = 0;
        private string _installProgressDescription = string.Empty;
        private double? _installProgressPercentage = null;
    }
}
