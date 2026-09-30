using HALProcessRecord.Data;
using HALProcessRecord.Models;
using HALProcessRecord.Services;
using HALProcessRecord.Utilities;
using HALProcessRecord.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;

namespace HALProcessRecord.Controllers;

public class ProcessRecordController : Controller
{
    private readonly AppDbContext _db;
    private readonly IExcelGeneratorService _excelGenerator;
    private readonly OpcUaService _opcUaService;
    private readonly TimingMonitorService _timingMonitorService;

    public ProcessRecordController(
        AppDbContext db,
        IExcelGeneratorService excelGenerator,
        OpcUaService opcUaService,
        TimingMonitorService timingMonitorService)
    {
        _db = db;
        _excelGenerator = excelGenerator;
        _opcUaService = opcUaService;
        _timingMonitorService = timingMonitorService;
    }

    [HttpGet]
    public async Task<IActionResult> Create()
    {
        var model = CreateDefaultModel();
        model.OpcUa = await _opcUaService.ReadStatusAsync(model.Furnace);
        model.RingRollingOpcUa = await _opcUaService.ReadRingRollingStatusAsync("MRB_3000");
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Generate(ProcessRecordViewModel model)
    {
        NormalizeEmptyTimeValues(model);
        NormalizeRows(model);

        if (!ModelState.IsValid)
            return View("Create", model);

        try
        {
            // Build report timings from persisted DB history. This captures every
            // start/stop cycle for the selected furnace + machine, not just one live read.
            await ApplyAutomaticTimingAsync(model, HttpContext.RequestAborted);

            var entity = MapToEntity(model);
            _db.ProcessRecords.Add(entity);
            await _db.SaveChangesAsync();

            var bytes = _excelGenerator.Generate(model);
            var fileName = $"GE_Process_Record_{model.BatchNo}_{IndiaTime.Now:yyyyMMdd_HHmmss}.xlsx";

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
    public async Task<IActionResult> TimingStatus(string furnace, string machine)
    {
        if (string.IsNullOrWhiteSpace(furnace) || string.IsNullOrWhiteSpace(machine))
            return BadRequest();

        var status = await _timingMonitorService.GetCurrentAsync(furnace, machine);
        return Json(status ?? new TimingStatusViewModel { Furnace = furnace, Machine = machine });
    }

    [HttpGet]
    public async Task<IActionResult> TimingHistory(
        string furnace,
        string machine,
        DateTime? loadingDate,
        TimeSpan? loadingTime,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(furnace) || string.IsNullOrWhiteSpace(machine))
            return BadRequest();

        var rows = await GetTimingHistoryAsync(
            furnace,
            machine,
            loadingDate?.Date ?? IndiaTime.Now.Date,
            loadingTime,
            cancellationToken);
        var machineRows = await GetMachineOperationHistoryAsync(
            machine,
            loadingDate?.Date ?? IndiaTime.Now.Date,
            loadingTime,
            cancellationToken);
        if (rows.Count == 0)
        {
            rows = machineRows;
        }
        else
        {
            var mergedRows = rows.ToList();
            mergedRows.AddRange(machineRows.Where(machineRow =>
                !rows.Any(furnaceRow => IsMatchingOperationCycle(furnaceRow, machineRow))));
            rows = mergedRows
                .OrderBy(x => x.AutoStartTime ?? DateTime.MaxValue)
                .Take(20)
                .ToList();
        }
        return Json(rows.Select((row, index) => new
        {
            slNo = index + 1,
            transferMinutes = row.TransferMinutes,
            transferSeconds = row.TransferSeconds,
            totalMinutes = row.TotalMinutes,
            totalSeconds = row.TotalSeconds,
            operationMinutes = row.OperationMinutes,
            operationSeconds = row.OperationSeconds,
            observation = row.Observation,
            furnaceDoorOpenTime = row.FurnaceDoorOpenTime,
            furnaceDoorCloseTime = row.FurnaceDoorCloseTime,
            autoStartTime = row.AutoStartTime,
            stopTime = row.StopTime,
            temperature = row.Temperature,
            highestTemperature = row.HighestTemperature
        }));
    }

    [HttpGet]
    public async Task<IActionResult> MachineOperationHistory(
        string machine,
        DateTime? loadingDate,
        TimeSpan? loadingTime,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(machine))
            return BadRequest();

        var rows = await GetMachineOperationHistoryAsync(
            machine,
            loadingDate?.Date ?? IndiaTime.Now.Date,
            loadingTime,
            cancellationToken);
        return Json(rows.Select((row, index) => new
        {
            slNo = index + 1,
            transferMinutes = row.TransferMinutes,
            transferSeconds = row.TransferSeconds,
            totalMinutes = row.TotalMinutes,
            totalSeconds = row.TotalSeconds,
            operationMinutes = row.OperationMinutes,
            operationSeconds = row.OperationSeconds,
            observation = row.Observation,
            furnaceDoorOpenTime = row.FurnaceDoorOpenTime,
            furnaceDoorCloseTime = row.FurnaceDoorCloseTime,
            autoStartTime = row.AutoStartTime,
            stopTime = row.StopTime,
            temperature = row.Temperature,
            highestTemperature = row.HighestTemperature
        }));
    }

    [HttpGet]
    public async Task<IActionResult> SignalHistory(string furnace, string machine, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(furnace) || string.IsNullOrWhiteSpace(machine))
            return BadRequest();

        var furnaceSamples = await _db.FurnaceData.AsNoTracking()
            .Where(x => x.Furnace == furnace)
            .OrderByDescending(x => x.CapturedAt)
            .Take(120)
            .ToListAsync(cancellationToken);
        var machineSamples = await _db.MachineData.AsNoTracking()
            .Where(x => x.Machine == machine)
            .OrderByDescending(x => x.CapturedAt)
            .Take(120)
            .ToListAsync(cancellationToken);

        return Json(new
        {
            furnace = BuildFurnaceSignalChanges(furnaceSamples),
            machine = BuildMachineSignalChanges(machineSamples)
        });
    }

