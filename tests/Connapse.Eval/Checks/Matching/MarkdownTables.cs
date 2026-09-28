namespace Connapse.Eval.Checks.Matching;

public readonly record struct CellPos(int Row, int Col);

/// <summary>
/// A table as a graph of cells: text per cell, heading cells, and the neighbouring cells in each
/// direction (a set, because spans can put several cells beside one).
/// </summary>
public sealed record TableData(
    IReadOnlyDictionary<CellPos, string> CellText,
    IReadOnlySet<CellPos> HeadingCells,
    bool IsRectangular,
    IReadOnlyDictionary<CellPos, IReadOnlySet<CellPos>> Up,
    IReadOnlyDictionary<CellPos, IReadOnlySet<CellPos>> Down,
    IReadOnlyDictionary<CellPos, IReadOnlySet<CellPos>> Left,
    IReadOnlyDictionary<CellPos, IReadOnlySet<CellPos>> Right)
{
    public IReadOnlySet<CellPos> TopHeadings(CellPos start) => WalkHeadings(start, Up);

    public IReadOnlySet<CellPos> LeftHeadings(CellPos start) => WalkHeadings(start, Left);

    // _walk_heading_relations: heading cells reachable from start, else the cells where the walk ends.
    private HashSet<CellPos> WalkHeadings(CellPos start, IReadOnlyDictionary<CellPos, IReadOnlySet<CellPos>> relation)
    {
        HashSet<CellPos> headings = [];
        HashSet<CellPos> ends = [];
        HashSet<CellPos> toVisit = [start];
        while (toVisit.Count > 0)
        {
            CellPos current = toVisit.First();
            toVisit.Remove(current);
            if (HeadingCells.Contains(current))
                headings.Add(current);
            if (!relation.TryGetValue(current, out IReadOnlySet<CellPos>? next) || next.Count == 0)
                ends.Add(current);
            else
                toVisit.UnionWith(next);
        }
        headings.Remove(start);
        ends.Remove(start);
        return headings.Count > 0 ? headings : ends;
    }
}

/// <summary>
/// Markdown table parsing, ported from olmocr <c>olmocr/bench/table_parsing.py</c>
/// (<c>parse_markdown_tables</c>, <c>_process_table_lines</c>, <c>_build_table_data_from_specs</c>;
/// commit f7cfe4c2; Apache-2.0, see THIRD_PARTY_NOTICES.md). <c>parse_html_tables</c> is not ported:
/// it relies on BeautifulSoup, and Connapse chunk text carries no HTML markup. Verified against the
/// reference in OlmOcrMatchingTests.
/// </summary>
public static class MarkdownTables
{
    public static IReadOnlyList<TableData> Parse(string mdContent)
    {
        string[] lines = PyText.Strip(mdContent).Split('\n');
        List<TableData> tables = [];
        List<string> current = [];
        bool inTable = false;

        foreach (string line in lines)
        {
            if (line.Contains('|'))
            {
                if (!inTable)
                {
                    inTable = true;
                    current = [line];
                }
                else
                {
                    current.Add(line);
                }
            }
            else if (inTable)
            {
                AddTable(current, tables);
                inTable = false;
            }
        }
        if (inTable)
            AddTable(current, tables);
        return tables;
    }

    private static void AddTable(List<string> tableLines, List<TableData> tables)
    {
        if (tableLines.Count < 2)
            return;
        List<List<string>> rows = ProcessTableLines(tableLines);
        if (rows.Count == 0)
            return;
        List<List<CellSpec>> specs = rows
            .Select((row, r) => row.Select((cell, c) => new CellSpec(cell, 1, 1, r == 0 || c == 0)).ToList())
            .ToList();
        TableData? table = Build(specs);
        if (table is not null)
            tables.Add(table);
    }

    private static List<List<string>> ProcessTableLines(List<string> tableLines)
    {
        int? separator = null;
        for (int i = 0; i < tableLines.Count; i++)
        {
            string withoutPipes = PyText.Strip(tableLines[i].Replace("|", ""));
            if (withoutPipes.Length > 0 && withoutPipes.All(c => c is '-' or ' ' or ':'))
            {
                separator = i;
                break;
            }
        }

        List<List<string>> rows = [];
        for (int i = 0; i < tableLines.Count; i++)
        {
            if (i == separator)
                continue;
            string line = tableLines[i];
            if (PyText.Strip(line).Length > 0 && line.All(c => c is '-' or ' ' or ':' or '|'))
                continue;

            List<string> cells = line.Split('|').Select(PyText.Strip).ToList();
            if (cells.Count > 0 && cells[0].Length == 0)
                cells.RemoveAt(0);
            if (cells.Count > 0 && cells[^1].Length == 0)
                cells.RemoveAt(cells.Count - 1);
            if (cells.Count > 0)
                rows.Add(cells);
        }
        return rows;
    }

    internal sealed record CellSpec(string Text, int RowSpan, int ColSpan, bool IsHeading);

    private sealed record CellMeta(int Row, int Col, int RowSpan, int ColSpan);

