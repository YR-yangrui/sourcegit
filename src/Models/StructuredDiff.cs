using System.Collections.ObjectModel;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.Models
{
    public enum StructuredDiffKind
    {
        Spreadsheet,
        ConfigBytes,
        PrefabHierarchy,
    }

    public enum StructuredDiffChangeKind
    {
        None,
        Added,
        Deleted,
        Modified,
    }

    public class StructuredDiff
    {
        public StructuredDiffKind Kind { get; set; } = StructuredDiffKind.Spreadsheet;
        public string Summary { get; set; } = string.Empty;
        public string FormattedText { get; set; } = string.Empty;
        public TextDiff RawTextDiff { get; set; } = null;
        public List<StructuredDiffSheet> Sheets { get; set; } = [];
        public List<StructuredDiffNode> Nodes { get; set; } = [];
        public List<StructuredDiffNode> Rows { get; set; } = [];
    }

    public class StructuredDiffSheet : ObservableObject
    {
        public string Name { get; set; } = string.Empty;
        public int Added { get; set; } = 0;
        public int Deleted { get; set; } = 0;
        public int Modified { get; set; } = 0;
        public List<StructuredDiffRow> Rows { get; set; } = [];

        public StructuredDiffChangeKind Change { get; set; } = StructuredDiffChangeKind.None;
        public bool IsAdded => Change == StructuredDiffChangeKind.Added;
        public bool IsDeleted => Change == StructuredDiffChangeKind.Deleted;
        public bool IsModified => Change == StructuredDiffChangeKind.Modified;
        public bool HasChanges => Change != StructuredDiffChangeKind.None;

        public string Header => $"{Name}  +{Added} -{Deleted} ~{Modified}";
        public List<StructuredDiffCell> HeaderCells => Rows.Count > 0 ? Rows[0].Cells : [];
        public List<StructuredDiffRow> DataRows => Rows.Count > 1 ? Rows.GetRange(1, Rows.Count - 1) : [];
        public ObservableCollection<StructuredDiffRow> VisibleRows { get; } = [];

        public List<bool> VisibleColumns
        {
            get
            {
                var columns = new List<bool>();
                var headers = HeaderCells;
                var firstDataColumn = headers.Count > 0 && headers[0].DisplayText == "#" ? 1 : 0;
                // Keep the row label and first data column as context, including on empty sheets.
                for (var i = 0; i < headers.Count; i++)
                {
                    var visible = !_showOnlyChangedRows || !_hideUnchangedColumns || i <= firstDataColumn ||
                        headers[i].Change != StructuredDiffChangeKind.None;
                    // Include every changed value, including fields that exist on only one side.
                    foreach (var row in VisibleRows)
                    {
                        if (row[i].Change != StructuredDiffChangeKind.None)
                        {
                            visible = true;
                            break;
                        }
                    }
                    columns.Add(visible);
                }
                return columns;
            }
        }

        public bool MergeChanges
        {
            get => _mergeChanges;
            set => SetProperty(ref _mergeChanges, value);
        }

        public void SetHideUnchangedColumns(bool value)
        {
            SetProperty(ref _hideUnchangedColumns, value, nameof(VisibleColumns));
        }

        public void SetShowOnlyChangedRows(bool value)
        {
            if (SetProperty(ref _showOnlyChangedRows, value, nameof(ShowOnlyChangedRows)))
                RefreshVisibleRows();
        }

        public void RefreshVisibleRows()
        {
            var rows = new List<StructuredDiffRow>();
            foreach (var row in DataRows)
            {
                if (!_showOnlyChangedRows || row.HasChanges)
                    rows.Add(row);
            }

            rows = SortRows(rows);
            // Keep a stable collection instance for Avalonia DataGrid. Replacing the ItemsSource
            // with short-lived lists can leave recycled rows showing duplicated Excel blocks.
            VisibleRows.Clear();
            foreach (var row in rows)
                VisibleRows.Add(row);
            OnPropertyChanged(nameof(VisibleColumns));
        }

        public bool ShowOnlyChangedRows => _showOnlyChangedRows;

        private bool _showOnlyChangedRows = false;
        private bool _hideUnchangedColumns = false;
        private bool _mergeChanges = false;

        private List<StructuredDiffRow> SortRows(List<StructuredDiffRow> rows)
        {
            var sorted = new List<StructuredDiffRow>(rows);
            if (HeaderCells.Count > 0 && HeaderCells[0].DisplayText == "#")
            {
                // Excel row labels are the source of truth for display order. Some sparse/filtered
                // paths can carry stale row metadata, so parse the visible # cell before falling back.
                sorted.Sort((a, b) => GetDisplayRowNumber(a).CompareTo(GetDisplayRowNumber(b)));
            }
            else
            {
                sorted.Sort((a, b) => a.SourceRowNumber.CompareTo(b.SourceRowNumber));
            }
            return sorted;
        }

        private static int GetDisplayRowNumber(StructuredDiffRow row)
        {
            if (row.Cells.Count > 0 && int.TryParse(row.Cells[0].DisplayText, out var value))
                return value;
            return row.SourceRowNumber;
        }
    }

    public class StructuredDiffRow
    {
        public int SourceRowNumber { get; set; } = 0;
        public List<StructuredDiffCell> Cells { get; set; } = [];
        public StructuredDiffCell this[int index] => index >= 0 && index < Cells.Count ? Cells[index] : new StructuredDiffCell();
        public bool HasChanges
        {
            get
            {
                foreach (var cell in Cells)
                {
                    if (!cell.IsHeader && cell.Change != StructuredDiffChangeKind.None)
                        return true;
                }

                return false;
            }
        }
    }

    public class StructuredDiffCell
    {
        public bool IsHeader { get; set; } = false;
        public string OldText { get; set; } = string.Empty;
        public string NewText { get; set; } = string.Empty;
        public StructuredDiffChangeKind Change { get; set; } = StructuredDiffChangeKind.None;

        public bool IsAdded => Change == StructuredDiffChangeKind.Added;
        public bool IsDeleted => Change == StructuredDiffChangeKind.Deleted;
        public bool IsModified => Change == StructuredDiffChangeKind.Modified;

        public string DisplayText
        {
            get
            {
                if (IsHeader)
                    return OneLine(NewText.Length > 0 ? NewText : OldText);

                return Change switch
                {
                    StructuredDiffChangeKind.Added => OneLine(NewText),
                    StructuredDiffChangeKind.Deleted => OneLine(OldText),
                    // Inline rendering highlights edits; the plain-text fallback stays on one line.
                    StructuredDiffChangeKind.Modified => OneLine(NewText.Length > 0 ? NewText : OldText),
                    _ => OneLine(NewText.Length > 0 ? NewText : OldText),
                };
            }
        }

        public string Tooltip
        {
            get
            {
                if (!IsHeader && OldText.Length > 0 && Change is StructuredDiffChangeKind.Modified or StructuredDiffChangeKind.Deleted)
                    return $"old: {OldText}\nnew: {NewText}";
                return DisplayText;
            }
        }

        internal static string OneLine(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            // JSON arrays/objects can arrive pretty-printed from flatc or Excel cells;
            // keep each old/new value on its own line rather than introducing extra row breaks.
            return value.Replace("\r\n", " ", System.StringComparison.Ordinal)
                .Replace('\r', ' ')
                .Replace('\n', ' ');
        }
    }

    public class StructuredDiffNode
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public int Depth { get; set; } = 0;
        public StructuredDiffChangeKind Change { get; set; } = StructuredDiffChangeKind.None;
        public List<StructuredDiffNode> Children { get; set; } = [];
        public List<StructuredPropertyChange> Properties { get; set; } = [];

        public bool IsAdded => Change == StructuredDiffChangeKind.Added;
        public bool IsDeleted => Change == StructuredDiffChangeKind.Deleted;
        public bool IsModified => Change == StructuredDiffChangeKind.Modified;
        public string ChangeMark => Change switch
        {
            StructuredDiffChangeKind.Added => "+",
            StructuredDiffChangeKind.Deleted => "-",
            StructuredDiffChangeKind.Modified => "~",
            _ => string.Empty,
        };
        public string ChangeLabel => Change == StructuredDiffChangeKind.None ? string.Empty : Change.ToString();
        public string Summary => $"{Path}  +{Count(StructuredDiffChangeKind.Added)} -{Count(StructuredDiffChangeKind.Deleted)} ~{Count(StructuredDiffChangeKind.Modified)}";
        public List<StructuredPropertyGroup> PropertyGroups => BuildPropertyGroups();

        private int Count(StructuredDiffChangeKind kind)
        {
            var count = 0;
            foreach (var prop in Properties)
            {
                if (prop.Change == kind)
                    count++;
            }

            return count;
        }

        private List<StructuredPropertyGroup> BuildPropertyGroups()
        {
            var groups = new List<StructuredPropertyGroup>();
            foreach (var prop in Properties)
            {
                var groupName = prop.GroupName;
                var group = FindGroup(groups, groupName);
                if (group == null)
                {
                    group = new StructuredPropertyGroup() { Name = groupName };
                    groups.Add(group);
                }

                group.Changes.Add(prop);
            }

            return groups;
        }

        private static StructuredPropertyGroup FindGroup(List<StructuredPropertyGroup> groups, string name)
        {
            foreach (var group in groups)
            {
                if (group.Name == name)
                    return group;
            }

            return null;
        }
    }

    public class StructuredPropertyGroup : ObservableObject
    {
        public string Name { get; set; } = string.Empty;
        public List<StructuredPropertyChange> Changes { get; set; } = [];
        public string Header => $"{Name}  +{Count(StructuredDiffChangeKind.Added)} -{Count(StructuredDiffChangeKind.Deleted)} ~{Count(StructuredDiffChangeKind.Modified)}";
        public string ChangeSummary => $"+{Count(StructuredDiffChangeKind.Added)} -{Count(StructuredDiffChangeKind.Deleted)} ~{Count(StructuredDiffChangeKind.Modified)}";
        public StructuredDiffChangeKind Change
        {
            get
            {
                if (Changes.Count == 0)
                    return StructuredDiffChangeKind.None;

                var first = Changes[0].Change;
                foreach (var change in Changes)
                {
                    if (change.Change != first)
                        return StructuredDiffChangeKind.Modified;
                }

                return first;
            }
        }

        public bool IsAdded => Change == StructuredDiffChangeKind.Added;
        public bool IsDeleted => Change == StructuredDiffChangeKind.Deleted;
        public bool IsModified => Change == StructuredDiffChangeKind.Modified;
        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value);
        }

        private int Count(StructuredDiffChangeKind kind)
        {
            var count = 0;
            foreach (var change in Changes)
            {
                if (change.Change == kind)
                    count++;
            }

            return count;
        }

        private bool _isExpanded = true;
    }

    public class StructuredPropertyChange
    {
        public string Key { get; set; } = string.Empty;
        public string OldValue { get; set; } = string.Empty;
        public string NewValue { get; set; } = string.Empty;
        public StructuredDiffChangeKind Change { get; set; } = StructuredDiffChangeKind.None;

        public bool IsAdded => Change == StructuredDiffChangeKind.Added;
        public bool IsDeleted => Change == StructuredDiffChangeKind.Deleted;
        public bool IsModified => Change == StructuredDiffChangeKind.Modified;
        public bool IsOldValueChanged => Change is StructuredDiffChangeKind.Deleted or StructuredDiffChangeKind.Modified;
        public bool IsNewValueChanged => Change is StructuredDiffChangeKind.Added or StructuredDiffChangeKind.Modified;
        public string ChangeLabel => Change == StructuredDiffChangeKind.None ? string.Empty : Change.ToString();
        public string GroupName
        {
            get
            {
                var dot = Key.IndexOf('.');
                if (Key.Length > 0 && Key[0] == '[')
                    return "Node";
                return dot > 0 ? Key.Substring(0, dot) : "Other";
            }
        }

        public string DisplayKey
        {
            get
            {
                var dot = Key.IndexOf('.');
                return dot > 0 && dot + 1 < Key.Length ? Key.Substring(dot + 1) : Key;
            }
        }
    }
}