    private static List<FurnaceSignalChangeViewModel> BuildFurnaceSignalChanges(
        IReadOnlyList<FurnaceDataEntity> samples)
    {
        var ordered = samples.OrderBy(x => x.CapturedAt).ToList();
        var changes = new List<FurnaceSignalChangeViewModel>();
        FurnaceDataEntity? previous = null;

        foreach (var sample in ordered)
        {
            if (previous is null || sample.DoorOpen != previous.DoorOpen ||
                sample.DoorClosed != previous.DoorClosed || sample.HalfDoor != previous.HalfDoor)
            {
                changes.Add(new FurnaceSignalChangeViewModel
                {
                    CapturedAt = sample.CapturedAt,
                    DoorOpen = sample.DoorOpen,
                    DoorClosed = sample.DoorClosed,
                    HalfDoor = sample.HalfDoor,
                    Change = previous is null ? "Initial sample" : "Door signal changed"
                });
            }

            previous = sample;
        }

        return changes.OrderByDescending(x => x.CapturedAt).Take(30).ToList();
    }

    private static List<MachineSignalChangeViewModel> BuildMachineSignalChanges(
        IReadOnlyList<MachineDataEntity> samples)
    {
        var ordered = samples.OrderBy(x => x.CapturedAt).ToList();
        var changes = new List<MachineSignalChangeViewModel>();
        MachineDataEntity? previous = null;

        foreach (var sample in ordered)
        {
            var forceThresholdChanged = previous is null ||
                (sample.MaterialForce >= 20m) != (previous.MaterialForce >= 20m);
            var signalChanged = previous is null ||
                sample.AutoStartLed != previous.AutoStartLed ||
                sample.AutoStartPushButton != previous.AutoStartPushButton ||
                sample.AutoStopLed != previous.AutoStopLed ||
                sample.AutoStopPushButton != previous.AutoStopPushButton ||
                forceThresholdChanged;

            if (signalChanged)
            {
                changes.Add(new MachineSignalChangeViewModel
                {
                    CapturedAt = sample.CapturedAt,
                    AutoStartLed = sample.AutoStartLed,
                    AutoStartPushButton = sample.AutoStartPushButton,
                    AutoStopLed = sample.AutoStopLed,
                    AutoStopPushButton = sample.AutoStopPushButton,
                    MaterialForce = sample.MaterialForce,
                    MaterialTemperature = sample.MaterialTemperature,
                    Change = previous is null ? "Initial sample" : "Machine signal changed"
                });
            }

            previous = sample;
        }

        return changes.OrderByDescending(x => x.CapturedAt).Take(30).ToList();
    }

    [HttpGet]
    public async Task<IActionResult> RingRollingOpcUaStatus(string? machine)
    {
        var status = await _opcUaService.ReadRingRollingStatusAsync(machine);
        return Json(status);
    }

    private async Task<List<OperationRowViewModel>> GetTimingHistoryAsync(
        string furnace,
        string machine,
        DateTime loadingDate,
        TimeSpan? loadingTime,
        CancellationToken cancellationToken)
    {
        var model = CreateDefaultModel();
        model.Furnace = furnace;
        model.Machine = machine;
        model.LoadingDate = loadingDate.Date;
        model.LoadingTime = loadingTime ?? TimeSpan.Zero;
        await ApplyAutomaticTimingAsync(model, cancellationToken);

        return model.Operations
            .Where(x => x.TransferMinutes > 0 || x.TransferSeconds > 0 || x.TotalMinutes > 0 || x.TotalSeconds > 0)
            .OrderBy(x => x.SlNo)
            .Take(20)
            .ToList();
    }

    private static bool IsMatchingOperationCycle(
        OperationRowViewModel furnaceRow,
        OperationRowViewModel machineRow)
    {
        if (furnaceRow.AutoStartTime is not DateTime furnaceStart ||
            machineRow.AutoStartTime is not DateTime machineStart ||
            Math.Abs((furnaceStart - machineStart).TotalSeconds) > 5)
            return false;

        if (furnaceRow.StopTime is DateTime furnaceStop &&
            machineRow.StopTime is DateTime machineStop &&
            Math.Abs((furnaceStop - machineStop).TotalSeconds) > 5)
            return false;

        return true;
    }

