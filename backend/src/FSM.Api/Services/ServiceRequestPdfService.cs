using FSM.Domain.Entities;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace FSM.Api.Services;

public sealed class ServiceRequestPdfService(IConfiguration configuration)
{
    public byte[] Generate(ServiceRequest request)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var company = configuration["ServiceRequests:CompanyTitle"]
            ?? request.Vessel?.CompanyName ?? "Fleet Safety Manager";
        return Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.A4.Landscape()); page.Margin(24);
            page.DefaultTextStyle(x => x.FontFamily(Fonts.Arial).FontSize(8));
            page.Header().Column(c =>
            {
                c.Item().Text(company).Bold().FontColor(Colors.Grey.Darken2);
                c.Item().Text("SERVICE REQUEST").Bold().FontSize(18);
                c.Item().Text($"{request.RequestNumber} · {request.RequestDate:dd MMM yyyy} · {request.Status}");
            });
            page.Content().PaddingVertical(10).Column(c =>
            {
                c.Spacing(8);
                c.Item().Row(row =>
                {
                    row.RelativeItem().Text($"Vessel: {request.Vessel!.VesselName}\nIMO: {request.Vessel.ImoNumber}\nContact: {request.ContactName}\nEmail: {request.ContactEmail}\nPhone: {request.ContactPhone ?? "—"}");
                    row.RelativeItem().Text($"Port: {request.PreferredPort ?? "—"}\nETA: {Date(request.ExpectedArrivalDate)}\nETD: {Date(request.ExpectedDepartureDate)}\nService company: {request.ServiceCompany ?? "—"}\nAttention: {request.AttentionTo ?? "—"}");
                });
                c.Item().Text($"Subject: {request.Subject}").Bold().FontSize(10);
                c.Item().Table(table =>
                {
                    table.ColumnsDefinition(x =>
                    {
                        x.ConstantColumn(25); x.ConstantColumn(50); x.RelativeColumn(1.3f);
                        x.RelativeColumn(); x.RelativeColumn(); x.RelativeColumn();
                        x.ConstantColumn(60); x.RelativeColumn(1.5f); x.RelativeColumn();
                    });
                    table.Header(h =>
                    {
                        foreach (var value in new[] {"No.","Type","Equipment / area","Maker / model","Serial","Location","Due / priority","Required service","Remarks"})
                            h.Cell().Background(Colors.Grey.Darken2).Padding(3).Text(value).Bold().FontColor(Colors.White);
                    });
                    foreach (var item in request.Items.OrderBy(x => x.SequenceNumber))
                    {
                        var values = new[] {
                            item.SequenceNumber.ToString(), item.ItemType.ToString(),
                            ItemTitle(item), $"{item.Maker} {item.Model}".Trim(),
                            item.SerialNumber ?? "—", item.Location ?? "—",
                            item.Priority ?? Date(item.CurrentDueDate),
                            item.ServiceDescription, item.Remarks ?? "—"
                        };
                        foreach (var value in values)
                            table.Cell().BorderBottom(1).BorderColor(Colors.Grey.Lighten2)
                                .Padding(3).Text(value);
                    }
                });
                if (!string.IsNullOrWhiteSpace(request.GeneralRemarks))
                    c.Item().Text($"General remarks\n{request.GeneralRemarks}");
                if (!string.IsNullOrWhiteSpace(request.InvoicingDetails))
                    c.Item().Text($"Invoicing details\n{request.InvoicingDetails}");
                if (request.Attachments.Count > 0)
                    c.Item().Text("Attachments\n" + string.Join("\n",
                        request.Attachments.Select(x => $"• {x.OriginalFileName}")));
                c.Item().Text($"Prepared by: {request.RequestedBy} · Generated: {DateTime.UtcNow:dd MMM yyyy HH:mm} UTC");
            });
            page.Footer().AlignCenter().Text(t =>
            {
                t.Span($"{request.RequestNumber} · {request.Vessel!.VesselName} · Page ");
                t.CurrentPageNumber(); t.Span(" of "); t.TotalPages();
            });
        })).GeneratePdf();
    }
    private static string ItemTitle(ServiceRequestItem item) =>
        item.ItemType == ServiceRequestItemType.InspectionFinding
            ? $"{item.Title}\n{item.InspectionReportNumber} finding {item.InspectionFindingSequence}: {item.Description}"
            : item.Title;
    private static string Date(DateOnly? date) => date?.ToString("dd MMM yyyy") ?? "—";
}
