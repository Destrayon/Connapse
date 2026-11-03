using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddContainerKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every container before CON-227 holds documents. The default stays on the column, so a row written
            // without a kind (raw SQL, an older build during a rolling start) reads as documents too.
            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "containers",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "documents");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "kind",
                table: "containers");
        }
    }
}