    private async Task<List<OperationRowViewModel>> GetMachineOperationHistoryAsync(
        string machine,
        DateTime loadingDate,
        TimeSpan? loadingTime,
        CancellationToken cancellationToken)
    {
        var fromIndiaTime = loadingDate.Date + (loadingTime ?? TimeSpan.Zero);
        var dayEndIndiaTime = loadingDate.Date.AddDays(1);
        var nowIndiaTime = IndiaTime.Now;
        var toIndiaTime = dayEndIndiaTime;

        var samples = await _db.MachineData
            .AsNoTracking()
            .Where(x => x.Machine == machine)
            .OrderBy(x => x.CapturedAt)
            .ToListAsync(cancellationToken);

        samples = samples
            .Where(x => x.CapturedAt >= fromIndiaTime.AddDays(-1) &&
                        x.CapturedAt <= toIndiaTime)
            .ToList();

        var starts = new List<DateTime>();
        var stops = new List<DateTime>();
        bool? previousStart = null;
        bool? previousStop = null;

        foreach (var sample in samples)
        {
            var startSignal = sample.AutoStartLed || sample.AutoStartPushButton;
            var stopSignal = sample.AutoStopLed || sample.AutoStopPushButton;

            if (startSignal && previousStart != true)
            {
                var startAt = sample.CapturedAt;
                if (startAt >= fromIndiaTime && startAt < toIndiaTime)
                    starts.Add(startAt);
            }

            if (stopSignal && previousStop != true)
            {
                var stopAt = sample.CapturedAt;
                if (stopAt >= fromIndiaTime && stopAt <= toIndiaTime)
                    stops.Add(stopAt);
            }

            previousStart = startSignal;
            previousStop = stopSignal;
        }

        var rows = new List<OperationRowViewModel>();
        var pendingStarts = new Queue<DateTime>(starts);
        foreach (var stopAt in stops)
        {
            if (pendingStarts.Count == 0 || pendingStarts.Peek() >= stopAt)
                continue;

            var startAt = pendingStarts.Dequeue();
            AddMachineOperationRow(rows, startAt, stopAt);
        }

        if (loadingDate.Date == nowIndiaTime.Date)
        {
            foreach (var startAt in pendingStarts)
                AddMachineOperationRow(rows, startAt, nowIndiaTime, true);
        }

        return rows
            .OrderBy(x => x.AutoStartTime)
            .Take(20)
            .Select((row, index) =>
            {
                row.SlNo = index + 1;
                return row;
            })
            .ToList();
    }

