using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Data;
using Avalonia.Data.Converters;
using SourceGit.Models;

namespace SourceGit.Views
{
    public partial class StructuredDiffSheetView : UserControl
    {
        public StructuredDiffSheetView()
        {
            InitializeComponent();
        }

        protected override void OnDataContextChanged(System.EventArgs e)
        {
            base.OnDataContextChanged(e);
            RebuildColumns(DataContext as StructuredDiffSheet);
        }

        private void RebuildColumns(StructuredDiffSheet sheet)
        {
            SheetGrid.Columns.Clear();
            if (sheet == null)
                return;

            var headers = sheet.HeaderCells;
            // Bind visibility without rebuilding columns so user-adjusted widths survive filtering.
            for (var i = 0; i < headers.Count; i++)
            {
                var columnIndex = i;
                var column = new DataGridTemplateColumn
                {
                    Header = BuildCell(headers[columnIndex], true),
                    Width = GetColumnWidth(headers[columnIndex].DisplayText),
                    IsReadOnly = true,
                    // Cells render static text for a specific row/column. Recycling the template
                    // reuses old TextBlocks with stale values, which looks like duplicated Excel rows.
                    CellTemplate = new FuncDataTemplate<StructuredDiffRow>((row, _) => BuildReactiveCell(row?[columnIndex], sheet), supportsRecycling: false),
                };
                column.Bind(DataGridColumn.IsVisibleProperty, new Binding($"VisibleColumns[{columnIndex}]") { Source = sheet });
                SheetGrid.Columns.Add(column);
            }
        }

        private static Control BuildReactiveCell(StructuredDiffCell cell, StructuredDiffSheet sheet)
        {
            var presenter = new ContentControl
            {
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            };
            // Bind the existing cell presenter to the mode instead of relying on DataGrid to rebuild row templates.
            // Avalonia owns the subscription lifetime; changing the mode leaves row selection and scroll position intact.
            presenter.Bind(ContentControl.ContentProperty, new Binding(nameof(StructuredDiffSheet.MergeChanges))
            {
                Source = sheet,
                Converter = new FuncValueConverter<bool, Control>(mergeChanges => BuildCell(cell, false, mergeChanges)),
            });
            return presenter;
        }

        private static DataGridLength GetColumnWidth(string header)
        {
            // Stable widths keep header and body aligned while still allowing users to resize columns.
            if (header == "#")
                return new DataGridLength(56);
            if (header == "key")
                return new DataGridLength(220);
            return new DataGridLength(160);
        }

        private static Control BuildCell(StructuredDiffCell cell, bool isHeader, bool mergeChanges = false)
        {
            cell ??= new StructuredDiffCell();

            var text = new TextBlock
            {
                Text = cell.DisplayText,
                FontFamily = FontFamily.Parse("fonts:SourceGit#JetBrains Mono NL"),
                FontSize = 12,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                HorizontalAlignment = isHeader ? Avalonia.Layout.HorizontalAlignment.Center : Avalonia.Layout.HorizontalAlignment.Stretch,
                TextAlignment = isHeader ? TextAlignment.Center : TextAlignment.Left,
            };

            if (!isHeader && !cell.IsHeader && cell.IsModified && cell.OldText.Length > 0 && cell.NewText.Length > 0)
                BuildInlineDiff(text, cell, mergeChanges);

            var isAddition = cell.IsAdded || (cell.IsModified && cell.OldText.Length == 0);
            var isDeletion = cell.IsDeleted || (cell.IsModified && cell.NewText.Length == 0);

            // Merged mode shows the resulting value; deleted values remain available in the tooltip.
            if (mergeChanges && !isHeader && !cell.IsHeader && isDeletion)
                text.Text = StructuredDiffCell.OneLine(cell.NewText);

            var border = new Border
            {
                Child = text,
                ClipToBounds = true,
            };
            border.Classes.Add("structured_diff_cell");
            if (isHeader || cell.IsHeader)
                border.Classes.Add("header");
            // Pure additions stay green in either mode, including empty-to-value changes.
            if (mergeChanges && !isHeader && !cell.IsHeader && isDeletion)
                border.Classes.Add("modified");
            else if (isAddition)
                border.Classes.Add("added");
            else if (isDeletion)
                border.Classes.Add("deleted");

            ToolTip.SetTip(border, cell.Tooltip);
            return border;
        }

        private static void BuildInlineDiff(TextBlock text, StructuredDiffCell cell, bool mergeChanges)
        {
            var oldValue = StructuredDiffCell.OneLine(cell.OldText);
            var newValue = StructuredDiffCell.OneLine(cell.NewText);
            text.Text = null;
            var oldPosition = 0;
            var newPosition = 0;
            // Reuse text diff alignment; zero-length sides have no start offset, so infer it from unchanged text.
            foreach (var change in TextInlineChange.Compare(oldValue, newValue))
            {
                var oldStart = change.DeletedCount > 0 ? change.DeletedStart :
                    oldPosition + change.AddedStart - newPosition;
                var newStart = change.AddedCount > 0 ? change.AddedStart :
                    newPosition + change.DeletedStart - oldPosition;
                AddInline(text, newValue[newPosition..newStart], StructuredDiffChangeKind.None);
                var deleted = oldValue.Substring(oldStart, change.DeletedCount);
                var added = newValue.Substring(newStart, change.AddedCount);
                var prefix = 0;
                var suffix = 0;
                // Numeric tokens are words to the shared differ; narrow their highlights to the changed digits.
                while (prefix < deleted.Length && prefix < added.Length && deleted[prefix] == added[prefix])
                    prefix++;
                while (suffix < deleted.Length - prefix && suffix < added.Length - prefix &&
                    deleted[deleted.Length - suffix - 1] == added[added.Length - suffix - 1])
                    suffix++;
                AddInline(text, added[..prefix], StructuredDiffChangeKind.None);
                if (!mergeChanges)
                    AddInline(text, deleted[prefix..(deleted.Length - suffix)], StructuredDiffChangeKind.Deleted);
                AddInline(text, added[prefix..(added.Length - suffix)],
                    mergeChanges ? StructuredDiffChangeKind.Modified : StructuredDiffChangeKind.Added);
                AddInline(text, added[(added.Length - suffix)..], StructuredDiffChangeKind.None);
                oldPosition = oldStart + change.DeletedCount;
                newPosition = newStart + change.AddedCount;
            }
            AddInline(text, newValue[newPosition..], StructuredDiffChangeKind.None);
        }

        private static void AddInline(TextBlock text, string value, StructuredDiffChangeKind change)
        {
            if (value.Length == 0)
                return;

            var run = new Run(value);
            if (change == StructuredDiffChangeKind.Added)
                run.Background = new SolidColorBrush(Color.Parse("#4033AA44"));
            else if (change == StructuredDiffChangeKind.Modified)
                run.Background = new SolidColorBrush(Color.Parse("#40D8A400"));
            else if (change == StructuredDiffChangeKind.Deleted)
            {
                run.Background = new SolidColorBrush(Color.Parse("#40CC3333"));
                run.TextDecorations = TextDecorations.Strikethrough;
            }
            text.Inlines!.Add(run);
        }

    }
}
