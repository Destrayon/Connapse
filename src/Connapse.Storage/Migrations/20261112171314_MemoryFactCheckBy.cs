using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <inheritdoc />
    public partial class MemoryFactCheckBy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "check_after_days",
                table: "memory_facts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "check_by",
                table: "memory_facts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "confirmed_at",
                table: "memory_facts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "memory_check_defaults",
                columns: table => new
                {
                    container_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attribute = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    days = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memory_check_defaults", x => new { x.container_id, x.attribute });
                    table.CheckConstraint("ck_memory_check_defaults_days", "days BETWEEN 1 AND 3650");
                    table.ForeignKey(
                        name: "FK_memory_check_defaults_containers_container_id",
                        column: x => x.container_id,
                        principalTable: "containers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "memory_check_defaults");

            migrationBuilder.DropColumn(
                name: "check_after_days",
                table: "memory_facts");

            migrationBuilder.DropColumn(
                name: "check_by",
                table: "memory_facts");

            migrationBuilder.DropColumn(
                name: "confirmed_at",
                table: "memory_facts");
        }
    }
}
