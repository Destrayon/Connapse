using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <inheritdoc />
    public partial class RemoveSummaries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "summary",
                table: "sources");

            migrationBuilder.DropColumn(
                name: "summary_doc_set_hash",
                table: "sources");

            migrationBuilder.DropColumn(
                name: "summary_generated_at",
                table: "sources");

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
                name: "summary_status",
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

            // Summary settings, global and per container or source. Nothing reads them any more.
            migrationBuilder.Sql("DELETE FROM settings WHERE lower(category) = 'summary';");
            migrationBuilder.Sql(
                "UPDATE containers SET settings_overrides = settings_overrides - 'Summary' - 'summary' " +
                "WHERE settings_overrides ?| array['Summary', 'summary'];");
            migrationBuilder.Sql(
                "UPDATE sources SET settings_overrides = settings_overrides - 'Summary' - 'summary' " +
                "WHERE settings_overrides ?| array['Summary', 'summary'];");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "summary",
                table: "sources",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "summary_doc_set_hash",
                table: "sources",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "summary_generated_at",
                table: "sources",
                type: "timestamp with time zone",
                nullable: true);

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
                name: "summary_status",
                table: "documents",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "NotNeeded");

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
    }
}
