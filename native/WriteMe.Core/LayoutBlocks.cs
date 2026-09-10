using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace WriteMe.Core;

public readonly record struct CellRange(int Top, int Left, int Bottom, int Right)
{
    public static CellRange Between(int row, int column, int otherRow, int otherColumn) =>
        new(Math.Min(row, otherRow), Math.Min(column, otherColumn), Math.Max(row, otherRow), Math.Max(column, otherColumn));
    public bool Contains(int row, int column) => row >= Top && row <= Bottom && column >= Left && column <= Right;
}

// Note: 表格/分栏的结构操作与区域输入共用历史，见 .agents/notes/implemented/feature/2026-09-11-native-tables-columns-and-editing.md
public static class LayoutBlocks
{
    public const int MaxRows = 100;
    public const int MaxColumns = 12;
    public const int MinColumnWidth = 80;
    public const int MaxColumnWidth = 1200;

    public static NoteNode Table(int rows = 3, int columns = 3, bool header = true)
    {
        if (rows is < 1 or > MaxRows || columns is < 1 or > MaxColumns) throw new ArgumentOutOfRangeException(nameof(rows), "表格支持 1–100 行、1–12 列");
        return new("table") { Content = Enumerable.Range(0, rows).Select(row => TableRow(columns, header && row == 0)).ToImmutableArray() };
    }

    internal static NoteNode Cell(bool header = false) => new(header ? "tableHeader" : "tableCell") { Content = [NoteNode.Paragraph()] };
    internal static NoteNode TableRow(int columns, bool header = false) => new("tableRow") { Content = Enumerable.Range(0, columns).Select(_ => Cell(header)).ToImmutableArray() };
    public static NoteNode Columns(int count = 2)
    {
        if (count is < 2 or > 3) throw new ArgumentOutOfRangeException(nameof(count));
        return new("columnList") { Content = Enumerable.Range(0, count).Select(_ => new NoteNode("column") { Content = [NoteNode.Paragraph()] }.WithAttr("width", 1)).ToImmutableArray() };
    }

    public static bool IsEditableTable(NoteNode? table)
    {
        if (table?.Type != "table" || table.Content.Length is < 1 or > MaxRows) return false;
        var count = table.Content[0].Content.Length;
        return count is >= 1 and <= MaxColumns && table.Content.All(row => row.Type == "tableRow" && row.Content.Length == count
            && row.Content.All(cell => cell.Type is "tableCell" or "tableHeader" && cell.Int("colspan", 1) == 1 && cell.Int("rowspan", 1) == 1));
    }
    public static bool IsEditableColumns(NoteNode? columns) => columns?.Type == "columnList" && columns.Content.Length is >= 2 and <= 3 && columns.Content.All(column => column.Type == "column");
    public static bool HasHeader(NoteNode table) => !table.Content.IsEmpty && table.Content[0].Content.All(cell => cell.Type == "tableHeader");
    public static ImmutableArray<int> ColumnWidths(NoteNode table) => !IsEditableTable(table) ? []
        : Enumerable.Range(0, table.Content[0].Content.Length).Select(column => table.Content.Select(row => CellWidth(row.Content[column])).FirstOrDefault(width => width > 0)).ToImmutableArray();
    private static int CellWidth(NoteNode cell) => cell.Attrs.TryGetValue("colwidth", out var value) && value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 1
        && value[0].ValueKind == JsonValueKind.Number && value[0].TryGetInt32(out var width) && width > 0 ? Math.Clamp(width, MinColumnWidth, MaxColumnWidth) : 0;
    public static NoteNode FirstText(NoteNode container) => NoteTree.Descendants(container).FirstOrDefault(node => node.IsTextBlock)
        ?? new DocumentProjection(NoteTree.Normalize(new("doc") { Content = container.Content })).Rows[0].Node;
    public static string CellText(NoteNode cell) => string.Join('\n', NoteTree.Descendants(cell).Where(node => node.IsTextBlock).Select(RichText.Plain)).Replace('\u2028', '\n');

    public static string CopyCells(NoteNode table, CellRange range)
    {
        if (!IsEditableTable(table) || !ValidRange(table, range)) return "";
        static string Quote(string value) => value.IndexOfAny(['\t', '\n', '\r', '"']) < 0 ? value : "\"" + value.Replace("\"", "\"\"") + "\"";
        return string.Join('\n', table.Content.Skip(range.Top).Take(range.Bottom - range.Top + 1)
            .Select(row => string.Join('\t', row.Content.Skip(range.Left).Take(range.Right - range.Left + 1).Select(cell => Quote(CellText(cell))))));
    }

