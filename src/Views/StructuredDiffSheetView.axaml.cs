using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
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
            for (var i = 0; i < headers.Count; i++)
            {
                var columnIndex = i;
                SheetGrid.Columns.Add(new DataGridTemplateColumn
                {
                    Header = BuildCell(headers[columnIndex], true),
                    Width = GetColumnWidth(headers[columnIndex].DisplayText),
                    IsReadOnly = true,
                    // Cells render static text for a specific row/column. Recycling the template
                    // reuses old TextBlocks with stale values, which looks like duplicated Excel rows.
                    CellTemplate = new FuncDataTemplate<StructuredDiffRow>((row, _) => BuildCell(row?[columnIndex], false), supportsRecycling: false),
                });
            }
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

        private static Control BuildCell(StructuredDiffCell cell, bool isHeader)
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

            var border = new Border
            {
                Child = text,
                ClipToBounds = true,
            };
            border.Classes.Add("structured_diff_cell");
            if (isHeader || cell.IsHeader)
                border.Classes.Add("header");
            if (cell.IsAdded)
                border.Classes.Add("added");
            else if (cell.IsDeleted)
                border.Classes.Add("deleted");
            else if (cell.IsModified)
                border.Classes.Add("modified");
            ToolTip.SetTip(border, cell.Tooltip);
            return border;
        }

    }
}
