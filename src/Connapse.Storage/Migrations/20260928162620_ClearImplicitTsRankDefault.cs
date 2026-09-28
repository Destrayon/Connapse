using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <summary>
    /// Drops a saved keyword ranker of "TsRank" so the new BM25 default applies (#550). No release
    /// shipped the setting and the Search tab had no control for it, so a stored "TsRank" is the old
    /// default carried along by saving other search settings, never a choice.
    /// </summary>
    public partial class ClearImplicitTsRankDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE settings
                SET values = values - 'keywordRanker', updated_at = now()
                WHERE lower(category) = 'search'
                  AND lower(values ->> 'keywordRanker') = 'tsrank';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversed: the dropped value was the old default, which the missing key already means.
        }
    }
}
