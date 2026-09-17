using HALProcessRecord.Data;
using HALProcessRecord.Models;
using HALProcessRecord.Services;
using HALProcessRecord.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HALProcessRecord.Controllers;

public class ProcessRecordController : Controller
{
    private readonly AppDbContext _db;
    private readonly IExcelGeneratorService _excelGenerator;
    private readonly OpcUaService _opcUaService;

    public ProcessRecordController(
        AppDbContext db,
        IExcelGeneratorService excelGenerator,
        OpcUaService opcUaService)
    {
        _db = db;
        _excelGenerator = excelGenerator;
        _opcUaService = opcUaService;
    }

    [HttpGet]
    public async Task<IActionResult> Create()
    {
        var model = CreateDefaultModel();
        model.OpcUa = await _opcUaService.ReadStatusAsync(model.Furnace);
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Generate(ProcessRecordViewModel model)
    {
        NormalizeRows(model);

        if (!ModelState.IsValid)
            return View("Create", model);

        try
        {
            // Read the PLC values again immediately before generating the report.
            model.OpcUa = await _opcUaService.ReadStatusAsync(model.Furnace);

            var entity = MapToEntity(model);
            _db.ProcessRecords.Add(entity);
            await _db.SaveChangesAsync();

            var bytes = _excelGenerator.Generate(model);
            var fileName = $"GE_Process_Record_{model.BatchNo}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";

            return File(bytes,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                fileName);
        }
        catch (Exception ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return View("Create", model);
        }
    }

    [HttpGet]
    public async Task<IActionResult> OpcUaStatus(string? furnace)
    {
        var status = await _opcUaService.ReadStatusAsync(furnace);
        return Json(status);
    }

    [HttpGet]
    public async Task<IActionResult> History()
    {
        var records = await _db.ProcessRecords
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .Take(100)
            .ToListAsync();

        return View(records);
    }

    [HttpGet]
    public async Task<IActionResult> ViewReport(int id)
    {
        var entity = await _db.ProcessRecords
            .AsNoTracking()
            .Include(x => x.Operations)
            .SingleOrDefaultAsync(x => x.Id == id);

        if (entity is null)
            return NotFound();

        return View(entity);
    }

    [HttpGet]
    public async Task<IActionResult> DownloadReport(int id)
    {
        var report = await GetReportAsync(id);

        if (report is null)
            return NotFound();

        return File(report.Value.Bytes, report.Value.ContentType, report.Value.FileName);
    }

    private async Task<(byte[] Bytes, string ContentType, string FileName)?> GetReportAsync(int id)
    {
        var entity = await _db.ProcessRecords
            .AsNoTracking()
            .Include(x => x.Operations)
            .SingleOrDefaultAsync(x => x.Id == id);

        if (entity is null)
            return null;

        var model = MapToViewModel(entity);
        var bytes = _excelGenerator.Generate(model);
        var fileName = $"GE_Process_Record_{entity.BatchNo}_{entity.CreatedAt:yyyyMMdd_HHmmss}.xlsx";
        const string contentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

        return (bytes, contentType, fileName);
    }

    private static ProcessRecordViewModel CreateDefaultModel()
    {
        return new ProcessRecordViewModel
        {
            Date = DateTime.Today,
            LoadingDate = DateTime.Today,
            LoadingTime = new TimeSpan(14, 0, 0),
            SoakingMin = new TimeSpan(1, 0, 0),
            SoakingMax = new TimeSpan(3, 0, 0),
            ForgingTemperature = 1000,
            TemperatureTolerance = 14,
            SoakingStartTemperature = 986,
            Operation = "Upset & Pierce",
            Equipment = "1500 Ton Press",
            Operations = Enumerable.Range(1, 13)
                .Select(i => new OperationRowViewModel { SlNo = i })
                .ToList()
        };
    }

    private static void NormalizeRows(ProcessRecordViewModel model)
    {
        model.Operations ??= new List<OperationRowViewModel>();

        for (var i = 0; i < model.Operations.Count; i++)
            model.Operations[i].SlNo = i + 1;
    }

    private static ProcessRecordEntity MapToEntity(ProcessRecordViewModel model)
    {
        return new ProcessRecordEntity
        {
            Date = model.Date,
            PartNo = model.PartNo.Trim(),
            Qty = model.Qty,
            BatchNo = model.BatchNo.Trim(),
            LoadingDate = model.LoadingDate,
            LoadingTime = model.LoadingTime,
            Furnace = model.Furnace.Trim(),
            ForgingTemperature = model.ForgingTemperature,
            TemperatureTolerance = model.TemperatureTolerance,
            SoakingStartTemperature = model.SoakingStartTemperature,
            SoakingMin = model.SoakingMin,
            SoakingMax = model.SoakingMax,
            SoakingStartTime = model.SoakingStartTime,
            MinimumSoakingComplete = model.MinimumSoakingComplete,
            OperationEndBefore = model.OperationEndBefore,
            ActualOperationStartTime = model.ActualOperationStartTime,
            ActualOperationEndTime = model.ActualOperationEndTime,
            OperatorName = model.OperatorName.Trim(),
            Operation = model.Operation,
            Equipment = model.Equipment,
            RingRollingStageNo = model.RingRollingStageNo.Trim(),
            MandrelDiameter = model.MandrelDiameter,
            OtherRemarks = model.OtherRemarks.Trim(),
            PreparedBy = model.PreparedBy.Trim(),
            VerifiedBy = model.VerifiedBy.Trim(),
            Operations = model.Operations.Select(x =>
            {
                var transfer = TimeSpan.FromMinutes(x.TransferMinutes) + TimeSpan.FromSeconds(x.TransferSeconds);
                var total = TimeSpan.FromMinutes(x.TotalMinutes) + TimeSpan.FromSeconds(x.TotalSeconds);
                var op = total >= transfer ? total - transfer : TimeSpan.Zero;

                return new OperationRecordEntity
                {
                    SlNo = x.SlNo,
                    TransferMinutes = x.TransferMinutes,
                    TransferSeconds = x.TransferSeconds,
                    TotalMinutes = x.TotalMinutes,
                    TotalSeconds = x.TotalSeconds,
                    OperationMinutes = (int)op.TotalMinutes,
                    OperationSeconds = op.Seconds,
                    Observation = x.Observation.Trim()
                };
            }).ToList()
        };
    }

    private static ProcessRecordViewModel MapToViewModel(ProcessRecordEntity entity)
    {
        return new ProcessRecordViewModel
        {
            Date = entity.Date,
            PartNo = entity.PartNo,
            Qty = entity.Qty,
            BatchNo = entity.BatchNo,
            LoadingDate = entity.LoadingDate,
            LoadingTime = entity.LoadingTime,
            Furnace = entity.Furnace,
            ForgingTemperature = entity.ForgingTemperature,
            TemperatureTolerance = entity.TemperatureTolerance,
            SoakingStartTemperature = entity.SoakingStartTemperature,
            SoakingMin = entity.SoakingMin,
            SoakingMax = entity.SoakingMax,
            SoakingStartTime = entity.SoakingStartTime,
            MinimumSoakingComplete = entity.MinimumSoakingComplete,
            OperationEndBefore = entity.OperationEndBefore,
            ActualOperationStartTime = entity.ActualOperationStartTime,
            ActualOperationEndTime = entity.ActualOperationEndTime,
            OperatorName = entity.OperatorName,
            Operation = entity.Operation,
            Equipment = entity.Equipment,
            RingRollingStageNo = entity.RingRollingStageNo,
            MandrelDiameter = entity.MandrelDiameter,
            OtherRemarks = entity.OtherRemarks,
            PreparedBy = entity.PreparedBy,
            VerifiedBy = entity.VerifiedBy,
            Operations = entity.Operations
                .OrderBy(x => x.SlNo)
                .Select(x => new OperationRowViewModel
                {
                    SlNo = x.SlNo,
                    TransferMinutes = x.TransferMinutes,
                    TransferSeconds = x.TransferSeconds,
                    TotalMinutes = x.TotalMinutes,
                    TotalSeconds = x.TotalSeconds,
                    OperationMinutes = x.OperationMinutes,
                    OperationSeconds = x.OperationSeconds,
                    Observation = x.Observation
                })
                .ToList()
        };
    }
}
