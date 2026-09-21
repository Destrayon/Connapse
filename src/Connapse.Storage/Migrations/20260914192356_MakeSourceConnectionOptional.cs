using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <inheritdoc />
    public partial class MakeSourceConnectionOptional : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "connection_id",
                table: "sources",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<int>(
                name: "provider",
                table: "sources",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_sources_connection_xor_provider",
                table: "sources",
                sql: "(connection_id IS NULL) <> (provider IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rolling back re-requires connection_id. A provider-owned source
            // (connection_id NULL) has no connection to restore, and we never delete or
            // silently reassign one. Fail early with an actionable message — before the
            // provider column is dropped, while those rows can still be identified — so the
            // operator resolves them first, rather than hitting a bare NOT NULL violation.
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM sources WHERE connection_id IS NULL) THEN
        RAISE EXCEPTION 'Cannot roll back MakeSourceConnectionOptional: % provider-owned source(s) have connection_id NULL. Reassign or remove them before rolling back.',
            (SELECT count(*) FROM sources WHERE connection_id IS NULL);
    END IF;
END $$;");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sources_connection_xor_provider",
                table: "sources");

            migrationBuilder.DropColumn(
                name: "provider",
                table: "sources");

            migrationBuilder.AlterColumn<Guid>(
                name: "connection_id",
                table: "sources",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
