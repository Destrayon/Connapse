using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddRefreshTokenScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "scope",
                table: "refresh_tokens",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "scope",
                table: "refresh_tokens");
        }
    }
}
