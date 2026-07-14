using ClosedXML.Excel;
using FSM.Domain.Entities;
using FSM.Domain.Enums;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace FSM.Api.Services;

public sealed class InspectionListService
{
    public byte[] GenerateExcel(
        Vessel vessel,
        IReadOnlyList<Equipment> equipment)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Inspection List");

        sheet.Cell("A1").Value = "INSPECTION LIST";
        sheet.Range("A1:I1").Merge();
        sheet.Cell("A1").Style.Font.Bold = true;
        sheet.Cell("A1").Style.Font.FontSize = 16;
        sheet.Cell("A1").Style.Alignment.Horizontal =
            XLAlignmentHorizontalValues.Center;

        sheet.Cell("A3").Value = "Vessel";
        sheet.Cell("A3").Style.Font.Bold = true;
        sheet.Cell("B3").Value = vessel.VesselName;

        sheet.Cell("D3").Value = "IMO Number";
        sheet.Cell("D3").Style.Font.Bold = true;
        sheet.Cell("E3").Value = vessel.ImoNumber;

        sheet.Cell("G3").Value = "Date";
        sheet.Cell("G3").Style.Font.Bold = true;
        sheet.Cell("H3").Value =
            DateOnly.FromDateTime(DateTime.UtcNow)
                .ToString("dd MMM yyyy");

        var headerRow = 5;

        var headers = new[]
        {
            "Checked",
            "Category",
            "Equipment",
            "Maker / Model",
            "Serial Number",
            "Qty",
            "Current Expiry Date",
            "Inspection Date",
            "Remarks"
        };

        for (var column = 1; column <= headers.Length; column++)
        {
            sheet.Cell(headerRow, column).Value =
                headers[column - 1];

            sheet.Cell(headerRow, column).Style.Font.Bold = true;
            sheet.Cell(headerRow, column).Style.Fill.BackgroundColor =
                XLColor.LightGray;
            sheet.Cell(headerRow, column).Style.Border.OutsideBorder =
                XLBorderStyleValues.Thin;
            sheet.Cell(headerRow, column).Style.Alignment.Horizontal =
                XLAlignmentHorizontalValues.Center;
            sheet.Cell(headerRow, column).Style.Alignment.Vertical =
                XLAlignmentVerticalValues.Center;
        }

        var row = headerRow + 1;

        foreach (var item in equipment)
        {
            sheet.Cell(row, 1).Value = "☐";
            sheet.Cell(row, 2).Value = FormatCategory(item.Category);
            sheet.Cell(row, 3).Value = item.EquipmentName;
            sheet.Cell(row, 4).Value =
                string.Join(
                    " / ",
                    new[] { item.Maker, item.Model }
                        .Where(value =>
                            !string.IsNullOrWhiteSpace(value)));
            sheet.Cell(row, 5).Value =
                item.SerialNumber ?? string.Empty;
            sheet.Cell(row, 6).Value = item.Quantity;
            sheet.Cell(row, 7).Value =
                FormatDate(item.ExpiryDate);
            sheet.Cell(row, 8).Value = string.Empty;
            sheet.Cell(row, 9).Value = string.Empty;

            sheet.Range(row, 1, row, 9)
                .Style.Border.OutsideBorder =
                    XLBorderStyleValues.Thin;
            sheet.Range(row, 1, row, 9)
                .Style.Border.InsideBorder =
                    XLBorderStyleValues.Thin;
            sheet.Range(row, 1, row, 9)
                .Style.Alignment.Vertical =
                    XLAlignmentVerticalValues.Center;
            sheet.Range(row, 1, row, 9)
                .Style.Alignment.WrapText = true;

            sheet.Cell(row, 1).Style.Alignment.Horizontal =
                XLAlignmentHorizontalValues.Center;
            sheet.Cell(row, 6).Style.Alignment.Horizontal =
                XLAlignmentHorizontalValues.Center;

            sheet.Row(row).Height = 28;
            row++;
        }

        sheet.Column(1).Width = 9;
        sheet.Column(2).Width = 18;
        sheet.Column(3).Width = 25;
        sheet.Column(4).Width = 24;
        sheet.Column(5).Width = 18;
        sheet.Column(6).Width = 7;
        sheet.Column(7).Width = 18;
        sheet.Column(8).Width = 18;
        sheet.Column(9).Width = 32;

