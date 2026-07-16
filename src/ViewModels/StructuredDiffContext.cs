using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class StructuredDiffContext : ObservableObject
    {
        public Models.StructuredDiff Diff { get; }
        public TextDiffContext RawPrefabDiff { get; private set; }

        public bool IsTable => Diff.Kind is Models.StructuredDiffKind.Spreadsheet or Models.StructuredDiffKind.ConfigBytes;
        public bool IsHierarchy => Diff.Kind == Models.StructuredDiffKind.PrefabHierarchy;
        public bool HasRawPrefabDiff => RawPrefabDiff != null;
        public string TableFilterButtonText => _showOnlyChangedRows ? "显示全部" : "只显示本地改动";
        public string PrefabFilterButtonText => _showOnlyChangedPrefabNodes ? "显示全部" : "只显示Diff";
        public bool ShowOnlyChangedPrefabNodes
        {
            get => _showOnlyChangedPrefabNodes;
            set
            {
                if (!SetProperty(ref _showOnlyChangedPrefabNodes, value))
                    return;

                RefreshPrefabNodeFilter();
            }
        }
        public bool IsShowingPrefabHierarchy => _prefabViewMode == PrefabViewMode.Hierarchy;
        public bool IsShowingRawPrefabDiff => _prefabViewMode == PrefabViewMode.Raw;
        public int PrefabAddedNodes => CountPrefabNodes(Models.StructuredDiffChangeKind.Added);
        public int PrefabDeletedNodes => CountPrefabNodes(Models.StructuredDiffChangeKind.Deleted);
        public int PrefabModifiedNodes => CountPrefabNodes(Models.StructuredDiffChangeKind.Modified);
        public int PrefabChangedProperties => CountPrefabProperties();
        public string PrefabSummaryText => $"节点 +{PrefabAddedNodes} -{PrefabDeletedNodes} ~{PrefabModifiedNodes}，属性 {PrefabChangedProperties} 处改动";

        public List<Models.StructuredDiffNode> VisiblePrefabNodes
        {
            get
            {
                if (!_showOnlyChangedPrefabNodes)
                    return Diff.Nodes;

                var nodes = new List<Models.StructuredDiffNode>();
                foreach (var node in Diff.Nodes)
                {
                    var visible = CloneVisiblePrefabNode(node);
                    if (visible != null)
                        nodes.Add(visible);
                }

                return nodes;
            }
        }

        public Models.StructuredDiffNode SelectedNode
        {
            get => _selectedNode;
            set => SetProperty(ref _selectedNode, value);
        }

        public StructuredDiffContext(Models.StructuredDiff diff, Models.DiffOption option = null, Models.TextDiff rawTextDiff = null)
        {
            Diff = diff;
            if (diff.Kind == Models.StructuredDiffKind.Spreadsheet)
            {
                _showOnlyChangedRows = true;
                foreach (var sheet in diff.Sheets)
                    sheet.SetShowOnlyChangedRows(true);
            }

            if (diff.Kind == Models.StructuredDiffKind.PrefabHierarchy && option != null)
            {
                _option = option;
                RefreshPrefabTextDiffs();
            }

            _selectedNode = FindFirstChangedNode(diff) ?? (diff.Rows.Count > 0 ? diff.Rows[0] : null);
        }

        public void ShowRawPrefabDiff()
        {
            if (RawPrefabDiff != null)
                SetPrefabViewMode(PrefabViewMode.Raw);
        }

        public void ShowPrefabHierarchy()
        {
            SetPrefabViewMode(PrefabViewMode.Hierarchy);
        }

        public void ToggleTableFilter()
        {
            _showOnlyChangedRows = !_showOnlyChangedRows;
            foreach (var sheet in Diff.Sheets)
                sheet.SetShowOnlyChangedRows(_showOnlyChangedRows);
            OnPropertyChanged(nameof(TableFilterButtonText));
        }

        public void TogglePrefabFilter()
        {
            _showOnlyChangedPrefabNodes = !_showOnlyChangedPrefabNodes;
            OnPropertyChanged(nameof(ShowOnlyChangedPrefabNodes));
            RefreshPrefabNodeFilter();
        }

        private void RefreshPrefabNodeFilter()
        {
            OnPropertyChanged(nameof(VisiblePrefabNodes));
            OnPropertyChanged(nameof(PrefabFilterButtonText));
            RefreshPrefabTextDiffs();
            if (_showOnlyChangedPrefabNodes && (SelectedNode == null || (SelectedNode.Change == Models.StructuredDiffChangeKind.None && SelectedNode.Properties.Count == 0)))
                SelectedNode = FindFirstChangedNode(Diff);
        }

        private void RefreshPrefabTextDiffs()
        {
            RawPrefabDiff = CreatePrefabTextDiffContext(Diff.RawTextDiff, RawPrefabDiff);
            OnPropertyChanged(nameof(RawPrefabDiff));
            OnPropertyChanged(nameof(HasRawPrefabDiff));
        }

        private TextDiffContext CreatePrefabTextDiffContext(Models.TextDiff diff, TextDiffContext previous)
        {
            if (diff == null || _option == null)
                return null;

            var display = _showOnlyChangedPrefabNodes ? CloneChangedTextDiff(diff) : diff;
            // These prefab text diffs are synthesized for inspection, not parsed from git hunk
            // headers, so they must not offer chunk staging/discard/copy-as-patch actions.
            return new CombinedTextDiff(_option, display, previous, false);
        }

        private static Models.TextDiff CloneChangedTextDiff(Models.TextDiff diff)
        {
            var clone = new Models.TextDiff()
            {
                MaxLineNumber = diff.MaxLineNumber,
                AddedLines = diff.AddedLines,
                DeletedLines = diff.DeletedLines,
                OldMode = diff.OldMode,
                NewMode = diff.NewMode,
                OldHash = diff.OldHash,
                NewHash = diff.NewHash,
            };

            foreach (var line in diff.Lines)
            {
                // Prefab formatted/raw views can be very noisy; keep only synthetic hunk labels
                // and actual changed lines so the filter never shows unrelated prefab content.
                if (line.Type is Models.TextDiffLineType.Indicator or Models.TextDiffLineType.Added or Models.TextDiffLineType.Deleted)
                    clone.Lines.Add(line);
            }

            return clone;
        }

        private void SetPrefabViewMode(PrefabViewMode mode)
        {
            if (_prefabViewMode == mode)
                return;

            _prefabViewMode = mode;
            OnPropertyChanged(nameof(IsShowingPrefabHierarchy));
            OnPropertyChanged(nameof(IsShowingRawPrefabDiff));
        }

        private static Models.StructuredDiffNode FindFirstChangedNode(Models.StructuredDiff diff)
        {
            foreach (var row in diff.Rows)
            {
                if (row.Change != Models.StructuredDiffChangeKind.None || row.Properties.Count > 0)
                    return row;
            }

            return null;
        }

        private int CountPrefabNodes(Models.StructuredDiffChangeKind kind)
        {
            var count = 0;
            foreach (var row in Diff.Rows)
            {
                if (row.Change == kind)
                    count++;
            }

            return count;
        }

        private int CountPrefabProperties()
        {
            var count = 0;
            foreach (var row in Diff.Rows)
                count += row.Properties.Count;

            return count;
        }

        private static Models.StructuredDiffNode CloneVisiblePrefabNode(Models.StructuredDiffNode node)
        {
            var children = new List<Models.StructuredDiffNode>();
            foreach (var child in node.Children)
            {
                var visible = CloneVisiblePrefabNode(child);
                if (visible != null)
                    children.Add(visible);
            }

            if (node.Change == Models.StructuredDiffChangeKind.None && node.Properties.Count == 0 && children.Count == 0)
                return null;

            return new Models.StructuredDiffNode()
            {
                Name = node.Name,
                Path = node.Path,
                Depth = node.Depth,
                Change = node.Change,
                Children = children,
                Properties = node.Properties,
            };
        }

        private Models.StructuredDiffNode _selectedNode = null;
        private Models.DiffOption _option = null;
        private bool _showOnlyChangedRows = false;
        private bool _showOnlyChangedPrefabNodes = true;
        private PrefabViewMode _prefabViewMode = PrefabViewMode.Hierarchy;

        private enum PrefabViewMode
        {
            Hierarchy,
            Raw,
        }
    }
}
