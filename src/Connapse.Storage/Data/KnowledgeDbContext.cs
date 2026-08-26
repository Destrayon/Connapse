using Connapse.Storage.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Connapse.Storage.Data;

public class KnowledgeDbContext(DbContextOptions<KnowledgeDbContext> options) : DbContext(options)
{
    public DbSet<ContainerEntity> Containers => Set<ContainerEntity>();
    public DbSet<DocumentEntity> Documents => Set<DocumentEntity>();
    public DbSet<ChunkEntity> Chunks => Set<ChunkEntity>();
    public DbSet<ChunkVectorEntity> ChunkVectors => Set<ChunkVectorEntity>();
    public DbSet<FolderEntity> Folders => Set<FolderEntity>();
    public DbSet<SettingEntity> Settings => Set<SettingEntity>();
    public DbSet<BatchEntity> Batches => Set<BatchEntity>();
    public DbSet<BatchDocumentEntity> BatchDocuments => Set<BatchDocumentEntity>();
    public DbSet<ConnectionEntity> Connections => Set<ConnectionEntity>();
    public DbSet<ProviderCredentialEntity> ProviderCredentials => Set<ProviderCredentialEntity>();
    public DbSet<SourceEntity> Sources => Set<SourceEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasPostgresExtension("vector");

        ConfigureContainers(modelBuilder);
        ConfigureFolders(modelBuilder);
        ConfigureDocuments(modelBuilder);
        ConfigureChunks(modelBuilder);
        ConfigureChunkVectors(modelBuilder);
        ConfigureSettings(modelBuilder);
        ConfigureBatches(modelBuilder);
        ConfigureBatchDocuments(modelBuilder);
        ConfigureConnections(modelBuilder);
        ConfigureSources(modelBuilder);
    }

    private static void ConfigureContainers(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ContainerEntity>(entity =>
        {
            entity.ToTable("containers");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Id)
                .HasColumnName("id")
                .HasDefaultValueSql("gen_random_uuid()");

            entity.Property(e => e.Name)
                .HasColumnName("name")
                .HasMaxLength(128)
                .IsRequired();

            entity.Property(e => e.Description)
                .HasColumnName("description");

            entity.Property(e => e.SettingsOverridesJson)
                .HasColumnName("settings_overrides")
                .HasColumnType("jsonb");

            entity.Property(e => e.CreatedAt)
                .HasColumnName("created_at")
                .HasDefaultValueSql("now()");

            entity.Property(e => e.UpdatedAt)
                .HasColumnName("updated_at")
                .HasDefaultValueSql("now()");

            entity.Property(e => e.Summary)
                .HasColumnName("summary");

            entity.Property(e => e.SummaryGeneratedAt)
                .HasColumnName("summary_generated_at");

            entity.Property(e => e.SummaryDocSetHash)
                .HasColumnName("summary_doc_set_hash")
                .HasMaxLength(64);

            entity.HasIndex(e => e.Name)
                .HasDatabaseName("ix_containers_name")
                .IsUnique();
        });
    }

    private static void ConfigureFolders(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FolderEntity>(entity =>
        {
            entity.ToTable("folders");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Id)
                .HasColumnName("id")
                .HasDefaultValueSql("gen_random_uuid()");

            entity.Property(e => e.ContainerId)
                .HasColumnName("container_id")
                .IsRequired();

            entity.Property(e => e.Path)
                .HasColumnName("path")
                .IsRequired();

            entity.Property(e => e.CreatedAt)
                .HasColumnName("created_at")
                .HasDefaultValueSql("now()");

            entity.HasIndex(e => new { e.ContainerId, e.Path })
                .HasDatabaseName("ix_folders_container_path")
                .IsUnique();

            entity.HasOne(e => e.Container)
                .WithMany(c => c.Folders)
                .HasForeignKey(e => e.ContainerId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureDocuments(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DocumentEntity>(entity =>
        {
            entity.ToTable("documents");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Id)
                .HasColumnName("id")
                .HasDefaultValueSql("gen_random_uuid()");

            entity.Property(e => e.ContainerId)
                .HasColumnName("container_id");

            entity.Property(e => e.SourceId)
                .HasColumnName("source_id");

            // Stored generated column. The search path filters on owner_id so it never
            // has to know whether the owner is a container or a source.
            entity.Property(e => e.OwnerId)
                .HasColumnName("owner_id")
                .HasComputedColumnSql("COALESCE(container_id, source_id)", stored: true);

            entity.Property(e => e.FileName)
                .HasColumnName("file_name")
                .IsRequired();

            entity.Property(e => e.ContentType)
                .HasColumnName("content_type");

            entity.Property(e => e.Path)
                .HasColumnName("path")
                .IsRequired();

            entity.Property(e => e.ContentHash)
                .HasColumnName("content_hash")
                .IsRequired();

            entity.Property(e => e.SizeBytes)
                .HasColumnName("size_bytes");

            entity.Property(e => e.ChunkCount)
                .HasColumnName("chunk_count")
                .HasDefaultValue(0);

            entity.Property(e => e.Generation)
                .HasColumnName("generation")
                .HasDefaultValue(1);

            entity.Property(e => e.Status)
                .HasColumnName("status")
                .IsRequired()
                .HasDefaultValue("Pending");

            entity.Property(e => e.ErrorMessage)
                .HasColumnName("error_message");

            entity.Property(e => e.CreatedAt)
                .HasColumnName("created_at")
                .HasDefaultValueSql("now()");

            entity.Property(e => e.LastIndexedAt)
                .HasColumnName("last_indexed_at");

            entity.Property(e => e.Metadata)
                .HasColumnName("metadata")
                .HasColumnType("jsonb")
                .HasDefaultValueSql("'{}'::jsonb");

            entity.Property(e => e.Summary)
                .HasColumnName("summary");

            entity.Property(e => e.SummaryGeneratedAt)
                .HasColumnName("summary_generated_at");

            entity.Property(e => e.SummaryContentHash)
                .HasColumnName("summary_content_hash")
                .HasMaxLength(64);

            entity.Property(e => e.IngestionState)
                .HasConversion<string>()
                .HasMaxLength(32)
                .HasColumnName("ingestion_state")
                .IsRequired()
                .HasDefaultValue(Core.IngestionState.Pending);

            entity.HasIndex(e => e.IngestionState)
                .HasDatabaseName("ix_documents_ingestion_state");

            // Exactly one owner. Postgres treats "(a IS NULL) <> (b IS NULL)" as XOR.
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_documents_single_owner",
                "(container_id IS NULL) <> (source_id IS NULL)"));

            entity.HasIndex(e => e.ContainerId)
                .HasDatabaseName("idx_documents_container_id");

            entity.HasIndex(e => e.SourceId)
                .HasDatabaseName("idx_documents_source_id");

            entity.HasIndex(e => e.OwnerId)
                .HasDatabaseName("ix_documents_owner_id");

            // Keyed on owner_id, not container_id: a unique index over a nullable
            // container_id would not constrain source-owned rows at all, since
            // Postgres treats each NULL as distinct.
            entity.HasIndex(e => new { e.OwnerId, e.Path })
                .HasDatabaseName("idx_documents_owner_path")
                .IsUnique();

            entity.HasOne(e => e.Container)
                .WithMany(c => c.Documents)
                .HasForeignKey(e => e.ContainerId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Source)
                .WithMany(s => s.Documents)
                .HasForeignKey(e => e.SourceId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    /// <summary>
    /// Configures EF Core mappings for ChunkEntity and maps it to the "chunks" table.
    /// </summary>
    /// <remarks>
    /// Defines column mappings (including a stored tsvector computed column "search_vector"), indexes on DocumentId and ContainerId, a GIN index for full-text search on SearchVector, and a cascade delete relationship to DocumentEntity.
    /// </remarks>
    private static void ConfigureChunks(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ChunkEntity>(entity =>
        {
            entity.ToTable("chunks");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Id)
                .HasColumnName("id")
                .HasDefaultValueSql("gen_random_uuid()");

            entity.Property(e => e.Content)
                .HasColumnName("content")
                .IsRequired();

            entity.Property(e => e.ChunkIndex)
                .HasColumnName("chunk_index");

            entity.Property(e => e.DocumentId)
                .HasColumnName("document_id");

            entity.Property(e => e.OwnerId)
                .HasColumnName("owner_id")
                .IsRequired();

            entity.Property(e => e.TokenCount)
                .HasColumnName("token_count");

            entity.Property(e => e.StartOffset)
                .HasColumnName("start_offset");

            entity.Property(e => e.EndOffset)
                .HasColumnName("end_offset");

            entity.Property(e => e.Metadata)
                .HasColumnName("metadata")
                .HasColumnType("jsonb")
                .HasDefaultValueSql("'{}'::jsonb");

            entity.Property(e => e.SearchVector)
                .HasColumnName("search_vector")
                .HasColumnType("tsvector")
                .HasComputedColumnSql("setweight(to_tsvector('simple', coalesce(content, '')), 'A') || setweight(to_tsvector('english', coalesce(content, '')), 'B')", stored: true);

            entity.HasIndex(e => e.DocumentId)
                .HasDatabaseName("idx_chunks_document_id");

            entity.HasIndex(e => e.OwnerId)
                .HasDatabaseName("idx_chunks_owner_id");

            entity.HasIndex(e => e.SearchVector)
                .HasDatabaseName("idx_chunks_fts")
                .HasMethod("GIN");

            entity.HasOne(e => e.Document)
                .WithMany(d => d.Chunks)
                .HasForeignKey(e => e.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureChunkVectors(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ChunkVectorEntity>(entity =>
        {
            entity.ToTable("chunk_vectors");
            entity.HasKey(e => e.ChunkId);

            entity.Property(e => e.ChunkId)
                .HasColumnName("chunk_id");

            entity.Property(e => e.DocumentId)
                .HasColumnName("document_id");

            entity.Property(e => e.OwnerId)
                .HasColumnName("owner_id")
                .IsRequired();

            entity.Property(e => e.Embedding)
                .HasColumnName("embedding")
                .IsRequired()
                .HasColumnType("vector");

            entity.Property(e => e.ModelId)
                .HasColumnName("model_id")
                .IsRequired();

            entity.Property(e => e.ContentHash)
                .HasColumnName("content_hash")
                .HasMaxLength(64);

            entity.Property(e => e.Dimensions)
                .HasColumnName("dimensions");

            entity.HasIndex(e => new { e.ContentHash, e.ModelId, e.Dimensions })
                .HasDatabaseName("idx_chunk_vectors_cache_lookup")
                .HasFilter("\"content_hash\" IS NOT NULL AND \"dimensions\" IS NOT NULL");

            entity.HasIndex(e => e.DocumentId)
                .HasDatabaseName("idx_chunk_vectors_document_id");

            entity.HasIndex(e => e.OwnerId)
                .HasDatabaseName("idx_chunk_vectors_owner_id");

            // B-tree index on model_id for search filtering and partial index WHERE clauses
            entity.HasIndex(e => e.ModelId)
                .HasDatabaseName("idx_chunk_vectors_model_id");

            // NOTE: IVFFlat partial indexes per model_id are managed dynamically
            // by VectorColumnManager at startup and when embedding settings change.

            entity.HasOne(e => e.Chunk)
                .WithOne(c => c.ChunkVector)
                .HasForeignKey<ChunkVectorEntity>(e => e.ChunkId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Document)
                .WithMany(d => d.ChunkVectors)
                .HasForeignKey(e => e.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureSettings(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SettingEntity>(entity =>
        {
            entity.ToTable("settings");
            entity.HasKey(e => e.Category);

            entity.Property(e => e.Category)
                .HasColumnName("category");

            entity.Property(e => e.Values)
                .HasColumnName("values")
                .HasColumnType("jsonb")
                .HasDefaultValueSql("'{}'::jsonb");

            entity.Property(e => e.UpdatedAt)
                .HasColumnName("updated_at")
                .HasDefaultValueSql("now()");
        });
    }

    private static void ConfigureBatches(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BatchEntity>(entity =>
        {
            entity.ToTable("batches");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Id)
                .HasColumnName("id")
                .HasDefaultValueSql("gen_random_uuid()");

            entity.Property(e => e.TotalFiles)
                .HasColumnName("total_files");

            entity.Property(e => e.Status)
                .HasColumnName("status")
                .IsRequired()
                .HasDefaultValue("Processing");

            entity.Property(e => e.Completed)
                .HasColumnName("completed")
                .HasDefaultValue(0);

            entity.Property(e => e.Failed)
                .HasColumnName("failed")
                .HasDefaultValue(0);

            entity.Property(e => e.CreatedAt)
                .HasColumnName("created_at")
                .HasDefaultValueSql("now()");

            entity.Property(e => e.CompletedAt)
                .HasColumnName("completed_at");
        });
    }

    private static void ConfigureBatchDocuments(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BatchDocumentEntity>(entity =>
        {
            entity.ToTable("batch_documents");
            entity.HasKey(e => new { e.BatchId, e.DocumentId });

            entity.Property(e => e.BatchId)
                .HasColumnName("batch_id");

            entity.Property(e => e.DocumentId)
                .HasColumnName("document_id");

            entity.HasOne(e => e.Batch)
                .WithMany(b => b.BatchDocuments)
                .HasForeignKey(e => e.BatchId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Document)
                .WithMany(d => d.BatchDocuments)
                .HasForeignKey(e => e.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureConnections(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProviderCredentialEntity>(entity =>
        {
            entity.ToTable("provider_credentials");

            // Keyed by provider, not a surrogate id: Connapse has one identity per cloud, and a
            // second row for the same provider would be an ambiguity nothing could resolve.
            entity.HasKey(e => e.Provider);

            entity.Property(e => e.Provider)
                .HasColumnName("provider")
                .HasMaxLength(32);

            entity.Property(e => e.PublicId)
                .HasColumnName("public_id")
                .HasMaxLength(256)
                .IsRequired();

            entity.Property(e => e.SecretProtected)
                .HasColumnName("secret_protected")
                .IsRequired();

            entity.Property(e => e.PrincipalName)
                .HasColumnName("principal_name")
                .HasMaxLength(256);

            entity.Property(e => e.CreatedAt)
                .HasColumnName("created_at");

            entity.Property(e => e.VerifiedAt)
                .HasColumnName("verified_at");

            entity.Property(e => e.CreatedByUserId)
                .HasColumnName("created_by_user_id");
        });

        modelBuilder.Entity<ConnectionEntity>(entity =>
        {
            entity.ToTable("connections");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Id)
                .HasColumnName("id");

            entity.Property(e => e.Name)
                .HasColumnName("name")
                .HasMaxLength(128)
                .IsRequired();

            entity.Property(e => e.Provider)
                .HasColumnName("provider")
                .IsRequired();

            entity.Property(e => e.ConfigJson)
                .HasColumnName("config")
                .HasColumnType("jsonb");

            entity.Property(e => e.SecretProtected)
                .HasColumnName("secret_protected");

            entity.Property(e => e.CreatedByUserId)
                .HasColumnName("created_by_user_id");

            entity.Property(e => e.CreatedAt)
                .HasColumnName("created_at");

            entity.Property(e => e.UpdatedAt)
                .HasColumnName("updated_at");

            entity.HasIndex(e => e.Name)
                .IsUnique()
                .HasDatabaseName("ix_connections_name");
        });
    }

    private static void ConfigureSources(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SourceEntity>(entity =>
        {
            entity.ToTable("sources");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Id)
                .HasColumnName("id");

            entity.Property(e => e.Name)
                .HasColumnName("name")
                .HasMaxLength(128)
                .IsRequired();

            entity.Property(e => e.Description)
                .HasColumnName("description");

            entity.Property(e => e.ConnectionId)
                .HasColumnName("connection_id")
                .IsRequired();

            entity.Property(e => e.ScopeJson)
                .HasColumnName("scope")
                .HasColumnType("jsonb")
                .IsRequired();

            entity.Property(e => e.SettingsOverridesJson)
                .HasColumnName("settings_overrides")
                .HasColumnType("jsonb");

            entity.Property(e => e.Enabled)
                .HasColumnName("enabled")
                .HasDefaultValue(true);

            entity.Property(e => e.SyncCursor)
                .HasColumnName("sync_cursor");

            entity.Property(e => e.LastSyncedAt)
                .HasColumnName("last_synced_at");

            entity.Property(e => e.LastSyncStatus)
                .HasColumnName("last_sync_status")
                .HasDefaultValue(0);

            entity.Property(e => e.LastSyncError)
                .HasColumnName("last_sync_error");

            entity.Property(e => e.SyncIntervalSeconds)
                .HasColumnName("sync_interval_seconds");

            entity.Property(e => e.WithheldDeletions)
                .HasColumnName("withheld_deletions");

            entity.Property(e => e.Summary)
                .HasColumnName("summary");

            entity.Property(e => e.SummaryGeneratedAt)
                .HasColumnName("summary_generated_at");

            entity.Property(e => e.SummaryDocSetHash)
                .HasColumnName("summary_doc_set_hash");

            entity.Property(e => e.CreatedAt)
                .HasColumnName("created_at");

            entity.Property(e => e.UpdatedAt)
                .HasColumnName("updated_at");

            entity.HasIndex(e => e.Name)
                .IsUnique()
                .HasDatabaseName("ix_sources_name");

            entity.HasIndex(e => e.ConnectionId)
                .HasDatabaseName("ix_sources_connection_id");

            // Restrict, not Cascade: deleting a connection that still has sources
            // must fail loudly rather than silently destroying their documents.
            entity.HasOne(e => e.Connection)
                .WithMany(c => c.Sources)
                .HasForeignKey(e => e.ConnectionId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
