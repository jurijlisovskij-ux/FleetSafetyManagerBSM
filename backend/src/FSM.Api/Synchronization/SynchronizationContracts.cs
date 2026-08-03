using FSM.Domain.Enums;

namespace FSM.Api.Synchronization;

public sealed record VesselSynchronizationItem(
    Guid Id,
    string VesselName,
    string ImoNumber,
    string? Flag,
    string? CompanyName,
    string? InvoicingDetails,
    string? AccountingEmail,
    DateTime LastModifiedUtc,
    bool IsDeleted);

public sealed record EquipmentSynchronizationItem(
    Guid Id,
    Guid VesselId,
    EquipmentCategory Category,
    string EquipmentName,
    string? Maker,
    string? Model,
    string? SerialNumber,
    int Quantity,
    InspectionBy InspectionBy,
    DateOnly? ExpiryDate,
    string? Responsible,
    string? Remarks,
    string? CurrentReportPath,
    string? CurrentReportFileName,
    string? CurrentCertificatePath,
    string? CurrentCertificateFileName,
    DateTime LastModifiedUtc,
    bool IsDeleted);

public sealed record UserSynchronizationItem(
    Guid Id,
    string FullName,
    string Email,
    string PasswordHash,
    UserRole Role,
    bool IsActive,
    bool ForcePasswordChange,
    bool IsLocked,
    DateTime LastModifiedUtc,
    bool IsDeleted);

public sealed record UserVesselSynchronizationItem(
    Guid UserId,
    Guid VesselId,
    DateTime LastModifiedUtc,
    bool IsDeleted);

public sealed record VesselSettingsSynchronizationItem(
    Guid VesselId,
    int AutomaticIntervalHours,
    DateTime LastModifiedUtc);

public sealed record MedicineSynchronizationItem(
    Guid Id, Guid VesselId, string ItemName, string? FormStrength,
    decimal RequiredQuantity, decimal ActualQuantity, string Unit,
    string? BatchNumber, DateOnly? ExpiryDate, string? StorageLocation,
    string? MedicalCategory, bool IsDangerousGoods, bool ReplacementRequired,
    string? Remarks, bool IsActive, bool IsStandardTemplateItem,
    DateTime LastModifiedUtc, bool IsDeleted);

public sealed record MedicineConfigurationSynchronizationItem(
    Guid VesselId, bool CategoryAEnabled, bool CategoryBEnabled,
    bool DangerousGoodsEnabled, int WarningDays, DateTime LastModifiedUtc,
    bool IsDeleted);

public sealed record InspectionReportSynchronizationItem(Guid Id, Guid VesselId,
    string ReportNumber, DateOnly InspectionDate, string Inspector, string PdfPath,
    string PdfSha256, DateTime LastModifiedUtc, bool IsDeleted);
public sealed record InspectionReportItemSynchronizationItem(Guid Id,
    Guid InspectionReportId, Guid EquipmentId, string Result, string? Remarks,
    string? CorrectiveAction, string? AttachmentPath, DateOnly? ClosureDate,
    string? ClosedBy, string? ClosureRemarks, string? ClosureAttachmentPath,
    DateTime LastModifiedUtc, bool IsDeleted);

public sealed record SynchronizationFileItem(
    string RelativePath,
    long Size,
    string Checksum,
    DateTime LastModifiedUtc);

public sealed record SynchronizationPayload(
    IReadOnlyList<VesselSynchronizationItem> Vessels,
    IReadOnlyList<EquipmentSynchronizationItem> Equipment,
    IReadOnlyList<UserSynchronizationItem> Users,
    IReadOnlyList<UserVesselSynchronizationItem> UserVessels,
    IReadOnlyList<VesselSettingsSynchronizationItem> VesselSettings,
    IReadOnlyList<MedicineSynchronizationItem> Medicine,
    IReadOnlyList<MedicineConfigurationSynchronizationItem> MedicineConfiguration,
    IReadOnlyList<InspectionReportSynchronizationItem> InspectionReports,
    IReadOnlyList<InspectionReportItemSynchronizationItem> InspectionReportItems,
    IReadOnlyList<SynchronizationFileItem> Files)
{
    public int RecordCount =>
        Vessels.Count + Equipment.Count + Users.Count +
        UserVessels.Count + VesselSettings.Count + Medicine.Count +
        MedicineConfiguration.Count + InspectionReports.Count +
        InspectionReportItems.Count;
}

public sealed record SynchronizationExchangeRequest(
    string ApplicationVersion,
    string SourceEdition,
    Guid VesselId,
    DateTime RemoteCursorUtc,
    SynchronizationPayload Changes);

public sealed record SynchronizationExchangeResponse(
    string ApplicationVersion,
    string SourceEdition,
    Guid VesselId,
    DateTime SnapshotUtc,
    SynchronizationPayload Changes,
    int AcceptedItems,
    int ConflictItems,
    IReadOnlyList<string> Warnings);

public sealed record SynchronizationHandshakeResponse(
    string ApplicationVersion,
    string Edition,
    Guid VesselId,
    DateTime ServerTimeUtc);

public sealed record PrepareSynchronizationFileRequest(
    string ApplicationVersion,
    Guid VesselId,
    SynchronizationFileItem File);

public sealed record PrepareSynchronizationFileResponse(
    Guid? TransferId,
    long NextOffset,
    bool Completed);

public sealed record CompleteSynchronizationFileRequest(
    string ApplicationVersion,
    Guid VesselId);

public sealed record SynchronizationRunResult(
    Guid VesselId,
    DateTime CompletedUtc,
    string Status,
    int UploadedItems,
    int DownloadedItems,
    int Conflicts,
    string? Message);

public sealed record UpdateSynchronizationSettingsRequest(
    int AutomaticIntervalHours,
    string? RemoteBaseUrl);

public sealed record SynchronizationStatusItem(
    Guid VesselId,
    string VesselName,
    string ImoNumber,
    DateTime? LastSynchronizationUtc,
    string Status,
    int PendingChanges,
    int AutomaticIntervalHours,
    string CurrentStage,
    bool Configured,
    string? RemoteBaseUrl,
    string? LastError);
