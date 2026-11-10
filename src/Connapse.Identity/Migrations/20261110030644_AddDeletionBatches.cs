using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddDeletionBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "deletion_batches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    decided_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    decided_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decision_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deletion_batches", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "deletion_batch_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    container_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    label = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    outcome = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    finished_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deletion_batch_items", x => x.id);
                    table.ForeignKey(
                        name: "FK_deletion_batch_items_deletion_batches_batch_id",
                        column: x => x.batch_id,
                        principalTable: "deletion_batches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_deletion_batch_items_batch_id_position",
                table: "deletion_batch_items",
                columns: new[] { "batch_id", "position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_deletion_batches_owner_user_id_status",
                table: "deletion_batches",
                columns: new[] { "owner_user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_deletion_batches_token_key",
                table: "deletion_batches",
                column: "token_key");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "deletion_batch_items");

            migrationBuilder.DropTable(
                name: "deletion_batches");
        }
    }
}
