using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <inheritdoc />
    public partial class DropContainerConnectorColumns : Migration
    {
        /// <inheritdoc />
        /// <remarks>
        /// Unconditional, and deliberately so. The #350 backfill that turned external
        /// containers into connection + source pairs never shipped in a release — it landed in
        /// this same milestone — and migrations run before hosted services start, so it could
        /// not have run before this point even if it had. Anything still carrying an external
        /// connector type here therefore predates v0.4.0 entirely, and v0.4.0 is a clean break:
        /// such a row keeps its documents and its search index, but loses the record of which
        /// remote system it mirrored, and must be re-registered as a connection plus a source.
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "connector_config",
                table: "containers");

            migrationBuilder.DropColumn(
                name: "connector_type",
                table: "containers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<JsonDocument>(
                name: "connector_config",
                table: "containers",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "connector_type",
                table: "containers",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }
    }
}