    // _build_table_data_from_specs.
    internal static TableData? Build(List<List<CellSpec>> rowSpecs)
    {
        if (rowSpecs.Count == 0)
            return null;

        Dictionary<CellPos, string> cellText = [];
        HashSet<CellPos> headings = [];
        Dictionary<CellPos, CellMeta> meta = [];
        List<List<CellPos?>> occupancy = [];
        List<(CellPos Cell, int Remaining)?> activeRowSpans = [];

        for (int rowIdx = 0; rowIdx < rowSpecs.Count; rowIdx++)
        {
            List<CellSpec> cells = rowSpecs[rowIdx];
            List<CellPos?> rowEntries = [];
            int colIndex = 0;
            int specIdx = 0;

            while (specIdx < cells.Count || colIndex < activeRowSpans.Count)
            {
                if (colIndex < activeRowSpans.Count && activeRowSpans[colIndex] is { } active)
                {
                    rowEntries.Add(active.Cell);
                    int remaining = active.Remaining - 1;
                    activeRowSpans[colIndex] = remaining > 0 ? (active.Cell, remaining) : null;
                    colIndex++;
                    continue;
                }

                if (specIdx >= cells.Count)
                {
                    if (colIndex < activeRowSpans.Count)
                    {
                        rowEntries.Add(null);
                        colIndex++;
                        continue;
                    }
                    break;
                }

                CellSpec spec = cells[specIdx++];
                int rowSpan = Math.Max(1, spec.RowSpan);
                int colSpan = Math.Max(1, spec.ColSpan);

                CellPos cellId = new(rowIdx, colIndex);
                cellText[cellId] = spec.Text;
                if (spec.IsHeading)
                    headings.Add(cellId);
                meta[cellId] = new CellMeta(rowIdx, colIndex, rowSpan, colSpan);

                int required = colIndex + colSpan;
                while (activeRowSpans.Count < required)
                    activeRowSpans.Add(null);

                for (int offset = 0; offset < colSpan; offset++)
                {
                    rowEntries.Add(cellId);
                    activeRowSpans[colIndex + offset] = rowSpan > 1 ? (cellId, rowSpan - 1) : null;
                }
                colIndex += colSpan;
            }

            occupancy.Add(rowEntries);
        }

        while (activeRowSpans.Any(e => e is not null))
        {
            List<CellPos?> rowEntries = [];
            for (int col = 0; col < activeRowSpans.Count; col++)
            {
                if (activeRowSpans[col] is not { } active)
                {
                    rowEntries.Add(null);
                    continue;
                }
                rowEntries.Add(active.Cell);
                int remaining = active.Remaining - 1;
                activeRowSpans[col] = remaining > 0 ? (active.Cell, remaining) : null;
            }
            occupancy.Add(rowEntries);
        }

        if (cellText.Count == 0)
            return null;

        int width = -1;
        foreach (List<CellPos?> row in occupancy)
            for (int i = 0; i < row.Count; i++)
                if (row[i] is not null)
                    width = Math.Max(width, i);
        if (width < 0)
            return null;
        width++;
        foreach (List<CellPos?> row in occupancy)
        {
            while (row.Count < width)
                row.Add(null);
            if (row.Count > width)
                row.RemoveRange(width, row.Count - width);
        }
        int height = occupancy.Count;

        Dictionary<CellPos, HashSet<CellPos>> up = [], down = [], left = [], right = [];
        foreach (CellPos cell in cellText.Keys)
        {
            up[cell] = [];
            down[cell] = [];
            left[cell] = [];
            right[cell] = [];
        }

        foreach ((CellPos cellId, CellMeta m) in meta)
        {
            int rowEnd = m.Row + m.RowSpan - 1;
            int colEnd = m.Col + m.ColSpan - 1;

            for (int row = m.Row; row <= rowEnd; row++)
                for (int col = colEnd + 1; col < width; col++)
                    if (occupancy[row][col] is { } n && n != cellId)
                    {
                        right[cellId].Add(n);
                        break;
                    }

            for (int row = m.Row; row <= rowEnd; row++)
                for (int col = m.Col - 1; col >= 0; col--)
                    if (occupancy[row][col] is { } n && n != cellId)
                    {
                        left[cellId].Add(n);
                        break;
                    }

            for (int col = m.Col; col <= colEnd; col++)
                for (int row = rowEnd + 1; row < height; row++)
                {
                    if (col >= occupancy[row].Count)
                        continue;
                    if (occupancy[row][col] is { } n && n != cellId)
                    {
                        down[cellId].Add(n);
                        break;
                    }
                }

            for (int col = m.Col; col <= colEnd; col++)
                for (int row = m.Row - 1; row >= 0; row--)
                    if (occupancy[row][col] is { } n && n != cellId)
                    {
                        up[cellId].Add(n);
                        break;
                    }
        }

        return new TableData(
            cellText,
            headings,
            !occupancy.Any(row => row.Any(x => x is null)),
            Freeze(up), Freeze(down), Freeze(left), Freeze(right));
    }

    private static IReadOnlyDictionary<CellPos, IReadOnlySet<CellPos>> Freeze(Dictionary<CellPos, HashSet<CellPos>> relation) =>
        relation.ToDictionary(p => p.Key, p => (IReadOnlySet<CellPos>)p.Value);
}
