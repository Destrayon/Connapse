using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "requires_approval",
                table: "personal_access_tokens",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "requires_approval",
                table: "agent_api_keys",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "mcp_approvals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tool_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    arguments_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    arguments_summary = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    decided_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    decided_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mcp_approvals", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_mcp_approvals_owner_user_id_status",
                table: "mcp_approvals",
                columns: new[] { "owner_user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_mcp_approvals_token_key_tool_name_arguments_hash",
                table: "mcp_approvals",
                columns: new[] { "token_key", "tool_name", "arguments_hash" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mcp_approvals");

            migrationBuilder.DropColumn(
                name: "requires_approval",
                table: "personal_access_tokens");

            migrationBuilder.DropColumn(
                name: "requires_approval",
                table: "agent_api_keys");
        }
    }
}
