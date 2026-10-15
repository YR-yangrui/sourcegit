using System;
using System.Collections.Generic;

namespace SourceGit.Models
{
    // Display rows and patch rows are separate: a merged row always selects both original edit lines.
    public class TextDiffProjection
    {
        public List<TextDiffLine> Lines { get; } = [];
        public TextDiff PatchData { get; }
        public List<(int Start, int End)> Ranges { get; } = [];

        public TextDiffProjection(TextDiff source, bool merge)
        {
            PatchData = new TextDiff
            {
                OldMode = source.OldMode, NewMode = source.NewMode,
                OldHash = source.OldHash, NewHash = source.NewHash,
                AddedLines = source.AddedLines, DeletedLines = source.DeletedLines,
                MaxLineNumber = source.MaxLineNumber,
            };
            var lines = source.Lines;
            // Process whole edit runs so reordering preserves the order of each side of the patch.
            for (var i = 0; i < lines.Count;)
            {
                if (!merge || lines[i].Type != TextDiffLineType.Deleted)
                {
                    Add(lines[i++]);
                    continue;
                }
                var deletedStart = i;
                while (i < lines.Count && lines[i].Type == TextDiffLineType.Deleted)
                    i++;
                var addedStart = i;
                while (i < lines.Count && lines[i].Type == TextDiffLineType.Added)
                    i++;
                var deletedCount = addedStart - deletedStart;
                var addedCount = i - addedStart;
                if (deletedCount == addedCount)
                {
                    // Positional pairs are accepted only when most of their text is shared.
                    for (var n = 0; n < deletedCount; n++)
                        AddPair(lines[deletedStart + n], lines[addedStart + n]);
                }
                else if (deletedCount == 1 && addedCount > 0)
                {
                    var match = -1;
                    // A single replacement can be surrounded by newly inserted comments or statements.
                    for (var n = addedStart; n < i; n++)
                    {
                        if (!CanMerge(lines[deletedStart], lines[n]))
                            continue;
                        if (match >= 0) { match = -1; break; }
                        match = n;
                    }
                    if (match < 0)
                    {
                        // Preserve ambiguous blocks without merging any of their rows.
                        for (var n = deletedStart; n < i; n++)
                            Add(lines[n]);
                    }
                    else
                    {
                        // Emit added context in new-side order without including it in the merged row's selection.
                        for (var n = addedStart; n < i; n++)
                        {
                            if (n == match)
                                AddPair(lines[deletedStart], lines[n]);
                            else
                                Add(lines[n]);
                        }
                    }
                }
                else
                {
                    // Unequal multi-line blocks have no unambiguous pairing; keep the normal diff.
                    for (var n = deletedStart; n < i; n++)
                        Add(lines[n]);
                }
            }
        }

        private static bool CanMerge(TextDiffLine oldLine, TextDiffLine newLine)
        {
            // Match the existing text highlighter limit to avoid quadratic work on generated/minified lines.
            if (oldLine.Content.Length > 1024 || newLine.Content.Length > 1024 ||
                oldLine.NoNewLineEndOfFile != newLine.NoNewLineEndOfFile)
                return false;
            var oldText = oldLine.Content.Trim();
            var newText = newLine.Content.Trim();
            var shared = Math.Min(oldText.Length, newText.Length);
            if (shared < 4)
                return false;
            // Ignore indentation when deciding similarity; unrelated indented statements must stay separate.
            foreach (var change in TextInlineChange.Compare(oldText, newText))
            {
                var deleted = oldText.Substring(change.DeletedStart, change.DeletedCount);
                var added = newText.Substring(change.AddedStart, change.AddedCount);
                TrimShared(deleted, added, out var prefix, out var suffix);
                shared -= Math.Min(deleted.Length, added.Length) - prefix - suffix;
            }
            return shared >= 4 && shared * 2 >= Math.Max(oldText.Length, newText.Length);
        }

        private void Add(TextDiffLine line)
        {
            Lines.Add(line);
            Ranges.Add((PatchData.Lines.Count, PatchData.Lines.Count));
            PatchData.Lines.Add(line);
        }

        private void AddPair(TextDiffLine oldLine, TextDiffLine newLine)
        {
            if (!CanMerge(oldLine, newLine))
            {
                Add(oldLine);
                Add(newLine);
                return;
            }
            var merged = new TextDiffLine(TextDiffLineType.Added, newLine.Content, newLine.RawContent,
                oldLine.OldLineNumber, newLine.NewLineNumber)
            {
                MergedOldContent = oldLine.Content,
                NoNewLineEndOfFile = newLine.NoNewLineEndOfFile,
            };
            // Reuse token alignment but narrow highlights within replacement words (including identifiers and numbers).
            foreach (var change in TextInlineChange.Compare(oldLine.Content, newLine.Content))
            {
                var deleted = oldLine.Content.Substring(change.DeletedStart, change.DeletedCount);
                var added = newLine.Content.Substring(change.AddedStart, change.AddedCount);
                TrimShared(deleted, added, out var prefix, out var suffix);
                // Retain the matching old-side spans for the tooltip without recomputing diff on pointer movement.
                var deletedCount = deleted.Length - prefix - suffix;
                if (deletedCount > 0)
                    merged.MergedOldHighlights.Add(new TextRange(change.DeletedStart + prefix, deletedCount));
                var count = added.Length - prefix - suffix;
                if (count > 0)
                    merged.Highlights.Add(new TextRange(change.AddedStart + prefix, count)
                    {
                        IsReplacement = deleted.Length > prefix + suffix,
                    });
            }
            Lines.Add(merged);
            Ranges.Add((PatchData.Lines.Count, PatchData.Lines.Count + 1));
            PatchData.Lines.Add(oldLine);
            PatchData.Lines.Add(newLine);
        }

        private static void TrimShared(string oldText, string newText, out int prefix, out int suffix)
        {
            prefix = 0;
            suffix = 0;
            // Keep the common edges of a changed token out of its colored span.
            while (prefix < oldText.Length && prefix < newText.Length && oldText[prefix] == newText[prefix])
                prefix++;
            while (suffix < oldText.Length - prefix && suffix < newText.Length - prefix &&
                oldText[oldText.Length - suffix - 1] == newText[newText.Length - suffix - 1])
                suffix++;
        }
    }
}
