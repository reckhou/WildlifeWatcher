using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using WildlifeWatcher.Models;

namespace WildlifeWatcher.Data;

public class WildlifeDbContext(DbContextOptions<WildlifeDbContext> options) : DbContext(options)
{
    public DbSet<Species> Species => Set<Species>();
    public DbSet<CaptureRecord> CaptureRecords => Set<CaptureRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Species>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.CommonName).IsRequired().HasMaxLength(200);
            e.Property(s => s.ScientificName).HasMaxLength(200);
            e.Property(s => s.ReferencePhotoPath).HasMaxLength(500);
            e.HasIndex(s => s.CommonName);
        });

        modelBuilder.Entity<CaptureRecord>(e =>
        {
            e.HasKey(c => c.Id);
            e.Property(c => c.ImageFilePath).IsRequired().HasMaxLength(500);
            e.HasOne(c => c.Species)
             .WithMany(s => s.Captures)
             .HasForeignKey(c => c.SpeciesId)
             .OnDelete(DeleteBehavior.Cascade);
        });
    }
}

// Required for 'dotnet ef migrations add' at design time (no running app needed)
public class WildlifeDbContextFactory : IDesignTimeDbContextFactory<WildlifeDbContext>
{
    public WildlifeDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<WildlifeDbContext>()
            .UseSqlite("Data Source=wildlife_design.db")
            .Options;
        return new WildlifeDbContext(options);
    }
}
