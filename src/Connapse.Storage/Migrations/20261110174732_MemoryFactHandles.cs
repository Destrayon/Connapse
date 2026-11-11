using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <inheritdoc />
    public partial class MemoryFactHandles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "memory_fact_handles",
                columns: table => new
                {
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    handle = table.Column<int>(type: "integer", nullable: false),
                    fact_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memory_fact_handles", x => new { x.owner_id, x.handle });
                });

            migrationBuilder.CreateIndex(
                name: "ix_memory_fact_handles_fact",
                table: "memory_fact_handles",
                columns: new[] { "owner_id", "fact_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "memory_fact_handles");
        }
    }
}
