using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <summary>
    /// Moves today's personal memory out of team containers into personal spaces, without changing who sees what
    /// (CON-236). A chain moves whole, to a personal space for its author named after the container, when every version
    /// in it is personal and written by the same user, or by the same agent acting for no user. Visibility is "every
    /// source readable, and team or yours", and a personal space is seen only by the author its facts are personal to,
    /// so every reader sees the same facts before and after. Team chains stay; chains that mix personal and team
    /// versions stay too, since moving them would split their history; chains with no author stay, as nobody can own
    /// their space. Fact ids, chain ids and container ids never change; only a moved fact's container id does.
    /// </summary>
    /// <remarks>
    /// The move runs in batches of 1,000 chains, each committed on its own, so a 50,000-fact container never sits in one
    /// long transaction. That needs the DO block to run outside the migration's transaction. Each batch first locks its
    /// chains the way a correction does, so a correction from an instance still serving during the deploy never leaves
    /// a new version behind in the old container. Running it again moves
    /// nothing new: moved chains are in personal spaces, which this never reads from.
    /// </remarks>
    public partial class MoveMemoryIntoSpaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    batch_size CONSTANT int := 1000;
                    moved_on text := to_char(now() AT TIME ZONE 'UTC', 'YYYY-MM-DD');
                    total bigint;
                    mixed bigint;
                    done bigint := 0;
                BEGIN
                    DROP TABLE IF EXISTS memory_space_moves;

                    -- One row per chain that moves: on a team container, all versions personal, one owner.
                    -- The owner is the author's user, or the author's agent when no user wrote it, as MemoryCaller.IsAuthorOf reads it.
                    CREATE TEMP TABLE memory_space_moves AS
                    SELECT row_number() OVER (ORDER BY chain.chain_id) AS n,
                           chain.chain_id,
                           chain.owner_user_id,
                           CASE WHEN chain.owner_user_id IS NULL THEN chain.owner_agent_id END AS owner_agent_id,
                           CASE WHEN length(c.name) > 119 THEN rtrim(left(c.name, 119), '-') ELSE c.name END || '-personal' AS space_name,
                           c.name AS from_name,
                           NULL::uuid AS space_id
                    FROM (
                        SELECT f.chain_id,
                               (array_agg(f.container_id))[1] AS container_id,
                               (array_agg(f.author_user_id))[1] AS owner_user_id,
                               (array_agg(f.author_agent_id))[1] AS owner_agent_id
                        FROM memory_facts f
                        GROUP BY f.chain_id
                        HAVING bool_and(f.scope = 0)
                           AND count(DISTINCT f.container_id) = 1
                           AND bool_and(f.author_user_id IS NOT NULL OR f.author_agent_id IS NOT NULL)
                           AND count(DISTINCT coalesce('u' || f.author_user_id::text, 'a' || f.author_agent_id::text)) = 1
                    ) chain
                    JOIN containers c ON c.id = chain.container_id
                    WHERE c.audience = 'team';

                    SELECT count(*) INTO total FROM memory_space_moves;

                    -- The spaces, made once per owner and name. A space CON-235 already made under that name is reused.
                    INSERT INTO containers (id, name, description, kind, audience, owner_user_id, owner_agent_id, created_at, updated_at)
                    SELECT DISTINCT ON (owner_user_id, owner_agent_id, space_name)
                           gen_random_uuid(), space_name,
                           'Moved from ' || from_name || '''s Memory tab on ' || moved_on,
                           'memory', 'personal', owner_user_id, owner_agent_id, now(), now()
                    FROM memory_space_moves
                    ORDER BY owner_user_id, owner_agent_id, space_name, from_name
                    ON CONFLICT (owner_user_id, owner_agent_id, name) WHERE audience = 'personal' DO NOTHING;

                    UPDATE memory_space_moves m
                    SET space_id = c.id
                    FROM containers c
                    WHERE c.audience = 'personal'
                      AND c.name = m.space_name
                      AND c.owner_user_id IS NOT DISTINCT FROM m.owner_user_id
                      AND c.owner_agent_id IS NOT DISTINCT FROM m.owner_agent_id;

                    CREATE INDEX ON memory_space_moves (n);
                    COMMIT;

                    WHILE done < total LOOP
                        -- Lock each chain's first fact as a correction does, so a correction running now finishes first
                        -- and later ones wait. The move is a statement of its own, so it sees the versions they added.
                        PERFORM 1
                        FROM memory_facts f
                        JOIN memory_space_moves m ON f.id = m.chain_id
                        WHERE m.n > done AND m.n <= done + batch_size
                        ORDER BY f.id
                        FOR UPDATE OF f;

                        UPDATE memory_facts f
                        SET container_id = m.space_id
                        FROM memory_space_moves m
                        WHERE m.n > done AND m.n <= done + batch_size
                          AND f.chain_id = m.chain_id;
                        done := done + batch_size;
                        COMMIT;
                    END LOOP;

                    SELECT count(*) INTO mixed
                    FROM (
                        SELECT f.chain_id
                        FROM memory_facts f
                        JOIN containers c ON c.id = f.container_id
                        WHERE c.audience = 'team'
                        GROUP BY f.chain_id
                        HAVING bool_or(f.scope = 0) AND bool_or(f.scope = 1)
                    ) m;

                    RAISE NOTICE 'Moved % memory chain(s) into personal spaces; % chain(s) mixing personal and team versions stay where they are.', total, mixed;

                    DROP TABLE memory_space_moves;
                END $$;
                """, suppressTransaction: true);
        }

        /// <inheritdoc />
        /// <remarks>
        /// Nothing to undo: moved facts keep their personal scope, so leaving them in their spaces keeps every reader's
        /// view, and moving them back could not tell a moved fact from one saved to the space after the move.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
