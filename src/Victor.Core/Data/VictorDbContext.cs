using Microsoft.EntityFrameworkCore;
using Victor.Core.Models;

namespace Victor.Core.Data;

public class VictorDbContext : DbContext
{
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<MemoryRecord> Memories => Set<MemoryRecord>();

    public VictorDbContext(DbContextOptions<VictorDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("vector");

        modelBuilder.Entity<Job>(entity =>
        {
            entity.ToTable("jobs");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Description).HasColumnName("description");
            entity.Property(e => e.RequestedBy).HasColumnName("requested_by");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            entity.Property(e => e.Status).HasColumnName("status").HasConversion<string>();
            entity.Property(e => e.Result).HasColumnName("result");
            entity.Property(e => e.Error).HasColumnName("error");
            entity.Property(e => e.CompletedAt).HasColumnName("completed_at");
        });

        modelBuilder.Entity<MemoryRecord>(entity =>
        {
            entity.ToTable("memories");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Timestamp).HasColumnName("timestamp").HasDefaultValueSql("now()");
            entity.Property(e => e.TaskId).HasColumnName("task_id");
            entity.Property(e => e.Category).HasColumnName("category");
            entity.Property(e => e.Summary).HasColumnName("summary");
            entity.Property(e => e.Embedding).HasColumnName("embedding").HasColumnType("vector(1536)");

            entity.HasIndex(e => e.Embedding)
                .HasMethod("ivfflat")
                .HasOperators("vector_cosine_ops")
                .HasStorageParameter("lists", 100);
        });
    }
}