        sheet.SheetView.FreezeRows(headerRow);
        sheet.PageSetup.PageOrientation =
            XLPageOrientation.Landscape;
        sheet.PageSetup.PaperSize =
            XLPaperSize.A4Paper;
        sheet.PageSetup.FitToPages(1, 0);
        sheet.PageSetup.Margins.Top = 0.35;
        sheet.PageSetup.Margins.Bottom = 0.35;
        sheet.PageSetup.Margins.Left = 0.25;
        sheet.PageSetup.Margins.Right = 0.25;

        sheet.PageSetup.Footer.Center.AddText(
            "Page &P of &N");

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public byte[] GeneratePrintablePdf(
        Vessel vessel,
        IReadOnlyList<Equipment> equipment)
    {
        QuestPDF.Settings.License =
            LicenseType.Community;

        return Document.Create(document =>
        {
            document.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(20);
                page.DefaultTextStyle(style =>
                    style.FontSize(8));

                page.Header().Column(column =>
                {
                    column.Item()
                        .AlignCenter()
                        .Text("INSPECTION LIST")
                        .Bold()
                        .FontSize(15);

                    column.Item().PaddingTop(6).Row(row =>
                    {
                        row.RelativeItem().Text(text =>
                        {
                            text.Span("Vessel: ").Bold();
                            text.Span(vessel.VesselName);
                        });

                        row.RelativeItem().Text(text =>
                        {
                            text.Span("IMO: ").Bold();
                            text.Span(vessel.ImoNumber);
                        });

                        row.RelativeItem().AlignRight().Text(text =>
                        {
                            text.Span("Date: ").Bold();
                            text.Span(
                                DateOnly
                                    .FromDateTime(DateTime.UtcNow)
                                    .ToString("dd MMM yyyy"));
                        });
                    });
                });

                page.Content()
                    .PaddingTop(10)
                    .Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.ConstantColumn(32);
                            columns.ConstantColumn(72);
                            columns.RelativeColumn(1.4f);
                            columns.RelativeColumn(1.25f);
                            columns.ConstantColumn(82);
                            columns.ConstantColumn(32);
                            columns.ConstantColumn(82);
                            columns.ConstantColumn(82);
                            columns.RelativeColumn(1.6f);
                        });

                        table.Header(header =>
                        {
                            HeaderCell(header, "✓");
                            HeaderCell(header, "Category");
                            HeaderCell(header, "Equipment");
                            HeaderCell(header, "Maker / Model");
                            HeaderCell(header, "Serial No.");
                            HeaderCell(header, "Qty");
                            HeaderCell(header, "Expiry Date");
                            HeaderCell(header, "Inspection Date");
                            HeaderCell(header, "Remarks");
                        });

                        foreach (var item in equipment)
                        {
                            BodyCell(table, "□", true);
                            BodyCell(
                                table,
                                FormatCategory(item.Category));
                            BodyCell(
                                table,
                                item.EquipmentName);
                            BodyCell(
                                table,
                                string.Join(
                                    " / ",
                                    new[]
                                    {
                                        item.Maker,
                                        item.Model
                                    }
                                    .Where(value =>
                                        !string.IsNullOrWhiteSpace(
                                            value))));
                            BodyCell(
                                table,
                                item.SerialNumber ?? string.Empty);
                            BodyCell(
                                table,
                                item.Quantity.ToString(),
                                true);
                            BodyCell(
                                table,
                                FormatDate(item.ExpiryDate));
                            BodyCell(
                                table,
                                "________________");
                            BodyCell(
                                table,
                                string.Empty,
                                false,
                                34);
                        }
                    });

                page.Footer()
                    .AlignCenter()
                    .Text(text =>
                    {
                        text.Span("Page ");
                        text.CurrentPageNumber();
                        text.Span(" of ");
                        text.TotalPages();
                    });
            });
        }).GeneratePdf();
    }

    private static void HeaderCell(
        TableCellDescriptor table,
        string value)
    {
        table.Cell()
            .Background(Colors.Grey.Lighten2)
            .Border(0.5f)
            .Padding(4)
            .AlignCenter()
            .AlignMiddle()
            .Text(value)
            .Bold();
    }

    private static void BodyCell(
        TableDescriptor table,
        string value,
        bool centered = false,
        float minimumHeight = 26)
    {
        var cell = table.Cell()
            .MinHeight(minimumHeight)
            .Border(0.5f)
            .Padding(4)
            .AlignMiddle();

        if (centered)
            cell = cell.AlignCenter();

        cell.Text(value);
    }

    private static string FormatCategory(
        EquipmentCategory category) =>
        category == EquipmentCategory.RadioAndNavigation
            ? "Radio & Navigation"
            : category.ToString();

    private static string FormatDate(DateOnly? value) =>
        value?.ToString("dd MMM yyyy")
        ?? string.Empty;
}
