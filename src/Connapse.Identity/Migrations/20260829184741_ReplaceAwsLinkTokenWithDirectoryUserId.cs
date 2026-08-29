using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Identity.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceAwsLinkTokenWithDirectoryUserId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "protected_refresh_token",
                table: "user_aws_identity_links");

            migrationBuilder.AddColumn<string>(
                name: "directory_user_id",
                table: "user_aws_identity_links",
                type: "character varying(47)",
                maxLength: 47,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "directory_user_id",
                table: "user_aws_identity_links");

            migrationBuilder.AddColumn<string>(
                name: "protected_refresh_token",
                table: "user_aws_identity_links",
                type: "text",
                nullable: false,
                defaultValue: "");
        }
    }
}