    private static void AddMachineOperationRow(
        List<OperationRowViewModel> rows,
        DateTime startAt,
        DateTime endAt,
        bool active = false)
    {
        var operationSeconds = Math.Max(0, (int)(endAt - startAt).TotalSeconds);
        rows.Add(new OperationRowViewModel
        {
            AutoStartTime = startAt,
            StopTime = active ? null : endAt,
            OperationMinutes = operationSeconds / 60,
            OperationSeconds = operationSeconds % 60,
            Observation = active ? "Machine operation in progress" : "Machine-only timing"
        });
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
            Date = IndiaTime.Now.Date,
            LoadingDate = IndiaTime.Now.Date,
            LoadingTime = new TimeSpan(14, 0, 0),
            SoakingMin = new TimeSpan(1, 0, 0),
            SoakingMax = new TimeSpan(3, 0, 0),
            ForgingTemperature = 1000,
            TemperatureTolerance = 14,
            SoakingStartTemperature = 986,
            Operation = "Upset & Pierce",
            Equipment = "1500 Ton Press",
            Operations = Enumerable.Range(1, 20)
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

    private void NormalizeEmptyTimeValues(ProcessRecordViewModel model)
    {
        var defaults = CreateDefaultModel();
        RestoreEmptyTimeValue(nameof(model.LoadingTime), ModelState, defaults.LoadingTime, value => model.LoadingTime = value);
        RestoreEmptyTimeValue(nameof(model.SoakingMin), ModelState, defaults.SoakingMin, value => model.SoakingMin = value);
        RestoreEmptyTimeValue(nameof(model.SoakingMax), ModelState, defaults.SoakingMax, value => model.SoakingMax = value);
        RestoreEmptyTimeValue(nameof(model.SoakingStartTime), ModelState, TimeSpan.Zero, value => model.SoakingStartTime = value);
        RestoreEmptyTimeValue(nameof(model.MinimumSoakingComplete), ModelState, TimeSpan.Zero, value => model.MinimumSoakingComplete = value);
        RestoreEmptyTimeValue(nameof(model.OperationEndBefore), ModelState, TimeSpan.Zero, value => model.OperationEndBefore = value);
        RestoreEmptyTimeValue(nameof(model.ActualOperationStartTime), ModelState, TimeSpan.Zero, value => model.ActualOperationStartTime = value);
        RestoreEmptyTimeValue(nameof(model.ActualOperationEndTime), ModelState, TimeSpan.Zero, value => model.ActualOperationEndTime = value);
    }

    private static void RestoreEmptyTimeValue(
        string key,
        ModelStateDictionary modelState,
        TimeSpan defaultValue,
        Action<TimeSpan> assign)
    {
        if (modelState.TryGetValue(key, out var entry) &&
            string.IsNullOrWhiteSpace(entry.AttemptedValue) &&
            entry.Errors.Count > 0)
        {
            modelState.Remove(key);
            assign(defaultValue);
        }
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
            Machine = model.Machine.Trim(),
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
            OtherRemarks = model.OtherRemarks?.Trim() ?? string.Empty,
            PreparedBy = model.PreparedBy.Trim(),
            VerifiedBy = model.VerifiedBy.Trim(),
            Operations = model.Operations
                .Where(x => x.TransferMinutes > 0 || x.TransferSeconds > 0 ||
                            x.TotalMinutes > 0 || x.TotalSeconds > 0 ||
                            x.FurnaceDoorOpenTime is not null || x.FurnaceDoorCloseTime is not null ||
                            x.AutoStartTime is not null || x.StopTime is not null ||
                            x.Temperature is not null || x.HighestTemperature is not null ||
                            !string.IsNullOrWhiteSpace(x.Observation))
                .Select((x, index) =>
            {
                var transfer = TimeSpan.FromMinutes(x.TransferMinutes) + TimeSpan.FromSeconds(x.TransferSeconds);
                var total = TimeSpan.FromMinutes(x.TotalMinutes) + TimeSpan.FromSeconds(x.TotalSeconds);
                var op = x.OperationMinutes > 0 || x.OperationSeconds > 0
                    ? TimeSpan.FromMinutes(x.OperationMinutes) + TimeSpan.FromSeconds(x.OperationSeconds)
                    : total >= transfer ? total - transfer : TimeSpan.Zero;

                return new OperationRecordEntity
                {
                    SlNo = index + 1,
                    TransferMinutes = x.TransferMinutes,
                    TransferSeconds = x.TransferSeconds,
                    TotalMinutes = x.TotalMinutes,
                    TotalSeconds = x.TotalSeconds,
                    OperationMinutes = (int)op.TotalMinutes,
                    OperationSeconds = op.Seconds,
                    Observation = x.Observation?.Trim() ?? string.Empty,
                    FurnaceDoorOpenTime = x.FurnaceDoorOpenTime,
                    FurnaceDoorCloseTime = x.FurnaceDoorCloseTime,
                    AutoStartTime = x.AutoStartTime,
                    StopTime = x.StopTime,
                    Temperature = x.Temperature,
                    HighestTemperature = x.HighestTemperature
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
            Machine = entity.Machine,
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
                    Observation = x.Observation,
                    FurnaceDoorOpenTime = x.FurnaceDoorOpenTime,
                    FurnaceDoorCloseTime = x.FurnaceDoorCloseTime,
                    AutoStartTime = x.AutoStartTime,
                    StopTime = x.StopTime,
                    Temperature = x.Temperature,
                    HighestTemperature = x.HighestTemperature
                })
                .ToList()
        };
    }

    private async Task ApplyAutomaticTimingAsync(
        ProcessRecordViewModel model,
        CancellationToken cancellationToken = default)
    {
        model.Operations ??= new List<OperationRowViewModel>();

        var startLocal = model.LoadingDate.Date;
        if (startLocal == DateTime.MinValue)
            startLocal = model.Date.Date;

        var fromIndiaTime = startLocal.Date + model.LoadingTime;
        var dayEndIndiaTime = startLocal.Date.AddDays(1);
        var nowIndiaTime = IndiaTime.Now;
        var toIndiaTime = nowIndiaTime < dayEndIndiaTime ? nowIndiaTime : dayEndIndiaTime;

        var intervals = await _db.TimingTransferIntervals
            .AsNoTracking()
            .Where(x => x.Furnace == model.Furnace &&
                        x.Machine == model.Machine &&
                        x.OpenAt >= fromIndiaTime &&
                        x.OpenAt < toIndiaTime &&
                        x.MachineStartAt != null &&
                        x.MachineStopAt != null &&
                        x.MachineStartAt >= x.OpenAt &&
                        (x.CloseAt == null || x.MachineStartAt >= x.CloseAt) &&
                        x.MachineStopAt >= x.MachineStartAt &&
                        x.MachineStopAt >= fromIndiaTime)
             .OrderByDescending(x => x.OpenAt)
            .ToListAsync(cancellationToken);

        intervals = intervals
            .Where(x => x.MachineStartAt!.Value - x.OpenAt <= TimingLimits.MaximumTransferDuration &&
                        x.MachineStopAt!.Value - x.OpenAt <= TimingLimits.MaximumTotalDuration &&
                        (x.CloseAt == null || x.MachineStartAt!.Value >= x.CloseAt.Value))
            .ToList();

        var rows = intervals
            .GroupBy(x => new
            {
                x.Furnace,
                x.Machine,
                x.OpenAt,
                x.MachineStartAt,
                x.MachineStopAt
            })
            .Select(x => x.OrderByDescending(y => y.Id).First())
            .Select(x => CreateIntervalReportRow(x, toIndiaTime))
            .OrderBy(x => x.StartAt)
            .ToList();

        if (rows.Count < 20)
        {
            var legacyRows = await _db.TimingCycles
                .AsNoTracking()
                .Where(x => x.Furnace == model.Furnace &&
                            x.Machine == model.Machine &&
            x.TransferStartAt >= fromIndiaTime &&
                            x.TotalStartAt < toIndiaTime &&
                            x.StoppedAt >= fromIndiaTime &&
            x.TransferStartAt != null &&
            x.StoppedAt >= x.TotalStartAt)
                 .OrderByDescending(x => x.TotalStartAt)
                .ToListAsync(cancellationToken);

            rows.AddRange(legacyRows
                .Where(cycle => cycle.StoppedAt >= cycle.TransferStartAt &&
                                cycle.TotalStartAt - cycle.TransferStartAt <= TimingLimits.MaximumTransferDuration &&
                                cycle.StoppedAt - cycle.TransferStartAt <= TimingLimits.MaximumTotalDuration &&
                                !rows.Any(row => row.StartAt == cycle.TransferStartAt && row.EndAt == cycle.StoppedAt))
                .Select(cycle => new TimingReportRow(
                    cycle.TransferStartAt!.Value,
                    cycle.StoppedAt,
                    Math.Max(0, cycle.TransferSeconds ?? 0),
                    Math.Max(0, Math.Max(cycle.TotalSeconds, cycle.TransferSeconds ?? 0)),
                    cycle.StartMode,
                    AutoStartTime: cycle.TotalStartAt)));

            rows = rows.OrderBy(x => x.StartAt).ToList();

        }

        if (rows.Count == 0)
        {
            var signalRows = await BuildSignalWorkOrderRowsAsync(
                model.Furnace,
                model.Machine,
                cancellationToken);
            var selectedSignalRows = signalRows
                .Where(x => x.StartAt >= fromIndiaTime)
                .ToList();

            if (selectedSignalRows.Count > 0)
                rows = selectedSignalRows;
        }

        var sampleFromIndiaTime = rows.Count == 0
            ? fromIndiaTime
            : rows.Min(x => x.StartAt).AddDays(-1);
        var sampleToIndiaTime = rows.Count == 0
            ? toIndiaTime
            : rows.Max(x => x.EndAt).AddSeconds(30);

        var machineSamples = await _db.MachineData
            .AsNoTracking()
            .Where(x => x.Machine == model.Machine &&
                        x.CapturedAt >= sampleFromIndiaTime &&
                        x.CapturedAt <= sampleToIndiaTime)
            .OrderBy(x => x.CapturedAt)
            .ToListAsync(cancellationToken);

        var furnaceSamples = await _db.FurnaceData
            .AsNoTracking()
            .Where(x => x.Furnace == model.Furnace &&
                        x.CapturedAt >= sampleFromIndiaTime &&
                        x.CapturedAt <= sampleToIndiaTime)
            .OrderBy(x => x.CapturedAt)
            .ToListAsync(cancellationToken);
        rows = rows
            .OrderBy(x => x.StartAt)
            .Select(row => CorrectTransferFromDoorSamples(row, furnaceSamples))
            .ToList();

        var state = await _db.MachineTimingStates
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.Furnace == model.Furnace && x.Machine == model.Machine,
                cancellationToken);

        // Include the current active block from the persisted state. It will be
        // finalized into TimingCycles when the PLC stop signal is received.
        if (state is not null &&
            startLocal.Date == IndiaTime.Now.Date &&
            (state.TransferStartAt ?? state.TotalStartAt) is DateTime activeTransferStartAt &&
            activeTransferStartAt >= fromIndiaTime &&
            activeTransferStartAt < toIndiaTime &&
            state.TotalStartAt is not null &&
            state.TotalStartAt.Value >= activeTransferStartAt &&
            state.LastUpdatedAt >= activeTransferStartAt &&
            state.LastUpdatedAt >= fromIndiaTime &&
            toIndiaTime - activeTransferStartAt <= GetMaximumOperationDuration())
        {
            var totalSeconds = Math.Max(0, (int)(toIndiaTime - activeTransferStartAt).TotalSeconds);
            var transferSeconds = state.TransferStartAt is null
                ? 0
                : state.TotalStartAt is null
                    ? totalSeconds
                    : Math.Max(0, (int)(state.TotalStartAt.Value - state.TransferStartAt.Value).TotalSeconds);

            if (!rows.Any(x => x.StartAt == activeTransferStartAt))
            {
                rows.Add(new TimingReportRow(
                    activeTransferStartAt,
                    toIndiaTime,
                    transferSeconds,
                    totalSeconds,
                    state.StartMode ?? "Active"));
            }

            rows = rows.OrderBy(x => x.StartAt).ToList();
        }

        rows = rows
            .Select(row => EnrichTimingRow(row, furnaceSamples, machineSamples))
            .ToList();

        if (rows.Count == 0 && model.Operations.Any(x =>
                x.TransferMinutes > 0 || x.TransferSeconds > 0 ||
                x.TotalMinutes > 0 || x.TotalSeconds > 0))
        {
            return;
        }

        model.Operations.Clear();

        foreach (var timing in rows.OrderBy(x => x.StartAt))
        {
            var operationSeconds = Math.Max(
                0,
                timing.TotalSeconds - timing.TransferSeconds);

            var start = ToIndiaTime(timing.StartAt);
            var end = ToIndiaTime(timing.EndAt);

            model.Operations.Add(new OperationRowViewModel
            {
                SlNo = model.Operations.Count + 1,
                TransferMinutes = timing.TransferSeconds / 60,
                TransferSeconds = timing.TransferSeconds % 60,
                TotalMinutes = timing.TotalSeconds / 60,
                TotalSeconds = timing.TotalSeconds % 60,
                OperationMinutes = operationSeconds / 60,
                OperationSeconds = operationSeconds % 60,
                Observation =
                    $"{timing.StartMode}: {start:dd-MM-yyyy HH:mm:ss} - {end:dd-MM-yyyy HH:mm:ss}",
                FurnaceDoorOpenTime = ToIndiaTimeOrNull(timing.FurnaceDoorOpenTime),
                FurnaceDoorCloseTime = ToIndiaTimeOrNull(timing.FurnaceDoorCloseTime),
                AutoStartTime = ToIndiaTimeOrNull(timing.AutoStartTime),
                StopTime = ToIndiaTimeOrNull(timing.StopTime),
                Temperature = timing.Temperature,
                HighestTemperature = timing.HighestTemperature
            });
        }

        if (rows.Count > 0)
        {
            var first = ToIndiaTime(rows.Min(x => x.StartAt));
            var last = ToIndiaTime(rows.Max(x => x.EndAt));
            model.ActualOperationStartTime = first.TimeOfDay;
            model.ActualOperationEndTime = last.TimeOfDay;
        }
    }

