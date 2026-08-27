using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentResourceUri : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "resource_uri",
                table: "documents",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "idx_documents_resource_uri",
                table: "documents",
                column: "resource_uri")
                .Annotation("Npgsql:IndexOperators", new[] { "text_pattern_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_documents_resource_uri",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "resource_uri",
                table: "documents");
        }
    }
}
