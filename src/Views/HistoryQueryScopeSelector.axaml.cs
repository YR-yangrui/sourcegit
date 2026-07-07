using System;
using System.Collections.Generic;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace SourceGit.Views
{
    public partial class HistoryQueryScopeSelector : UserControl
    {
        public static readonly StyledProperty<List<Models.HistoryQueryScope>> ScopesProperty =
            AvaloniaProperty.Register<HistoryQueryScopeSelector, List<Models.HistoryQueryScope>>(nameof(Scopes));

        public List<Models.HistoryQueryScope> Scopes
        {
            get => GetValue(ScopesProperty);
            set => SetValue(ScopesProperty, value);
        }

        public static readonly StyledProperty<List<Models.HistoryQueryScope>> VisibleScopesProperty =
            AvaloniaProperty.Register<HistoryQueryScopeSelector, List<Models.HistoryQueryScope>>(nameof(VisibleScopes));

        public List<Models.HistoryQueryScope> VisibleScopes
        {
            get => GetValue(VisibleScopesProperty);
            set => SetValue(VisibleScopesProperty, value);
        }

        public static readonly DirectProperty<HistoryQueryScopeSelector, Models.HistoryQueryScope> SelectedScopeProperty =
            AvaloniaProperty.RegisterDirect<HistoryQueryScopeSelector, Models.HistoryQueryScope>(
                nameof(SelectedScope),
                o => o.SelectedScope,
                (o, v) => o.SelectedScope = v);

        public Models.HistoryQueryScope SelectedScope
        {
            get => _selectedScope;
            set => SetAndRaise(SelectedScopeProperty, ref _selectedScope, value);
        }

        public static readonly DirectProperty<HistoryQueryScopeSelector, Models.HistoryQueryScope> HighlightedScopeProperty =
            AvaloniaProperty.RegisterDirect<HistoryQueryScopeSelector, Models.HistoryQueryScope>(
                nameof(HighlightedScope),
                o => o.HighlightedScope,
                (o, v) => o.HighlightedScope = v);

        public Models.HistoryQueryScope HighlightedScope
        {
            get => _highlightedScope;
            set => SetAndRaise(HighlightedScopeProperty, ref _highlightedScope, value);
        }

        public static readonly StyledProperty<bool> IsDropDownOpenedProperty =
            AvaloniaProperty.Register<HistoryQueryScopeSelector, bool>(nameof(IsDropDownOpened));

        public bool IsDropDownOpened
        {
            get => GetValue(IsDropDownOpenedProperty);
            set => SetValue(IsDropDownOpenedProperty, value);
        }

        public static readonly StyledProperty<string> SearchFilterProperty =
            AvaloniaProperty.Register<HistoryQueryScopeSelector, string>(nameof(SearchFilter));

        public string SearchFilter
        {
            get => GetValue(SearchFilterProperty);
            set => SetValue(SearchFilterProperty, value);
        }

        public HistoryQueryScopeSelector()
        {
            Focusable = true;
            InitializeComponent();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == SelectedScopeProperty && !IsDropDownOpened)
            {
                HighlightedScope = SelectedScope;
            }
            else if (change.Property == ScopesProperty || change.Property == SearchFilterProperty)
            {
                var scopes = Scopes;
                var filter = SearchFilter;
                if (scopes is not { Count: > 0 })
                {
                    SetCurrentValue(VisibleScopesProperty, []);
                    HighlightedScope = null;
                }
                else if (string.IsNullOrEmpty(filter))
                {
                    SetCurrentValue(VisibleScopesProperty, scopes);
                    if (!scopes.Contains(HighlightedScope))
                        HighlightedScope = scopes.Contains(SelectedScope) ? SelectedScope : scopes[0];
                }
                else
                {
                    var visible = new List<Models.HistoryQueryScope>();
                    var oldHighlight = HighlightedScope;
                    var keepHighlight = false;

                    foreach (var scope in scopes)
                    {
                        if (scope.Matches(filter))
                        {
                            visible.Add(scope);
                            if (!keepHighlight)
                                keepHighlight = scope == oldHighlight;
                        }
                    }

                    SetCurrentValue(VisibleScopesProperty, visible);
                    if (!keepHighlight)
                        HighlightedScope = visible.Count > 0 ? visible[0] : null;
                }
            }
        }

        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            base.OnApplyTemplate(e);

            if (_popup != null)
            {
                _popup.Opened -= OnPopupOpened;
                _popup.Closed -= OnPopupClosed;
            }

            _popup = e.NameScope.Get<Popup>("PART_Popup");
            _popup.Opened += OnPopupOpened;
            _popup.Closed += OnPopupClosed;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (e.Key == Key.Space && !IsDropDownOpened)
            {
                IsDropDownOpened = true;
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && IsDropDownOpened)
            {
                IsDropDownOpened = false;
                e.Handled = true;
            }
        }

        private void OnPopupOpened(object sender, EventArgs e)
        {
            HighlightedScope = SelectedScope;
            if (VisibleScopes is { Count: > 0 } && !VisibleScopes.Contains(HighlightedScope))
                HighlightedScope = VisibleScopes[0];

            var listBox = _popup?.Child?.FindDescendantOfType<ListBox>();
            listBox?.Focus();
        }

        private void OnPopupClosed(object sender, EventArgs e)
        {
            Focus(NavigationMethod.Directional);
        }

        private void OnToggleDropDown(object sender, PointerPressedEventArgs e)
        {
            IsDropDownOpened = !IsDropDownOpened;
            e.Handled = true;
        }

        private void OnSearchBoxKeyDown(object _, KeyEventArgs e)
        {
            if (e.Key == Key.Tab)
            {
                var listBox = _popup?.Child?.FindDescendantOfType<ListBox>();
                listBox?.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                var listBox = _popup?.Child?.FindDescendantOfType<ListBox>();
                if (listBox != null)
                {
                    if (listBox.SelectedIndex > 0)
                        listBox.SelectedIndex--;
                    listBox.Focus();
                }

                e.Handled = true;
            }
            else if (e.Key == Key.Down)
            {
                var listBox = _popup?.Child?.FindDescendantOfType<ListBox>();
                if (listBox != null)
                {
                    if (listBox.SelectedIndex < listBox.Items.Count - 1)
                        listBox.SelectedIndex++;
                    listBox.Focus();
                }

                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                ConfirmHighlightedScope();
                IsDropDownOpened = false;
                e.Handled = true;
            }
        }

        private void OnClearSearchFilter(object sender, RoutedEventArgs e)
        {
            SearchFilter = string.Empty;
            e.Handled = true;
        }

        private void OnDropDownListKeyDown(object _, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ConfirmHighlightedScope();
                IsDropDownOpened = false;
                e.Handled = true;
            }
            else if (e.Key == Key.F && e.KeyModifiers == (OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control))
            {
                var searchBox = _popup?.Child?.FindDescendantOfType<TextBox>();
                if (searchBox != null)
                {
                    searchBox.CaretIndex = SearchFilter?.Length ?? 0;
                    searchBox.Focus();
                }

                e.Handled = true;
            }
        }

        private void OnDropDownListSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 1 && e.AddedItems[0] is Models.HistoryQueryScope scope)
                HighlightedScope = scope;
        }

        private void OnDropDownItemPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (sender is Control { DataContext: Models.HistoryQueryScope scope })
                SelectedScope = scope;

            IsDropDownOpened = false;
            e.Handled = true;
        }

        private void ConfirmHighlightedScope()
        {
            if (HighlightedScope != null)
                SelectedScope = HighlightedScope;
        }

        private Popup _popup = null;
        private Models.HistoryQueryScope _selectedScope = null;
        private Models.HistoryQueryScope _highlightedScope = null;
    }
}
