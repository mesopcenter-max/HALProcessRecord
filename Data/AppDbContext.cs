using HALProcessRecord.Models;
using Microsoft.EntityFrameworkCore;

namespace HALProcessRecord.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<ProcessRecordEntity> ProcessRecords => Set<ProcessRecordEntity>();
    public DbSet<OperationRecordEntity> OperationRecords => Set<OperationRecordEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProcessRecordEntity>()
            .HasMany(x => x.Operations)
            .WithOne(x => x.ProcessRecord)
            .HasForeignKey(x => x.ProcessRecordId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ProcessRecordEntity>()
            .Property(x => x.PartNo).HasMaxLength(100).IsRequired();

        modelBuilder.Entity<ProcessRecordEntity>()
            .Property(x => x.BatchNo).HasMaxLength(100).IsRequired();

        modelBuilder.Entity<ProcessRecordEntity>()
            .Property(x => x.OperatorName).HasMaxLength(100);

        modelBuilder.Entity<ProcessRecordEntity>()
            .Property(x => x.Operation).HasMaxLength(100);

        modelBuilder.Entity<ProcessRecordEntity>()
            .Property(x => x.Equipment).HasMaxLength(100);

        modelBuilder.Entity<ProcessRecordEntity>()
            .Property(x => x.ForgingTemperature).HasPrecision(18, 2);

        modelBuilder.Entity<ProcessRecordEntity>()
            .Property(x => x.TemperatureTolerance).HasPrecision(18, 2);

        modelBuilder.Entity<ProcessRecordEntity>()
            .Property(x => x.SoakingStartTemperature).HasPrecision(18, 2);

        modelBuilder.Entity<ProcessRecordEntity>()
            .Property(x => x.MandrelDiameter).HasPrecision(18, 2);

        modelBuilder.Entity<OperationRecordEntity>()
            .Property(x => x.Observation).HasMaxLength(500);
    }
}
