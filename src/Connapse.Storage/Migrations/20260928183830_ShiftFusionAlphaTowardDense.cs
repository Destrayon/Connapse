using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <summary>
    /// Moves a saved hybrid-search weight of exactly 0.3 (the previous default) to the new default
    /// 0.75 (#552). The Search tab saves every field at once, so a stored 0.3 is almost always the old
    /// default carried along rather than a choice; any other stored value is left alone.
    /// </summary>
    public partial class ShiftFusionAlphaTowardDense : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE settings
                SET values = jsonb_set(values, '{fusionAlpha}', '0.75'::jsonb), updated_at = now()
                WHERE lower(category) = 'search'
                  AND jsonb_typeof(values -> 'fusionAlpha') = 'number'
                  AND (values ->> 'fusionAlpha')::numeric = 0.3;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversed: a stored 0.75 after this point can't be told apart from a chosen one.
        }
    }
}
