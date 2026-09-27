using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <summary>
    /// Per-owner corpus statistics for BM25 keyword ranking in plain PostgreSQL (#548), so it runs
    /// wherever the database has no BM25 extension (Azure Flexible Server, RDS, ...).
    ///
    /// BM25 needs, per owner, the chunk count and total length (for avgdl) and, per term, how many
    /// chunks contain it (df). Counting rows directly on every chunk write makes concurrent ingests
    /// contend on the same common-term rows and deadlock, so the triggers only append signed deltas
    /// and <c>Bm25StatsFolder</c> folds them into the stats tables. Readers add any unfolded deltas,
    /// so statistics are exact at all times.
    ///
    /// Terms come from the english (weight B) positions of chunks.search_vector: the simple (weight A)
    /// copy shares lexemes with it, and counting both would double every count. Each chunk's length
    /// is stored in chunks.bm25_length, so ranking never re-counts it.
    /// See docs/research/portable-bm25-postgres-2026-09-26.md.
    /// </summary>
    public partial class AddBm25Statistics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                -- Tokens left after english analysis, duplicates counted: Lucene's document length.
                CREATE FUNCTION bm25_text_length(content text) RETURNS integer
                LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
                    SELECT coalesce(sum(cardinality(u.positions)), 0)::integer
                    FROM unnest(to_tsvector('english', coalesce(content, ''))) u
                $$;

                -- The same count read from search_vector's english copy; for rows without bm25_length.
                CREATE FUNCTION bm25_doc_length(sv tsvector) RETURNS bigint
                LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
                    SELECT coalesce(sum(cardinality(u.positions)), 0)
                    FROM unnest(ts_filter(sv, '{b}')) u
                $$;

                -- A plain column set by a BEFORE trigger rather than a generated column: generated
                -- columns cannot build on search_vector, and adding one would rewrite the table.
                ALTER TABLE chunks ADD COLUMN bm25_length integer;

                CREATE FUNCTION bm25_chunks_length() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    NEW.bm25_length := bm25_text_length(NEW.content);
                    RETURN NEW;
                END
                $$;

                CREATE TRIGGER bm25_chunks_length BEFORE INSERT OR UPDATE OF content ON chunks
                    FOR EACH ROW EXECUTE FUNCTION bm25_chunks_length();

                -- Before the statistics triggers exist, so the backfill writes no deltas.
                UPDATE chunks SET bm25_length = bm25_text_length(content);

                CREATE TABLE bm25_owner_stats (
                    owner_id uuid PRIMARY KEY,
                    n_docs bigint NOT NULL,
                    total_len bigint NOT NULL);

                CREATE TABLE bm25_term_stats (
                    owner_id uuid NOT NULL,
                    term text NOT NULL,
                    df bigint NOT NULL,
                    PRIMARY KEY (owner_id, term));

                -- term IS NULL: a change to the owner's chunk count and total length.
                CREATE TABLE bm25_delta (
                    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    owner_id uuid NOT NULL,
                    term text,
                    d_df bigint NOT NULL,
                    d_n bigint NOT NULL,
                    d_len bigint NOT NULL);
                CREATE INDEX idx_bm25_delta_owner_term ON bm25_delta (owner_id, term);

                CREATE FUNCTION bm25_chunks_delta() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'UPDATE' THEN
                        -- Only rows whose terms or owner changed; a metadata update nets to nothing.
                        INSERT INTO bm25_delta (owner_id, term, d_df, d_n, d_len)
                        SELECT o.owner_id, u.lexeme, -1, 0, 0
                        FROM old_rows o JOIN new_rows n ON n.id = o.id,
                             unnest(ts_filter(o.search_vector, '{b}')) u
                        WHERE o.search_vector IS DISTINCT FROM n.search_vector OR o.owner_id <> n.owner_id
                        UNION ALL
                        SELECT n.owner_id, u.lexeme, 1, 0, 0
                        FROM old_rows o JOIN new_rows n ON n.id = o.id,
                             unnest(ts_filter(n.search_vector, '{b}')) u
                        WHERE o.search_vector IS DISTINCT FROM n.search_vector OR o.owner_id <> n.owner_id
                        UNION ALL
                        SELECT o.owner_id, NULL, 0, -count(*), -sum(o.bm25_length)
                        FROM old_rows o JOIN new_rows n ON n.id = o.id
                        WHERE o.search_vector IS DISTINCT FROM n.search_vector OR o.owner_id <> n.owner_id
                        GROUP BY o.owner_id
                        UNION ALL
                        SELECT n.owner_id, NULL, 0, count(*), sum(n.bm25_length)
                        FROM old_rows o JOIN new_rows n ON n.id = o.id
                        WHERE o.search_vector IS DISTINCT FROM n.search_vector OR o.owner_id <> n.owner_id
                        GROUP BY n.owner_id;
                    ELSIF TG_OP = 'INSERT' THEN
                        INSERT INTO bm25_delta (owner_id, term, d_df, d_n, d_len)
                        SELECT r.owner_id, u.lexeme, 1, 0, 0
                        FROM new_rows r, unnest(ts_filter(r.search_vector, '{b}')) u
                        UNION ALL
                        SELECT owner_id, NULL, 0, count(*), sum(bm25_length)
                        FROM new_rows GROUP BY owner_id;
                    ELSE
                        INSERT INTO bm25_delta (owner_id, term, d_df, d_n, d_len)
                        SELECT r.owner_id, u.lexeme, -1, 0, 0
                        FROM old_rows r, unnest(ts_filter(r.search_vector, '{b}')) u
                        UNION ALL
                        SELECT owner_id, NULL, 0, -count(*), -sum(bm25_length)
                        FROM old_rows GROUP BY owner_id;
                    END IF;
                    RETURN NULL;
                END
                $$;

                CREATE TRIGGER bm25_chunks_insert AFTER INSERT ON chunks
                    REFERENCING NEW TABLE AS new_rows
                    FOR EACH STATEMENT EXECUTE FUNCTION bm25_chunks_delta();
                CREATE TRIGGER bm25_chunks_delete AFTER DELETE ON chunks
                    REFERENCING OLD TABLE AS old_rows
                    FOR EACH STATEMENT EXECUTE FUNCTION bm25_chunks_delta();
                CREATE TRIGGER bm25_chunks_update AFTER UPDATE ON chunks
                    REFERENCING OLD TABLE AS old_rows NEW TABLE AS new_rows
                    FOR EACH STATEMENT EXECUTE FUNCTION bm25_chunks_delta();

                -- Existing chunks. Migrations run before the app serves traffic, so nothing writes
                -- chunks between the triggers above and this backfill.
                INSERT INTO bm25_term_stats (owner_id, term, df)
                SELECT c.owner_id, u.lexeme, count(*)
                FROM chunks c, unnest(ts_filter(c.search_vector, '{b}')) u
                GROUP BY c.owner_id, u.lexeme;

                INSERT INTO bm25_owner_stats (owner_id, n_docs, total_len)
                SELECT owner_id, count(*), sum(bm25_length)
                FROM chunks GROUP BY owner_id;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS bm25_chunks_insert ON chunks;
                DROP TRIGGER IF EXISTS bm25_chunks_delete ON chunks;
                DROP TRIGGER IF EXISTS bm25_chunks_update ON chunks;
                DROP FUNCTION IF EXISTS bm25_chunks_delta();
                DROP TRIGGER IF EXISTS bm25_chunks_length ON chunks;
                DROP FUNCTION IF EXISTS bm25_chunks_length();
                ALTER TABLE chunks DROP COLUMN IF EXISTS bm25_length;
                DROP FUNCTION IF EXISTS bm25_doc_length(tsvector);
                DROP FUNCTION IF EXISTS bm25_text_length(text);
                DROP TABLE IF EXISTS bm25_delta;
                DROP TABLE IF EXISTS bm25_term_stats;
                DROP TABLE IF EXISTS bm25_owner_stats;
                """);
        }
    }
}
