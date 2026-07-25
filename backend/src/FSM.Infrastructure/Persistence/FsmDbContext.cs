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
    public DbSet<EquipmentDocument> EquipmentDocuments =>
        Set<EquipmentDocument>();
    public DbSet<InspectionReport> InspectionReports => Set<InspectionReport>();
    public DbSet<InspectionFinding> InspectionFindings => Set<InspectionFinding>();
    public DbSet<InspectionPhoto> InspectionPhotos => Set<InspectionPhoto>();
    public DbSet<InspectionReportPdf> InspectionReportPdfs => Set<InspectionReportPdf>();

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
        });

        modelBuilder.Entity<Vessel>(entity =>
        {
            entity.ToTable("vessels");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.VesselName).HasMaxLength(120).IsRequired();
            entity.Property(x => x.ImoNumber).HasMaxLength(7).IsRequired();
            entity.Property(x => x.Flag).HasMaxLength(80);
            entity.HasIndex(x => x.ImoNumber).IsUnique();
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
        });

        modelBuilder.Entity<Equipment>(entity =>
        {
            entity.ToTable("equipment");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EquipmentName).HasMaxLength(180).IsRequired();
            entity.Property(x => x.Maker).HasMaxLength(180);
            entity.Property(x => x.Model).HasMaxLength(180);
            entity.Property(x => x.SerialNumber).HasMaxLength(180);
            entity.Property(x => x.Category).HasConversion<string>();
            entity.Property(x => x.InspectionBy).HasConversion<string>();
            entity.HasOne(x => x.Vessel)
                .WithMany(x => x.Equipment)
                .HasForeignKey(x => x.VesselId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.VesselId, x.Category });
            entity.HasIndex(x => x.ExpiryDate);
        });

        modelBuilder.Entity<EquipmentDocument>(entity =>
        {
            entity.ToTable("equipment_documents");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.DocumentType).HasConversion<string>();
            entity.Property(x => x.FileName).HasMaxLength(260).IsRequired();
            entity.Property(x => x.StoredFileName).HasMaxLength(520).IsRequired();
            entity.Property(x => x.MimeType).HasMaxLength(120).IsRequired();
            entity.Property(x => x.DocumentNumber).HasMaxLength(180);
            entity.Property(x => x.Issuer).HasMaxLength(240);
            entity.HasOne(x => x.Equipment)
                .WithMany(x => x.Documents)
                .HasForeignKey(x => x.EquipmentId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new
            {
                x.EquipmentId,
                x.DocumentType,
                x.CreatedUtc
            });
            entity.HasIndex(x => new
            {
                x.EquipmentId,
                x.DocumentType
            })
            .IsUnique()
            .HasFilter("\"IsCurrent\" = 1")
            .HasDatabaseName(
                "IX_equipment_documents_EquipmentId_DocumentType_Current");
        });

        modelBuilder.Entity<InspectionReport>(entity =>
        {
            entity.ToTable("inspection_reports");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ReportNumber).HasMaxLength(80).IsRequired();
            entity.Property(x => x.InspectorName).HasMaxLength(120).IsRequired();
            entity.Property(x => x.InspectionType).HasConversion<string>();
            entity.Property(x => x.State).HasConversion<string>();
            entity.HasIndex(x => x.ReportNumber).IsUnique();
            entity.HasIndex(x => new { x.VesselId, x.InspectionDate });
            entity.HasOne(x => x.Vessel).WithMany(x => x.InspectionReports)
                .HasForeignKey(x => x.VesselId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<InspectionFinding>(entity =>
        {
            entity.ToTable("inspection_findings");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Department).HasConversion<string>();
            entity.Property(x => x.Priority).HasConversion<string>();
            entity.Property(x => x.Status).HasConversion<string>();
            entity.Property(x => x.EquipmentOrArea).HasMaxLength(240).IsRequired();
            entity.HasIndex(x => new { x.InspectionReportId, x.SequenceNumber }).IsUnique();
            entity.HasOne(x => x.InspectionReport).WithMany(x => x.Findings)
                .HasForeignKey(x => x.InspectionReportId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<InspectionPhoto>(entity =>
        {
            entity.ToTable("inspection_photos");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OriginalFileName).HasMaxLength(260).IsRequired();
            entity.Property(x => x.StoredFileName).HasMaxLength(520).IsRequired();
            entity.Property(x => x.MimeType).HasMaxLength(80).IsRequired();
            entity.HasOne(x => x.Finding).WithMany(x => x.Photos)
                .HasForeignKey(x => x.FindingId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<InspectionReportPdf>(entity =>
        {
            entity.ToTable("inspection_report_pdfs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.FileName).HasMaxLength(260).IsRequired();
            entity.Property(x => x.StoredFileName).HasMaxLength(520).IsRequired();
            entity.Property(x => x.MimeType).HasMaxLength(80).IsRequired();
            entity.HasIndex(x => new { x.InspectionReportId, x.VersionNumber }).IsUnique();
            entity.HasIndex(x => x.InspectionReportId).IsUnique()
                .HasFilter("\"IsCurrent\" = 1")
                .HasDatabaseName("IX_inspection_report_pdfs_Report_Current");
            entity.HasOne(x => x.InspectionReport).WithMany(x => x.PdfVersions)
                .HasForeignKey(x => x.InspectionReportId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
