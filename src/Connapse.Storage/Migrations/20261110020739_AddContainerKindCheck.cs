using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <summary>
    /// Adds the tables container kind (CON-300). The kind column is already text, so the value needs no column change;
    /// the constraint keeps it to the kinds this build knows, so no row can hold a kind that reads back as another.
    /// </summary>
    public partial class AddContainerKindCheck : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "ck_containers_kind",
                table: "containers",
                sql: "kind IN ('documents', 'memory', 'tables')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_containers_kind",
                table: "containers");
        }
    }
}
