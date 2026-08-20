using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace SourceGit.Views
{
    public static class ControlExtensions
    {
        public static Control CreateFromViewModels(object data)
        {
            var dataTypeName = data.GetType().FullName;
            if (string.IsNullOrEmpty(dataTypeName) || !dataTypeName.Contains(".ViewModels.", StringComparison.Ordinal))
                return null;

            var viewTypeName = dataTypeName.Replace(".ViewModels.", ".Views.");
            var viewType = Type.GetType(viewTypeName);
            if (viewType != null)
                return Activator.CreateInstance(viewType) as Control;

            return null;
        }

        public static async Task CopyTextAsync(this Control control, string text)
        {
            var clipboard = TopLevel.GetTopLevel(control)?.Clipboard;
            if (clipboard != null)
                await clipboard.SetTextAsync(text);
        }

        public static Path CreateMenuIcon(this Control control, string iconKey)
        {
            if (control?.FindResource(iconKey) is StreamGeometry geo)
            {
                return new Path()
                {
                    Data = geo,
                    Width = 12,
                    Height = 12,
                    Stretch = Stretch.Uniform
                };
            }

            return null;
        }

        public static void ShowWindow(this Control control, object data)
        {
            if (data == null)
                return;

            if (data is not ChromelessWindow window)
            {
                window = CreateFromViewModels(data) as ChromelessWindow;
                if (window == null)
                    return;

                window.DataContext = data;
            }

            DisposeDataContextOnClosed(window);

            do
            {
                var owner = TopLevel.GetTopLevel(control) as Window;
                if (owner != null)
                {
                    // Get the screen where current window locates.
                    var screen = owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.Primary;
                    if (screen == null)
                        break;

                    // Calculate the startup position (Center Screen Mode) of target window
                    var rect = new PixelRect(PixelSize.FromSize(window.ClientSize, owner.DesktopScaling));
                    var centeredRect = screen.WorkingArea.CenterRect(rect);
                    if (owner.Screens.ScreenFromPoint(centeredRect.Position) == null)
                        break;

                    // Use the startup position
                    window.WindowStartupLocation = WindowStartupLocation.Manual;
                    window.Position = centeredRect.Position;
                }
            } while (false);

            window.Show();
        }

        public static Task ShowDialogAsync(this Control control, object data)
        {
            var owner = TopLevel.GetTopLevel(control) as Window;
            if (owner == null)
                return null;

            if (data is ChromelessWindow window)
            {
                DisposeDataContextOnClosed(window);
                return ShowDialogWithNativeWebViewsHiddenAsync(window, owner);
            }

            window = CreateFromViewModels(data) as ChromelessWindow;
            if (window != null)
            {
                window.DataContext = data;
                DisposeDataContextOnClosed(window);
                return ShowDialogWithNativeWebViewsHiddenAsync(window, owner);
            }

            return null;
        }

        internal static async Task ShowDialogWithNativeWebViewsHiddenAsync(Window dialog, Window owner)
        {
            using var nativeViews = HideNativeWebViews(owner);
            await dialog.ShowDialog(owner);
        }

        private static IDisposable HideNativeWebViews(Window owner)
        {
            var hidden = new List<NativeWebView>();
            // Native views bypass Avalonia's z-order, so suspend only the currently visible ones for modal UI.
            foreach (var webView in owner.GetVisualDescendants().OfType<NativeWebView>())
            {
                if (webView.IsVisible)
                {
                    webView.IsVisible = false;
                    hidden.Add(webView);
                }
            }

            return new NativeWebViewVisibilityScope(hidden);
        }

        private static void DisposeDataContextOnClosed(Window window)
        {
            if (window.DataContext is not IDisposable disposable)
                return;

            window.Closed += (_, _) => disposable.Dispose();
        }

        private sealed class NativeWebViewVisibilityScope : IDisposable
        {
            public NativeWebViewVisibilityScope(List<NativeWebView> hidden)
            {
                _hidden = hidden;
            }

            public void Dispose()
            {
                // Restore only the instances hidden by this scope so nested dialogs cannot reveal a parent dialog's WebView.
                foreach (var webView in _hidden)
                    webView.IsVisible = true;
            }

            private readonly List<NativeWebView> _hidden;
        }
    }
}
