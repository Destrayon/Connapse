using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <inheritdoc />
    public partial class RemoveEphemeralConnector : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Delete any existing InMemory containers (connector_type = 2) and their data
            migrationBuilder.Sql("DELETE FROM documents WHERE container_id IN (SELECT id FROM containers WHERE connector_type = 2);");
            migrationBuilder.Sql("DELETE FROM folders WHERE container_id IN (SELECT id FROM containers WHERE connector_type = 2);");
            migrationBuilder.Sql("DELETE FROM containers WHERE connector_type = 2;");

            migrationBuilder.DropColumn(
                name: "is_ephemeral",
                table: "containers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_ephemeral",
                table: "containers",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
