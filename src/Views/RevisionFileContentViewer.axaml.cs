using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.TextMate;

namespace SourceGit.Views
{
    public class RevisionTextFileView : TextEditor
    {
        public static readonly StyledProperty<int> TabWidthProperty =
            AvaloniaProperty.Register<RevisionTextFileView, int>(nameof(TabWidth), 4);

        public int TabWidth
        {
            get => GetValue(TabWidthProperty);
            set => SetValue(TabWidthProperty, value);
        }

        public static readonly StyledProperty<bool> UseSyntaxHighlightingProperty =
            AvaloniaProperty.Register<RevisionTextFileView, bool>(nameof(UseSyntaxHighlighting));

        public bool UseSyntaxHighlighting
        {
            get => GetValue(UseSyntaxHighlightingProperty);
            set => SetValue(UseSyntaxHighlightingProperty, value);
        }

        public static readonly StyledProperty<Vector> ScrollOffsetProperty =
            AvaloniaProperty.Register<RevisionTextFileView, Vector>(nameof(ScrollOffset));

        public Vector ScrollOffset
        {
            get => GetValue(ScrollOffsetProperty);
            set => SetValue(ScrollOffsetProperty, value);
        }

        protected override Type StyleKeyOverride => typeof(TextEditor);

        public RevisionTextFileView() : base(new TextArea(), new TextDocument())
        {
            IsReadOnly = true;
            ShowLineNumbers = true;
            WordWrap = false;

            Options.IndentationSize = TabWidth;
            Options.EnableHyperlinks = false;
            Options.EnableEmailHyperlinks = false;

            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

            TextArea.LeftMargins[0].Margin = new Thickness(8, 0);
            TextArea.TextView.Margin = new Thickness(4, 0);
        }

        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);

            TextArea.TextView.ContextRequested += OnTextViewContextRequested;
            _scrollViewer = this.FindDescendantOfType<ScrollViewer>();
            if (_scrollViewer != null)
            {
                _scrollViewer.ScrollChanged += OnScrollChanged;
                RestoreScrollOffset();
            }

            UpdateTextMate();
        }

        protected override void OnUnloaded(RoutedEventArgs e)
        {
            base.OnUnloaded(e);

            TextArea.TextView.ContextRequested -= OnTextViewContextRequested;
            if (_scrollViewer != null)
            {
                _scrollViewer.ScrollChanged -= OnScrollChanged;
                _scrollViewer = null;
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

            if (DataContext is Models.RevisionTextFile source)
            {
                Text = source.Content;
                Models.TextMateHelper.SetGrammarByFileName(_textMate, source.FileName);
                RestoreScrollOffset();
            }
            else
            {
                Text = string.Empty;
            }
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == TabWidthProperty)
                Options.IndentationSize = TabWidth;
            else if (change.Property == UseSyntaxHighlightingProperty)
                UpdateTextMate();
            else if (change.Property == ScrollOffsetProperty && !_isRestoringScrollOffset)
                RestoreScrollOffset();
            else if (change.Property.Name == nameof(ActualThemeVariant) && change.NewValue != null)
                Models.TextMateHelper.SetThemeByApp(_textMate);
        }

        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_isRestoringScrollOffset || _scrollViewer == null)
                return;

            SetCurrentValue(ScrollOffsetProperty, _scrollViewer.Offset);
        }

        private void RestoreScrollOffset()
        {
            if (_scrollViewer == null)
                return;

            var offset = ScrollOffset;
            Dispatcher.UIThread.Post(() =>
            {
                if (_scrollViewer == null)
                    return;

                var current = _scrollViewer.Offset;
                if (Math.Abs(current.X - offset.X) < 0.5 && Math.Abs(current.Y - offset.Y) < 0.5)
                    return;

                _isRestoringScrollOffset = true;
                _scrollViewer.Offset = offset;
                _isRestoringScrollOffset = false;
            });
        }

        private void OnTextViewContextRequested(object sender, ContextRequestedEventArgs e)
        {
            var selected = SelectedText;
            if (string.IsNullOrEmpty(selected))
                return;

            var copy = new MenuItem() { Header = App.Text("Copy") };
            copy.Click += async (_, ev) =>
            {
                await this.CopyTextAsync(selected);
                ev.Handled = true;
            };

            if (this.FindResource("Icons.Copy") is Geometry geo)
            {
                copy.Icon = new Avalonia.Controls.Shapes.Path()
                {
                    Width = 10,
                    Height = 10,
                    Stretch = Stretch.Uniform,
                    Data = geo,
                };
            }

            var menu = new ContextMenu();
            menu.Items.Add(copy);
            menu.Open(TextArea.TextView);

            e.Handled = true;
        }

        private void UpdateTextMate()
        {
            if (UseSyntaxHighlighting)
            {
                _textMate ??= Models.TextMateHelper.CreateForEditor(this);

                if (DataContext is Models.RevisionTextFile file)
                    Models.TextMateHelper.SetGrammarByFileName(_textMate, file.FileName);
            }
            else if (_textMate != null)
            {
                _textMate.Dispose();
                _textMate = null;
                GC.Collect();

                TextArea.TextView.Redraw();
            }
        }

        private TextMate.Installation _textMate = null;
        private ScrollViewer _scrollViewer = null;
        private bool _isRestoringScrollOffset = false;
    }

    public partial class RevisionFileContentViewer : UserControl
    {
        public static readonly StyledProperty<Vector> ScrollOffsetProperty =
            AvaloniaProperty.Register<RevisionFileContentViewer, Vector>(nameof(ScrollOffset));

        public Vector ScrollOffset
        {
            get => GetValue(ScrollOffsetProperty);
            set => SetValue(ScrollOffsetProperty, value);
        }

        public RevisionFileContentViewer()
        {
            InitializeComponent();
        }
    }
}