    internal static bool ValidRange(NoteNode table, CellRange range) => range.Top >= 0 && range.Left >= 0 && range.Bottom >= range.Top && range.Right >= range.Left
        && range.Bottom < table.Content.Length && range.Right < table.Content[0].Content.Length;

    // Spreadsheet clipboard format: quotes escape embedded tabs/newlines; a final row terminator is not a new row.
    public static ImmutableArray<ImmutableArray<string>> ParseTsv(string text)
    {
        var rows = ImmutableArray.CreateBuilder<ImmutableArray<string>>();
        var cells = ImmutableArray.CreateBuilder<string>();
        var field = new StringBuilder();
        var quoted = false;
        var terminated = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            terminated = false;
            if (c == '"' && (quoted || field.Length == 0))
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (!quoted && c == '\t') { cells.Add(field.ToString()); field.Clear(); }
            else if (!quoted && c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                cells.Add(field.ToString()); field.Clear(); rows.Add(cells.ToImmutable()); cells.Clear(); terminated = true;
            }
            else field.Append(c);
            if (rows.Count > MaxRows || cells.Count >= MaxColumns) throw new ArgumentException("粘贴内容超过表格的 100 行或 12 列上限");
        }
        if (quoted) throw new ArgumentException("粘贴内容的引号未闭合");
        if (!terminated || rows.Count == 0) { cells.Add(field.ToString()); rows.Add(cells.ToImmutable()); }
        if (rows.Count > MaxRows) throw new ArgumentException("粘贴内容超过表格的 100 行上限");
        return rows.ToImmutable();
    }
}

public sealed partial class DocumentSession
{
    public bool InsertTable(int offset, int rows = 3, int columns = 3) => InsertLayout(offset, LayoutBlocks.Table(rows, columns));
    public bool InsertColumns(int offset, int count = 2) => InsertLayout(offset, LayoutBlocks.Columns(count));

    private bool InsertLayout(int offset, NoteNode block)
    {
        var row = Projection.At(offset);
        var first = LayoutBlocks.FirstText(block);
        if (row.Node.Id == row.Block.Id && row.Node.Type == "paragraph" && row.Text.Length == 0)
            Commit(NoteTree.Replace(Root, row.Node.Id, block), EditorSelection.At(first.Id));
        else InsertAfter(row, [block], first);
        return true;
    }

    public bool InsertLayoutCommand(int offset, int prefixLength, string kind, int count = 2)
    {
        var row = Projection.At(offset);
        if (row.IsAtomic || prefixLength < 0 || prefixLength > row.Text.Length || kind is not ("table" or "columnList")) return false;
        var block = kind == "table" ? LayoutBlocks.Table() : LayoutBlocks.Columns(count);
        // A slash command replaces its own empty paragraph; any following text and owning subtree survive.
        var remaining = RichText.Splice(row.Node, 0, prefixLength, "");
        var root = NoteTree.Update(Root, row.Node.Id, _ => remaining);
        if (row.Block.Id == row.Node.Id)
            root = NoteTree.Replace(root, row.Node.Id, RichText.Plain(remaining).Length == 0 ? [block] : [block, remaining]);
        else
            root = NoteTree.Update(root, row.Block.Id, owner => owner with { Content = owner.Content.Insert(1, block) });
        Commit(root, EditorSelection.At(LayoutBlocks.FirstText(block).Id));
        return true;
    }

    public bool InsertTableRow(Guid id, int index)
    {
        var table = NoteTree.Find(Root, id);
        if (!LayoutBlocks.IsEditableTable(table) || index < 0 || index > table!.Content.Length || table.Content.Length == LayoutBlocks.MaxRows) return false;
        var header = LayoutBlocks.HasHeader(table);
        var widths = LayoutBlocks.ColumnWidths(table);
        var inserted = LayoutBlocks.TableRow(table.Content[0].Content.Length);
        inserted = inserted with { Content = inserted.Content.Select((cell, column) => widths[column] > 0 ? cell.WithAttr("colwidth", new[] { widths[column] }) : cell).ToImmutableArray() };
        var rows = table.Content.Insert(index, inserted);
        if (header) rows = HeaderRows(rows, true);
        return CommitTable(table, table with { Content = rows }, index, 0);
    }

