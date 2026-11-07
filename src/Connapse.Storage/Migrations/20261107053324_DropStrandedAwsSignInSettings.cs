using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <summary>
    /// Deletes the settings the AWS SSO device flow and the Cognito integration left behind (#448). Their code and
    /// screens are gone, so a client secret stored here could be neither seen nor cleared, and a rollback to a build
    /// that reads them would quietly turn that sign-in back on. Both categories are named exactly: the SAML and
    /// Identity Center settings that replaced them are in use.
    /// </summary>
    public partial class DropStrandedAwsSignInSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM settings WHERE lower(category) IN ('awssso', 'cognito');");
        }

        /// <inheritdoc />
        /// <remarks>Nothing to undo: the deleted secrets are gone, and bringing them back is what this prevents.</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
