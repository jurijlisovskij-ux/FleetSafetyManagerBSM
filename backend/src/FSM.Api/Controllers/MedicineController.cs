using ClosedXML.Excel;
using FSM.Api.Authorization;
using FSM.Domain.Entities;
using FSM.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace FSM.Api.Controllers;

public sealed record MedicineConfigurationRequest(bool CategoryAEnabled, bool CategoryBEnabled, bool DangerousGoodsEnabled, int WarningDays);
public sealed record SaveMedicineItemRequest(string ItemName, string? FormStrength, decimal RequiredQuantity, decimal ActualQuantity,
    string Unit, string? BatchNumber, string? ExpiryDate, string? StorageLocation, string? MedicalCategory,
    bool IsDangerousGoods, bool ReplacementRequired, string? Remarks, bool IsActive, bool IsStandardTemplateItem);

[ApiController]
[Authorize]
[Route("api/vessels/{vesselId:guid}/medicine")]
public sealed class MedicineController(FsmDbContext db, VesselAccessService vesselAccess) : ControllerBase
{
    private static readonly string[] ImportColumns = ["English item name", "Form / strength", "Required quantity", "Actual quantity", "Unit", "Batch number", "Expiry date", "Storage location", "Category A / B", "Dangerous Goods", "Replacement required", "Remarks"];

