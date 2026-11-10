using System.Text;
using System.Text.RegularExpressions;
using Connapse.Core;

namespace Connapse.Storage.Tables;

/// <summary>
/// Row history for synced tables (CON-306; docs/research/con-306-table-history-2026-11-10.md). A table whose file has
/// a key column keeps every version of each row with the half-open span Connapse saw it in,
/// <c>[_seen_from, _seen_until)</c>, the until value <c>'infinity'</c> while the version is current. A re-sync closes
/// the current versions whose key went away or whose values changed and inserts the new ones, the way a dbt snapshot's
/// check strategy does. The times are when Connapse saw each version, not when it changed at the source.
/// <para>
/// CSV files declare no key, so one is inferred conservatively: a text or whole-number column whose header names an
/// identifier (<c>id</c>, <c>order_id</c>, <c>Order ID</c>, <c>CustomerID</c>, <c>sku</c>, …), whose header no other
/// column shares, and whose values are unique and non-empty in the load. It is re-checked on every load.
/// </para>
/// </summary>
internal static partial class TableHistory
{
    internal const string SeenFrom = "_seen_from";
    internal const string SeenUntil = "_seen_until";

    /// <summary>The history columns a table with history adds to its own.</summary>
    internal const int ExtraColumns = 2;

    /// <summary>A header that is, or ends in, an identifier word: <c>id</c>, <c>order_id</c>, <c>Order ID</c>, <c>sku</c>.</summary>
    [GeneratedRegex(@"(?:^|[\s_.\-])(?:id|key|code|sku|uuid|guid)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyWord();

    /// <summary>A camel-case identifier suffix: <c>orderId</c>, <c>CustomerID</c>, but not <c>paid</c> or <c>ANDROID</c>.</summary>
    [GeneratedRegex("[a-z]I[Dd]$", RegexOptions.CultureInvariant)]
    private static partial Regex CamelId();

    internal static bool IsKeyName(string name)
    {
        string trimmed = name.Trim();
        return trimmed.Length > 0 && (KeyWord().IsMatch(trimmed) || CamelId().IsMatch(trimmed));
    }

    /// <summary>
    /// The columns that could be the table's key, in column order: identifier headers of text or whole-number columns
    /// that no other column shares. Whether their values are unique is checked once the rows are in.
    /// </summary>
    internal static IReadOnlyList<int> KeyCandidates(IReadOnlyList<TableColumn> columns)
    {
        if (columns.Count + ExtraColumns > TableDdl.MaxColumns)
            return [];
        return columns
            .Select((c, i) => (c, i))
            .Where(x => x.c.Type is TableColumnType.Text or TableColumnType.WholeNumber
                && IsKeyName(x.c.Name)
                && columns.Count(o => string.Equals(o.Name, x.c.Name, StringComparison.Ordinal)) == 1)
            .Select(x => x.i)
            .ToList();
    }

    /// <summary>One boolean per candidate: whether its values in <paramref name="table"/> are unique and none is empty.</summary>
    internal static string ValidKeysSql(string table, IReadOnlyList<int> candidates) =>
        "SELECT " + string.Join(", ", candidates.Select(i =>
        {
            string c = Column(i);
            return $"pg_catalog.count(*) = pg_catalog.count(DISTINCT {c}) AND pg_catalog.count({c}) = pg_catalog.count(*)";
        }))
        + " FROM " + table;

    /// <summary>
    /// Closes, at <c>$1</c>, each current version of <paramref name="existing"/> whose key isn't in
    /// <paramref name="incoming"/> or whose values differ there. Values compare null-safely, so empty to empty is no
    /// change and empty to a value is one.
    /// </summary>
    internal static string CloseSql(string existing, string incoming, int key, int columnCount) =>
        $"UPDATE {existing} AS e SET {Quote(SeenUntil)} = $1 WHERE e.{Quote(SeenUntil)} = 'infinity' "
        + $"AND NOT EXISTS (SELECT 1 FROM {incoming} AS i WHERE i.{Column(key)} = e.{Column(key)} "
        + $"AND {Row("i", columnCount)} IS NOT DISTINCT FROM {Row("e", columnCount)})";

    /// <summary>
    /// Inserts, seen from <c>$1</c>, each row of <paramref name="incoming"/> whose key has no current version left in
    /// <paramref name="existing"/>: the new keys and, after <see cref="CloseSql"/>, the changed ones.
    /// </summary>
    internal static string InsertSql(string existing, string incoming, int key, int columnCount)
    {
        string columns = string.Join(", ", Enumerable.Range(0, columnCount).Select(Column));
        return $"INSERT INTO {existing} ({columns}, {Quote(SeenFrom)}) "
            + $"SELECT {string.Join(", ", Enumerable.Range(0, columnCount).Select(i => "i." + Column(i)))}, $1 FROM {incoming} AS i "
            + $"WHERE NOT EXISTS (SELECT 1 FROM {existing} AS e WHERE e.{Quote(SeenUntil)} = 'infinity' AND e.{Column(key)} = i.{Column(key)})";
    }

    /// <summary>One current version per key, enforced; it also serves the re-sync's lookups by key.</summary>
    internal static string CurrentKeyIndexSql(Guid containerId, string tableName, int key) =>
        $"CREATE UNIQUE INDEX {TableDdl.Quote(tableName + "_current")} ON {TableDdl.QualifiedName(containerId, tableName)} "
        + $"({Column(key)}) WHERE {Quote(SeenUntil)} = 'infinity'";

    internal static string Row(string alias, int columnCount)
    {
        var row = new StringBuilder("ROW(");
        row.AppendJoin(", ", Enumerable.Range(0, columnCount).Select(i => alias + "." + Column(i)));
        return row.Append(')').ToString();
    }

    private static string Column(int index) => TableDdl.Quote(TableDdl.ColumnName(index));

    private static string Quote(string name) => TableDdl.Quote(name);
}
