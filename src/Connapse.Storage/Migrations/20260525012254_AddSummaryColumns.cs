using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddSummaryColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "summary",
                table: "documents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "summary_content_hash",
                table: "documents",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "summary_generated_at",
                table: "documents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "summary",
                table: "containers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "summary_doc_set_hash",
                table: "containers",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "summary_generated_at",
                table: "containers",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "summary",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "summary_content_hash",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "summary_generated_at",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "summary",
                table: "containers");

            migrationBuilder.DropColumn(
                name: "summary_doc_set_hash",
                table: "containers");

            migrationBuilder.DropColumn(
                name: "summary_generated_at",
                table: "containers");
        }
    }
}
