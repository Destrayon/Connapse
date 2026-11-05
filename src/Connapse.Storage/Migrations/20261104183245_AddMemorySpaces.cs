using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddMemorySpaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_containers_name",
                table: "containers");

            migrationBuilder.AddColumn<string>(
                name: "audience",
                table: "containers",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "team");

            migrationBuilder.AddColumn<Guid>(
                name: "owner_agent_id",
                table: "containers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "owner_user_id",
                table: "containers",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_containers_name",
                table: "containers",
                column: "name",
                unique: true,
                filter: "audience = 'team'");

            migrationBuilder.CreateIndex(
                name: "ix_containers_personal_name",
                table: "containers",
                columns: new[] { "owner_user_id", "owner_agent_id", "name" },
                unique: true,
                filter: "audience = 'personal'")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.AddCheckConstraint(
                name: "ck_containers_audience_owner",
                table: "containers",
                sql: "(audience = 'team' AND owner_user_id IS NULL AND owner_agent_id IS NULL) OR (audience = 'personal' AND kind = 'memory' AND (owner_user_id IS NULL) <> (owner_agent_id IS NULL))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_containers_name",
                table: "containers");

            migrationBuilder.DropIndex(
                name: "ix_containers_personal_name",
                table: "containers");

            migrationBuilder.DropCheckConstraint(
                name: "ck_containers_audience_owner",
                table: "containers");

            migrationBuilder.DropColumn(
                name: "audience",
                table: "containers");

            migrationBuilder.DropColumn(
                name: "owner_agent_id",
                table: "containers");

            migrationBuilder.DropColumn(
                name: "owner_user_id",
                table: "containers");

            migrationBuilder.CreateIndex(
                name: "ix_containers_name",
                table: "containers",
                column: "name",
                unique: true);
        }
    }
}