    private static List<TimingReportRow> BuildMachineHistoryRows(
        IReadOnlyList<MachineDataEntity> samples,
        DateTime now)
    {
        var rows = new List<TimingReportRow>();
        DateTime? machineStart = null;
        var previousStart = false;
        var previousStop = false;

        foreach (var sample in samples)
        {
            var startSignal = sample.AutoStartLed || sample.AutoStartPushButton || sample.MaterialForce >= 20m;
            var stopSignal = sample.AutoStopLed || sample.AutoStopPushButton;
            var startEdge = startSignal && !previousStart;
            var stopEdge = stopSignal && !previousStop;

            if (startEdge && machineStart is null)
            {
                var startTimestamp = sample.AutoStartLed
                    ? FurnaceDoorTiming.EventTime(sample.AutoStartLedSourceTimestamp, sample.CapturedAt)
                    : sample.AutoStartPushButton
                        ? FurnaceDoorTiming.EventTime(sample.AutoStartPushButtonSourceTimestamp, sample.CapturedAt)
                        : sample.CapturedAt;
                machineStart = startTimestamp;
            }

            if (stopEdge && machineStart is not null)
            {
                var stopTimestamp = sample.AutoStopLed
                    ? FurnaceDoorTiming.EventTime(sample.AutoStopLedSourceTimestamp, sample.CapturedAt)
                    : sample.AutoStopPushButton
                        ? FurnaceDoorTiming.EventTime(sample.AutoStopPushButtonSourceTimestamp, sample.CapturedAt)
                        : sample.CapturedAt;
                if (stopTimestamp < machineStart.Value)
                    stopTimestamp = sample.CapturedAt;
                if (stopTimestamp >= machineStart.Value)
                {
                    var totalSeconds = Math.Max(0, (int)(stopTimestamp - machineStart.Value).TotalSeconds);
                    rows.Add(new TimingReportRow(machineStart.Value, stopTimestamp, 0, totalSeconds, "Machine history"));
                    machineStart = null;
                }
            }

            previousStart = startSignal;
            previousStop = stopSignal;
        }

        if (machineStart is not null)
        {
            var activeDuration = now - machineStart.Value;
            if (activeDuration <= GetMaximumOperationDuration())
            {
                var totalSeconds = Math.Max(0, (int)activeDuration.TotalSeconds);
                rows.Add(new TimingReportRow(machineStart.Value, now, 0, totalSeconds, "Machine active"));
            }
        }

        return rows;
    }