    public bool DeleteTableRow(Guid id, int index)
    {
        var table = NoteTree.Find(Root, id);
        if (!LayoutBlocks.IsEditableTable(table) || index < 0 || index >= table!.Content.Length) return false;
        if (table.Content.Length == 1) { DeleteBlock(id); return true; }
        var rows = table.Content.RemoveAt(index);
        if (index == 0 && LayoutBlocks.HasHeader(table)) rows = HeaderRows(rows, true);
        return CommitTable(table, table with { Content = rows }, Math.Min(index, rows.Length - 1), 0);
    }

    public bool InsertTableColumn(Guid id, int index)
    {
        var table = NoteTree.Find(Root, id);
        if (!LayoutBlocks.IsEditableTable(table) || index < 0 || index > table!.Content[0].Content.Length || table.Content[0].Content.Length == LayoutBlocks.MaxColumns) return false;
        var header = LayoutBlocks.HasHeader(table);
        var rows = table.Content.Select((row, r) => row with { Content = row.Content.Insert(index, LayoutBlocks.Cell(header && r == 0)) }).ToImmutableArray();
        return CommitTable(table, table with { Content = rows }, 0, index);
    }

    public bool DeleteTableColumn(Guid id, int index)
    {
        var table = NoteTree.Find(Root, id);
        if (!LayoutBlocks.IsEditableTable(table) || index < 0 || index >= table!.Content[0].Content.Length) return false;
        if (table.Content[0].Content.Length == 1) { DeleteBlock(id); return true; }
        var rows = table.Content.Select(row => row with { Content = row.Content.RemoveAt(index) }).ToImmutableArray();
        return CommitTable(table, table with { Content = rows }, 0, Math.Min(index, rows[0].Content.Length - 1));
    }

    public bool SetTableHeader(Guid id, bool header)
    {
        var table = NoteTree.Find(Root, id);
        if (!LayoutBlocks.IsEditableTable(table) || LayoutBlocks.HasHeader(table!) == header) return false;
        Commit(NoteTree.Update(Root, id, node => node with { Content = HeaderRows(node.Content, header) }));
        return true;
    }
    private static ImmutableArray<NoteNode> HeaderRows(ImmutableArray<NoteNode> rows, bool header) => rows.Select((row, index) => row with
    { Content = row.Content.Select(cell => cell with { Type = header && index == 0 ? "tableHeader" : "tableCell" }).ToImmutableArray() }).ToImmutableArray();

    public bool SetTableStriped(Guid id, bool striped)
    {
        var table = NoteTree.Find(Root, id);
        if (table?.Type != "table" || table.Bool("striped") == striped) return false;
        Commit(NoteTree.Update(Root, id, node => node.WithAttr("striped", striped)));
        return true;
    }

    public bool ClearTableCells(Guid id, CellRange range)
        => ReplaceTableRange(id, range, null);

    public bool SetTableColumnWidths(Guid id, params int[] widths)
    {
        var table = NoteTree.Find(Root, id);
        if (!LayoutBlocks.IsEditableTable(table)) return false;
        if (widths.Length != 0 && (widths.Length != table!.Content[0].Content.Length || widths.Any(width => width is < LayoutBlocks.MinColumnWidth or > LayoutBlocks.MaxColumnWidth)))
            throw new ArgumentException("列宽须为 80–1200 px，且每一列都有宽度", nameof(widths));
        var reset = widths.Length == 0;
        if (reset ? table!.Content.All(row => row.Content.All(cell => !cell.Attrs.ContainsKey("colwidth"))) : LayoutBlocks.ColumnWidths(table!).SequenceEqual(widths)) return false;
        var rows = table!.Content.Select(row => row with { Content = row.Content.Select((cell, column) => reset
            ? cell with { Attrs = cell.Attrs.Remove("colwidth") } : cell.WithAttr("colwidth", new[] { widths[column] })).ToImmutableArray() }).ToImmutableArray();
        Commit(NoteTree.Update(Root, id, _ => table with { Content = rows }));
        return true;
    }

