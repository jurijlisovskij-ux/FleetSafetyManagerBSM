using FSM.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FSM.Infrastructure.Persistence;

public sealed class FsmDbContext(DbContextOptions<FsmDbContext> options)
    : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Vessel> Vessels => Set<Vessel>();
    public DbSet<UserVessel> UserVessels => Set<UserVessel>();
    public DbSet<Equipment> Equipment => Set<Equipment>();
    public DbSet<SynchronizationSetting> SynchronizationSettings => Set<SynchronizationSetting>();
    public DbSet<SynchronizationLog> SynchronizationLogs => Set<SynchronizationLog>();
    public DbSet<SynchronizationTransfer> SynchronizationTransfers => Set<SynchronizationTransfer>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<MedicineItem> MedicineItems => Set<MedicineItem>();
    public DbSet<VesselMedicineConfiguration> VesselMedicineConfigurations => Set<VesselMedicineConfiguration>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.FullName).HasMaxLength(120).IsRequired();
            entity.Property(x => x.Email).HasMaxLength(255).IsRequired();
            entity.Property(x => x.PasswordHash).IsRequired();
            entity.Property(x => x.Role).HasConversion<string>();
            entity.HasIndex(x => x.Email).IsUnique();
            entity.HasQueryFilter(x => !x.IsDeleted);
        });

        modelBuilder.Entity<Vessel>(entity =>
        {
            entity.ToTable("vessels");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.VesselName).HasMaxLength(120).IsRequired();
            entity.Property(x => x.ImoNumber).HasMaxLength(7).IsRequired();
            entity.Property(x => x.Flag).HasMaxLength(80);
            entity.HasIndex(x => x.ImoNumber).IsUnique();
            entity.HasQueryFilter(x => !x.IsDeleted);
        });

        modelBuilder.Entity<UserVessel>(entity =>
        {
            entity.ToTable("user_vessels");
            entity.HasKey(x => new { x.UserId, x.VesselId });
            entity.HasOne(x => x.User)
                .WithMany(x => x.VesselAssignments)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Vessel)
                .WithMany(x => x.UserAssignments)
                .HasForeignKey(x => x.VesselId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(x => !x.IsDeleted);
        });

        modelBuilder.Entity<Equipment>(entity =>
        {
            entity.ToTable("equipment");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EquipmentName).HasMaxLength(180).IsRequired();
            entity.Property(x => x.Maker).HasMaxLength(180);
            entity.Property(x => x.Model).HasMaxLength(180);
            entity.Property(x => x.SerialNumber).HasMaxLength(180);
            entity.Property(x => x.Responsible).HasMaxLength(180);
            entity.Property(x => x.Category).HasConversion<string>();
            entity.Property(x => x.InspectionBy).HasConversion<string>();
            entity.HasOne(x => x.Vessel)
                .WithMany(x => x.Equipment)
                .HasForeignKey(x => x.VesselId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.VesselId, x.Category });
            entity.HasIndex(x => x.ExpiryDate);
            entity.HasQueryFilter(x => !x.IsDeleted);
        });

        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.ToTable("audit_logs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Action).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Operator).HasMaxLength(255).IsRequired();
            entity.Property(x => x.TargetUser).HasMaxLength(255);
            entity.Property(x => x.ComputerName).HasMaxLength(255).IsRequired();
            entity.Property(x => x.Result).HasMaxLength(20).IsRequired();
            entity.Property(x => x.Summary).HasMaxLength(500);
            entity.HasIndex(x => x.DateUtc);
        });

        modelBuilder.Entity<MedicineItem>(entity =>
        {
            entity.ToTable("medicine_items"); entity.HasKey(x => x.Id);
            entity.Property(x => x.ItemName).HasMaxLength(250).IsRequired();
            entity.Property(x => x.Unit).HasMaxLength(50).IsRequired();
            entity.HasIndex(x => new { x.VesselId, x.ItemName, x.FormStrength, x.IsDangerousGoods }).IsUnique();
            entity.HasQueryFilter(x => !x.IsDeleted);
        });
        modelBuilder.Entity<VesselMedicineConfiguration>(entity =>
        {
            entity.ToTable("vessel_medicine_configuration"); entity.HasKey(x => x.VesselId);
            entity.HasQueryFilter(x => !x.IsDeleted);
        });

        modelBuilder.Entity<SynchronizationSetting>(entity =>
        {
            entity.ToTable("synchronization_settings");
            entity.HasKey(x => x.VesselId);
            entity.Property(x => x.RemoteBaseUrl).HasMaxLength(500);
            entity.Property(x => x.TokenHash).HasMaxLength(128);
            entity.Property(x => x.LastStatus).HasMaxLength(20).IsRequired();
            entity.Property(x => x.CurrentStage).HasMaxLength(80).IsRequired();
            entity.HasOne(x => x.Vessel)
                .WithOne()
                .HasForeignKey<SynchronizationSetting>(x => x.VesselId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(x => !x.Vessel!.IsDeleted);
        });

        modelBuilder.Entity<SynchronizationLog>(entity =>
        {
            entity.ToTable("synchronization_logs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Direction).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Result).HasMaxLength(20).IsRequired();
            entity.HasIndex(x => new { x.VesselId, x.DateUtc });
            entity.HasOne(x => x.Vessel)
                .WithMany()
                .HasForeignKey(x => x.VesselId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(x => !x.Vessel!.IsDeleted);
        });

        modelBuilder.Entity<SynchronizationTransfer>(entity =>
        {
            entity.ToTable("synchronization_transfers");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.RelativePath).HasMaxLength(700).IsRequired();
            entity.Property(x => x.Checksum).HasMaxLength(64).IsRequired();
            entity.Property(x => x.TemporaryPath).HasMaxLength(700).IsRequired();
            entity.HasIndex(x => new { x.VesselId, x.RelativePath, x.Checksum });
        });
    }

    public override int SaveChanges()
    {
        PrepareSynchronizableEntities(preserveMetadata: false);
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(
        CancellationToken cancellationToken = default)
    {
        PrepareSynchronizableEntities(preserveMetadata: false);
        return base.SaveChangesAsync(cancellationToken);
    }

    public Task<int> SaveSynchronizationChangesAsync(
        CancellationToken cancellationToken = default)
    {
        PrepareSynchronizableEntities(preserveMetadata: true);
        return base.SaveChangesAsync(cancellationToken);
    }

    private void PrepareSynchronizableEntities(bool preserveMetadata)
    {
        var now = DateTime.UtcNow;

        foreach (var entry in ChangeTracker
            .Entries<ISynchronizableEntity>())
        {
            if (entry.State == EntityState.Deleted)
            {
                entry.State = EntityState.Modified;
                entry.Entity.IsDeleted = true;
                entry.Entity.LastModifiedUtc = now;
                continue;
            }

            if (preserveMetadata)
                continue;

            if (entry.State == EntityState.Added &&
                entry.Entity.LastModifiedUtc == default)
            {
                entry.Entity.LastModifiedUtc = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.LastModifiedUtc = now;
            }
        }
    }
}
