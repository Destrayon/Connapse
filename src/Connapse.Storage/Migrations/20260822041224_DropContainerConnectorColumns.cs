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
        /// such a row keeps the container itself but loses its documents, its search index and
        /// the record of which remote system it mirrored, and must be re-registered as a
        /// connection plus a source (CON-226).
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // An external container's documents were indexed from a remote whose permissions
            // (S3 and Azure grants, filesystem ACLs) decided who could read them. Once the
            // connector type is gone those rows look exactly like uploads: no source, no
            // resource_uri, so every reader of the container would be let in. This is the last
            // point at which they can be told apart, so they go here. The remote still holds
            // the originals; re-registering it as a connection plus a source indexes them again
            // under its permissions. Chunks, vectors and batch entries cascade from documents.
            // Folders go too, because their names are the remote's paths.
            migrationBuilder.Sql(
                "DELETE FROM documents WHERE container_id IN (SELECT id FROM containers WHERE connector_type <> 0);");
            migrationBuilder.Sql(
                "DELETE FROM folders WHERE container_id IN (SELECT id FROM containers WHERE connector_type <> 0);");

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
