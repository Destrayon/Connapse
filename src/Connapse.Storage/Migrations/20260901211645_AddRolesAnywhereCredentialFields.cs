using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddRolesAnywhereCredentialFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "certificate_pem",
                table: "provider_credentials",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "private_key_protected",
                table: "provider_credentials",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "profile_arn",
                table: "provider_credentials",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "region",
                table: "provider_credentials",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "role_arn",
                table: "provider_credentials",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "trust_anchor_arn",
                table: "provider_credentials",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "certificate_pem",
                table: "provider_credentials");

            migrationBuilder.DropColumn(
                name: "private_key_protected",
                table: "provider_credentials");

            migrationBuilder.DropColumn(
                name: "profile_arn",
                table: "provider_credentials");

            migrationBuilder.DropColumn(
                name: "region",
                table: "provider_credentials");

            migrationBuilder.DropColumn(
                name: "role_arn",
                table: "provider_credentials");

            migrationBuilder.DropColumn(
                name: "trust_anchor_arn",
                table: "provider_credentials");
        }
    }
}
