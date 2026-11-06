using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <summary>
    /// Deletes container memory for good (CON-255): every fact saved in a document container, with its sources, and every
    /// star on a document container. Memory now lives only in personal and team spaces, which are memory containers, so
    /// their facts and stars are untouched. Sources go through their cascade from the facts.
    /// </summary>
    public partial class DeleteContainerMemory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM memory_facts f
                USING containers c
                WHERE c.id = f.container_id AND c.kind = 'documents';

                DELETE FROM memory_space_stars s
                USING containers c
                WHERE c.id = s.container_id AND c.kind = 'documents';
                """);
        }

        /// <inheritdoc />
        /// <remarks>Nothing to undo: the deleted facts are gone, and this can't bring them back.</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
