using HALProcessRecord.Models;
using Microsoft.EntityFrameworkCore;

namespace HALProcessRecord.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<ProcessRecordEntity> ProcessRecords => Set<ProcessRecordEntity>();
    public DbSet<OperationRecordEntity> OperationRecords => Set<OperationRecordEntity>();
    public DbSet<MachineTimingStateEntity> MachineTimingStates => Set<MachineTimingStateEntity>();
    public DbSet<TimingCycleEntity> TimingCycles => Set<TimingCycleEntity>();
    public DbSet<TimingTransferIntervalEntity> TimingTransferIntervals => Set<TimingTransferIntervalEntity>();
    public DbSet<FurnaceDataEntity> FurnaceData => Set<FurnaceDataEntity>();
    public DbSet<MachineDataEntity> MachineData => Set<MachineDataEntity>();

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
            .Property(x => x.Machine).HasMaxLength(50);

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

        modelBuilder.Entity<OperationRecordEntity>()
            .Property(x => x.Temperature).HasPrecision(18, 2);

        modelBuilder.Entity<OperationRecordEntity>()
            .Property(x => x.HighestTemperature).HasPrecision(18, 2);

        modelBuilder.Entity<MachineTimingStateEntity>()
            .HasIndex(x => new { x.Furnace, x.Machine })
            .IsUnique();

        modelBuilder.Entity<MachineTimingStateEntity>()
            .Property(x => x.Furnace).HasMaxLength(20).IsRequired();

        modelBuilder.Entity<MachineTimingStateEntity>()
            .Property(x => x.Machine).HasMaxLength(50).IsRequired();

        modelBuilder.Entity<MachineTimingStateEntity>()
            .Property(x => x.LastMaterialForce).HasPrecision(18, 2);

        modelBuilder.Entity<TimingCycleEntity>()
            .Property(x => x.Furnace).HasMaxLength(20).IsRequired();

        modelBuilder.Entity<TimingCycleEntity>()
            .Property(x => x.StartMode).HasMaxLength(20).IsRequired();

        modelBuilder.Entity<TimingCycleEntity>()
            .HasIndex(x => new { x.Furnace, x.Machine, x.TotalStartAt, x.StoppedAt });

        modelBuilder.Entity<TimingTransferIntervalEntity>()
            .HasIndex(x => new { x.Furnace, x.Machine, x.OpenAt });

        modelBuilder.Entity<TimingTransferIntervalEntity>()
            .Property(x => x.Furnace).HasMaxLength(20).IsRequired();

        modelBuilder.Entity<TimingTransferIntervalEntity>()
            .Property(x => x.Machine).HasMaxLength(50).IsRequired();

        modelBuilder.Entity<FurnaceDataEntity>()
            .HasIndex(x => new { x.Furnace, x.CapturedAt });

        modelBuilder.Entity<FurnaceDataEntity>()
            .Property(x => x.Furnace).HasMaxLength(20).IsRequired();

        modelBuilder.Entity<FurnaceDataEntity>()
            .Property(x => x.Code).HasMaxLength(100);

        modelBuilder.Entity<MachineDataEntity>()
            .HasIndex(x => new { x.Machine, x.CapturedAt });

        modelBuilder.Entity<MachineDataEntity>()
            .Property(x => x.Machine).HasMaxLength(50).IsRequired();

        modelBuilder.Entity<MachineDataEntity>()
            .Property(x => x.MaterialForce).HasPrecision(18, 2);

        modelBuilder.Entity<MachineDataEntity>()
            .Property(x => x.MaterialTemperature).HasPrecision(18, 2);
    }
}
