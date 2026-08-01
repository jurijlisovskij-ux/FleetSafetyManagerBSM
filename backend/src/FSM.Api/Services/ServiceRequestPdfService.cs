using FSM.Domain.Entities;
using FSM.Domain.Enums;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace FSM.Api.Services;

public sealed class ServiceRequestPdfService
{
    public byte[] Generate(
        Vessel vessel,
        IReadOnlyList<Equipment> equipment,
        string port,
        DateOnly? eta,
        DateOnly? etd,
        string? remarks)
    {
        QuestPDF.Settings.License = LicenseType.Community;

        return Document.Create(document =>
        {
            document.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(24);
                page.DefaultTextStyle(style => style.FontSize(9));

                page.Header().Column(column =>
                {
                    column.Item()
                        .AlignCenter()
                        .Text("SERVICE REQUEST")
                        .Bold()
                        .FontSize(16);

                    column.Item().PaddingTop(10).Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.ConstantColumn(95);
                            columns.RelativeColumn();
                            columns.ConstantColumn(95);
                            columns.RelativeColumn();
                        });

                        AddInfoRow(table, "Vessel", vessel.VesselName,
                            "IMO Number", vessel.ImoNumber);
                        AddInfoRow(table, "Flag", vessel.Flag ?? string.Empty,
                            "Company", vessel.CompanyName ?? string.Empty);
                        AddInfoRow(table, "Accounting Email",
                            vessel.AccountingEmail ?? string.Empty,
                            "Request Date",
                            DateOnly.FromDateTime(DateTime.UtcNow)
                                .ToString("dd MMM yyyy"));
                        AddInfoRow(table, "Port", port,
                            "ETA", FormatDate(eta));
                        AddInfoRow(table, "ETD", FormatDate(etd),
                            string.Empty, string.Empty);
                    });
                });

                page.Content().PaddingTop(14).Column(column =>
                {
                    column.Item().Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.ConstantColumn(72);
                            columns.RelativeColumn(1.5f);
                            columns.RelativeColumn();
                            columns.RelativeColumn();
                            columns.RelativeColumn();
                            columns.ConstantColumn(42);
                            columns.ConstantColumn(92);
                            columns.ConstantColumn(82);
                        });

                        table.Header(header =>
                        {
                            HeaderCell(header, "Category");
                            HeaderCell(header, "Equipment");
                            HeaderCell(header, "Maker");
                            HeaderCell(header, "Model");
                            HeaderCell(header, "Serial Number");
                            HeaderCell(header, "Qty");
                            HeaderCell(header, "Inspection By");
                            HeaderCell(header, "Expiry Date");
                        });

                        foreach (var item in equipment)
                        {
                            BodyCell(table, FormatCategory(item.Category));
                            BodyCell(table, item.EquipmentName);
                            BodyCell(table, item.Maker ?? string.Empty);
                            BodyCell(table, item.Model ?? string.Empty);
                            BodyCell(table, item.SerialNumber ?? string.Empty);
                            BodyCell(table, item.Quantity.ToString(), true);
                            BodyCell(table,
                                item.InspectionBy == InspectionBy.ServiceCompany
                                    ? "Service Company"
                                    : "Crew");
                            BodyCell(table, FormatDate(item.ExpiryDate));
                        }
                    });

                    if (!string.IsNullOrWhiteSpace(remarks))
                    {
                        column.Item().PaddingTop(14).Text("Remarks").Bold();
                        column.Item()
                            .PaddingTop(4)
                            .MinHeight(60)
                            .Border(0.5f)
                            .Padding(8)
                            .Text(remarks);
                    }
                });

                page.Footer().AlignCenter().Text(text =>
                {
                    text.Span("Page ");
                    text.CurrentPageNumber();
                    text.Span(" of ");
                    text.TotalPages();
                });
            });
        }).GeneratePdf();
    }

    private static void AddInfoRow(
        TableDescriptor table,
        string label1,
        string value1,
        string label2,
        string value2)
    {
        InfoLabel(table, label1);
        InfoValue(table, value1);
        InfoLabel(table, label2);
        InfoValue(table, value2);
    }

    private static void InfoLabel(TableDescriptor table, string value) =>
        table.Cell()
            .Background(Colors.Grey.Lighten3)
            .Border(0.5f)
            .Padding(4)
            .Text(value)
            .Bold();

    private static void InfoValue(TableDescriptor table, string value) =>
        table.Cell()
            .Border(0.5f)
            .Padding(4)
            .Text(value);

    private static void HeaderCell(TableCellDescriptor table, string value) =>
        table.Cell()
            .Background(Colors.Grey.Lighten2)
            .Border(0.5f)
            .Padding(4)
            .AlignCenter()
            .AlignMiddle()
            .Text(value)
            .Bold();

    private static void BodyCell(
        TableCellDescriptor table,
        string value,
        bool centered = false)
    {
        var cell = table.Cell()
            .Border(0.5f)
            .Padding(4)
            .AlignMiddle();

        if (centered)
            cell = cell.AlignCenter();

        cell.Text(value);
    }

    private static string FormatCategory(EquipmentCategory category) =>
        category == EquipmentCategory.RadioAndNavigation
            ? "Radio & Navigation"
            : category.ToString();

    private static string FormatDate(DateOnly? value) =>
        value?.ToString("dd MMM yyyy") ?? string.Empty;
}
