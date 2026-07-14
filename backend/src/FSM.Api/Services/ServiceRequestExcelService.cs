using ClosedXML.Excel;
using FSM.Domain.Entities;
using FSM.Domain.Enums;

namespace FSM.Api.Services;

public sealed class ServiceRequestExcelService
{
    public byte[] Generate(
        Vessel vessel,
        IReadOnlyList<Equipment> equipment,
        string port,
        DateOnly? eta,
        DateOnly? etd,
        string? remarks)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Service Request");

        sheet.Cell("A1").Value = "SERVICE REQUEST";
        sheet.Range("A1:H1").Merge();
        sheet.Cell("A1").Style.Font.Bold = true;
        sheet.Cell("A1").Style.Font.FontSize = 16;
        sheet.Cell("A1").Style.Alignment.Horizontal =
            XLAlignmentHorizontalValues.Center;

        var row = 3;

        WritePair(sheet, row++, "Vessel", vessel.VesselName);
        WritePair(sheet, row++, "IMO Number", vessel.ImoNumber);
        WritePair(sheet, row++, "Flag", vessel.Flag ?? string.Empty);
        WritePair(
            sheet,
            row++,
            "Company",
            vessel.CompanyName ?? string.Empty);
        WritePair(
            sheet,
            row++,
            "Accounting Email",
            vessel.AccountingEmail ?? string.Empty);
        WritePair(sheet, row++, "Port", port);
        WritePair(sheet, row++, "ETA", FormatDate(eta));
        WritePair(sheet, row++, "ETD", FormatDate(etd));
        WritePair(
            sheet,
            row++,
            "Request Date",
            DateOnly.FromDateTime(DateTime.UtcNow)
                .ToString(
                    "dd MMM yyyy",
                    System.Globalization.CultureInfo.InvariantCulture));

        row++;

        var headers = new[]
        {
            "Category",
            "Equipment",
            "Maker",
            "Model",
            "Serial Number",
            "Quantity",
            "Inspection By",
            "Expiry Date"
        };

        for (var column = 1; column <= headers.Length; column++)
        {
            sheet.Cell(row, column).Value = headers[column - 1];
            sheet.Cell(row, column).Style.Font.Bold = true;
            sheet.Cell(row, column).Style.Fill.BackgroundColor =
                XLColor.LightGray;
            sheet.Cell(row, column).Style.Border.BottomBorder =
                XLBorderStyleValues.Thin;
        }

        var tableHeaderRow = row;
        row++;

        foreach (var item in equipment)
        {
            sheet.Cell(row, 1).Value =
                item.Category == EquipmentCategory.RadioAndNavigation
                    ? "Radio & Navigation"
                    : item.Category.ToString();

            sheet.Cell(row, 2).Value = item.EquipmentName;
            sheet.Cell(row, 3).Value = item.Maker ?? string.Empty;
            sheet.Cell(row, 4).Value = item.Model ?? string.Empty;
            sheet.Cell(row, 5).Value =
                item.SerialNumber ?? string.Empty;
            sheet.Cell(row, 6).Value = item.Quantity;
            sheet.Cell(row, 7).Value =
                item.InspectionBy == InspectionBy.ServiceCompany
                    ? "Service Company"
                    : "Crew";
            sheet.Cell(row, 8).Value =
                FormatDate(item.ExpiryDate);

            row++;
        }

        if (!string.IsNullOrWhiteSpace(remarks))
        {
            row++;
            sheet.Cell(row, 1).Value = "Remarks";
            sheet.Cell(row, 1).Style.Font.Bold = true;
            row++;

            sheet.Cell(row, 1).Value = remarks;
            sheet.Range(row, 1, row + 2, 8).Merge();
            sheet.Range(row, 1, row + 2, 8)
                .Style.Alignment.WrapText = true;
            sheet.Range(row, 1, row + 2, 8)
                .Style.Alignment.Vertical =
                    XLAlignmentVerticalValues.Top;
            sheet.Range(row, 1, row + 2, 8)
                .Style.Border.OutsideBorder =
                    XLBorderStyleValues.Thin;
        }

        sheet.Columns(1, 8).AdjustToContents();
        sheet.Column(2).Width =
            Math.Max(sheet.Column(2).Width, 25);
        sheet.Column(3).Width =
            Math.Max(sheet.Column(3).Width, 18);
        sheet.Column(4).Width =
            Math.Max(sheet.Column(4).Width, 18);
        sheet.Column(5).Width =
            Math.Max(sheet.Column(5).Width, 18);
        sheet.Column(7).Width =
            Math.Max(sheet.Column(7).Width, 18);
        sheet.Column(8).Width =
            Math.Max(sheet.Column(8).Width, 14);

        sheet.SheetView.FreezeRows(tableHeaderRow);
        sheet.PageSetup.PageOrientation =
            XLPageOrientation.Landscape;
        sheet.PageSetup.FitToPages(1, 0);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static void WritePair(
        IXLWorksheet sheet,
        int row,
        string label,
        string value)
    {
        sheet.Cell(row, 1).Value = label;
        sheet.Cell(row, 1).Style.Font.Bold = true;
        sheet.Cell(row, 2).Value = value;
        sheet.Range(row, 2, row, 4).Merge();
    }

    private static string FormatDate(DateOnly? value) =>
        value?.ToString(
            "dd MMM yyyy",
            System.Globalization.CultureInfo.InvariantCulture)
        ?? string.Empty;
}
