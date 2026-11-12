using System.Security.Cryptography;
using System.Text;

namespace Connapse.Core;

/// <summary>
/// A table's search card (CON-310): the text a search for the right table matches against. It holds the table's name,
/// its description, and each visible column's header and description, headers split into words so <c>order_date</c>
/// and <c>orderDate</c> read as "order date". Hidden columns never go in, so a search can't reveal them.
/// </summary>
public sealed record TableCard(Guid TableId, string Text)
{
    /// <summary>The card of <paramref name="table"/>, from its visible columns only.</summary>
    public static TableCard For(TableInfo table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var text = new StringBuilder(Words(table.Name));
        if (!string.IsNullOrWhiteSpace(table.Description))
            text.Append('\n').Append(table.Description.Trim());
        IReadOnlyList<TableColumn> columns = table.VisibleColumns;
        if (columns.Count > 0)
        {
            text.Append("\nColumns: ");
            text.AppendJoin("; ", columns.Select(c => string.IsNullOrWhiteSpace(c.Description)
                ? Words(c.Name)
                : $"{Words(c.Name)} ({c.Description.Trim()})"));
        }
        return new TableCard(table.Id, text.ToString());
    }

    /// <summary>A hash of <see cref="Text"/>, so a stored vector is known to be stale once the card changes.</summary>
    public string Hash => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Text)));

    /// <summary>
    /// <paramref name="name"/> split into words at underscores, hyphens, dots, lower-to-upper case changes and
    /// letter-digit changes: <c>CustomerOrders_2024</c> becomes "Customer Orders 2024".
    /// </summary>
    public static string Words(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var words = new StringBuilder(name.Length + 8);
        char previous = ' ';
        foreach (char c in name)
        {
            char next = c is '_' or '-' or '.' ? ' ' : c;
            bool boundary = next != ' ' && previous != ' '
                && ((char.IsLower(previous) && char.IsUpper(next))
                    || (char.IsLetter(previous) && char.IsDigit(next))
                    || (char.IsDigit(previous) && char.IsLetter(next)));
            if (boundary)
                words.Append(' ');
            words.Append(next);
            previous = next;
        }
        return string.Join(' ', words.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