    private static List<TimingReportRow> ApplyFurnaceTransferTimes(
        IReadOnlyList<TimingReportRow> machineRows,
        IReadOnlyList<FurnaceDataEntity> samples)
    {
        var doorIntervals = GetDoorCloseTransitionIntervals(samples);
        var usedIntervals = new HashSet<int>();
        var result = new List<TimingReportRow>(machineRows.Count);

        foreach (var row in machineRows.OrderBy(x => x.StartAt))
        {
            var candidate = doorIntervals
                .Select((interval, index) => new { interval, index })
                .Where(x => !usedIntervals.Contains(x.index) &&
                            x.interval.CloseAt <= row.StartAt)
                .OrderByDescending(x => x.interval.CloseAt)
                .FirstOrDefault();

            if (candidate is null)
            {
                if (row.EndAt >= row.StartAt &&
                    row.EndAt - row.StartAt <= GetMaximumOperationDuration())
                {
                    result.Add(row);
                }

                continue;
            }

            var interval = candidate.interval;
            var totalDuration = row.EndAt - interval.OpenAt;

            if (totalDuration < TimeSpan.Zero ||
                totalDuration > TimingLimits.MaximumTotalDuration ||
                row.StartAt - interval.OpenAt > TimingLimits.MaximumTransferDuration)
            {
                continue;
            }

            var transferSeconds =
                Math.Max(0, (int)(row.StartAt - interval.OpenAt).TotalSeconds);

            var totalSeconds = Math.Max(
                transferSeconds,
                Math.Max(0, (int)totalDuration.TotalSeconds));

            usedIntervals.Add(candidate.index);

            result.Add(row with
            {
                StartAt = interval.OpenAt,
                TransferSeconds = transferSeconds,
                TotalSeconds = totalSeconds,
                FurnaceDoorOpenTime = interval.OpenAt,
                FurnaceDoorCloseTime = interval.CloseAt
            });
        }

        return result;
    }

    private static List<(DateTime OpenAt, DateTime CloseAt)> GetDoorCloseTransitionIntervals(
        IReadOnlyList<FurnaceDataEntity> samples)
    {
        return FurnaceDoorTiming.GetCompletedIntervals(samples);
    }

