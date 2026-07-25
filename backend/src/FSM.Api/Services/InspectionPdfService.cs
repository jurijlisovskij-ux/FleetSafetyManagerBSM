using FSM.Domain.Entities;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace FSM.Api.Services;

public sealed class InspectionPdfService(IConfiguration configuration)
{
    public byte[] Generate(InspectionReport report, IReadOnlyDictionary<Guid, string> photos)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var company = configuration["Inspections:CompanyTitle"]
            ?? report.Vessel?.CompanyName ?? "Fleet Safety Manager";
        return Document.Create(document => document.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(28);
            page.DefaultTextStyle(x => x.FontSize(9).FontFamily(Fonts.Arial));
            page.Header().Column(column =>
            {
                column.Item().Text(company).Bold().FontSize(10).FontColor(Colors.Grey.Darken2);
                column.Item().Text("VESSEL INSPECTION REPORT").Bold().FontSize(18);
                column.Item().PaddingTop(4).Table(table =>
                {
                    table.ColumnsDefinition(c => { c.RelativeColumn(); c.RelativeColumn(); });
                    Cell(table, "Vessel", report.Vessel!.VesselName);
                    Cell(table, "IMO number", report.Vessel.ImoNumber);
                    Cell(table, "Report number", report.ReportNumber);
                    Cell(table, "Inspection date", report.InspectionDate.ToString("dd MMM yyyy"));
                    Cell(table, "Inspector", report.InspectorName);
                    Cell(table, "Inspection type", Display(report.InspectionType));
                });
            });
            page.Content().PaddingVertical(12).Column(column =>
            {
                var findings = report.Findings.OrderBy(x => x.SequenceNumber).ToList();
                column.Spacing(10);
                column.Item().Background(Colors.Grey.Lighten3).Padding(8).Column(summary =>
                {
                    summary.Item().Text("SUMMARY").Bold();
                    summary.Item().Text($"Total {findings.Count}  |  Critical {findings.Count(x => x.Priority == InspectionPriority.Critical)}  |  Urgent {findings.Count(x => x.Priority == InspectionPriority.Urgent)}  |  Normal {findings.Count(x => x.Priority == InspectionPriority.Normal)}");
                    summary.Item().Text($"Open {findings.Count(x => x.Status == InspectionFindingStatus.Open)}  |  Closed {findings.Count(x => x.Status == InspectionFindingStatus.Closed)}");
                    if (!string.IsNullOrWhiteSpace(report.GeneralRemarks))
                        summary.Item().PaddingTop(3).Text(report.GeneralRemarks);
                });
                foreach (var finding in findings)
                {
                    column.Item().EnsureSpace(90).Border(1).BorderColor(Colors.Grey.Medium).Column(item =>
                    {
                        var shade = finding.Priority switch
                        {
                            InspectionPriority.Critical => Colors.Grey.Darken3,
                            InspectionPriority.Urgent => Colors.Grey.Darken1,
                            _ => Colors.Grey.Lighten2
                        };
                        item.Item().Background(shade).Padding(6).Text(
                            $"FINDING {finding.SequenceNumber} · {finding.Priority} · {finding.Status}")
                            .Bold().FontColor(finding.Priority == InspectionPriority.Normal
                                ? Colors.Black : Colors.White);
                        item.Item().Padding(7).Column(details =>
                        {
                            details.Item().Text($"{finding.Department} — {finding.EquipmentOrArea}").Bold();
                            if (!string.IsNullOrWhiteSpace(finding.ExactLocation))
                                details.Item().Text($"Location: {finding.ExactLocation}");
                            details.Item().PaddingTop(4).Text(finding.Description);
                            if (!string.IsNullOrWhiteSpace(finding.InspectorRemarks))
                                details.Item().PaddingTop(4).Text($"Inspector remarks: {finding.InspectorRemarks}");
                            foreach (var photo in finding.Photos.OrderBy(x => x.CreatedUtc))
                                if (photos.TryGetValue(photo.Id, out var path) && File.Exists(path))
                                {
                                    details.Item().PaddingTop(6).Height(220).AlignCenter()
                                        .Image(path).FitArea();
                                    if (!string.IsNullOrWhiteSpace(photo.Caption))
                                        details.Item().AlignCenter().Text(photo.Caption).Italic();
                                }
                        });
                    });
                }
            });
            page.Footer().AlignCenter().Text(text =>
            {
                text.Span($"{report.ReportNumber} · {report.Vessel!.VesselName} · {DateTime.UtcNow:dd MMM yyyy} · Page ");
                text.CurrentPageNumber();
                text.Span(" of ");
                text.TotalPages();
            });
        })).GeneratePdf();
    }

    private static void Cell(TableDescriptor table, string label, string value) =>
        table.Cell().PaddingVertical(2).Text(text =>
        {
            text.Span($"{label}: ").SemiBold();
            text.Span(value);
        });
    private static string Display(InspectionType type) =>
        type == InspectionType.DeckAndEngine ? "Deck and Engine" : type.ToString();
}
