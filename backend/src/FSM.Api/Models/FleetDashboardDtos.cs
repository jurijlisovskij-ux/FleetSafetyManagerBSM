namespace FSM.Api.Models;

public sealed record FleetDashboardCountsDto(
    int EquipmentTotal, int EquipmentOk, int EquipmentSoon, int EquipmentOverdue,
    int InspectionDraft, int InspectionFinalized,
    int CriticalOpen, int UrgentOpen, int NormalOpen,
    int ServiceDraft, int ServiceReady, int ServiceSent,
    int ServiceCompleted, int ServiceCancelled);
public sealed record FleetDashboardVesselDto(
    Guid VesselId, string VesselName, string ImoNumber,
    int EquipmentOverdue, int CriticalFindings, int OpenFindings,
    int DraftInspectionReports, int ActiveServiceRequests,
    DateTime? LastSynchronizationUtc, string OverallStatus);
public sealed record FleetDashboardChartDto(
    string Name, IReadOnlyList<FleetDashboardChartValueDto> Values);
public sealed record FleetDashboardChartValueDto(string Label, int Value);
public sealed record FleetNotificationDto(
    string Source, string Severity, Guid VesselId, string VesselName,
    string Title, string Detail, DateTime EventUtc);
public sealed record FleetDashboardFilterOptionDto(Guid Id, string Name);
public sealed record FleetDashboardDto(
    FleetDashboardCountsDto Counts,
    IReadOnlyList<FleetDashboardVesselDto> Vessels,
    IReadOnlyList<FleetDashboardChartDto> Charts,
    IReadOnlyList<FleetNotificationDto> Notifications,
    IReadOnlyList<FleetDashboardFilterOptionDto> VesselOptions,
    IReadOnlyList<FleetDashboardFilterOptionDto> SuperintendentOptions);