    public bool ReplaceTableRange(Guid id, CellRange range, string? text)
    {
        var table = NoteTree.Find(Root, id);
        if (!LayoutBlocks.IsEditableTable(table) || !LayoutBlocks.ValidRange(table!, range)) return false;
        var changed = false;
        var rows = table!.Content.Select((row, r) => row with { Content = row.Content.Select((cell, c) =>
        {
            if (!range.Contains(r, c)) return cell;
            var value = r == range.Top && c == range.Left ? text ?? "" : "";
            if (value.Length == 0 && cell.Content.Length == 1 && cell.Content[0].Type == "paragraph" && cell.Content[0].Content.IsEmpty) return cell;
            changed = true;
            return cell with { Content = [NoteNode.Paragraph() with { Content = RichText.FromText(value.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\n', '\u2028'), []) }] };
        }).ToImmutableArray() }).ToImmutableArray();
        if (!changed) return false;
        var after = table with { Content = rows };
        Commit(NoteTree.Update(Root, id, _ => after), EditorSelection.At(LayoutBlocks.FirstText(after.Content[range.Top].Content[range.Left]).Id, text?.Length ?? 0));
        return true;
    }

    public bool PasteTableCells(Guid id, int row, int column, string text)
    {
        var table = NoteTree.Find(Root, id);
        if (!LayoutBlocks.IsEditableTable(table) || row < 0 || column < 0 || row >= table!.Content.Length || column >= table.Content[0].Content.Length) return false;
        var values = LayoutBlocks.ParseTsv(text);
        var width = Math.Max(table.Content[0].Content.Length, column + values.Max(line => line.Length));
        var height = Math.Max(table.Content.Length, row + values.Length);
        if (width > LayoutBlocks.MaxColumns || height > LayoutBlocks.MaxRows) throw new ArgumentException("粘贴后的表格超过 100 行或 12 列，原表格未改动");
        var header = LayoutBlocks.HasHeader(table);
        var rows = table.Content.ToBuilder();
        while (rows.Count < height) rows.Add(LayoutBlocks.TableRow(width));
        for (var r = 0; r < height; r++)
        {
            var cells = rows[r].Content.ToBuilder();
            while (cells.Count < width) cells.Add(LayoutBlocks.Cell(header && r == 0));
            if (r >= row && r < row + values.Length)
                for (var c = 0; c < values[r - row].Length; c++)
                    cells[column + c] = cells[column + c] with { Content = [NoteNode.Paragraph() with { Content = RichText.FromText(values[r - row][c].Replace("\r\n", "\n").Replace('\r', '\n').Replace('\n', '\u2028'), []) }] };
            rows[r] = rows[r] with { Content = cells.ToImmutable() };
        }
        return CommitTable(table, table with { Content = rows.ToImmutable() }, row, column);
    }

    private bool CommitTable(NoteNode before, NoteNode after, int row, int column)
    {
        Commit(NoteTree.Update(Root, before.Id, _ => after), EditorSelection.At(LayoutBlocks.FirstText(after.Content[row].Content[column]).Id));
        return true;
    }

    public bool SetColumns(Guid id, int count, params int[] weights)
    {
        var block = NoteTree.Find(Root, id);
        if (!LayoutBlocks.IsEditableColumns(block) || count is < 2 or > 3) return false;
        if (weights.Length != 0 && (weights.Length != count || weights.Any(weight => weight is < 1 or > 100))) throw new ArgumentException("分栏比例无效", nameof(weights));
        if (weights.Length == 0) weights = Enumerable.Repeat(1, count).ToArray();
        if (block!.Content.Length == count && block.Content.Select(column => column.Int("width", 1)).SequenceEqual(weights)) return false;
        var columns = block.Content.ToBuilder();
        if (columns.Count > count)
        {
            var remaining = columns.Skip(count).SelectMany(column => column.Content);
            columns[count - 1] = columns[count - 1] with { Content = columns[count - 1].Content.AddRange(remaining) };
            columns.RemoveRange(count, columns.Count - count);
        }
        while (columns.Count < count) columns.Add(new("column") { Content = [NoteNode.Paragraph()] });
        for (var i = 0; i < count; i++) columns[i] = columns[i].WithAttr("width", weights[i]);
        Commit(NoteTree.Update(Root, id, node => node with { Content = columns.ToImmutable() }));
        return true;
    }

    public bool UnwrapColumns(Guid id)
    {
        var block = NoteTree.Find(Root, id);
        if (!LayoutBlocks.IsEditableColumns(block)) return false;
        var children = block!.Content.SelectMany(column => column.Content).ToArray();
        Commit(NoteTree.Replace(Root, id, children));
        return true;
    }

    public bool DeleteColumn(Guid id, int index)
    {
        var block = NoteTree.Find(Root, id);
        if (!LayoutBlocks.IsEditableColumns(block) || index < 0 || index >= block!.Content.Length) return false;
        var columns = block.Content.RemoveAt(index);
        var root = columns.Length == 1 ? NoteTree.Replace(Root, id, columns[0].Content.ToArray())
            : NoteTree.Update(Root, id, node => node with { Content = columns });
        Commit(root, EditorSelection.At(LayoutBlocks.FirstText(columns[Math.Min(index, columns.Length - 1)]).Id));
        return true;
    }
}
