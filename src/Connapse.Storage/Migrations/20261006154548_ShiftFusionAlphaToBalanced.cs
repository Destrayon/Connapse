using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <summary>
    /// Moves a saved hybrid-search weight of exactly 0.75 (the previous default) to the new default
    /// 0.65 (#668). The Search tab saves every field at once, so a stored 0.75 is almost always the old
    /// default carried along rather than a choice; any other stored value is left alone.
    /// </summary>
    public partial class ShiftFusionAlphaToBalanced : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE settings
                SET values = jsonb_set(values, '{fusionAlpha}', '0.65'::jsonb), updated_at = now()
                WHERE lower(category) = 'search'
                  AND jsonb_typeof(values -> 'fusionAlpha') = 'number'
                  AND (values ->> 'fusionAlpha')::numeric = 0.75;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversed: a stored 0.65 after this point can't be told apart from a chosen one.
        }
    }
}
