using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Connapse.Storage.Migrations
{
    /// <summary>
    /// Replaces the two status columns — free-text <c>status</c> and the <c>ingestion_state</c>
    /// enum, which disagreed with each other — with <c>ingestion_status</c> and a separate
    /// <c>summary_status</c> (#562). Columns are added and backfilled before the old ones drop.
    /// </summary>
    public partial class UnifyDocumentStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "attempt_count",
                table: "documents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ingestion_status",
                table: "documents",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Queued");

            migrationBuilder.AddColumn<string>(
                name: "job_id",
                table: "documents",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "status_changed_at",
                table: "documents",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<string>(
                name: "summary_status",
                table: "documents",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "NotNeeded");

            // status is the job lifecycle: Ready/Failed are settled; everything else was waiting
            // on a job and is Queued now — the stuck-job sweep re-enqueues any whose job is gone.
            // A Failed row may have failed for either reason; retryable is the one that lets sync
            // try it again. The sync engine's retry counter moves out of metadata into its column.
            migrationBuilder.Sql("""
                UPDATE documents SET
                    ingestion_status = CASE status
                        WHEN 'Ready'  THEN 'Ready'
                        WHEN 'Failed' THEN 'FailedRetryable'
                        ELSE 'Queued'
                    END,
                    summary_status = CASE
                        WHEN summary IS NOT NULL          THEN 'Done'
                        WHEN ingestion_state = 'Indexed'  THEN 'Pending'
                        ELSE 'NotNeeded'
                    END,
                    attempt_count = CASE
                        WHEN metadata->>'SyncFailedAttempts' ~ '^[0-9]+$'
                        THEN (metadata->>'SyncFailedAttempts')::int
                        ELSE 0
                    END,
                    status_changed_at = coalesce(last_indexed_at, created_at),
                    metadata = metadata - 'SyncFailedAttempts';
                """);

            migrationBuilder.DropIndex(
                name: "ix_documents_ingestion_state",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "ingestion_state",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "status",
                table: "documents");

            migrationBuilder.CreateIndex(
                name: "ix_documents_ingestion_status",
                table: "documents",
                column: "ingestion_status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ingestion_state",
                table: "documents",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Pending");

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "documents",
                type: "text",
                nullable: false,
                defaultValue: "Pending");

            migrationBuilder.Sql("""
                UPDATE documents SET
                    status = CASE ingestion_status
                        WHEN 'Ready'           THEN 'Ready'
                        WHEN 'Processing'      THEN 'Processing'
                        WHEN 'FailedRetryable' THEN 'Failed'
                        WHEN 'FailedPermanent' THEN 'Failed'
                        ELSE 'Pending'
                    END,
                    ingestion_state = CASE
                        WHEN ingestion_status IN ('FailedRetryable', 'FailedPermanent') THEN 'Failed'
                        WHEN ingestion_status <> 'Ready'  THEN 'Pending'
                        WHEN summary_status = 'Pending'   THEN 'Indexed'
                        ELSE 'SummaryIndexed'
                    END;
                """);

            migrationBuilder.DropIndex(
                name: "ix_documents_ingestion_status",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "attempt_count",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "ingestion_status",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "job_id",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "status_changed_at",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "summary_status",
                table: "documents");

            migrationBuilder.CreateIndex(
                name: "ix_documents_ingestion_state",
                table: "documents",
                column: "ingestion_state");
        }
    }
}
