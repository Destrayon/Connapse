using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <summary>
    /// Drops the tables container kind (CON-319): a documents container holds any number of tables beside its files,
    /// so there is no tables-only kind. The kind never shipped; a container made as one on a pre-release build becomes
    /// a documents container and keeps its tables, since both kinds keep them in the same per-container schema.
    /// </summary>
    public partial class DropTablesContainerKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE containers SET kind = 'documents' WHERE kind = 'tables';");

            migrationBuilder.DropCheckConstraint(
                name: "ck_containers_kind",
                table: "containers");

            migrationBuilder.AddCheckConstraint(
                name: "ck_containers_kind",
                table: "containers",
                sql: "kind IN ('documents', 'memory')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_containers_kind",
                table: "containers");

            migrationBuilder.AddCheckConstraint(
                name: "ck_containers_kind",
                table: "containers",
                sql: "kind IN ('documents', 'memory', 'tables')");
        }
    }
}