    [HttpGet]
    public async Task<IActionResult> List(Guid vesselId, CancellationToken ct)
    {
        var denied = await CheckAccess(vesselId, ct); if (denied is not null) return denied;
        var config = await GetConfiguration(vesselId, ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var items = await db.MedicineItems.AsNoTracking().Where(x => x.VesselId == vesselId)
            .OrderBy(x => x.IsDangerousGoods).ThenBy(x => x.ItemName).ToListAsync(ct);
        return Ok(new { configuration = config, items = items.Select(x => Response(x, today, config.WarningDays)) });
    }

    [HttpPut("configuration")]
    public async Task<IActionResult> Configure(Guid vesselId, MedicineConfigurationRequest request, CancellationToken ct)
    {
        var denied = await CheckAccess(vesselId, ct); if (denied is not null) return denied;
        if (request.WarningDays is < 0 or > 730) return BadRequest(new { message = "Warning days must be between 0 and 730." });
        var config = await db.VesselMedicineConfigurations.SingleOrDefaultAsync(x => x.VesselId == vesselId, ct);
        if (config is null) { config = new VesselMedicineConfiguration { VesselId = vesselId }; db.Add(config); }
        config.CategoryAEnabled = request.CategoryAEnabled; config.CategoryBEnabled = request.CategoryBEnabled;
        config.DangerousGoodsEnabled = request.DangerousGoodsEnabled; config.WarningDays = request.WarningDays;
        config.LastModifiedUtc = DateTime.UtcNow; await db.SaveChangesAsync(ct); return Ok(config);
    }

    [HttpPost("items")]
    public async Task<IActionResult> Create(Guid vesselId, SaveMedicineItemRequest request, CancellationToken ct)
    {
        var denied = await CheckAccess(vesselId, ct); if (denied is not null) return denied;
        var error = Validate(request); if (error is not null) return BadRequest(new { message = error });
        if (await Duplicate(vesselId, request, null, ct)) return Conflict(new { message = "This medicine item already exists for the vessel and list." });
        var item = new MedicineItem { Id = Guid.NewGuid(), VesselId = vesselId, ItemName = request.ItemName.Trim(), Unit = request.Unit.Trim() };
        Apply(item, request); db.Add(item); await db.SaveChangesAsync(ct);
        return Created($"api/vessels/{vesselId}/medicine/items/{item.Id}", Response(item, DateOnly.FromDateTime(DateTime.UtcNow), 60));
    }

    [HttpPut("items/{itemId:guid}")]
    public async Task<IActionResult> Update(Guid vesselId, Guid itemId, SaveMedicineItemRequest request, CancellationToken ct)
    {
        var denied = await CheckAccess(vesselId, ct); if (denied is not null) return denied;
        var error = Validate(request); if (error is not null) return BadRequest(new { message = error });
        var item = await db.MedicineItems.SingleOrDefaultAsync(x => x.Id == itemId && x.VesselId == vesselId, ct); if (item is null) return NotFound();
        if (await Duplicate(vesselId, request, itemId, ct)) return Conflict(new { message = "This medicine item already exists for the vessel and list." });
        Apply(item, request); await db.SaveChangesAsync(ct); var config = await GetConfiguration(vesselId, ct);
        return Ok(Response(item, DateOnly.FromDateTime(DateTime.UtcNow), config.WarningDays));
    }

    [HttpDelete("items/{itemId:guid}")]
    public async Task<IActionResult> Remove(Guid vesselId, Guid itemId, CancellationToken ct)
    {
        var denied = await CheckAccess(vesselId, ct); if (denied is not null) return denied;
        var item = await db.MedicineItems.SingleOrDefaultAsync(x => x.Id == itemId && x.VesselId == vesselId, ct); if (item is null) return NotFound();
        if (item.IsStandardTemplateItem) item.IsActive = false; else item.IsDeleted = true;
        item.LastModifiedUtc = DateTime.UtcNow; await db.SaveChangesAsync(ct); return NoContent();
    }

    [HttpGet("export")]
    public async Task<IActionResult> Export(Guid vesselId, CancellationToken ct) => await ExportWorkbook(vesselId, false, ct);

    [HttpGet("replenishment")]
    public async Task<IActionResult> Replenishment(Guid vesselId, CancellationToken ct) => await ExportWorkbook(vesselId, true, ct);

    [HttpPost("import")]
    [RequestSizeLimit(10_000_000)]
    public async Task<IActionResult> Import(Guid vesselId, IFormFile file, [FromQuery] bool apply = false, [FromQuery] bool overwriteActual = false, CancellationToken ct = default)
    {
        var denied = await CheckAccess(vesselId, ct); if (denied is not null) return denied;
        if (file.Length == 0) return BadRequest(new { message = "Select a non-empty Excel or CSV file." });
        List<Dictionary<string, string>> rows;
        try { rows = ReadRows(file); } catch (Exception ex) { return BadRequest(new { message = $"Template could not be read: {ex.Message}" }); }
        var errors = new List<string>(); var parsed = new List<SaveMedicineItemRequest>(); var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var request = ParseRow(row, errors, index + 2); if (request is null) continue;
            var identity = $"{request.ItemName.Trim()}|{request.FormStrength?.Trim()}|{request.IsDangerousGoods}";
            if (!identities.Add(identity)) errors.Add($"Row {index + 2}: duplicate item in import file."); else parsed.Add(request);
        }
        if (errors.Count > 0) return BadRequest(new { message = "Template validation failed.", errors });
        var existing = await db.MedicineItems.Where(x => x.VesselId == vesselId).ToListAsync(ct);
        var preview = parsed.Select(x => new { item = x.ItemName, action = existing.Any(e => Identity(e, x)) ? "Update" : "Add", actualQuantityWillChange = overwriteActual && existing.Any(e => Identity(e, x)) }).ToList();
        if (!apply) return Ok(new { valid = true, rowCount = parsed.Count, preview, requiresActualQuantityConfirmation = preview.Any(x => x.actualQuantityWillChange) });
        foreach (var request in parsed)
        {
            var item = existing.SingleOrDefault(x => Identity(x, request));
            if (item is null) { item = new MedicineItem { Id = Guid.NewGuid(), VesselId = vesselId, ItemName = request.ItemName.Trim(), Unit = request.Unit.Trim(), IsStandardTemplateItem = true }; db.Add(item); }
            var actual = item.ActualQuantity; var batch = item.BatchNumber; var expiry = item.ExpiryDate;
            Apply(item, request); item.IsStandardTemplateItem = true;
            if (!overwriteActual) { item.ActualQuantity = actual; item.BatchNumber = batch; item.ExpiryDate = expiry; }
        }
        await db.SaveChangesAsync(ct); return Ok(new { imported = parsed.Count, overwriteActual });
    }

