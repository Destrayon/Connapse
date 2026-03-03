using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectorSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
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

            migrationBuilder.AddColumn<bool>(
                name: "is_ephemeral",
                table: "containers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "settings_overrides",
                table: "containers",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "connector_config",
                table: "containers");

            migrationBuilder.DropColumn(
                name: "connector_type",
                table: "containers");

            migrationBuilder.DropColumn(
                name: "is_ephemeral",
                table: "containers");

            migrationBuilder.DropColumn(
                name: "settings_overrides",
                table: "containers");
        }
    }
}
