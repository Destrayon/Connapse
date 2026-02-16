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

            entity.Property(e => e.CreatedAt)
                .HasColumnName("created_at")
                .HasDefaultValueSql("now()");

            entity.Property(e => e.UpdatedAt)
                .HasColumnName("updated_at")
                .HasDefaultValueSql("now()");

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
                .HasColumnName("container_id")
                .IsRequired();

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

            entity.HasIndex(e => e.ContainerId)
                .HasDatabaseName("idx_documents_container_id");

            entity.HasIndex(e => new { e.ContainerId, e.Path })
                .HasDatabaseName("idx_documents_container_path")
                .IsUnique();

            entity.HasOne(e => e.Container)
                .WithMany(c => c.Documents)
                .HasForeignKey(e => e.ContainerId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

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

            entity.Property(e => e.ContainerId)
                .HasColumnName("container_id")
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
                .HasComputedColumnSql("to_tsvector('english', content)", stored: true);

            entity.HasIndex(e => e.DocumentId)
                .HasDatabaseName("idx_chunks_document_id");

            entity.HasIndex(e => e.ContainerId)
                .HasDatabaseName("idx_chunks_container_id");

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

            entity.Property(e => e.ContainerId)
                .HasColumnName("container_id")
                .IsRequired();

            entity.Property(e => e.Embedding)
                .HasColumnName("embedding")
                .IsRequired()
                .HasColumnType("vector(768)");

            entity.Property(e => e.ModelId)
                .HasColumnName("model_id")
                .IsRequired();

            entity.HasIndex(e => e.DocumentId)
                .HasDatabaseName("idx_chunk_vectors_document_id");

            entity.HasIndex(e => e.ContainerId)
                .HasDatabaseName("idx_chunk_vectors_container_id");

            entity.HasIndex(e => e.Embedding)
                .HasDatabaseName("idx_chunk_vectors_embedding")
                .HasMethod("ivfflat")
                .HasOperators("vector_cosine_ops")
                .HasStorageParameter("lists", 100);

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
}