    private async Task<IActionResult> ExportWorkbook(Guid vesselId, bool actionOnly, CancellationToken ct)
    {
        var denied = await CheckAccess(vesselId, ct); if (denied is not null) return denied;
        var vessel = await db.Vessels.AsNoTracking().SingleAsync(x => x.Id == vesselId, ct); var config = await GetConfiguration(vesselId, ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow); var items = await db.MedicineItems.AsNoTracking().Where(x => x.VesselId == vesselId).OrderBy(x => x.ItemName).ToListAsync(ct);
        if (actionOnly) items = items.Where(x => Status(x, today, config.WarningDays) is "Missing" or "Soon" or "Overdue / Expired" || x.ReplacementRequired).ToList();
        using var workbook = new XLWorkbook(); var sheet = workbook.AddWorksheet(actionOnly ? "Replenishment" : "Medicine Register");
        sheet.Cell(1, 1).Value = vessel.VesselName; sheet.Cell(1, 2).Value = $"IMO {vessel.ImoNumber}"; sheet.Cell(1, 3).Value = DateTime.UtcNow.ToString("yyyy-MM-dd 'UTC'");
        var headers = actionOnly ? new[] { "Item", "Form / strength", "Required", "Actual", "Missing quantity", "Expiry date", "Status", "Remarks" } : ImportColumns;
        for (var c = 0; c < headers.Length; c++) sheet.Cell(3, c + 1).Value = headers[c];
        for (var r = 0; r < items.Count; r++)
        {
            var x = items[r]; var values = actionOnly
                ? new object?[] { x.ItemName, x.FormStrength, x.RequiredQuantity, x.ActualQuantity, Math.Max(0, x.RequiredQuantity - x.ActualQuantity), x.ExpiryDate?.ToString("yyyy-MM-dd"), Status(x, today, config.WarningDays), x.Remarks }
                : new object?[] { x.ItemName, x.FormStrength, x.RequiredQuantity, x.ActualQuantity, x.Unit, x.BatchNumber, x.ExpiryDate?.ToString("yyyy-MM-dd"), x.StorageLocation, x.MedicalCategory, x.IsDangerousGoods, x.ReplacementRequired, x.Remarks };
            for (var c = 0; c < values.Length; c++) sheet.Cell(r + 4, c + 1).Value = XLCellValue.FromObject(values[c]);
        }
        sheet.Range(3, 1, 3, headers.Length).Style.Font.Bold = true; sheet.Columns().AdjustToContents(); using var stream = new MemoryStream(); workbook.SaveAs(stream);
        return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{(actionOnly ? "Medicine-Replenishment" : "Medicine-Register")}-{vessel.VesselName}-{DateTime.UtcNow:yyyyMMdd}.xlsx");
    }

