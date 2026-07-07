using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace SourceGit.Models
{
    public static class StructuredDiffBuilder
    {
        public static bool CanHandle(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".xlsx" or ".xlsm" or ".prefab" or ".unity" or ".bytes";
        }

        public static async Task<StructuredDiff> BuildAsync(string repo, DiffOption option)
        {
            if (!CanHandle(option.Path))
                return null;

            var oldPath = string.IsNullOrEmpty(option.OrgPath) ? option.Path : option.OrgPath;
            var oldBytesTask = ReadOldSideAsync(repo, option, oldPath);
            var newBytesTask = ReadNewSideAsync(repo, option);
            await Task.WhenAll(oldBytesTask, newBytesTask).ConfigureAwait(false);

            var oldBytes = oldBytesTask.Result;
            var newBytes = newBytesTask.Result;
            if (oldBytes == null && newBytes == null)
                return null;

            var ext = Path.GetExtension(option.Path).ToLowerInvariant();
            try
            {
                if (ext is ".xlsx" or ".xlsm")
                {
                    var oldTablesTask = oldBytes == null ? Task.FromResult(new List<TableSource>()) : Task.Run(() => ReadXlsxTables(oldBytes));
                    var newTablesTask = newBytes == null ? Task.FromResult(new List<TableSource>()) : Task.Run(() => ReadXlsxTables(newBytes));
                    await Task.WhenAll(oldTablesTask, newTablesTask).ConfigureAwait(false);
                    return BuildGridDiff(StructuredDiffKind.Spreadsheet, "Excel table diff", oldTablesTask.Result, newTablesTask.Result);
                }

                if (ext is ".prefab" or ".unity")
                {
                    var oldText = oldBytes == null ? string.Empty : TextEncoding.Decode(oldBytes);
                    var newText = newBytes == null ? string.Empty : TextEncoding.Decode(newBytes);
                    return BuildPrefabDiff(repo, oldText, newText);
                }

                if (ext == ".bytes")
                {
                    var flatc = FindFlatc(repo);
                    if (string.IsNullOrEmpty(flatc))
                        return null;

                    var oldFbsTask = oldBytes == null ? Task.FromResult<ConfigFbsSource>(null) : ReadOldConfigFbsAsync(repo, option);
                    var newFbsTask = newBytes == null ? Task.FromResult<ConfigFbsSource>(null) : ReadNewConfigFbsAsync(repo, option);
                    await Task.WhenAll(oldFbsTask, newFbsTask).ConfigureAwait(false);

                    var oldFbs = oldFbsTask.Result;
                    var newFbs = newFbsTask.Result;
                    if (oldFbs == null && newFbs == null)
                        return null;

                    var oldTablesTask = oldBytes == null ? Task.FromResult(new List<TableSource>()) : ReadConfigBytesTablesAsync(oldPath, oldBytes, oldFbs ?? newFbs, flatc);
                    var newTablesTask = newBytes == null ? Task.FromResult(new List<TableSource>()) : ReadConfigBytesTablesAsync(option.Path, newBytes, newFbs ?? oldFbs, flatc);
                    await Task.WhenAll(oldTablesTask, newTablesTask).ConfigureAwait(false);

                    var oldTables = oldTablesTask.Result;
                    var newTables = newTablesTask.Result;
                    if (oldTables.Count == 0 && newTables.Count == 0)
                        return null;

                    return BuildRecordDiff("Config bytes table diff", oldTables, newTables);
                }
            }
            catch
            {
                // Structured views are best-effort. If a parser cannot understand a file,
                // keep SourceGit's original text/binary diff path available.
                return null;
            }

            return null;
        }

        private static async Task<byte[]> ReadOldSideAsync(string repo, DiffOption option, string oldPath)
        {
            if (string.IsNullOrEmpty(oldPath) || oldPath == "/dev/null")
                return null;

            if (option.Revisions.Count == 2)
                return await ReadRevisionSideAsync(repo, option.Revisions[0], oldPath).ConfigureAwait(false);

            if (option.IsUnstaged)
            {
                // Unstaged diffs compare index -> worktree, not HEAD -> worktree.
                // Fall back to HEAD for untracked or newly added files that are not in the index.
                var indexed = await Commands.QueryFileContent.RunSpecAsBytesAsync(repo, $":{oldPath}").ConfigureAwait(false);
                if (indexed != null)
                    return indexed;
            }

            return await Commands.QueryFileContent.RunSpecAsBytesAsync(repo, $"HEAD:{oldPath}").ConfigureAwait(false);
        }

        private static async Task<byte[]> ReadNewSideAsync(string repo, DiffOption option)
        {
            if (string.IsNullOrEmpty(option.Path) || option.Path == "/dev/null")
                return null;

            if (option.Revisions.Count == 2)
                return await ReadRevisionSideAsync(repo, option.Revisions[1], option.Path).ConfigureAwait(false);

            if (option.IsUnstaged)
            {
                var fullPath = Path.Combine(repo, option.Path);
                if (File.Exists(fullPath))
                    return await File.ReadAllBytesAsync(fullPath).ConfigureAwait(false);
                return null;
            }

            return await Commands.QueryFileContent.RunSpecAsBytesAsync(repo, $":{option.Path}").ConfigureAwait(false);
        }

        private static Task<byte[]> ReadRevisionSideAsync(string repo, string revision, string path)
        {
            if (string.IsNullOrEmpty(revision) || revision == "-R")
                return Task.FromResult<byte[]>(null);

            var spec = BuildGitObjectSpec(revision, path);
            return Commands.QueryFileContent.RunSpecAsBytesAsync(repo, spec);
        }

        private static Task<ConfigFbsSource> ReadOldConfigFbsAsync(string repo, DiffOption option)
        {
            if (option.Revisions.Count == 2)
                return ReadRevisionConfigFbsAsync(repo, option.Revisions[0]);

            return option.IsUnstaged ? ReadIndexedConfigFbsAsync(repo, fallbackToHead: true) : ReadRevisionConfigFbsAsync(repo, "HEAD");
        }

        private static Task<ConfigFbsSource> ReadNewConfigFbsAsync(string repo, DiffOption option)
        {
            if (option.Revisions.Count == 2)
                return ReadRevisionConfigFbsAsync(repo, option.Revisions[1]);

            return option.IsUnstaged ? ReadWorkingConfigFbsAsync(repo) : ReadIndexedConfigFbsAsync(repo, fallbackToHead: false);
        }

        private static string BuildGitObjectSpec(string revision, string path)
        {
            var colon = revision.IndexOf(':');
            if (colon > 0)
            {
                // FileHistory rename options already store a full "revision:path" spec.
                // Those specs were originally shell-quoted; remove the shell quotes because
                // QueryFileContent passes the whole spec as one process argument.
                var rev = revision.Substring(0, colon);
                var file = revision.Substring(colon + 1);
                if (file.Length >= 2 && file[0] == '"' && file[^1] == '"')
                    file = file.Substring(1, file.Length - 2).Replace("\\\"", "\"", StringComparison.Ordinal);
                return $"{rev}:{file}";
            }

            return $"{revision}:{path}";
        }

        // ======================== generic table diff ========================

        private sealed class TableSource
        {
            public string Name { get; set; } = string.Empty;
            public List<string> Headers { get; set; } = [];
            public List<string> Keys { get; set; } = [];
            public List<List<string>> Rows { get; set; } = [];
        }

        private static StructuredDiff BuildGridDiff(StructuredDiffKind kind, string summary, List<TableSource> oldTables, List<TableSource> newTables)
        {
            var diff = new StructuredDiff() { Kind = kind, Summary = summary };
            // Prefer the local/new side order; deleted old-only sheets are appended after it.
            var names = UnionNames(newTables, oldTables);
            foreach (var name in names)
            {
                var oldTable = FindTable(oldTables, name);
                var newTable = FindTable(newTables, name);
                if (IsRecordTable(oldTable) || IsRecordTable(newTable))
                    diff.Sheets.Add(CompareRecordTable(name, oldTable, newTable));
                else
                    diff.Sheets.Add(CompareGridTable(name, oldTable, newTable));
            }

            return diff;
        }

        private static StructuredDiff BuildRecordDiff(string summary, List<TableSource> oldTables, List<TableSource> newTables)
        {
            var diff = new StructuredDiff() { Kind = StructuredDiffKind.ConfigBytes, Summary = summary };
            // Prefer the local/new side order; deleted old-only config tables are appended after it.
            var names = UnionNames(newTables, oldTables);
            foreach (var name in names)
            {
                var oldTable = FindTable(oldTables, name);
                var newTable = FindTable(newTables, name);
                diff.Sheets.Add(CompareRecordTable(name, oldTable, newTable));
            }

            return diff;
        }

        private static List<string> UnionNames(List<TableSource> oldTables, List<TableSource> newTables)
        {
            var names = new List<string>();
            foreach (var table in oldTables)
                AddIfMissing(names, table.Name);
            foreach (var table in newTables)
                AddIfMissing(names, table.Name);
            return names;
        }

        private static void AddIfMissing(List<string> values, string value)
        {
            foreach (var item in values)
            {
                if (item.Equals(value, StringComparison.Ordinal))
                    return;
            }

            values.Add(value);
        }

        private static TableSource FindTable(List<TableSource> tables, string name)
        {
            foreach (var table in tables)
            {
                if (table.Name.Equals(name, StringComparison.Ordinal))
                    return table;
            }

            return null;
        }

        private static bool IsRecordTable(TableSource table)
        {
            return table != null && table.Headers.Count > 0 && table.Keys.Count == table.Rows.Count;
        }

        private static StructuredDiffSheet CompareGridTable(string name, TableSource oldTable, TableSource newTable)
        {
            var sheet = new StructuredDiffSheet() { Name = name };
            var oldRows = oldTable?.Rows ?? [];
            var newRows = newTable?.Rows ?? [];
            var maxRows = Math.Max(oldRows.Count, newRows.Count);
            var maxCols = 0;

            for (var i = 0; i < oldRows.Count; i++)
                maxCols = Math.Max(maxCols, oldRows[i].Count);
            for (var i = 0; i < newRows.Count; i++)
                maxCols = Math.Max(maxCols, newRows[i].Count);

            var header = new StructuredDiffRow();
            header.Cells.Add(HeaderCell("#"));
            for (var c = 0; c < maxCols; c++)
                header.Cells.Add(HeaderCell(ColumnName(c)));
            sheet.Rows.Add(header);

            for (var r = 0; r < maxRows; r++)
            {
                var row = new StructuredDiffRow();
                row.SourceRowNumber = r + 1;
                row.Cells.Add(HeaderCell((r + 1).ToString(CultureInfo.InvariantCulture)));
                var oldExists = r < oldRows.Count;
                var newExists = r < newRows.Count;

                for (var c = 0; c < maxCols; c++)
                {
                    var oldValue = oldExists && c < oldRows[r].Count ? oldRows[r][c] : string.Empty;
                    var newValue = newExists && c < newRows[r].Count ? newRows[r][c] : string.Empty;
                    var change = GetCellChange(oldExists, newExists, oldValue, newValue);
                    CountCell(sheet, change);
                    row.Cells.Add(new StructuredDiffCell() { OldText = oldValue, NewText = newValue, Change = change });
                }

                sheet.Rows.Add(row);
            }

            SortGridRowsByDisplayNumber(sheet);
            sheet.RefreshVisibleRows();
            return sheet;
        }

        private static void SortGridRowsByDisplayNumber(StructuredDiffSheet sheet)
        {
            if (sheet.Rows.Count <= 2)
                return;

            var header = sheet.Rows[0];
            var rows = sheet.Rows.GetRange(1, sheet.Rows.Count - 1);
            // Keep Excel rows ordered by their visible source row number even if a DataGrid
            // refresh path accidentally binds to Rows instead of VisibleRows.
            rows.Sort((a, b) => GetGridDisplayNumber(a).CompareTo(GetGridDisplayNumber(b)));
            sheet.Rows.Clear();
            sheet.Rows.Add(header);
            sheet.Rows.AddRange(rows);
        }

        private static int GetGridDisplayNumber(StructuredDiffRow row)
        {
            if (row.Cells.Count > 0 && int.TryParse(row.Cells[0].DisplayText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                return value;
            return row.SourceRowNumber;
        }

        private static StructuredDiffSheet CompareRecordTable(string name, TableSource oldTable, TableSource newTable)
        {
            var sheet = new StructuredDiffSheet() { Name = name };
            var headers = new List<string>();
            // Keep the local/new table layout first so the structured diff does not re-sort columns.
            if (newTable != null)
            {
                foreach (var header in newTable.Headers)
                    AddIfMissing(headers, header);
            }
            if (oldTable != null)
            {
                foreach (var header in oldTable.Headers)
                    AddIfMissing(headers, header);
            }

            var rowKeys = new List<string>();
            // Keep the local/new row order first; deleted old-only records are appended afterward.
            AddKeys(rowKeys, newTable);
            AddKeys(rowKeys, oldTable);

            var headerRow = new StructuredDiffRow();
            foreach (var header in headers)
                headerRow.Cells.Add(HeaderCell(header));
            sheet.Rows.Add(headerRow);

            for (var i = 0; i < rowKeys.Count; i++)
            {
                var key = rowKeys[i];
                var oldIndex = IndexOfKey(oldTable, key);
                var newIndex = IndexOfKey(newTable, key);
                var oldExists = oldIndex >= 0;
                var newExists = newIndex >= 0;
                var row = new StructuredDiffRow();
                row.SourceRowNumber = i + 1;

                foreach (var header in headers)
                {
                    var oldValue = oldExists ? GetRecordValue(oldTable, oldIndex, header) : string.Empty;
                    var newValue = newExists ? GetRecordValue(newTable, newIndex, header) : string.Empty;
                    var change = GetCellChange(oldExists, newExists, oldValue, newValue);
                    CountCell(sheet, change);
                    row.Cells.Add(new StructuredDiffCell() { OldText = oldValue, NewText = newValue, Change = change });
                }

                sheet.Rows.Add(row);
            }

            sheet.RefreshVisibleRows();
            return sheet;
        }

        private static void AddKeys(List<string> target, TableSource table)
        {
            if (table == null)
                return;

            foreach (var key in table.Keys)
                AddIfMissing(target, key);
        }

        private static int IndexOfKey(TableSource table, string key)
        {
            if (table == null)
                return -1;

            for (var i = 0; i < table.Keys.Count; i++)
            {
                if (table.Keys[i].Equals(key, StringComparison.Ordinal))
                    return i;
            }

            return -1;
        }

        private static string GetRecordValue(TableSource table, int rowIndex, string header)
        {
            if (table == null || rowIndex < 0 || rowIndex >= table.Rows.Count)
                return string.Empty;

            for (var i = 0; i < table.Headers.Count; i++)
            {
                if (!table.Headers[i].Equals(header, StringComparison.Ordinal))
                    continue;
                return i < table.Rows[rowIndex].Count ? table.Rows[rowIndex][i] : string.Empty;
            }

            return string.Empty;
        }

        private static StructuredDiffCell HeaderCell(string text)
        {
            return new StructuredDiffCell() { IsHeader = true, NewText = text };
        }

        private static StructuredDiffChangeKind GetCellChange(bool oldExists, bool newExists, string oldValue, string newValue)
        {
            if (!oldExists && newExists && newValue.Length > 0)
                return StructuredDiffChangeKind.Added;
            if (oldExists && !newExists && oldValue.Length > 0)
                return StructuredDiffChangeKind.Deleted;
            if (oldValue != newValue)
                return StructuredDiffChangeKind.Modified;
            return StructuredDiffChangeKind.None;
        }

        private static void CountCell(StructuredDiffSheet sheet, StructuredDiffChangeKind change)
        {
            if (change == StructuredDiffChangeKind.Added)
                sheet.Added++;
            else if (change == StructuredDiffChangeKind.Deleted)
                sheet.Deleted++;
            else if (change == StructuredDiffChangeKind.Modified)
                sheet.Modified++;
        }

        private static string ColumnName(int index)
        {
            var n = index + 1;
            var chars = new Stack<char>();
            while (n > 0)
            {
                n--;
                chars.Push((char)('A' + (n % 26)));
                n /= 26;
            }

            return new string(chars.ToArray());
        }

        // ======================== xlsx/xlsm ========================

        private static readonly XNamespace SsNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private static readonly XNamespace PkgRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";

        private static List<TableSource> ReadXlsxTables(byte[] data)
        {
            var result = new List<TableSource>();
            using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
            var shared = ReadSharedStrings(zip);
            var sheets = ReadWorkbookSheets(zip);
            var rels = ReadWorkbookRels(zip);

            foreach (var sheet in sheets)
            {
                var table = new TableSource() { Name = sheet.Name };
                if (rels.TryGetValue(sheet.Rid, out var target) && target.Length > 0)
                {
                    var rel = target.TrimStart('/');
                    var entry = zip.GetEntry("xl/" + rel) ?? zip.GetEntry(rel);
                    if (entry != null)
                        table.Rows = ReadSheetRows(entry, shared);
                }

                NormalizeSpreadsheetRecordTable(table);
                result.Add(table);
            }

            return result;
        }

        private static void NormalizeSpreadsheetRecordTable(TableSource table)
        {
            if (table.Rows.Count < 2)
                return;

            var headerRow = table.Rows[0];
            var keyIndex = FindConfigIdColumn(headerRow);
            if (keyIndex < 0)
                return;

            var dataStart = GetSpreadsheetDataStartRow(table.Rows, keyIndex);
            if (dataStart <= 0 || dataStart >= table.Rows.Count)
                return;

            var headers = BuildSpreadsheetHeaders(table.Rows, dataStart);
            if (keyIndex >= headers.Count)
                return;

            var dataRows = new List<List<string>>();
            var keys = new List<string>();
            for (var i = dataStart; i < table.Rows.Count; i++)
            {
                var sourceRow = table.Rows[i];
                if (IsEmptySheetRow(sourceRow) || keyIndex >= sourceRow.Count)
                    continue;

                var key = sourceRow[keyIndex];
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                var row = new List<string>(headers.Count);
                for (var c = 0; c < headers.Count; c++)
                    row.Add(c < sourceRow.Count ? sourceRow[c] : string.Empty);
                dataRows.Add(row);
                keys.Add(key);
            }

            if (keys.Count == 0)
                return;

            // ProjectN config workbooks carry type/comment rows before the real data rows.
            // Treat ID-keyed sheets as records so inserted/reordered rows do not make every
            // following row look modified, while non-table workbooks still use raw grid diff.
            table.Headers = headers;
            table.Rows = dataRows;
            table.Keys = keys;
        }

        private static int GetSpreadsheetDataStartRow(List<List<string>> rows, int keyIndex)
        {
            if (rows.Count > 3 && IsProjectNTypeRow(rows[1], keyIndex))
                return 3;
            return 1;
        }

        private static bool IsProjectNTypeRow(List<string> row, int keyIndex)
        {
            if (keyIndex < 0 || keyIndex >= row.Count)
                return false;

            var value = row[keyIndex].Trim();
            return value.Equals("int", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("long", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("float", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("double", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("string", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("bool", StringComparison.OrdinalIgnoreCase) ||
                value.EndsWith("[]", StringComparison.Ordinal);
        }

        private static List<string> BuildSpreadsheetHeaders(List<List<string>> rows, int dataStart)
        {
            var maxCols = 0;
            for (var i = 0; i < rows.Count; i++)
                maxCols = Math.Max(maxCols, rows[i].Count);

            var headers = new List<string>(maxCols);
            for (var c = 0; c < maxCols; c++)
            {
                var name = c < rows[0].Count ? rows[0][c].Trim() : string.Empty;
                if (string.IsNullOrEmpty(name) && dataStart > 2 && c < rows[2].Count)
                    name = rows[2][c].Trim();
                if (string.IsNullOrEmpty(name))
                    name = ColumnName(c);

                headers.Add(MakeUniqueHeader(headers, name));
            }

            return headers;
        }

        private static string MakeUniqueHeader(List<string> headers, string name)
        {
            var candidate = name;
            var index = 2;
            while (ContainsHeader(headers, candidate))
            {
                candidate = $"{name}#{index}";
                index++;
            }

            return candidate;
        }

        private static bool ContainsHeader(List<string> headers, string name)
        {
            foreach (var header in headers)
            {
                if (header.Equals(name, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static List<string> ReadSharedStrings(ZipArchive zip)
        {
            var shared = new List<string>();
            var entry = zip.GetEntry("xl/sharedStrings.xml");
            if (entry == null)
                return shared;

            var doc = LoadXml(entry);
            foreach (var si in doc.Root!.Elements(SsNs + "si"))
            {
                var sb = new StringBuilder();
                foreach (var text in si.Descendants(SsNs + "t"))
                    sb.Append(text.Value);
                shared.Add(sb.ToString());
            }

            return shared;
        }

        private static List<(string Name, string Rid)> ReadWorkbookSheets(ZipArchive zip)
        {
            var sheets = new List<(string, string)>();
            var doc = LoadXml(GetRequired(zip, "xl/workbook.xml"));
            var sheetsEl = doc.Root!.Element(SsNs + "sheets");
            if (sheetsEl == null)
                return sheets;

            foreach (var s in sheetsEl.Elements(SsNs + "sheet"))
                sheets.Add((s.Attribute("name")?.Value ?? string.Empty, s.Attribute(RelNs + "id")?.Value ?? string.Empty));
            return sheets;
        }

        private static Dictionary<string, string> ReadWorkbookRels(ZipArchive zip)
        {
            var rels = new Dictionary<string, string>();
            var entry = zip.GetEntry("xl/_rels/workbook.xml.rels");
            if (entry == null)
                return rels;

            var doc = LoadXml(entry);
            foreach (var rel in doc.Root!.Elements(PkgRelNs + "Relationship"))
                rels[rel.Attribute("Id")?.Value ?? string.Empty] = rel.Attribute("Target")?.Value ?? string.Empty;
            return rels;
        }

        private static List<List<string>> ReadSheetRows(ZipArchiveEntry entry, List<string> shared)
        {
            var rows = new List<List<string>>();
            var doc = LoadXml(entry);
            var sheetData = doc.Root!.Element(SsNs + "sheetData");
            if (sheetData == null)
                return rows;

            foreach (var row in sheetData.Elements(SsNs + "row"))
            {
                var rowIndex = RowIndex(row.Attribute("r")?.Value);
                while (rows.Count < rowIndex)
                    rows.Add([]);

                var cells = new List<string>();
                foreach (var c in row.Elements(SsNs + "c"))
                {
                    var colIdx = ColIndex(c.Attribute("r")?.Value);
                    while (cells.Count < colIdx)
                        cells.Add(string.Empty);
                    cells.Add(CellValue(c, shared));
                }

                while (cells.Count > 0 && cells[^1].Length == 0)
                    cells.RemoveAt(cells.Count - 1);

                if (rowIndex >= 0 && rowIndex < rows.Count)
                    rows[rowIndex] = cells;
                else
                    rows.Add(cells);
            }

            return RemoveEmptySheetRows(rows);
        }

        private static List<List<string>> RemoveEmptySheetRows(List<List<string>> rows)
        {
            var result = new List<List<string>>(rows.Count);
            foreach (var row in rows)
            {
                // Empty Excel rows only affect physical row numbers; keeping them shifts later
                // row-by-row comparison and turns a blank-line insertion into many modifications.
                if (!IsEmptySheetRow(row))
                    result.Add(row);
            }

            return result;
        }

        private static bool IsEmptySheetRow(List<string> row)
        {
            foreach (var cell in row)
            {
                if (cell.Length > 0)
                    return false;
            }

            return true;
        }

        private static string CellValue(XElement c, List<string> shared)
        {
            var t = c.Attribute("t")?.Value;
            switch (t)
            {
                case "s":
                    var v = c.Element(SsNs + "v")?.Value;
                    return v != null && int.TryParse(v, out var idx) && idx >= 0 && idx < shared.Count ? shared[idx] : string.Empty;
                case "inlineStr":
                    var isEl = c.Element(SsNs + "is");
                    if (isEl == null)
                        return string.Empty;
                    var sb = new StringBuilder();
                    foreach (var text in isEl.Descendants(SsNs + "t"))
                        sb.Append(text.Value);
                    return sb.ToString();
                case "b":
                    return c.Element(SsNs + "v")?.Value == "1" ? "TRUE" : "FALSE";
                default:
                    return c.Element(SsNs + "v")?.Value ?? string.Empty;
            }
        }

        private static int RowIndex(string rowRef)
        {
            if (string.IsNullOrEmpty(rowRef))
                return -1;

            return int.TryParse(rowRef, NumberStyles.Integer, CultureInfo.InvariantCulture, out var row) ? Math.Max(0, row - 1) : -1;
        }

        private static int ColIndex(string cellRef)
        {
            if (string.IsNullOrEmpty(cellRef))
                return 0;

            var col = 0;
            foreach (var ch in cellRef)
            {
                if (ch >= 'A' && ch <= 'Z')
                    col = col * 26 + (ch - 'A' + 1);
                else if (ch >= 'a' && ch <= 'z')
                    col = col * 26 + (ch - 'a' + 1);
                else
                    break;
            }

            return Math.Max(0, col - 1);
        }

        private static XDocument LoadXml(ZipArchiveEntry entry)
        {
            using var stream = entry.Open();
            return XDocument.Load(stream);
        }

        private static ZipArchiveEntry GetRequired(ZipArchive zip, string name)
        {
            return zip.GetEntry(name) ?? throw new FileNotFoundException("xlsx missing " + name);
        }

        // ======================== config bytes ========================

        private sealed class ConfigArchiveField
        {
            public ConfigArchiveField(string name, string type, int id)
            {
                Name = name;
                Type = type;
                Id = id;
            }

            public string Name { get; }
            public string Type { get; }
            public int Id { get; }
        }

        private sealed class ConfigFbsSource
        {
            public string Path { get; set; } = string.Empty;
            public string Content { get; set; } = string.Empty;
        }

        private static async Task<List<TableSource>> ReadConfigBytesTablesAsync(string logicalPath, byte[] data, ConfigFbsSource fbs, string flatc)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "SourceGit_ConfigBytes_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var binPath = Path.Combine(tempDir, Path.GetFileName(logicalPath));
                await File.WriteAllBytesAsync(binPath, data).ConfigureAwait(false);
                var fbsPath = WriteConfigFbsToTemp(fbs, tempDir);
                var decodeFbs = GetConfigBytesDecodeFbs(fbsPath, binPath, tempDir);
                var jsonPath = await RunFlatcAsync(flatc, decodeFbs, binPath, tempDir).ConfigureAwait(false);
                if (string.IsNullOrEmpty(jsonPath) || !File.Exists(jsonPath))
                    return [];

                return ReadConfigJsonTables(await File.ReadAllTextAsync(jsonPath).ConfigureAwait(false));
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { /* temp cleanup failure is harmless */ }
            }
        }

        private static string WriteConfigFbsToTemp(ConfigFbsSource fbs, string tempDir)
        {
            var path = Path.Combine(tempDir, string.IsNullOrEmpty(fbs.Path) ? "config.fbs" : fbs.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, fbs.Content, Encoding.UTF8);
            return path;
        }

        private static async Task<ConfigFbsSource> ReadRevisionConfigFbsAsync(string repo, string revision)
        {
            if (string.IsNullOrEmpty(revision) || revision == "-R")
                return await ReadFallbackConfigFbsAsync(repo).ConfigureAwait(false);

            var rev = ExtractRevisionForTreeLookup(revision);
            var path = await FindConfigFbsPathInTreeAsync(repo, rev).ConfigureAwait(false);
            if (string.IsNullOrEmpty(path))
                return await ReadFallbackConfigFbsAsync(repo).ConfigureAwait(false);

            var bytes = await Commands.QueryFileContent.RunSpecAsBytesAsync(repo, $"{rev}:{path}").ConfigureAwait(false);
            if (bytes == null)
                return await ReadFallbackConfigFbsAsync(repo).ConfigureAwait(false);

            return new ConfigFbsSource() { Path = path, Content = TextEncoding.Decode(bytes) };
        }

        private static async Task<ConfigFbsSource> ReadIndexedConfigFbsAsync(string repo, bool fallbackToHead)
        {
            var path = await FindConfigFbsPathInIndexAsync(repo).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(path))
            {
                var bytes = await Commands.QueryFileContent.RunSpecAsBytesAsync(repo, $":{path}").ConfigureAwait(false);
                if (bytes != null)
                    return new ConfigFbsSource() { Path = path, Content = TextEncoding.Decode(bytes) };
            }

            return fallbackToHead ? await ReadRevisionConfigFbsAsync(repo, "HEAD").ConfigureAwait(false) : await ReadFallbackConfigFbsAsync(repo).ConfigureAwait(false);
        }

        private static async Task<ConfigFbsSource> ReadWorkingConfigFbsAsync(string repo)
        {
            var path = FindConfigFbs(repo);
            if (!string.IsNullOrEmpty(path))
                return new ConfigFbsSource() { Path = Path.GetRelativePath(repo, path).Replace('\\', '/'), Content = await ReadTextFileAsync(path).ConfigureAwait(false) };

            return await ReadFallbackConfigFbsAsync(repo).ConfigureAwait(false);
        }

        private static async Task<ConfigFbsSource> ReadFallbackConfigFbsAsync(string repo)
        {
            var path = FindConfigFbs(repo);
            if (string.IsNullOrEmpty(path))
                return null;

            return new ConfigFbsSource() { Path = Path.GetFileName(path), Content = await ReadTextFileAsync(path).ConfigureAwait(false) };
        }

        private static async Task<string> ReadTextFileAsync(string path)
        {
            return TextEncoding.Decode(await File.ReadAllBytesAsync(path).ConfigureAwait(false));
        }

        private static string ExtractRevisionForTreeLookup(string revision)
        {
            var colon = revision.IndexOf(':');
            return colon > 0 ? revision[..colon] : revision;
        }

        private static async Task<string> FindConfigFbsPathInTreeAsync(string repo, string revision)
        {
            var lines = await RunGitLinesAsync(repo, "ls-tree", "-r", "--name-only", revision).ConfigureAwait(false);
            return FindConfigFbsPath(lines);
        }

        private static async Task<string> FindConfigFbsPathInIndexAsync(string repo)
        {
            var lines = await RunGitLinesAsync(repo, "ls-files").ConfigureAwait(false);
            return FindConfigFbsPath(lines);
        }

        private static string FindConfigFbsPath(List<string> paths)
        {
            foreach (var path in paths)
            {
                if (path.EndsWith("/config.fbs", StringComparison.OrdinalIgnoreCase) || path.Equals("config.fbs", StringComparison.OrdinalIgnoreCase))
                    return path;
            }

            return null;
        }

        private static async Task<List<string>> RunGitLinesAsync(string repo, params string[] args)
        {
            var psi = new ProcessStartInfo(Native.OS.GitExecutable)
            {
                WorkingDirectory = repo,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            var lines = new List<string>();
            try
            {
                using var process = Process.Start(psi);
                if (process == null)
                    return lines;

                while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                    lines.Add(line);
                await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                await process.WaitForExitAsync().ConfigureAwait(false);
                if (process.ExitCode != 0)
                    lines.Clear();
            }
            catch
            {
                lines.Clear();
            }

            return lines;
        }

        private static string FindConfigFbs(string repo)
        {
            var env = Environment.GetEnvironmentVariable("DIFFTOOL_CONFIG_FBS");
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
                return env;

            var queue = new Queue<string>();
            queue.Enqueue(repo);
            while (queue.Count > 0)
            {
                var dir = queue.Dequeue();
                var hit = Path.Combine(dir, "config.fbs");
                if (File.Exists(hit))
                    return hit;

                string[] subdirs;
                try { subdirs = Directory.GetDirectories(dir); }
                catch { continue; }
                Array.Sort(subdirs, StringComparer.OrdinalIgnoreCase);
                foreach (var subdir in subdirs)
                {
                    var name = Path.GetFileName(subdir);
                    if (name is ".git" or ".vs" or "Library" or "Temp" or "Logs" or "obj" or "bin" or "Build" or "Builds" or "node_modules" or "PackageCache")
                        continue;
                    queue.Enqueue(subdir);
                }
            }

            return null;
        }

        private static string FindFlatc(string repo)
        {
            var env = Environment.GetEnvironmentVariable("DIFFTOOL_FLATC");
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
                return env;

            var names = OperatingSystem.IsWindows() ? new[] { "flatc.exe", "flatc" } : new[] { "flatc", "flatc.exe" };
            foreach (var name in names)
            {
                var candidates = new[]
                {
                    Path.Combine(repo, "Tools", "DiffTool", name),
                    Path.Combine(AppContext.BaseDirectory, name),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiffTool", name),
                    Path.Combine("C:\\ProjectN", "Tools", "DiffTool", name),
                };

                foreach (var candidate in candidates)
                {
                    if (File.Exists(candidate))
                        return candidate;
                }
            }

            return "flatc";
        }

        private static async Task<string> RunFlatcAsync(string flatc, string fbs, string binPath, string tempDir)
        {
            var psi = new ProcessStartInfo(flatc)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in new[] { "--json", "--raw-binary", "--size-prefixed", "--strict-json", "--natural-utf8", "-o", tempDir, fbs, "--", binPath })
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process == null)
                return null;

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().ConfigureAwait(false);
            await stdout.ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0)
                return null;

            return Path.Combine(tempDir, Path.GetFileNameWithoutExtension(binPath) + ".json");
        }

        private static string GetConfigBytesDecodeFbs(string fbs, string binPath, string tempDir)
        {
            var expectedType = Path.GetFileNameWithoutExtension(binPath);
            if (string.IsNullOrEmpty(expectedType))
                return fbs;

            var fields = ParseConfigArchiveFields(fbs);
            if (!fields.TryGetValue(expectedType, out var expectedField))
                return fbs;

            if (!TryGetSinglePresentRootFieldId(binPath, out var presentId) || presentId == expectedField.Id)
                return fbs;

            // Historical ConfigArchive field ids can shift. The bytes file name tells us
            // the intended table type, while the payload vtable tells us the actual root id.
            // A temporary compat schema binds those two facts together before running flatc.
            return CreateConfigArchiveCompatFbs(fbs, tempDir, fields.Values, expectedField, presentId) ?? fbs;
        }

        private static Dictionary<string, ConfigArchiveField> ParseConfigArchiveFields(string fbs)
        {
            var result = new Dictionary<string, ConfigArchiveField>(StringComparer.Ordinal);
            var lines = File.ReadAllLines(fbs);
            var inArchive = false;
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (!inArchive)
                {
                    if (trimmed == "table ConfigArchive {")
                        inArchive = true;
                    continue;
                }

                if (trimmed == "}")
                    break;

                if (TryParseConfigArchiveField(trimmed, out var field))
                    result[field.Type] = field;
            }

            return result;
        }

        private static bool TryParseConfigArchiveField(string line, out ConfigArchiveField field)
        {
            field = null;
            var colon = line.IndexOf(':');
            var arrayStart = line.IndexOf('[', colon + 1);
            var arrayEnd = line.IndexOf(']', arrayStart + 1);
            var idStart = line.IndexOf("(id:", StringComparison.Ordinal);
            if (colon <= 0 || arrayStart < 0 || arrayEnd <= arrayStart || idStart < 0)
                return false;

            idStart += 4;
            var idEnd = line.IndexOf(')', idStart);
            if (idEnd <= idStart || !int.TryParse(line[idStart..idEnd], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                return false;

            var name = line[..colon].Trim();
            var type = line[(arrayStart + 1)..arrayEnd].Trim();
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(type))
                return false;

            field = new ConfigArchiveField(name, type, id);
            return true;
        }

        private static bool TryGetSinglePresentRootFieldId(string binPath, out int fieldId)
        {
            fieldId = -1;
            try
            {
                var data = File.ReadAllBytes(binPath);
                if (!TryGetPresentRootFieldIds(data, 4, out var ids) && !TryGetPresentRootFieldIds(data, 0, out ids))
                    return false;
                if (ids.Count != 1)
                    return false;

                fieldId = ids[0];
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetPresentRootFieldIds(byte[] data, int dataStart, out List<int> ids)
        {
            ids = [];
            if (data.Length < dataStart + 8)
                return false;

            var rootOffset = ReadInt32Le(data, dataStart);
            var tablePos = dataStart + rootOffset;
            if (rootOffset <= 0 || tablePos < 0 || tablePos + 4 > data.Length)
                return false;

            var vtableOffset = ReadInt32Le(data, tablePos);
            var vtablePos = tablePos - vtableOffset;
            if (vtableOffset <= 0 || vtablePos < 0 || vtablePos + 4 > data.Length)
                return false;

            var vtableLength = ReadUInt16Le(data, vtablePos);
            if (vtableLength < 4 || vtablePos + vtableLength > data.Length || (vtableLength & 1) != 0)
                return false;

            var fieldCount = (vtableLength - 4) / 2;
            for (var i = 0; i < fieldCount; i++)
            {
                var fieldOffset = ReadUInt16Le(data, vtablePos + 4 + i * 2);
                if (fieldOffset == 0)
                    continue;

                var fieldValuePos = tablePos + fieldOffset;
                if (fieldValuePos + 4 > data.Length)
                    continue;

                // ConfigArchive top-level fields are vectors. Only non-empty vectors
                // represent the concrete XxxConfig table carried by this bytes file.
                var vectorOffset = ReadInt32Le(data, fieldValuePos);
                var vectorPos = fieldValuePos + vectorOffset;
                if (vectorOffset > 0 && vectorPos + 4 <= data.Length && ReadInt32Le(data, vectorPos) > 0)
                    ids.Add(i);
            }

            return true;
        }

        private static int ReadInt32Le(byte[] data, int offset)
            => data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24);

        private static ushort ReadUInt16Le(byte[] data, int offset)
            => (ushort)(data[offset] | (data[offset + 1] << 8));

        private static string CreateConfigArchiveCompatFbs(
            string fbs,
            string tempDir,
            IEnumerable<ConfigArchiveField> archiveFields,
            ConfigArchiveField expectedField,
            int presentId)
        {
            ConfigArchiveField displacedField = null;
            foreach (var field in archiveFields)
            {
                if (field.Id == presentId)
                {
                    displacedField = field;
                    break;
                }
            }

            if (displacedField == null)
                return null;

            var lines = File.ReadAllLines(fbs);
            var inArchive = false;
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (!inArchive)
                {
                    if (trimmed == "table ConfigArchive {")
                        inArchive = true;
                    continue;
                }

                if (trimmed == "}")
                    break;

                if (!TryParseConfigArchiveField(trimmed, out var field))
                    continue;

                if (field.Id == presentId)
                    lines[i] = $"  {expectedField.Name}:[{expectedField.Type}] (id:{presentId});";
                else if (field.Id == expectedField.Id)
                    lines[i] = $"  difftool_compat_{field.Name}:[{field.Type}] (id:{field.Id});";
            }

            var compatPath = Path.Combine(tempDir, "config.compat.fbs");
            File.WriteAllLines(compatPath, lines, Encoding.UTF8);
            return compatPath;
        }

        private static List<TableSource> ReadConfigJsonTables(string json)
        {
            var tables = new List<TableSource>();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return tables;

            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Array || property.Value.GetArrayLength() == 0)
                    continue;

                var table = new TableSource() { Name = property.Name };
                foreach (var item in property.Value.EnumerateArray())
                    AddConfigRow(table, item);
                tables.Add(table);
            }

            return tables;
        }

        private static void AddConfigRow(TableSource table, JsonElement item)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            if (item.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in item.EnumerateObject())
                {
                    AddIfMissing(table.Headers, property.Name);
                    values[property.Name] = JsonValue(property.Value);
                }
            }
            else
            {
                AddIfMissing(table.Headers, "value");
                values["value"] = JsonValue(item);
            }

            var row = new List<string>();
            foreach (var header in table.Headers)
                row.Add(values.TryGetValue(header, out var value) ? value : string.Empty);
            table.Rows.Add(row);

            var key = GetConfigRowKey(table, row, table.Rows.Count);
            table.Keys.Add(key);
        }

        private static string JsonValue(JsonElement element)
        {
            return element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.GetRawText();
        }

        private static string GetConfigRowKey(TableSource table, List<string> row, int rowNumber)
        {
            var idIndex = FindConfigIdColumn(table.Headers);
            if (idIndex >= 0 && idIndex < row.Count && row[idIndex].Length > 0)
                return row[idIndex];
            return rowNumber.ToString(CultureInfo.InvariantCulture);
        }

        private static int FindConfigIdColumn(List<string> headers)
        {
            for (var i = 0; i < headers.Count; i++)
            {
                var normalized = headers[i].Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
                if (normalized == "id")
                    return i;
            }

            return headers.Count > 0 ? 0 : -1;
        }

        // ======================== prefab / scene hierarchy ========================

        private static readonly Regex PrefabDocRe = new(@"^--- !u!(\d+) &(\d+)", RegexOptions.Multiline | RegexOptions.Compiled);

        private static readonly Dictionary<int, string> PrefabTypeNames = new()
        {
            { 1, "GameObject" }, { 4, "Transform" }, { 20, "Camera" }, { 23, "MeshRenderer" },
            { 33, "MeshFilter" }, { 54, "Rigidbody2D" }, { 61, "BoxCollider2D" }, { 64, "MeshCollider" },
            { 65, "BoxCollider" }, { 81, "AudioListener" }, { 82, "AudioSource" }, { 95, "Animator" },
            { 96, "TrailRenderer" }, { 108, "Light" }, { 111, "Animation" }, { 114, "MonoBehaviour" },
            { 120, "LineRenderer" }, { 135, "SphereCollider" }, { 136, "CapsuleCollider" },
            { 137, "SkinnedMeshRenderer" }, { 154, "TerrainCollider" }, { 198, "ParticleSystem" },
            { 199, "ParticleSystemRenderer" }, { 212, "SpriteRenderer" }, { 218, "Terrain" },
            { 222, "CanvasRenderer" }, { 223, "Canvas" }, { 224, "RectTransform" }, { 225, "CanvasGroup" },
            { 258, "HorizontalLayoutGroup" }, { 259, "VerticalLayoutGroup" }, { 264, "GridLayoutGroup" },
            { 320, "PlayableDirector" }, { 328, "VideoPlayer" }, { 330, "GraphicRaycaster" },
            { 331, "ScrollRect" }, { 369, "ContentSizeFitter" }, { 372, "AspectRatioFitter" },
            { 1001, "PrefabInstance" }, { 1183024399, "LookAtConstraint" },
        };

        private static readonly Dictionary<string, string> PrefabGuidFallbackNames = new(StringComparer.OrdinalIgnoreCase)
        {
            { "fe87c0e1cc204ed48ad3b37840f39efc", "Image" },
            { "f4688fdb7df04437aeb418b961361dc5", "TMP_Text" },
            { "99081db55ede7af4399615f956b00b27", "ColorfulImage" },
            { "4e29b1a8efbd4b44bb3f3716e73f07ff", "Button" },
            { "1367256648004ba4a9cb869e3436c557", "RawImage" },
            { "2a4db7a114972834c8e4117be1d82ba3", "LayoutElement" },
            { "3312d7739989d2b4e91e6319e9a96d76", "Mask" },
            { "31a19414677d06e4884707c6e22bfee8", "RectMask2D" },
            { "1344c3c82d178a64d8d011048bf4b4e7", "Toggle" },
            { "1aa08ab6e0800fa44ae55d278d1423e3", "ScrollRect" },
            { "30649d3a9faa99c48a7b1166b86bf2a0", "HorizontalLayoutGroup" },
            { "59f8146938fff824cb5fd77236b75b03", "VerticalLayoutGroup" },
            { "dc42784cf5e3c4ac9b5c2e1f4476e774", "ContentSizeFitter" },
            { "cfabb0440166ab443bba8876756a24be", "GridLayoutGroup" },
        };

        private static readonly HashSet<string> PrefabSkipProps = new()
        {
            "m_ObjectHideFlags", "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset",
            "m_EditorHideFlags", "m_EditorClassIdentifier", "serializedVersion", "m_Father", "m_Children",
            "m_Component", "m_GameObject", "m_TagString", "m_Icon", "m_NavMeshLayer", "m_StaticEditorFlags",
            "m_ConstrainProportionsScale", "m_SelectOnUp", "m_SelectOnDown", "m_SelectOnLeft", "m_SelectOnRight",
            "m_NormalTrigger", "m_HighlightedTrigger", "m_PressedTrigger", "m_SelectedTrigger", "m_DisabledTrigger",
            "m_WrapAround", "m_Name", "m_IsActive", "m_Layer", "m_LocalEulerAnglesHint", "m_RootOrder", "m_Script",
        };

        private static StructuredDiff BuildPrefabDiff(string repo, string oldText, string newText)
        {
            var assetNames = BuildPrefabAssetNameMap(repo, oldText, newText);
            var oldFormatted = ConvertPrefabYaml(repo, oldText, assetNames);
            var newFormatted = ConvertPrefabYaml(repo, newText, assetNames);
            var oldNodes = ParsePrefabDump(oldFormatted);
            var newNodes = ParsePrefabDump(newFormatted);
            var diff = new StructuredDiff() { Kind = StructuredDiffKind.PrefabHierarchy, Summary = "Prefab hierarchy diff" };
            var nodeMap = ComparePrefabNodes(oldNodes, newNodes);

            foreach (var node in nodeMap.Values)
            {
                if (node.Path.IndexOf('/') < 0)
                    diff.Nodes.Add(node);
            }

            diff.Nodes.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            foreach (var root in diff.Nodes)
                FlattenNode(diff.Rows, root, 0);
            diff.FormattedText = BuildPrefabFormattedText(diff.Rows);
            diff.FormattedTextDiff = BuildLineByLineTextDiff("formatted prefab diff", oldFormatted, newFormatted);
            diff.RawTextDiff = BuildLineByLineTextDiff("raw prefab diff", SplitRawText(oldText), SplitRawText(newText));
            return diff;
        }

        private static TextDiff BuildLineByLineTextDiff(string label, List<string> oldLines, List<string> newLines)
        {
            var diff = new TextDiff();
            diff.Lines.Add(new TextDiffLine(TextDiffLineType.Indicator, "@@ " + label + " @@", [], 0, 0));

            var max = Math.Max(oldLines.Count, newLines.Count);
            for (var i = 0; i < max; i++)
            {
                var hasOld = i < oldLines.Count;
                var hasNew = i < newLines.Count;
                if (hasOld && hasNew && oldLines[i].Equals(newLines[i], StringComparison.Ordinal))
                {
                    diff.Lines.Add(NewTextDiffLine(TextDiffLineType.Normal, oldLines[i], i + 1, i + 1));
                    continue;
                }

                if (hasOld)
                {
                    diff.Lines.Add(NewTextDiffLine(TextDiffLineType.Deleted, oldLines[i], i + 1, 0));
                    diff.DeletedLines++;
                }

                if (hasNew)
                {
                    diff.Lines.Add(NewTextDiffLine(TextDiffLineType.Added, newLines[i], 0, i + 1));
                    diff.AddedLines++;
                }
            }

            diff.MaxLineNumber = max;
            return diff;
        }

        private static List<string> SplitRawText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return [];

            return new List<string>(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'));
        }

        private static TextDiffLine NewTextDiffLine(TextDiffLineType type, string line, int oldLine, int newLine)
        {
            return new TextDiffLine(type, line, Encoding.UTF8.GetBytes(line), oldLine, newLine);
        }

        private static string BuildPrefabFormattedText(List<StructuredDiffNode> rows)
        {
            var builder = new StringBuilder();
            foreach (var node in rows)
            {
                if (node.Change == StructuredDiffChangeKind.None && node.Properties.Count == 0)
                    continue;

                builder.Append(ChangePrefix(node.Change)).Append(' ').AppendLine(node.Path);
                foreach (var prop in node.Properties)
                {
                    builder.Append("  ").Append(ChangePrefix(prop.Change)).Append(' ').Append(prop.Key).Append(": ");
                    if (prop.Change == StructuredDiffChangeKind.Modified)
                        builder.Append(prop.OldValue).Append(" => ").Append(prop.NewValue);
                    else
                        builder.Append(prop.Change == StructuredDiffChangeKind.Deleted ? prop.OldValue : prop.NewValue);
                    builder.AppendLine();
                }
            }

            return builder.Length > 0 ? builder.ToString() : "No structured prefab changes.";
        }

        private static char ChangePrefix(StructuredDiffChangeKind change)
        {
            return change switch
            {
                StructuredDiffChangeKind.Added => '+',
                StructuredDiffChangeKind.Deleted => '-',
                StructuredDiffChangeKind.Modified => '~',
                _ => ' ',
            };
        }

        private sealed class PrefabNodeSource
        {
            public string Path { get; set; } = string.Empty;
            public Dictionary<string, string> Properties { get; set; } = new(StringComparer.Ordinal);
        }

        private static Dictionary<string, PrefabNodeSource> ParsePrefabDump(List<string> lines)
        {
            var nodes = new Dictionary<string, PrefabNodeSource>(StringComparer.Ordinal);
            var pathCount = new Dictionary<string, int>(StringComparer.Ordinal);
            PrefabNodeSource current = null;
            foreach (var line in lines)
            {
                if (line.StartsWith("[", StringComparison.Ordinal))
                {
                    var end = line.IndexOf(']');
                    if (end <= 1)
                        continue;

                    var path = line.Substring(1, end - 1);
                    if (pathCount.TryGetValue(path, out var count))
                    {
                        count++;
                        pathCount[path] = count;
                        path += "#" + count.ToString(CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        pathCount[path] = 1;
                    }

                    if (!nodes.TryGetValue(path, out current))
                    {
                        current = new PrefabNodeSource() { Path = path };
                        nodes[path] = current;
                    }
                    var flags = line.Substring(Math.Min(end + 1, line.Length)).Trim();
                    if (flags.Length > 0)
                        current.Properties["__flags__"] = flags;
                    continue;
                }

                if (current == null || !line.StartsWith("  ", StringComparison.Ordinal))
                    continue;

                var trimmed = line.Trim();
                var colon = trimmed.IndexOf(':');
                if (colon <= 0)
                    continue;
                current.Properties[trimmed.Substring(0, colon)] = trimmed.Substring(colon + 1).Trim();
            }

            return nodes;
        }

        private static Dictionary<string, StructuredDiffNode> ComparePrefabNodes(
            Dictionary<string, PrefabNodeSource> oldNodes,
            Dictionary<string, PrefabNodeSource> newNodes)
        {
            var result = new Dictionary<string, StructuredDiffNode>(StringComparer.Ordinal);
            var paths = new List<string>();
            foreach (var path in oldNodes.Keys)
                AddPrefabPath(paths, path);
            foreach (var path in newNodes.Keys)
                AddPrefabPath(paths, path);
            paths.Sort(StringComparer.Ordinal);

            foreach (var path in paths)
            {
                oldNodes.TryGetValue(path, out var oldNode);
                newNodes.TryGetValue(path, out var newNode);
                var node = EnsurePrefabNode(result, path);
                node.Change = oldNode == null && newNode == null
                    ? StructuredDiffChangeKind.None
                    : oldNode == null
                        ? StructuredDiffChangeKind.Added
                        : newNode == null
                            ? StructuredDiffChangeKind.Deleted
                            : StructuredDiffChangeKind.None;
                ComparePrefabProperties(node, oldNode?.Properties, newNode?.Properties);
                if (node.Change == StructuredDiffChangeKind.None && node.Properties.Count > 0)
                    node.Change = StructuredDiffChangeKind.Modified;
            }

            return result;
        }

        private static void AddPrefabPath(List<string> paths, string path)
        {
            var parts = path.Split('/');
            var current = string.Empty;
            foreach (var part in parts)
            {
                current = current.Length == 0 ? part : current + "/" + part;
                AddIfMissing(paths, current);
            }
        }

        private static StructuredDiffNode EnsurePrefabNode(Dictionary<string, StructuredDiffNode> nodes, string path)
        {
            if (nodes.TryGetValue(path, out var node))
                return node;

            var slash = path.LastIndexOf('/');
            node = new StructuredDiffNode()
            {
                Path = path,
                Name = slash >= 0 ? path.Substring(slash + 1) : path,
            };
            nodes[path] = node;

            if (slash > 0)
            {
                var parent = EnsurePrefabNode(nodes, path.Substring(0, slash));
                parent.Children.Add(node);
                parent.Children.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            }

            return node;
        }

        private static void ComparePrefabProperties(StructuredDiffNode target, Dictionary<string, string> oldProps, Dictionary<string, string> newProps)
        {
            var keys = new List<string>();
            if (oldProps != null)
            {
                foreach (var key in oldProps.Keys)
                    AddIfMissing(keys, key);
            }
            if (newProps != null)
            {
                foreach (var key in newProps.Keys)
                    AddIfMissing(keys, key);
            }
            keys.Sort(StringComparer.Ordinal);

            foreach (var key in keys)
            {
                if (key == "__flags__")
                    continue;

                var oldValue = string.Empty;
                var newValue = string.Empty;
                var hasOld = oldProps != null && oldProps.TryGetValue(key, out oldValue);
                var hasNew = newProps != null && newProps.TryGetValue(key, out newValue);

                var change = !hasOld ? StructuredDiffChangeKind.Added : !hasNew ? StructuredDiffChangeKind.Deleted : oldValue != newValue ? StructuredDiffChangeKind.Modified : StructuredDiffChangeKind.None;
                if (change == StructuredDiffChangeKind.None)
                    continue;

                target.Properties.Add(new StructuredPropertyChange()
                {
                    Key = key,
                    OldValue = oldValue,
                    NewValue = newValue,
                    Change = change,
                });
            }

            var oldFlags = oldProps != null && oldProps.TryGetValue("__flags__", out var oldFlagValue) ? oldFlagValue : string.Empty;
            var newFlags = newProps != null && newProps.TryGetValue("__flags__", out var newFlagValue) ? newFlagValue : string.Empty;
            if (oldFlags != newFlags)
            {
                target.Properties.Add(new StructuredPropertyChange()
                {
                    Key = "[Flags]",
                    OldValue = oldFlags.Length > 0 ? oldFlags : "(none)",
                    NewValue = newFlags.Length > 0 ? newFlags : "(none)",
                    Change = StructuredDiffChangeKind.Modified,
                });
            }
        }

        private static void FlattenNode(List<StructuredDiffNode> rows, StructuredDiffNode node, int depth)
        {
            node.Depth = depth;
            rows.Add(node);
            foreach (var child in node.Children)
                FlattenNode(rows, child, depth + 1);
        }

        private sealed class PrefabSection
        {
            public int TypeId { get; set; }
            public string Fid { get; set; } = string.Empty;
            public string Body { get; set; } = string.Empty;
        }

        private sealed class PrefabInstanceInfo
        {
            public string SourceGuid { get; set; } = string.Empty;
            public string ParentTransformFid { get; set; } = string.Empty;
            public string Body { get; set; } = string.Empty;
        }

        private readonly record struct PrefabInstanceMod(string TargetFid, string TargetGuid, string Property, string Value);

        private static List<string> ConvertPrefabYaml(string repo, string content, Dictionary<string, string> assetNames)
        {
            var sections = ParsePrefabSections(content);
            var byFid = new Dictionary<string, PrefabSection>(StringComparer.Ordinal);
            var goName = new Dictionary<string, string>(StringComparer.Ordinal);
            var goActive = new Dictionary<string, bool>(StringComparer.Ordinal);
            var goTag = new Dictionary<string, string>(StringComparer.Ordinal);
            var tfmGo = new Dictionary<string, string>(StringComparer.Ordinal);
            var tfmParent = new Dictionary<string, string>(StringComparer.Ordinal);
            var tfmChildren = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var goTfm = new Dictionary<string, string>(StringComparer.Ordinal);
            var compGo = new Dictionary<string, List<PrefabSection>>(StringComparer.Ordinal);
            var prefabInstances = new Dictionary<string, PrefabInstanceInfo>(StringComparer.Ordinal);
            var prefabInstancesByParent = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var transformOrder = new List<string>();

            foreach (var section in sections)
            {
                byFid[section.Fid] = section;
                if (section.TypeId == 1)
                {
                    goName[section.Fid] = DecodeUnityEscapes(MatchValue(section.Body, @"m_Name:\s*(.+)", section.Fid));
                    goActive[section.Fid] = MatchValue(section.Body, @"m_IsActive:\s*(\d+)", "1") != "0";
                    goTag[section.Fid] = DecodeUnityEscapes(MatchValue(section.Body, @"m_TagString:\s*(.+)", string.Empty));
                }
                else if (section.TypeId is 4 or 224)
                {
                    var go = MatchValue(section.Body, @"m_GameObject:\s*\{fileID:\s*(-?\d+)\}", string.Empty);
                    if (go.Length > 0)
                    {
                        tfmGo[section.Fid] = go;
                        goTfm[go] = section.Fid;
                    }

                    var parent = MatchValue(section.Body, @"m_Father:\s*\{fileID:\s*(-?\d+)\}", string.Empty);
                    if (parent != "0")
                        tfmParent[section.Fid] = parent;

                    tfmChildren[section.Fid] = ParseChildren(section.Body);
                    transformOrder.Add(section.Fid);
                }
                else if (section.TypeId == 1001)
                {
                    var sourceGuid = MatchValue(section.Body, @"m_SourcePrefab:.*?guid:\s*([0-9a-fA-F]+)", string.Empty);
                    var parent = MatchValue(section.Body, @"m_TransformParent:\s*\{fileID:\s*(-?\d+)\}", "0");
                    if (sourceGuid.Length == 0)
                        continue;

                    prefabInstances[section.Fid] = new PrefabInstanceInfo()
                    {
                        SourceGuid = sourceGuid,
                        ParentTransformFid = parent,
                        Body = section.Body,
                    };
                    if (!prefabInstancesByParent.TryGetValue(parent, out var pis))
                    {
                        pis = [];
                        prefabInstancesByParent[parent] = pis;
                    }
                    pis.Add(section.Fid);
                }
                else if (section.TypeId != 1001)
                {
                    var go = MatchValue(section.Body, @"m_GameObject:\s*\{fileID:\s*(-?\d+)\}", string.Empty);
                    if (go.Length == 0)
                        continue;
                    if (!compGo.TryGetValue(go, out var list))
                    {
                        list = [];
                        compGo[go] = list;
                    }
                    list.Add(section);
                }
            }

            var lines = new List<string>();
            var emitted = new HashSet<string>(StringComparer.Ordinal);
            var emittedPrefabInstances = new HashSet<string>(StringComparer.Ordinal);
            var refLabels = new Dictionary<string, string>(StringComparer.Ordinal);

            string ResolveLocalRef(string fid)
            {
                return refLabels.TryGetValue(fid, out var label) ? label : string.Empty;
            }

            void RenderNode(string tfmFid, string prefix)
            {
                if (!tfmGo.TryGetValue(tfmFid, out var goFid) || emitted.Contains(tfmFid))
                    return;
                emitted.Add(tfmFid);
                emitted.Add(goFid);

                var name = goName.TryGetValue(goFid, out var goDisplay) ? goDisplay : goFid;
                var path = prefix.Length == 0 ? name : prefix + "/" + name;
                var flags = new List<string>();
                if (goActive.TryGetValue(goFid, out var active) && !active)
                    flags.Add("Inactive");
                if (goTag.TryGetValue(goFid, out var tag) && tag.Length > 0 && tag != "Untagged")
                    flags.Add("tag:" + tag);
                lines.Add(flags.Count > 0 ? $"[{path}]  [{string.Join(", ", flags)}]" : $"[{path}]");

                if (byFid.TryGetValue(tfmFid, out var tfm))
                {
                    var typeName = tfm.TypeId == 224 ? "RectTransform" : "Transform";
                    refLabels[goFid] = path + "/GameObject";
                    refLabels[tfmFid] = path + "/" + typeName;
                    if (compGo.TryGetValue(goFid, out var nodeCompsForRefs))
                    {
                        foreach (var comp in nodeCompsForRefs)
                            refLabels[comp.Fid] = path + "/" + PrefabComponentName(comp.TypeId, comp.Body, assetNames);
                    }
                    foreach (var prop in ExtractPrefabProps(tfm.Body, assetNames, ResolveLocalRef))
                        lines.Add("  " + typeName + "." + prop.Key + ": " + prop.Value);
                }

                if (compGo.TryGetValue(goFid, out var comps))
                {
                    foreach (var comp in comps)
                    {
                        emitted.Add(comp.Fid);
                        var compName = PrefabComponentName(comp.TypeId, comp.Body, assetNames);
                        refLabels[comp.Fid] = path + "/" + compName;
                        foreach (var prop in ExtractPrefabProps(comp.Body, assetNames, ResolveLocalRef))
                            lines.Add("  " + compName + "." + prop.Key + ": " + prop.Value);
                    }
                }

                if (tfmChildren.TryGetValue(tfmFid, out var children))
                {
                    foreach (var child in children)
                        RenderNode(child, path);
                }

                if (prefabInstancesByParent.TryGetValue(tfmFid, out var nestedPrefabInstances))
                {
                    foreach (var prefabInstanceFid in nestedPrefabInstances)
                        RenderPrefabInstance(prefabInstanceFid, path);
                }
            }

            void RenderPrefabInstance(string prefabInstanceFid, string prefix)
            {
                if (!prefabInstances.TryGetValue(prefabInstanceFid, out var prefabInstance) || !emittedPrefabInstances.Add(prefabInstanceFid))
                    return;

                var sourceName = PrefabAssetLabel(prefabInstance.SourceGuid, assetNames);
                var mods = NormalizePrefabInstanceMods(ParsePrefabInstanceMods(prefabInstance.Body));
                var customName = string.Empty;
                foreach (var mod in mods)
                {
                    if (mod.Property == "m_Name")
                    {
                        customName = mod.Value;
                        break;
                    }
                }

                var nodeName = customName.Length > 0 ? customName + " (" + sourceName + ")" : sourceName;
                var path = prefix.Length == 0 ? nodeName : prefix + "/" + nodeName;
                lines.Add($"[{path}]  [Prefab]");
                emitted.Add(prefabInstanceFid);

                foreach (var mod in mods)
                {
                    if (mod.Property == "m_Name")
                        continue;

                    var targetPath = ResolvePrefabOverridePath(path, mod, prefabInstance.SourceGuid, assetNames, ResolveLocalRef);
                    if (!string.Equals(targetPath, path, StringComparison.Ordinal))
                        lines.Add($"[{targetPath}]  [PrefabOverride]");
                    var value = NormalizeUnityRefs(mod.Value, assetNames, ResolveLocalRef);
                    lines.Add("  PrefabOverride." + mod.Property + ": " + value);
                }
            }

            foreach (var fid in transformOrder)
            {
                if (!tfmParent.ContainsKey(fid))
                    RenderNode(fid, string.Empty);
            }

            if (prefabInstancesByParent.TryGetValue("0", out var rootPrefabInstances))
            {
                foreach (var prefabInstanceFid in rootPrefabInstances)
                    RenderPrefabInstance(prefabInstanceFid, string.Empty);
            }

            foreach (var section in sections)
            {
                if (emitted.Contains(section.Fid) || section.TypeId is 4 or 224)
                    continue;
                if (section.TypeId == 1 && goTfm.TryGetValue(section.Fid, out var tfm))
                {
                    RenderNode(tfm, string.Empty);
                    continue;
                }

                var props = ExtractPrefabProps(section.Body, assetNames, ResolveLocalRef);
                if (props.Count == 0)
                    continue;

                var label = section.TypeId == 1
                    ? "GameObject:" + (goName.TryGetValue(section.Fid, out var name) ? name : section.Fid)
                    : PrefabComponentName(section.TypeId, section.Body, assetNames);
                lines.Add($"[{label} #{section.Fid}]");
                foreach (var prop in props)
                    lines.Add("  " + label + "." + prop.Key + ": " + prop.Value);
            }

            return lines;
        }

        private static List<PrefabSection> ParsePrefabSections(string content)
        {
            var sections = new List<PrefabSection>();
            var matches = PrefabDocRe.Matches(content);
            for (var i = 0; i < matches.Count; i++)
            {
                var match = matches[i];
                var start = match.Index + match.Length;
                var end = i + 1 < matches.Count ? matches[i + 1].Index : content.Length;
                sections.Add(new PrefabSection()
                {
                    TypeId = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                    Fid = match.Groups[2].Value,
                    Body = content.Substring(start, end - start),
                });
            }

            return sections;
        }

        private static string MatchValue(string body, string pattern, string fallback)
        {
            var match = Regex.Match(body, pattern);
            return match.Success ? match.Groups[1].Value.Trim() : fallback;
        }

        private static List<string> ParseChildren(string body)
        {
            var children = new List<string>();
            var start = body.IndexOf("m_Children:", StringComparison.Ordinal);
            if (start < 0)
                return children;

            var end = body.IndexOf("m_Father:", start, StringComparison.Ordinal);
            var section = end >= 0 ? body.Substring(start, end - start) : body.Substring(start);
            foreach (Match match in Regex.Matches(section, @"\{fileID:\s*(-?\d+)\}"))
            {
                if (match.Groups[1].Value != "0")
                    children.Add(match.Groups[1].Value);
            }

            return children;
        }

        private static string PrefabComponentName(int typeId, string body, Dictionary<string, string> assetNames)
        {
            var name = PrefabTypeNames.TryGetValue(typeId, out var typeName) ? typeName : "Type" + typeId.ToString(CultureInfo.InvariantCulture);
            if (typeId != 114)
                return name;

            var scriptGuid = MatchValue(body, @"m_Script:.*?guid:\s*([0-9a-f]+)", string.Empty);
            if (scriptGuid.Length == 0)
                return "MonoBehaviour";

            if (assetNames != null && assetNames.TryGetValue(scriptGuid, out var scriptName))
                return scriptName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(scriptName) : scriptName;

            if (PrefabGuidFallbackNames.TryGetValue(scriptGuid, out var fallbackName))
                return fallbackName;

            return "Script:" + scriptGuid.Substring(0, Math.Min(8, scriptGuid.Length));
        }

        private static Dictionary<string, string> BuildPrefabAssetNameMap(string repo, string oldText, string newText)
        {
            var wantedGuids = CollectPrefabGuids(oldText, newText);
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (wantedGuids.Count == 0 || string.IsNullOrEmpty(repo) || !Directory.Exists(repo))
                return map;

            var assetsRoot = Path.Combine(repo, "Assets");
            if (!Directory.Exists(assetsRoot))
                assetsRoot = repo;

            try
            {
                foreach (var meta in Directory.EnumerateFiles(assetsRoot, "*.meta", SearchOption.AllDirectories))
                {
                    var content = File.ReadAllText(meta);
                    var guidMatch = Regex.Match(content, @"^guid:\s*([0-9a-fA-F]+)", RegexOptions.Multiline);
                    var guid = guidMatch.Success ? guidMatch.Groups[1].Value : string.Empty;
                    if (guid.Length == 0 || !wantedGuids.Contains(guid))
                        continue;

                    // Prefab references only store GUID/fileID. Resolving only GUIDs present in this
                    // diff keeps short prefab views fast while still making cached references readable.
                    map[guid] = Path.GetFileName(meta.Substring(0, meta.Length - ".meta".Length));
                    if (map.Count == wantedGuids.Count)
                        break;
                }
            }
            catch
            {
                // Asset name resolution is best-effort; short GUIDs remain as a safe fallback.
            }

            return map;
        }

        private static HashSet<string> CollectPrefabGuids(string oldText, string newText)
        {
            var guids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddPrefabGuids(guids, oldText);
            AddPrefabGuids(guids, newText);
            return guids;
        }

        private static void AddPrefabGuids(HashSet<string> guids, string text)
        {
            if (string.IsNullOrEmpty(text))
                return;

            foreach (Match match in Regex.Matches(text, @"guid:\s*([0-9a-fA-F]+)"))
            {
                var guid = match.Groups[1].Value;
                if (guid.Length > 0)
                    guids.Add(guid);
            }
        }

        private static List<KeyValuePair<string, string>> ExtractPrefabProps(
            string body,
            Dictionary<string, string> assetNames,
            Func<string, string> refResolver = null)
        {
            var props = new List<KeyValuePair<string, string>>();
            var lines = body.Split('\n');
            foreach (var rawLine in lines)
            {
                var stripped = rawLine.Trim();
                if (stripped.Length == 0 || stripped[0] == '-' || stripped.IndexOf(':') < 0)
                    continue;

                var colon = stripped.IndexOf(':');
                var key = stripped.Substring(0, colon).Trim();
                var value = stripped.Substring(colon + 1).Trim();
                if (value.Length == 0 || PrefabSkipProps.Contains(key))
                    continue;

                value = NormalizeUnityRefs(value, assetNames, refResolver);
                props.Add(new KeyValuePair<string, string>(key, value));
            }

            return CombineVectorPrefabProps(FormatStandalonePrefabColorProps(CombineColorPrefabProps(props)));
        }

        private static string NormalizeUnityRefs(string value, Dictionary<string, string> assetNames, Func<string, string> refResolver = null)
        {
            if (string.IsNullOrEmpty(value) || value.IndexOf("fileID:", StringComparison.Ordinal) < 0)
                return DecodeUnityEscapes(value);

            return Regex.Replace(DecodeUnityEscapes(value), @"\{fileID:\s*(-?\d+)(?:\s*,\s*guid:\s*([0-9a-fA-F]+))?(?:\s*,\s*type:\s*\d+)?\s*\}", match =>
            {
                var fileId = match.Groups[1].Value;
                var guid = match.Groups[2].Success ? match.Groups[2].Value : string.Empty;
                if (fileId == "0")
                    return "null";

                if (guid.Length > 0)
                {
                    var asset = PrefabAssetLabel(guid, assetNames);
                    return asset.Length > 0 ? $"{{fileID:{fileId}, asset:{asset}}}" : $"{{fileID:{fileId}, guid:{ShortGuid(guid)}}}";
                }

                var label = refResolver?.Invoke(fileId) ?? string.Empty;
                return label.Length > 0 ? $"{{fileID:{fileId} -> {label}}}" : $"{{fileID:{fileId}}}";
            });
        }

        private static string PrefabAssetLabel(string guid, Dictionary<string, string> assetNames)
        {
            if (string.IsNullOrEmpty(guid))
                return string.Empty;
            if (assetNames != null && assetNames.TryGetValue(guid, out var assetName))
                return assetName;
            if (PrefabGuidFallbackNames.TryGetValue(guid, out var fallbackName))
                return fallbackName;
            return ShortGuid(guid);
        }

        private static string ShortGuid(string guid)
        {
            return guid.Substring(0, Math.Min(8, guid.Length)) + "...";
        }

        private static string ResolvePrefabOverridePath(
            string instancePath,
            PrefabInstanceMod mod,
            string sourceGuid,
            Dictionary<string, string> assetNames,
            Func<string, string> refResolver)
        {
            var localLabel = refResolver?.Invoke(mod.TargetFid) ?? string.Empty;
            if (!string.IsNullOrEmpty(localLabel))
            {
                var slash = localLabel.LastIndexOf('/');
                return slash > 0 ? localLabel.Substring(0, slash) : localLabel;
            }

            var targetGuid = string.IsNullOrEmpty(mod.TargetGuid) ? sourceGuid : mod.TargetGuid;
            var sourceName = PrefabAssetLabel(targetGuid, assetNames);
            return instancePath + "/PrefabOverrides/" + sourceName + "#" + mod.TargetFid;
        }

        private static List<PrefabInstanceMod> ParsePrefabInstanceMods(string body)
        {
            var mods = new List<PrefabInstanceMod>();
            foreach (var block in Regex.Split(body, @"(?=\n\s*- target:)"))
            {
                var target = Regex.Match(block, @"target:\s*\{fileID:\s*(-?\d+)(?:,\s*guid:\s*([0-9a-fA-F]+))?");
                var property = Regex.Match(block, @"propertyPath:\s*(.+)");
                if (!target.Success || !property.Success)
                    continue;

                var prop = property.Groups[1].Value.Trim();
                if (prop.Length == 0 || (prop != "m_Name" && PrefabSkipProps.Contains(prop)))
                    continue;

                var value = MatchValue(block, @"(?m)^\s+value:\s*(.*)$", string.Empty);
                if (value.Length == 0)
                    value = MatchValue(block, @"(?m)^\s+objectReference:\s*(.*)$", string.Empty);
                mods.Add(new PrefabInstanceMod(
                    target.Groups[1].Value,
                    target.Groups[2].Success ? target.Groups[2].Value : string.Empty,
                    prop,
                    DecodeUnityEscapes(value.Trim())));
            }

            return mods;
        }

        private static List<PrefabInstanceMod> NormalizePrefabInstanceMods(List<PrefabInstanceMod> mods)
        {
            return CombineVectorPrefabMods(FormatStandalonePrefabColorMods(CombineColorPrefabMods(mods)));
        }

        private static string DecodeUnityEscapes(string value)
        {
            if (string.IsNullOrEmpty(value) || value.IndexOf("\\u", StringComparison.Ordinal) < 0)
                return value;

            var builder = new StringBuilder();
            var matches = Regex.Matches(value, @"\\u([0-9a-fA-F]{4})");
            var position = 0;
            for (var i = 0; i < matches.Count; i++)
            {
                var match = matches[i];
                builder.Append(value, position, match.Index - position);
                var code = int.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var next = i + 1 < matches.Count ? matches[i + 1] : null;
                if (code is >= 0xD800 and <= 0xDBFF && next != null && next.Index == match.Index + match.Length)
                {
                    var low = int.Parse(next.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    if (low is >= 0xDC00 and <= 0xDFFF)
                    {
                        var codePoint = 0x10000 + ((code - 0xD800) << 10) + (low - 0xDC00);
                        builder.Append(char.ConvertFromUtf32(codePoint));
                        position = next.Index + next.Length;
                        i++;
                        continue;
                    }
                }

                // Unity YAML may contain unmatched surrogate escape sequences in damaged text.
                // Preserve those code units instead of failing the entire structured diff view.
                builder.Append(code is >= 0xD800 and <= 0xDFFF ? (char)code : char.ConvertFromUtf32(code));
                position = match.Index + match.Length;
            }

            builder.Append(value, position, value.Length - position);
            return builder.ToString();
        }

        private static List<KeyValuePair<string, string>> FormatStandalonePrefabColorProps(List<KeyValuePair<string, string>> props)
        {
            var result = new List<KeyValuePair<string, string>>(props.Count);
            foreach (var prop in props)
            {
                var key = prop.Key;
                var value = prop.Value;
                FormatStandalonePrefabColor(ref key, ref value);
                result.Add(new KeyValuePair<string, string>(key, value));
            }

            return result;
        }

        private static List<PrefabInstanceMod> FormatStandalonePrefabColorMods(List<PrefabInstanceMod> mods)
        {
            var result = new List<PrefabInstanceMod>(mods.Count);
            foreach (var mod in mods)
            {
                var key = mod.Property;
                var value = mod.Value;
                FormatStandalonePrefabColor(ref key, ref value);
                result.Add(mod with { Property = key, Value = value });
            }

            return result;
        }

        private static void FormatStandalonePrefabColor(ref string key, ref string value)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value))
                return;

            if (key.EndsWith(".rgba", StringComparison.Ordinal) && TryFormatPackedPrefabRgba(value, out var packed))
            {
                var baseKey = key.Substring(0, key.Length - ".rgba".Length);
                if (IsColorPrefabKey(baseKey))
                {
                    key = baseKey;
                    value = packed;
                    return;
                }
            }

            if (IsColorPrefabKey(key) && TryFormatInlinePrefabColor(value, out var inline))
                value = inline;
        }

        private static bool TryFormatInlinePrefabColor(string value, out string formatted)
        {
            formatted = string.Empty;
            var match = Regex.Match(value, @"^\{\s*r\s*:\s*([^,{}]+)\s*,\s*g\s*:\s*([^,{}]+)\s*,\s*b\s*:\s*([^,{}]+)\s*,\s*a\s*:\s*([^,{}]+)\s*\}$");
            if (!match.Success)
                return false;

            var values = new Dictionary<char, string>()
            {
                ['r'] = match.Groups[1].Value.Trim(),
                ['g'] = match.Groups[2].Value.Trim(),
                ['b'] = match.Groups[3].Value.Trim(),
                ['a'] = match.Groups[4].Value.Trim(),
            };
            formatted = FormatPrefabColorValue(values);
            return true;
        }

        private static bool TryFormatPackedPrefabRgba(string value, out string formatted)
        {
            formatted = string.Empty;
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var raw))
                return false;

            var packed = unchecked((uint)raw);
            if (raw != packed && raw + (1L << 32) != packed)
                return false;

            var r = packed & 0xFF;
            var g = (packed >> 8) & 0xFF;
            var b = (packed >> 16) & 0xFF;
            var a = (packed >> 24) & 0xFF;
            var alpha = ((double)a / 255).ToString("0.####", CultureInfo.InvariantCulture);
            formatted = string.Format(CultureInfo.InvariantCulture, "rgba({0}, {1}, {2}, {3}) #{0:X2}{1:X2}{2:X2}{4:X2} {{rgba: {5}, hex: 0x{6:X8}, r: {0}, g: {1}, b: {2}, a: {4}}}", r, g, b, alpha, a, value, packed);
            return true;
        }

        private static bool IsColorPrefabKey(string key)
        {
            return key.IndexOf("color", StringComparison.OrdinalIgnoreCase) >= 0 ||
                key.IndexOf("tint", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool TryParsePrefabChannelKey(string key, string channels, out string name, out char channel)
        {
            name = string.Empty;
            channel = '\0';
            if (string.IsNullOrEmpty(key) || key.Length < 3 || key[^2] != '.')
                return false;

            channel = key[^1];
            if (channels.IndexOf(channel) < 0)
                return false;
            name = key.Substring(0, key.Length - 2);
            return name.Length > 0;
        }

        private static List<KeyValuePair<string, string>> CombineColorPrefabProps(List<KeyValuePair<string, string>> props)
        {
            return CombinePrefabChannelProps(props, "rgba", name => IsColorPrefabKey(name), FormatPrefabColorValue);
        }

        private static List<KeyValuePair<string, string>> CombineVectorPrefabProps(List<KeyValuePair<string, string>> props)
        {
            return CombinePrefabChannelProps(props, "xyzw", _ => true, FormatPrefabVectorValue);
        }

        private static List<KeyValuePair<string, string>> CombinePrefabChannelProps(
            List<KeyValuePair<string, string>> props,
            string channels,
            Func<string, bool> acceptName,
            Func<Dictionary<char, string>, string> format)
        {
            var grouped = new Dictionary<string, Dictionary<char, (string Value, int Index)>>(StringComparer.Ordinal);
            for (var i = 0; i < props.Count; i++)
            {
                if (!TryParsePrefabChannelKey(props[i].Key, channels, out var name, out var channel) || !acceptName(name) || !float.TryParse(props[i].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    continue;
                if (!grouped.TryGetValue(name, out var values))
                {
                    values = [];
                    grouped[name] = values;
                }
                values[channel] = (props[i].Value, i);
            }

            var consumed = new HashSet<int>();
            var combined = new Dictionary<int, KeyValuePair<string, string>>();
            foreach (var entry in grouped)
            {
                var order = RequiredPrefabChannelOrder(entry.Value.Keys, channels);
                if (order.Length == 0)
                    continue;

                var first = int.MaxValue;
                var values = new Dictionary<char, string>();
                foreach (var channel in order)
                {
                    var item = entry.Value[channel];
                    consumed.Add(item.Index);
                    first = Math.Min(first, item.Index);
                    values[channel] = item.Value;
                }
                combined[first] = new KeyValuePair<string, string>(entry.Key, format(values));
            }

            var result = new List<KeyValuePair<string, string>>();
            for (var i = 0; i < props.Count; i++)
            {
                if (combined.TryGetValue(i, out var item))
                    result.Add(item);
                if (!consumed.Contains(i))
                    result.Add(props[i]);
            }
            return result;
        }

        private static string RequiredPrefabChannelOrder(Dictionary<char, (string Value, int Index)>.KeyCollection present, string channels)
        {
            bool Has(char channel) => present.Contains(channel);
            if (channels == "rgba")
                return Has('r') && Has('g') && Has('b') && Has('a') ? "rgba" : string.Empty;
            if (Has('x') && Has('y') && Has('z') && Has('w'))
                return "xyzw";
            if (Has('x') && Has('y') && Has('z'))
                return "xyz";
            return Has('x') && Has('y') ? "xy" : string.Empty;
        }

        private static string FormatPrefabVectorValue(Dictionary<char, string> values)
        {
            var keys = new List<char>(values.Keys);
            keys.Sort();
            var parts = new List<string>();
            foreach (var key in keys)
                parts.Add(key + ": " + values[key]);
            return "{" + string.Join(", ", parts) + "}";
        }

        private static string FormatPrefabColorValue(Dictionary<char, string> values)
        {
            var r = ClampPrefabColor(values['r']);
            var g = ClampPrefabColor(values['g']);
            var b = ClampPrefabColor(values['b']);
            var a = ClampPrefabColor(values['a']);
            var ri = (int)Math.Round(r * 255);
            var gi = (int)Math.Round(g * 255);
            var bi = (int)Math.Round(b * 255);
            var ai = (int)Math.Round(a * 255);
            var alpha = a.ToString("0.####", CultureInfo.InvariantCulture);
            return string.Format(CultureInfo.InvariantCulture, "rgba({0}, {1}, {2}, {3}) #{0:X2}{1:X2}{2:X2}{4:X2} {{r: {5}, g: {6}, b: {7}, a: {8}}}", ri, gi, bi, alpha, ai, values['r'], values['g'], values['b'], values['a']);
        }

        private static double ClampPrefabColor(string value)
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                return 0;
            return Math.Max(0, Math.Min(1, parsed));
        }

        private static List<PrefabInstanceMod> CombineColorPrefabMods(List<PrefabInstanceMod> mods)
        {
            return CombinePrefabChannelMods(mods, "rgba", name => IsColorPrefabKey(name), FormatPrefabColorValue);
        }

        private static List<PrefabInstanceMod> CombineVectorPrefabMods(List<PrefabInstanceMod> mods)
        {
            return CombinePrefabChannelMods(mods, "xyzw", _ => true, FormatPrefabVectorValue);
        }

        private static List<PrefabInstanceMod> CombinePrefabChannelMods(
            List<PrefabInstanceMod> mods,
            string channels,
            Func<string, bool> acceptName,
            Func<Dictionary<char, string>, string> format)
        {
            var grouped = new Dictionary<string, Dictionary<char, (string Value, int Index)>>(StringComparer.Ordinal);
            for (var i = 0; i < mods.Count; i++)
            {
                if (!TryParsePrefabChannelKey(mods[i].Property, channels, out var name, out var channel) || !acceptName(name) || !float.TryParse(mods[i].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    continue;
                var groupKey = mods[i].TargetGuid + "\n" + mods[i].TargetFid + "\n" + name;
                if (!grouped.TryGetValue(groupKey, out var values))
                {
                    values = [];
                    grouped[groupKey] = values;
                }
                values[channel] = (mods[i].Value, i);
            }

            var consumed = new HashSet<int>();
            var combined = new Dictionary<int, PrefabInstanceMod>();
            foreach (var entry in grouped)
            {
                var order = RequiredPrefabChannelOrder(entry.Value.Keys, channels);
                if (order.Length == 0)
                    continue;

                var first = int.MaxValue;
                var values = new Dictionary<char, string>();
                foreach (var channel in order)
                {
                    var item = entry.Value[channel];
                    consumed.Add(item.Index);
                    first = Math.Min(first, item.Index);
                    values[channel] = item.Value;
                }
                var mod = mods[first];
                var name = mod.Property.Substring(0, mod.Property.Length - 2);
                combined[first] = mod with { Property = name, Value = format(values) };
            }

            var result = new List<PrefabInstanceMod>();
            for (var i = 0; i < mods.Count; i++)
            {
                if (combined.TryGetValue(i, out var item))
                    result.Add(item);
                if (!consumed.Contains(i))
                    result.Add(mods[i]);
            }
            return result;
        }
    }
}