    private static TimeSpan GetMaximumOperationDuration() => TimingLimits.MaximumTotalDuration;

    private static DateTime ToIndiaTime(DateTime timestamp)
    {
        return IndiaTime.AsWallClock(timestamp);
    }

    private static TimingReportRow CreateIntervalReportRow(
        TimingTransferIntervalEntity interval,
        DateTime now)
    {
        var totalEnd = interval.MachineStopAt ?? interval.MachineStartAt ?? now;
        DateTime? closeAt = interval.CloseAt is DateTime candidateClose &&
                      candidateClose >= interval.OpenAt &&
                      candidateClose <= totalEnd
            ? candidateClose
            : null;
        var transferSeconds = interval.MachineStartAt is null
            ? 0
            : Math.Max(0, (int)(interval.MachineStartAt.Value - interval.OpenAt).TotalSeconds);
        var totalSeconds = Math.Max(transferSeconds,
            Math.Max(0, (int)(totalEnd - interval.OpenAt).TotalSeconds));

        if (interval.MachineStartAt is null ||
            interval.MachineStartAt.Value - interval.OpenAt > TimingLimits.MaximumTransferDuration ||
            totalEnd - interval.OpenAt > TimingLimits.MaximumTotalDuration)
        {
            return new TimingReportRow(interval.OpenAt, interval.OpenAt, 0, 0, "Invalid");
        }

        return new TimingReportRow(
            interval.OpenAt,
            totalEnd,
            transferSeconds,
            totalSeconds,
            interval.MachineStartAt is null ? "Active" : "Automatic",
            interval.OpenAt,
            closeAt,
            interval.MachineStartAt,
            interval.MachineStopAt);
    }

    private static TimingReportRow EnrichTimingRow(
        TimingReportRow row,
        IReadOnlyList<FurnaceDataEntity> furnaceSamples,
        IReadOnlyList<MachineDataEntity> machineSamples)
    {
        var furnace = furnaceSamples
            .Where(x => x.CapturedAt >= row.StartAt && x.CapturedAt <= row.EndAt)
            .ToList();
        var machine = machineSamples
            .Where(x => x.CapturedAt >= row.StartAt && x.CapturedAt <= row.EndAt)
            .ToList();
        if (machine.Count == 0)
        {
            machine = machineSamples
                .Where(x => x.CapturedAt >= row.StartAt.AddSeconds(-30) &&
                            x.CapturedAt <= row.EndAt.AddSeconds(30))
                .ToList();
        }
        var temperatures = machine.Select(x => x.MaterialTemperature).ToList();

        var autoStart = row.AutoStartTime ?? machine.SelectMany(x => new[]
        {
            x.AutoStartLedSourceTimestamp,
            x.AutoStartPushButtonSourceTimestamp
        }).Where(IsValidSourceTimestamp).Select(x => (DateTime?)x!.Value).FirstOrDefault() ?? row.StartAt;
        var stop = row.StopTime ?? machine.SelectMany(x => new[]
        {
            x.AutoStopLedSourceTimestamp,
            x.AutoStopPushButtonSourceTimestamp
        }).Where(IsValidSourceTimestamp).Select(x => (DateTime?)x!.Value).LastOrDefault() ?? row.EndAt;
        autoStart = autoStart < row.StartAt || autoStart > row.EndAt ? row.StartAt : autoStart;
        stop = stop < autoStart || stop > row.EndAt ? row.EndAt : stop;

        var close = row.FurnaceDoorCloseTime ?? furnace
            .Where(x => x.CapturedAt >= row.StartAt && x.CapturedAt <= autoStart && x.DoorClosed)
            .Select(x => x.DoorClosedSourceTimestamp ?? x.CapturedAt)
            .Where(x => x >= row.StartAt && x <= autoStart)
            .LastOrDefault();

        return row with
        {
            FurnaceDoorOpenTime = row.FurnaceDoorOpenTime ?? furnace.FirstOrDefault()?.CapturedAt,
            FurnaceDoorCloseTime = close == default ? null : close,
            AutoStartTime = autoStart,
            StopTime = stop,
            Temperature = row.Temperature ?? (temperatures.Count == 0 ? null : temperatures.Max()),
            HighestTemperature = row.HighestTemperature ?? (temperatures.Count == 0 ? null : temperatures.Max())
        };
    }

    private static bool IsValidSourceTimestamp(DateTime? timestamp) =>
        timestamp is DateTime value && value >= new DateTime(2020, 1, 1);

