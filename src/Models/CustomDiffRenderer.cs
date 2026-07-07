using System;
using System.IO;
using System.IO.Enumeration;

using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.Models
{
    public class CustomDiffRenderer : ObservableObject
    {
        public bool IsEnabled
        {
            get => _isEnabled;
            set => SetProperty(ref _isEnabled, value);
        }

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        public string Patterns
        {
            get => _patterns;
            set => SetProperty(ref _patterns, value);
        }

        public string Executable
        {
            get => _executable;
            set => SetProperty(ref _executable, value);
        }

        public string Arguments
        {
            get => _arguments;
            set => SetProperty(ref _arguments, value);
        }

        public bool ClearPreviousContentOnLoad
        {
            get => _clearPreviousContentOnLoad;
            set => SetProperty(ref _clearPreviousContentOnLoad, value);
        }

        public string Identity => $"{_isEnabled}\0{_name}\0{_patterns}\0{_executable}\0{_arguments}";

        public bool Matches(string path)
        {
            if (!_isEnabled || string.IsNullOrWhiteSpace(_patterns) || string.IsNullOrWhiteSpace(_executable))
                return false;

            var normalized = path.Replace('\\', '/');
            var fileName = Path.GetFileName(normalized);
            foreach (var raw in _patterns.Split([';', ','], StringSplitOptions.RemoveEmptyEntries))
            {
                var pattern = raw.Trim().Replace('\\', '/');
                if (string.IsNullOrEmpty(pattern))
                    continue;

                if (pattern.StartsWith(".", StringComparison.Ordinal))
                {
                    if (normalized.EndsWith(pattern, StringComparison.OrdinalIgnoreCase))
                        return true;
                    continue;
                }

                var candidate = pattern.Contains('/') ? normalized : fileName;
                if (FileSystemName.MatchesSimpleExpression(pattern, candidate, true))
                    return true;
            }

            return false;
        }

        private bool _isEnabled = true;
        private string _name = string.Empty;
        private string _patterns = string.Empty;
        private string _executable = string.Empty;
        private string _arguments = "\"$OLD\" \"$NEW\"";
        private bool _clearPreviousContentOnLoad = false;
    }

    public class HtmlDiff
    {
        public Uri Source { get; set; } = null;
        public string TempDirectory { get; set; } = string.Empty;
    }

    public class CustomDiffEmpty
    {
        public string RendererName { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }

    public class CustomDiffError
    {
        public string RendererName { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }
}
