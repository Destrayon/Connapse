using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentAccessKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "access_keys",
                table: "documents",
                type: "text[]",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "idx_documents_access_keys",
                table: "documents",
                column: "access_keys")
                .Annotation("Npgsql:IndexMethod", "gin");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_documents_access_keys",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "access_keys",
                table: "documents");
        }
    }
}