    private async Task<List<TimingReportRow>> BuildSignalWorkOrderRowsAsync(
        string furnace,
        string machine,
        CancellationToken cancellationToken)
    {
        var fromIndiaTime = IndiaTime.Now.AddDays(-7);
        var toIndiaTime = IndiaTime.Now.AddMinutes(1);
        var furnaceSamples = await _db.FurnaceData
            .AsNoTracking()
            .Where(x => x.Furnace == furnace &&
                        x.CapturedAt >= fromIndiaTime &&
                        x.CapturedAt <= toIndiaTime)
            .OrderBy(x => x.CapturedAt)
            .ToListAsync(cancellationToken);
        var machineSamples = await _db.MachineData
            .AsNoTracking()
            .Where(x => x.Machine == machine &&
                        x.CapturedAt >= fromIndiaTime &&
                        x.CapturedAt <= toIndiaTime)
            .OrderBy(x => x.CapturedAt)
            .ToListAsync(cancellationToken);

        var doorEvents = GetDoorCloseTransitionIntervals(furnaceSamples);

        var machineStarts = new List<DateTime>();
        var machineStops = new List<DateTime>();
        bool? previousStart = null;
        bool? previousStop = null;
        foreach (var sample in machineSamples)
        {
            var startSignal = sample.AutoStartLed ||
                              sample.AutoStartPushButton ||
                              sample.MaterialForce >= 20m;
            var stopSignal = sample.AutoStopLed || sample.AutoStopPushButton;

            if (startSignal && previousStart != true)
            {
                var machineStartAt = sample.AutoStartLed
                    ? FurnaceDoorTiming.EventTime(sample.AutoStartLedSourceTimestamp, sample.CapturedAt)
                    : sample.AutoStartPushButton
                        ? FurnaceDoorTiming.EventTime(sample.AutoStartPushButtonSourceTimestamp, sample.CapturedAt)
                        : sample.CapturedAt;
                machineStarts.Add(machineStartAt);
            }

            if (stopSignal && previousStop != true)
            {
                var machineStopAt = sample.AutoStopLed
                    ? FurnaceDoorTiming.EventTime(sample.AutoStopLedSourceTimestamp, sample.CapturedAt)
                    : FurnaceDoorTiming.EventTime(sample.AutoStopPushButtonSourceTimestamp, sample.CapturedAt);
                machineStops.Add(machineStopAt);
            }

            previousStart = startSignal;
            previousStop = stopSignal;
        }

        var rows = new List<TimingReportRow>();
        var usedStarts = new HashSet<DateTime>();
        var usedStops = new HashSet<DateTime>();
        foreach (var doorEvent in doorEvents)
        {
            var machineStart = machineStarts
                .Where(x => !usedStarts.Contains(x) &&
                            x >= doorEvent.CloseAt &&
                            x <= doorEvent.CloseAt + TimingLimits.MaximumTransferDuration)
                .OrderBy(x => Math.Abs((x - doorEvent.CloseAt).TotalSeconds))
                .FirstOrDefault();

            if (machineStart == default)
                continue;

            var machineStop = machineStops
                .Where(x => !usedStops.Contains(x) &&
                            x > machineStart &&
                            x - doorEvent.OpenAt <= TimingLimits.MaximumTotalDuration)
                .OrderBy(x => x)
                .FirstOrDefault();
            if (machineStop == default)
                continue;

            usedStarts.Add(machineStart);
            usedStops.Add(machineStop);
            var transferStart = doorEvent.OpenAt;
            var transferSeconds =
                Math.Max(0, (int)(machineStart - transferStart).TotalSeconds);
            var totalSeconds = Math.Max(transferSeconds, (int)(machineStop - transferStart).TotalSeconds);

            rows.Add(new TimingReportRow(
                transferStart,
                machineStop,
                transferSeconds,
                totalSeconds,
                "Signal history",
                transferStart,
                doorEvent.CloseAt,
                machineStart,
                machineStop));
        }

        return rows
            .Where(x => x.EndAt >= x.StartAt &&
                        x.AutoStartTime is DateTime autoStart &&
                        autoStart - x.StartAt <= TimingLimits.MaximumTransferDuration &&
                        x.EndAt - x.StartAt <= TimingLimits.MaximumTotalDuration)
            .OrderBy(x => x.StartAt)
            .ToList();
    }

    private static TimingReportRow CorrectTransferFromDoorSamples(
        TimingReportRow row,
        IReadOnlyList<FurnaceDataEntity> furnaceSamples)
    {
        var machineStart = row.AutoStartTime ?? row.StartAt;
        var doorInterval = GetDoorCloseTransitionIntervals(furnaceSamples)
            .Where(x => x.CloseAt <= machineStart)
            .OrderByDescending(x => x.CloseAt)
            .FirstOrDefault();

        if (doorInterval == default)
            return row;

        var openAt = doorInterval.OpenAt;
        if (openAt >= machineStart)
            return row;

        var totalDuration = row.EndAt - openAt;
        if (totalDuration < TimeSpan.Zero ||
            totalDuration > GetMaximumOperationDuration())
            return row;

        var transferSeconds =
            Math.Max(0, (int)(machineStart - openAt).TotalSeconds);
        var totalSeconds = Math.Max(
            transferSeconds,
            Math.Max(0, (int)totalDuration.TotalSeconds));

        return row with
        {
            StartAt = openAt,
            TransferSeconds = transferSeconds,
            TotalSeconds = totalSeconds,
            FurnaceDoorOpenTime = openAt,
            FurnaceDoorCloseTime = doorInterval.CloseAt
        };
    }

    private static DateTime? ToIndiaTimeOrNull(DateTime? timestamp) =>
        timestamp.HasValue ? ToIndiaTime(timestamp.Value) : null;

    private sealed record TimingReportRow(
        DateTime StartAt,
        DateTime EndAt,
        int TransferSeconds,
        int TotalSeconds,
        string StartMode,
        DateTime? FurnaceDoorOpenTime = null,
        DateTime? FurnaceDoorCloseTime = null,
        DateTime? AutoStartTime = null,
        DateTime? StopTime = null,
        decimal? Temperature = null,
        decimal? HighestTemperature = null);

}
