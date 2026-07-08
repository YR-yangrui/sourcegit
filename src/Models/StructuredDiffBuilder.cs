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
                    return BuildPrefabDiff(repo, option, oldText, newText);
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

        private static StructuredDiff BuildPrefabDiff(string repo, DiffOption option, string oldText, string newText)
        {
            return TryBuildPrefabDiffWithBundledTool(repo, option, oldText, newText) ??
                BuildPrefabToolUnavailableDiff(oldText, newText);
        }

        private static StructuredDiff TryBuildPrefabDiffWithBundledTool(string repo, DiffOption option, string oldText, string newText)
        {
            var toolDir = Path.Combine(AppContext.BaseDirectory, "Resources", "PrefabDiffTool");
            var bridge = Path.Combine(toolDir, "sourcegit_prefab_bridge.py");
            if (!File.Exists(bridge))
                return null;

            var tempDir = Path.Combine(Path.GetTempPath(), "sourcegit-prefab-diff-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tempDir);
                var oldFile = Path.Combine(tempDir, "old.prefab");
                var newFile = Path.Combine(tempDir, "new.prefab");
                File.WriteAllText(oldFile, oldText ?? string.Empty, new UTF8Encoding(false));
                File.WriteAllText(newFile, newText ?? string.Empty, new UTF8Encoding(false));

                var json = RunPrefabBridge(toolDir, bridge, oldFile, newFile, repo, option);
                if (string.IsNullOrWhiteSpace(json))
                    return null;

                using var doc = JsonDocument.Parse(json);
                return BuildPrefabDiffFromToolReport(doc.RootElement, oldText, newText);
            }
            catch
            {
                // The bundled parser is best-effort because it depends on a Python runtime.
                // If it cannot run, return null so SourceGit falls back to its normal diff path
                // instead of showing stale results from the removed native prefab parser.
                return null;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDir))
                        Directory.Delete(tempDir, true);
                }
                catch
                {
                    // Temp cleanup failure should not affect diff rendering.
                }
            }
        }

        private static string RunPrefabBridge(string toolDir, string bridge, string oldFile, string newFile, string repo, DiffOption option)
        {
            foreach (var candidate in PythonCandidates())
            {
                try
                {
                    var start = new ProcessStartInfo(candidate.FileName)
                    {
                        WorkingDirectory = toolDir,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8,
                    };
                    start.Environment["PREFAB_DIFF_PRINT_OUTPUT"] = "1";

                    foreach (var arg in candidate.PrefixArgs)
                        start.ArgumentList.Add(arg);
                    start.ArgumentList.Add(bridge);
                    start.ArgumentList.Add("--old");
                    start.ArgumentList.Add(oldFile);
                    start.ArgumentList.Add("--new");
                    start.ArgumentList.Add(newFile);
                    start.ArgumentList.Add("--repo");
                    start.ArgumentList.Add(repo ?? string.Empty);
                    start.ArgumentList.Add("--path");
                    start.ArgumentList.Add(option.Path ?? string.Empty);
                    start.ArgumentList.Add("--project-root");
                    start.ArgumentList.Add(repo ?? string.Empty);
                    start.ArgumentList.Add("--filename");
                    start.ArgumentList.Add(Path.GetFileName(option.Path) ?? string.Empty);

                    using var process = Process.Start(start);
                    if (process == null)
                        continue;

                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(60000))
                    {
                        process.Kill(true);
                        continue;
                    }

                    var output = stdout.GetAwaiter().GetResult();
                    _ = stderr.GetAwaiter().GetResult();
                    if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                        return output;
                }
                catch
                {
                    // Try the next common Python launcher.
                }
            }

            return null;
        }

        private static StructuredDiff BuildPrefabToolUnavailableDiff(string oldText, string newText)
        {
            var diff = new StructuredDiff()
            {
                Kind = StructuredDiffKind.PrefabHierarchy,
                Summary = "Unity-Prefab-Diff tool unavailable",
                RawTextDiff = BuildLineByLineTextDiff("raw prefab diff", SplitRawText(oldText), SplitRawText(newText)),
            };

            var node = new StructuredDiffNode()
            {
                Path = "Unity-Prefab-Diff Tool",
                Name = "Unity-Prefab-Diff Tool",
                Change = StructuredDiffChangeKind.Modified,
                Properties =
                [
                    new StructuredPropertyChange()
                    {
                        Key = "[Error]",
                        OldValue = string.Empty,
                        NewValue = "The bundled Unity-Prefab-Diff bridge could not run. Check that Resources/PrefabDiffTool is included next to SourceGit.exe and Python 3 is available.",
                        Change = StructuredDiffChangeKind.Modified,
                    }
                ],
            };

            diff.Nodes.Add(node);
            FlattenNode(diff.Rows, node, 0);
            diff.FormattedText = BuildPrefabFormattedText(diff.Rows);
            return diff;
        }

        private static IEnumerable<(string FileName, string[] PrefixArgs)> PythonCandidates()
        {
            yield return ("python", []);
            yield return ("python3", []);
            yield return ("py", ["-3"]);
        }

        private static StructuredDiff BuildPrefabDiffFromToolReport(JsonElement report, string oldText, string newText)
        {
            var diff = new StructuredDiff() { Kind = StructuredDiffKind.PrefabHierarchy, Summary = "Prefab hierarchy diff" };
            var nodeMap = new Dictionary<string, StructuredDiffNode>(StringComparer.Ordinal);

            AddReportPaths(nodeMap, report, "oldPaths");
            AddReportPaths(nodeMap, report, "newPaths");
            AddRenamedReportPaths(nodeMap, report);
            ApplyReportNodes(nodeMap, report, "added", StructuredDiffChangeKind.Added);
            ApplyReportNodes(nodeMap, report, "removed", StructuredDiffChangeKind.Deleted);
            ApplyModifiedReportNodes(nodeMap, report);

            foreach (var node in nodeMap.Values)
            {
                if (node.Path.IndexOf('/') < 0)
                    diff.Nodes.Add(node);
            }

            diff.Nodes.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            foreach (var root in diff.Nodes)
                FlattenNode(diff.Rows, root, 0);
            diff.FormattedText = BuildPrefabFormattedText(diff.Rows);
            diff.RawTextDiff = BuildLineByLineTextDiff("raw prefab diff", SplitRawText(oldText), SplitRawText(newText));
            return diff;
        }

        private static void AddReportPaths(Dictionary<string, StructuredDiffNode> nodeMap, JsonElement report, string property)
        {
            if (!report.TryGetProperty(property, out var paths) || paths.ValueKind != JsonValueKind.Array)
                return;

            foreach (var path in paths.EnumerateArray())
                EnsureReportPrefabNode(nodeMap, path.GetString());
        }

        private static void AddRenamedReportPaths(Dictionary<string, StructuredDiffNode> nodeMap, JsonElement report)
        {
            if (!report.TryGetProperty("renamedPaths", out var renamed) || renamed.ValueKind != JsonValueKind.Array)
                return;

            foreach (var item in renamed.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                if (item.TryGetProperty("old", out var oldPath))
                    EnsureReportPrefabNode(nodeMap, oldPath.GetString());
                if (item.TryGetProperty("new", out var newPath))
                    EnsureReportPrefabNode(nodeMap, newPath.GetString());
            }
        }

        private static void ApplyReportNodes(Dictionary<string, StructuredDiffNode> nodeMap, JsonElement report, string property, StructuredDiffChangeKind change)
        {
            if (!report.TryGetProperty(property, out var nodes) || nodes.ValueKind != JsonValueKind.Array)
                return;

            foreach (var item in nodes.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("path", out var pathValue))
                    continue;

                var node = EnsureReportPrefabNode(nodeMap, pathValue.GetString());
                if (node == null)
                    continue;
                node.Change = change;
                if (!item.TryGetProperty("props", out var props) || props.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var prop in props.EnumerateArray())
                {
                    if (TryReadReportPair(prop, out var key, out var value))
                    {
                        if (IsInternalPrefabReportProperty(key))
                            continue;

                        node.Properties.Add(new StructuredPropertyChange()
                        {
                            Key = key,
                            OldValue = change == StructuredDiffChangeKind.Deleted ? value : string.Empty,
                            NewValue = change == StructuredDiffChangeKind.Added ? value : string.Empty,
                            Change = change,
                        });
                    }
                }
            }
        }

        private static void ApplyModifiedReportNodes(Dictionary<string, StructuredDiffNode> nodeMap, JsonElement report)
        {
            if (!report.TryGetProperty("modified", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
                return;

            foreach (var item in nodes.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("path", out var pathValue))
                    continue;

                var node = EnsureReportPrefabNode(nodeMap, pathValue.GetString());
                if (node == null)
                    continue;
                node.Change = StructuredDiffChangeKind.Modified;
                AddReportPropertyList(node, item, "added", StructuredDiffChangeKind.Added);
                AddReportPropertyList(node, item, "removed", StructuredDiffChangeKind.Deleted);
                AddChangedReportPropertyList(node, item);
            }
        }

        private static void AddReportPropertyList(StructuredDiffNode node, JsonElement item, string property, StructuredDiffChangeKind change)
        {
            if (!item.TryGetProperty(property, out var props) || props.ValueKind != JsonValueKind.Array)
                return;

            foreach (var prop in props.EnumerateArray())
            {
                if (TryReadReportPair(prop, out var key, out var value) && !IsInternalPrefabReportProperty(key))
                {
                    node.Properties.Add(new StructuredPropertyChange()
                    {
                        Key = key,
                        OldValue = change == StructuredDiffChangeKind.Deleted ? value : string.Empty,
                        NewValue = change == StructuredDiffChangeKind.Added ? value : string.Empty,
                        Change = change,
                    });
                }
            }
        }

        private static void AddChangedReportPropertyList(StructuredDiffNode node, JsonElement item)
        {
            if (!item.TryGetProperty("changed", out var props) || props.ValueKind != JsonValueKind.Array)
                return;

            foreach (var prop in props.EnumerateArray())
            {
                if (prop.ValueKind != JsonValueKind.Object || !prop.TryGetProperty("key", out var keyValue))
                    continue;

                var key = keyValue.GetString() ?? string.Empty;
                if (IsInternalPrefabReportProperty(key))
                    continue;

                node.Properties.Add(new StructuredPropertyChange()
                {
                    Key = key,
                    OldValue = prop.TryGetProperty("old", out var oldValue) ? JsonReportValue(oldValue) : string.Empty,
                    NewValue = prop.TryGetProperty("new", out var newValue) ? JsonReportValue(newValue) : string.Empty,
                    Change = StructuredDiffChangeKind.Modified,
                });
            }
        }

        private static bool TryReadReportPair(JsonElement prop, out string key, out string value)
        {
            key = string.Empty;
            value = string.Empty;
            if (prop.ValueKind != JsonValueKind.Object || !prop.TryGetProperty("key", out var keyValue))
                return false;

            key = keyValue.GetString() ?? string.Empty;
            value = prop.TryGetProperty("value", out var valueElement) ? JsonReportValue(valueElement) : string.Empty;
            return key.Length > 0;
        }

        private static string JsonReportValue(JsonElement value)
        {
            return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();
        }

        private static StructuredDiffNode EnsureReportPrefabNode(Dictionary<string, StructuredDiffNode> nodeMap, string path)
        {
            return string.IsNullOrWhiteSpace(path) ? null : EnsurePrefabNode(nodeMap, path);
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

        private static bool IsInternalPrefabReportProperty(string key)
        {
            return key is "__flags__" or "__id__" or "__parent_id__";
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

        private static void FlattenNode(List<StructuredDiffNode> rows, StructuredDiffNode node, int depth)
        {
            node.Depth = depth;
            rows.Add(node);
            foreach (var child in node.Children)
                FlattenNode(rows, child, depth + 1);
        }

    }
}
