using Connapse.Core;
using Connapse.Web.Mcp;
using FluentAssertions;

namespace Connapse.Web.Tests.Mcp;

/// <summary>
/// CON-312: a search result prints a table's preview under its Tables line, ending with how to query the whole table,
/// and prints nothing for a table without one.
/// </summary>
[Trait("Category", "Unit")]
public class McpTablePreviewTests
{
    private static readonly Guid Id = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void TablePreviews_TableWithPreview_PrintsColumnsRowsAndHowToQueryIt()
    {
        var table = new PassageTable(Id, Guid.NewGuid(), "Revenue", 3, 1,
            Preview: new TablePreview(["Region", "Revenue"], 2, [["East", "10"], ["West", "7"]], 40));

        string text = McpTools.TablePreviews([table]);

        text.Should().Be(
            "Table preview \"Revenue\" (40 rows; sample of 2): Region | Revenue (+2 more columns)\n"
            + "  East | 10\n  West | 7\n"
            + $"Preview only. Query the full table with table_read or table_aggregate using \"table\": \"{Id}\". "
            + TableQueryResult.UntrustedDataNotice + "\n");
    }

    [Fact]
    public void TablePreviews_NoPreview_PrintsNothing()
    {
        McpTools.TablePreviews([new PassageTable(Id, Guid.NewGuid(), "Revenue", null, 1)]).Should().BeEmpty();
        McpTools.TablePreviews(null).Should().BeEmpty();
    }
}