    private async Task<IActionResult?> CheckAccess(Guid vesselId, CancellationToken ct)
    {
        if (!await db.Vessels.AsNoTracking().AnyAsync(x => x.Id == vesselId, ct)) return NotFound();
        return await vesselAccess.CanAccessVesselAsync(User, vesselId, ct) ? null : Forbid();
    }
    private async Task<VesselMedicineConfiguration> GetConfiguration(Guid vesselId, CancellationToken ct) =>
        await db.VesselMedicineConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.VesselId == vesselId, ct) ?? new VesselMedicineConfiguration { VesselId = vesselId };
    private async Task<bool> Duplicate(Guid vesselId, SaveMedicineItemRequest r, Guid? except, CancellationToken ct) =>
        await db.MedicineItems.AnyAsync(x => x.VesselId == vesselId && x.Id != except && x.ItemName.ToLower() == r.ItemName.Trim().ToLower() && x.FormStrength == Clean(r.FormStrength) && x.IsDangerousGoods == r.IsDangerousGoods, ct);
    private static bool Identity(MedicineItem x, SaveMedicineItemRequest r) => x.ItemName.Equals(r.ItemName.Trim(), StringComparison.OrdinalIgnoreCase) && string.Equals(x.FormStrength, Clean(r.FormStrength), StringComparison.OrdinalIgnoreCase) && x.IsDangerousGoods == r.IsDangerousGoods;
    private static string? Validate(SaveMedicineItemRequest r) => string.IsNullOrWhiteSpace(r.ItemName) ? "English item name is required." : string.IsNullOrWhiteSpace(r.Unit) ? "Unit is required." : r.RequiredQuantity < 0 || r.ActualQuantity < 0 ? "Quantities cannot be negative." : r.MedicalCategory is not null && r.MedicalCategory is not ("A" or "B") ? "Category must be A or B." : null;
    private static void Apply(MedicineItem x, SaveMedicineItemRequest r) { x.ItemName = r.ItemName.Trim(); x.FormStrength = Clean(r.FormStrength); x.RequiredQuantity = r.RequiredQuantity; x.ActualQuantity = r.ActualQuantity; x.Unit = r.Unit.Trim(); x.BatchNumber = Clean(r.BatchNumber); x.ExpiryDate = DateOnly.TryParseExact(r.ExpiryDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null; x.StorageLocation = Clean(r.StorageLocation); x.MedicalCategory = Clean(r.MedicalCategory)?.ToUpperInvariant(); x.IsDangerousGoods = r.IsDangerousGoods; x.ReplacementRequired = r.ReplacementRequired; x.Remarks = Clean(r.Remarks); x.IsActive = r.IsActive; x.IsStandardTemplateItem = r.IsStandardTemplateItem; x.LastModifiedUtc = DateTime.UtcNow; }
    private static string Status(MedicineItem x, DateOnly today, int warningDays) => !x.IsActive ? "Inactive" : x.ActualQuantity < x.RequiredQuantity ? "Missing" : x.ExpiryDate < today ? "Overdue / Expired" : x.ExpiryDate <= today.AddDays(warningDays) ? "Soon" : "OK";
    private static object Response(MedicineItem x, DateOnly today, int warningDays) => new { x.Id, x.VesselId, x.ItemName, x.FormStrength, x.RequiredQuantity, x.ActualQuantity, x.Unit, x.BatchNumber, x.ExpiryDate, x.StorageLocation, x.MedicalCategory, x.IsDangerousGoods, x.ReplacementRequired, x.Remarks, x.IsActive, x.IsStandardTemplateItem, Status = Status(x, today, warningDays) };
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static List<Dictionary<string, string>> ReadRows(IFormFile file)
    {
        using var stream = file.OpenReadStream(); var rows = new List<Dictionary<string, string>>();
        if (Path.GetExtension(file.FileName).Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            using var reader = new StreamReader(stream); var lines = new List<string>(); while (reader.ReadLine() is { } line) lines.Add(line);
            if (lines.Count == 0) return rows; var headers = lines[0].Split(',').Select(x => x.Trim().Trim('"')).ToArray(); ValidateHeaders(headers);
            foreach (var line in lines.Skip(1).Where(x => !string.IsNullOrWhiteSpace(x))) { var cells = line.Split(',').Select(x => x.Trim().Trim('"')).ToArray(); rows.Add(headers.Select((h, i) => (h, v: i < cells.Length ? cells[i] : "")).ToDictionary(x => x.h, x => x.v, StringComparer.OrdinalIgnoreCase)); }
        }
        else
        {
            using var book = new XLWorkbook(stream); var sheet = book.Worksheet(1); var headers = sheet.Row(1).CellsUsed().Select(x => x.GetString().Trim()).ToArray(); ValidateHeaders(headers);
            foreach (var row in sheet.RowsUsed().Skip(1)) rows.Add(headers.Select((h, i) => (h, v: row.Cell(i + 1).GetFormattedString().Trim())).ToDictionary(x => x.h, x => x.v, StringComparer.OrdinalIgnoreCase));
        }
        return rows;
    }
    private static void ValidateHeaders(string[] headers) { var missing = ImportColumns.Where(x => !headers.Contains(x, StringComparer.OrdinalIgnoreCase)).ToArray(); if (missing.Length > 0) throw new InvalidDataException($"Missing required columns: {string.Join(", ", missing)}"); }
    private static SaveMedicineItemRequest? ParseRow(Dictionary<string, string> r, List<string> errors, int row)
    {
        decimal ParseDecimal(string column) { if (decimal.TryParse(r[column], NumberStyles.Number, CultureInfo.InvariantCulture, out var n)) return n; errors.Add($"Row {row}: {column} must be a number."); return -1; }
        bool ParseBool(string column) => r[column].Equals("true", StringComparison.OrdinalIgnoreCase) || r[column].Equals("yes", StringComparison.OrdinalIgnoreCase) || r[column] == "1";
        var request = new SaveMedicineItemRequest(r[ImportColumns[0]], Clean(r[ImportColumns[1]]), ParseDecimal(ImportColumns[2]), ParseDecimal(ImportColumns[3]), r[ImportColumns[4]], Clean(r[ImportColumns[5]]), Clean(r[ImportColumns[6]]), Clean(r[ImportColumns[7]]), Clean(r[ImportColumns[8]])?.ToUpperInvariant(), ParseBool(ImportColumns[9]), ParseBool(ImportColumns[10]), Clean(r[ImportColumns[11]]), true, true);
        var error = Validate(request); if (error is not null) errors.Add($"Row {row}: {error}"); if (request.ExpiryDate is not null && !DateOnly.TryParseExact(request.ExpiryDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) errors.Add($"Row {row}: Expiry date must use yyyy-MM-dd."); return request;
    }
}
