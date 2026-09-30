using HALProcessRecord.Data;
using HALProcessRecord.Models;
using HALProcessRecord.ViewModels;
using HALProcessRecord.Utilities;
using Microsoft.EntityFrameworkCore;

namespace HALProcessRecord.Services;

public sealed class TimingMonitorService : BackgroundService
{
    private static readonly string[] AllowedFurnaces =
    {
        "RRP_1", "RRP_2", "RRP_3", "RRP_4", "RRP_5",
        "RRP_10", "RRP_11", "RRP_12", "RRP_13", "RRP_14",
        "RRP_15", "RRP_16", "RRP_17", "RRP_18", "RRP_19"
    };

    private static readonly string[] DefaultMachines = { "MRB_3000", "MRB_3001" };
    private static readonly HashSet<string> MachineOneFurnaces = new(StringComparer.OrdinalIgnoreCase)
    {
        "RRP_11", "RRP_12", "RRP_13", "RRP_19"
    };
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OpcUaService _opcUaService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TimingMonitorService> _logger;

    public TimingMonitorService(
        IServiceScopeFactory scopeFactory,
        OpcUaService opcUaService,
        IConfiguration configuration,
        ILogger<TimingMonitorService> logger)
    {
        _scopeFactory = scopeFactory;
        _opcUaService = opcUaService;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = Math.Max(1, _configuration.GetValue("OpcUa:MonitoringIntervalSeconds", 5));
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await MonitorAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Timing monitor iteration failed; monitoring will retry on the next interval.");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                    return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    public async Task<TimingStatusViewModel?> GetCurrentAsync(
        string furnace,
        string machine,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var state = await db.MachineTimingStates.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Furnace == furnace && x.Machine == machine, cancellationToken);

        var now = IndiaTime.Now;
        var latestCycle = await db.TimingCycles.AsNoTracking()
            .Where(x => x.Furnace == furnace && x.Machine == machine)
            .OrderByDescending(x => x.StoppedAt)
            .FirstOrDefaultAsync(cancellationToken);
        var activeStartAt = state?.TransferStartAt ?? state?.TotalStartAt;
        var totalSeconds = activeStartAt is null
            ? latestCycle?.TotalSeconds ?? 0
            : Math.Max(0, (int)(now - activeStartAt.Value).TotalSeconds);
        var transferSeconds = state?.TransferStartAt is null
            ? latestCycle?.TransferSeconds ?? 0
            : state.TotalStartAt is null
                ? Math.Max(0, (int)(now - state.TransferStartAt.Value).TotalSeconds)
                : Math.Max(0, (int)(state.TotalStartAt.Value - state.TransferStartAt.Value).TotalSeconds);

        var latestFurnace = await db.FurnaceData.AsNoTracking()
            .Where(x => x.Furnace == furnace)
            .OrderByDescending(x => x.CapturedAt)
            .FirstOrDefaultAsync(cancellationToken);
        var latestMachine = await db.MachineData.AsNoTracking()
            .Where(x => x.Machine == machine)
            .OrderByDescending(x => x.CapturedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return new TimingStatusViewModel
        {
            Furnace = furnace,
            Machine = machine,
            IsActive = state?.TotalStartAt is not null || state?.TransferStartAt is not null,
            StartMode = state?.StartMode ?? string.Empty,
            TransferStartAt = state?.TransferStartAt,
            TotalStartAt = state?.TotalStartAt,
            TransferSeconds = transferSeconds,
            TotalSeconds = totalSeconds,
            OperationSeconds = Math.Max(0, totalSeconds - transferSeconds),
            LastCompletedAt = latestCycle?.StoppedAt,
            LastDoorOpen = latestFurnace?.DoorOpen,
            LastDoorClosed = latestFurnace?.DoorClosed,
            LastAutoStartLED = latestMachine?.AutoStartLed,
            LastAutoStartPB = latestMachine?.AutoStartPushButton,
            LastAutoStopLED = latestMachine?.AutoStopLed,
            LastAutoStopPB = latestMachine?.AutoStopPushButton,
            LastMaterialForce = latestMachine?.MaterialForce ?? 0,
            LastMaterialTemperature = latestMachine?.MaterialTemperature ?? 0,
            DoorOpenSourceTimestamp = latestFurnace?.DoorOpenSourceTimestamp,
            DoorClosedSourceTimestamp = latestFurnace?.DoorClosedSourceTimestamp,
            FurnaceCapturedAt = latestFurnace?.CapturedAt,
            MachineCapturedAt = latestMachine?.CapturedAt,
            AutoStartLEDSourceTimestamp = latestMachine?.AutoStartLedSourceTimestamp,
            AutoStopLEDSourceTimestamp = latestMachine?.AutoStopLedSourceTimestamp,
            AutoStartPBSourceTimestamp = latestMachine?.AutoStartPushButtonSourceTimestamp,
            AutoStopPBSourceTimestamp = latestMachine?.AutoStopPushButtonSourceTimestamp
        };
    }

    private async Task MonitorAllAsync(CancellationToken cancellationToken)
    {
        var furnaces = GetFurnaces().ToList();
        var machines = GetMachines().ToList();
        var furnaceStatuses = new Dictionary<string, OpcUaStatusViewModel>(StringComparer.OrdinalIgnoreCase);
        var machineStatuses = new Dictionary<string, OpcMachineStatusViewModel>(StringComparer.OrdinalIgnoreCase);

        foreach (var furnace in furnaces)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            try
            {
                furnaceStatuses[furnace] = await _opcUaService.ReadStatusAsync(furnace, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to read OPC UA furnace signals for {Furnace}.", furnace);
            }
        }

        foreach (var machine in machines)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            try
            {
                machineStatuses[machine] = await _opcUaService.ReadRingRollingStatusAsync(machine, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to read OPC UA machine signals for {Machine}.", machine);
            }
        }

        foreach (var furnace in furnaces)
        {
            foreach (var machine in machines)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;
                if (!furnaceStatuses.TryGetValue(furnace, out var furnaceStatus) ||
                    !machineStatuses.TryGetValue(machine, out var machineStatus))
                    continue;

                try
                {
                    await ProcessPairAsync(furnace, machine, furnaceStatus, machineStatus, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Unable to monitor timing for {Furnace}/{Machine}.", furnace, machine);
                }
            }
        }

        await PersistMachineSamplesAsync(machineStatuses, cancellationToken);
    }

    private async Task PersistMachineSamplesAsync(
        IReadOnlyDictionary<string, OpcMachineStatusViewModel> statuses,
        CancellationToken cancellationToken)
    {
        var connected = statuses.Where(x => x.Value.ConnectionStatus == "Connected").ToList();
        if (connected.Count == 0)
            return;

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var capturedAt = IndiaTime.Now;
            foreach (var (machine, status) in connected)
                AddMachineSample(db, machine, status, capturedAt);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to persist the latest OPC UA machine samples.");
        }
    }

    private async Task ProcessPairAsync(
        string furnace,
        string machine,
        OpcUaStatusViewModel furnaceStatus,
        OpcMachineStatusViewModel machineStatus,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var state = await db.MachineTimingStates
            .SingleOrDefaultAsync(x => x.Furnace == furnace && x.Machine == machine, cancellationToken);

        var anotherFurnaceOwnsMachine = await db.MachineTimingStates
            .AsNoTracking()
            .AnyAsync(x => x.Machine == machine && x.Furnace != furnace &&
                           (x.TransferStartAt != null || x.TotalStartAt != null), cancellationToken);
        if (anotherFurnaceOwnsMachine)
            return;

        var now = IndiaTime.Now;
        var furnaceConnected = furnaceStatus.ConnectionStatus == "Connected";
        var machineConnected = machineStatus.ConnectionStatus == "Connected";
        var machineOnePair = machine.Equals("MRB_3000", StringComparison.OrdinalIgnoreCase);
        var mappedToMachineOne = MachineOneFurnaces.Contains(furnace);
        var timingPairEnabled = !machineOnePair || mappedToMachineOne;

        if (!timingPairEnabled)
            return;

        if (state is null)
        {
            state = new MachineTimingStateEntity { Furnace = furnace, Machine = machine };
            db.MachineTimingStates.Add(state);

            if (furnaceConnected && !furnaceStatus.DoorClosed)
            {
                var openAt = FurnaceDoorTiming.EventTime(furnaceStatus.DoorClosedSourceTimestamp, now);
                var interval = await GetOrCreateOpenDoorIntervalAsync(
                    db, furnace, machine, openAt, cancellationToken);
                state.TransferStartAt = interval.OpenAt;
            }
            else if (furnaceConnected && furnaceStatus.DoorClosed)
            {
                var recovered = await ReconcileDoorIntervalAsync(
                    db, furnace, machine, now, cancellationToken);
                if (recovered is not null)
                {
                    state.TransferStartAt = recovered.OpenAt;
                    state.TransferEndAt = recovered.CloseAt;
                    state.TotalStartAt = recovered.MachineStopAt is null
                        ? recovered.MachineStartAt
                        : null;
                    state.StartMode = state.TotalStartAt is null ? null : "Automatic";
                }
            }

            SetSignals(state, furnaceStatus, machineStatus);
            if (furnaceConnected)
                AddFurnaceSample(db, furnace, furnaceStatus, state.HalfDoor, now);
            if (state.TotalStartAt is not null)
                await AssociateMachineStartAsync(db, furnace, machine, state.TotalStartAt.Value, cancellationToken);

            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var currentOpenInterval = await db.TimingTransferIntervals
            .Where(x => x.Furnace == furnace && x.Machine == machine && x.CloseAt == null)
            .OrderByDescending(x => x.OpenAt)
            .FirstOrDefaultAsync(cancellationToken);

        currentOpenInterval ??= db.ChangeTracker.Entries<TimingTransferIntervalEntity>()
            .Select(x => x.Entity)
            .Where(x => x.Furnace == furnace && x.Machine == machine && x.CloseAt == null)
            .OrderByDescending(x => x.OpenAt)
            .FirstOrDefault();

        var pendingTransferInterval = currentOpenInterval ?? await db.TimingTransferIntervals
            .Where(x => x.Furnace == furnace && x.Machine == machine && x.MachineStartAt == null)
            .OrderByDescending(x => x.OpenAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (pendingTransferInterval is not null &&
            state.TotalStartAt is not null &&
            pendingTransferInterval.OpenAt > state.TotalStartAt &&
            pendingTransferInterval.MachineStartAt is null)
        {
            state.TransferStartAt = pendingTransferInterval.OpenAt;
            state.TransferEndAt = null;
            state.TotalStartAt = null;
            state.StartMode = null;
        }

        if (furnaceConnected && furnaceStatus.DoorOpen)
        {
            state.HalfDoorCloseTransitions = 0;
            state.HalfDoor = false;
        }

        var doorClosedOpened = furnaceConnected &&
                               !furnaceStatus.DoorClosed &&
                               state.LastDoorClosed == true;
        var doorClosedTransition = furnaceConnected &&
                                   furnaceStatus.DoorClosed &&
                                   state.LastDoorClosed == false;

        if (doorClosedOpened && !state.HalfDoor)
        {
            var openAt = FurnaceDoorTiming.EventTime(furnaceStatus.DoorClosedSourceTimestamp, now);
            var openInterval = await GetOrCreateOpenDoorIntervalAsync(
                db, furnace, machine, openAt, cancellationToken);
            currentOpenInterval = openInterval;
            state.TransferStartAt = openInterval.OpenAt;
            state.TransferEndAt = null;
        }

        var ambiguousHalfDoorClose = doorClosedTransition &&
                                     state.LastDoorOpen == false &&
                                     state.LastDoorClosed == false &&
                                     !furnaceStatus.DoorOpen;
        if (ambiguousHalfDoorClose)
        {
            state.HalfDoorCloseTransitions++;
            if (state.HalfDoorCloseTransitions > 1)
                state.HalfDoor = true;
        }

        var needsHistoryReconciliation =
            (state.TransferStartAt is null && state.TotalStartAt is null) ||
            (state.TransferStartAt is not null && state.TransferEndAt is null && !doorClosedTransition);
        if (furnaceConnected && furnaceStatus.DoorClosed && !state.HalfDoor &&
            needsHistoryReconciliation && (!doorClosedTransition || state.TransferStartAt is null))
        {
            var recovered = await ReconcileDoorIntervalAsync(
                db, furnace, machine, now, cancellationToken);
            if (recovered is not null)
            {
                currentOpenInterval = recovered;
                state.TransferStartAt = recovered.OpenAt;
                state.TransferEndAt = recovered.CloseAt;
                if (recovered.MachineStartAt is DateTime machineStartAt && recovered.MachineStopAt is null)
                {
                    state.TotalStartAt = machineStartAt;
                    state.StartMode = "Automatic";
                }
            }
        }

        var hasFurnaceWorkOrder = state.TotalStartAt is not null ||
                                   state.TransferEndAt is not null ||
                                   (doorClosedTransition && state.TransferStartAt is not null) ||
                                   pendingTransferInterval?.CloseAt is not null;
        var doorClosed = doorClosedTransition && !state.HalfDoor;

        var autoStartLedTriggered = hasFurnaceWorkOrder && machineConnected &&
                                    machineStatus.AutoStartLED && state.LastAutoStartLED != true &&
                                    state.TotalStartAt is null;
        var autoStartPbTriggered = hasFurnaceWorkOrder && machineConnected &&
                                   machineStatus.AutoStartPB && state.LastAutoStartPB != true &&
                                   state.TotalStartAt is null;
        var automaticSignalStarted = autoStartLedTriggered || autoStartPbTriggered;
        var manualStarted = hasFurnaceWorkOrder && machineConnected && state.LastMaterialForce < 20m && machineStatus.MaterialForce >= 20f;
        var autoStopLedTriggered = hasFurnaceWorkOrder && machineConnected &&
                                   machineStatus.AutoStopLED && state.LastAutoStopLED != true &&
                                   state.TotalStartAt is not null;
        var autoStopPbTriggered = hasFurnaceWorkOrder && machineConnected &&
                                  machineStatus.AutoStopPB && state.LastAutoStopPB != true &&
                                  state.TotalStartAt is not null;
        var stopped = autoStopLedTriggered || autoStopPbTriggered;

        if (doorClosed && currentOpenInterval is not null && state.TransferStartAt is not null)
        {
            var closeAt = FurnaceDoorTiming.EventTime(furnaceStatus.DoorClosedSourceTimestamp, now);
            var finalOpenSample = await db.FurnaceData
                .AsNoTracking()
                .Where(x => x.Furnace == furnace &&
                            !x.DoorClosed &&
                            x.CapturedAt <= now)
                .OrderByDescending(x => x.CapturedAt)
                .FirstOrDefaultAsync(cancellationToken);
            var transferStart = finalOpenSample is null
                ? state.TransferStartAt.Value
                : FurnaceDoorTiming.EventTime(
                    finalOpenSample.DoorClosedSourceTimestamp,
                    finalOpenSample.CapturedAt);

            if (closeAt > transferStart &&
                closeAt - transferStart <= TimingLimits.MaximumTotalDuration)
            {
                currentOpenInterval.OpenAt = transferStart;
                currentOpenInterval.CloseAt = closeAt;
                state.TransferStartAt = transferStart;
                state.TransferEndAt = closeAt;
            }
        }
        if (manualStarted && state.TotalStartAt is null)
        {
            state.TotalStartAt = now;
            state.StartMode = "Manual";
            await AssociateMachineStartAsync(db, furnace, machine, state.TotalStartAt.Value, cancellationToken);
        }
        else if (automaticSignalStarted && state.TotalStartAt is null)
        {
            var machineStartAt = NormalizeEventTimestamp(
                GetTriggeredTimestamp(
                    autoStartLedTriggered,
                    machineStatus.AutoStartLEDSourceTimestamp,
                    autoStartPbTriggered,
                    machineStatus.AutoStartPBSourceTimestamp), now);
            if (state.TransferEndAt is DateTime doorClosedAt && machineStartAt < doorClosedAt)
                machineStartAt = now;
            state.TotalStartAt = machineStartAt;
            state.StartMode = "Automatic";
            await AssociateMachineStartAsync(db, furnace, machine, state.TotalStartAt.Value, cancellationToken);
        }
        if (furnaceConnected)
            AddFurnaceSample(db, furnace, furnaceStatus, state.HalfDoor, now);
        if (stopped && state.TotalStartAt is not null)
        {
            var stoppedAt = NormalizeEventTimestamp(
                GetTriggeredTimestamp(
                    autoStopLedTriggered,
                    machineStatus.AutoStopLEDSourceTimestamp,
                    autoStopPbTriggered,
                    machineStatus.AutoStopPBSourceTimestamp), now);
            if (stoppedAt < state.TotalStartAt.Value)
                stoppedAt = now;
            var intervals = await db.TimingTransferIntervals
                .Where(x => x.Furnace == furnace && x.Machine == machine &&
                            x.MachineStartAt == state.TotalStartAt && x.TimingCycleId == null)
                .OrderByDescending(x => x.OpenAt)
                .Take(1)
                .ToListAsync(cancellationToken);

            foreach (var interval in intervals)
            {
                interval.MachineStopAt = stoppedAt;
                var transferSeconds = Math.Max(0, (int)(state.TotalStartAt.Value - interval.OpenAt).TotalSeconds);
                var totalSeconds = Math.Max(transferSeconds, (int)(stoppedAt - interval.OpenAt).TotalSeconds);
                if (state.TotalStartAt.Value - interval.OpenAt > TimingLimits.MaximumTransferDuration ||
                    stoppedAt - interval.OpenAt > TimingLimits.MaximumTotalDuration)
                {
                    interval.MachineStartAt = null;
                    interval.MachineStopAt = null;
                    continue;
                }
                db.TimingCycles.Add(new TimingCycleEntity
                {
                    Furnace = furnace,
                    Machine = machine,
                    StartMode = state.StartMode ?? "",
                    TransferStartAt = interval.OpenAt,
                    TotalStartAt = state.TotalStartAt.Value,
                    StoppedAt = stoppedAt,
                    TransferSeconds = transferSeconds,
                    TotalSeconds = totalSeconds,
                    OperationSeconds = totalSeconds - transferSeconds
                });
            }

            state.TransferStartAt = null;
            state.TransferEndAt = null;
            state.TotalStartAt = null;
            state.StartMode = null;
            state.HalfDoorCloseTransitions = 0;
            state.HalfDoor = false;
        }

        SetSignals(state, furnaceStatus, machineStatus);
        state.LastUpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);

        if (stopped)
        {
            var unlinkedIntervals = await db.TimingTransferIntervals
                .Where(x => x.Furnace == furnace && x.Machine == machine &&
                            x.MachineStopAt != null && x.TimingCycleId == null)
                .OrderBy(x => x.OpenAt)
                .ToListAsync(cancellationToken);
            var cycles = await db.TimingCycles
                .Where(x => x.Furnace == furnace && x.Machine == machine && x.StoppedAt == unlinkedIntervals.Select(i => i.MachineStopAt).FirstOrDefault())
                .OrderByDescending(x => x.Id)
                .Take(unlinkedIntervals.Count)
                .ToListAsync(cancellationToken);
            for (var i = 0; i < Math.Min(unlinkedIntervals.Count, cycles.Count); i++)
                unlinkedIntervals[i].TimingCycleId = cycles[cycles.Count - 1 - i].Id;
            if (unlinkedIntervals.Count > 0)
                await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static DateTime NormalizeEventTimestamp(DateTime? sourceTimestamp, DateTime capturedAt) =>
        FurnaceDoorTiming.EventTime(sourceTimestamp, capturedAt);

    private static async Task AssociateMachineStartAsync(
        AppDbContext db,
        string furnace,
        string machine,
        DateTime machineStartAt,
        CancellationToken cancellationToken)
    {
        var intervals = await db.TimingTransferIntervals
            .Where(x => x.Furnace == furnace && x.Machine == machine &&
                        x.MachineStartAt == null && x.MachineStopAt == null &&
                        x.OpenAt <= machineStartAt)
            .OrderByDescending(x => x.OpenAt)
            .Take(1)
            .ToListAsync(cancellationToken);

        var trackedInterval = db.ChangeTracker.Entries<TimingTransferIntervalEntity>()
            .Select(x => x.Entity)
            .Where(x => x.Furnace == furnace && x.Machine == machine &&
                        x.MachineStartAt == null && x.MachineStopAt == null &&
                        x.OpenAt <= machineStartAt)
            .OrderByDescending(x => x.OpenAt)
            .FirstOrDefault();

        if (trackedInterval is not null && intervals.All(x => x.Id != trackedInterval.Id))
            intervals.Add(trackedInterval);

        foreach (var interval in intervals)
            interval.MachineStartAt = machineStartAt;
    }

    private static DateTime? NormalizeStoredSourceTimestamp(DateTime? value, DateTime capturedAt)
    {
        return value is null ? null : NormalizeEventTimestamp(value, capturedAt);
    }

    private static DateTime? GetTriggeredTimestamp(bool firstTriggered, DateTime? firstTimestamp, bool secondTriggered, DateTime? secondTimestamp)
    {
        if (firstTriggered && secondTriggered)
            return firstTimestamp is null ? secondTimestamp : secondTimestamp is null ? firstTimestamp : (firstTimestamp <= secondTimestamp ? firstTimestamp : secondTimestamp);
        return firstTriggered ? firstTimestamp : secondTimestamp;
    }

    private static DateTime? GetInitialStartTimestamp(bool first, DateTime? firstTimestamp, bool second, DateTime? secondTimestamp)
    {
        return GetTriggeredTimestamp(first, firstTimestamp, second, secondTimestamp);
    }

    private static DateTime? PreserveSourceTimestamp(DateTime? value)
    {
        return value is null ? null : DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified);
    }

    private static void AddFurnaceSample(AppDbContext db, string furnace, OpcUaStatusViewModel status, bool halfDoor, DateTime capturedAt)
    {
        db.FurnaceData.Add(new FurnaceDataEntity { Furnace = furnace, DoorOpen = status.DoorOpen, DoorClosed = status.DoorClosed, HalfDoor = halfDoor, Code = status.Code, DoorOpenSourceTimestamp = NormalizeStoredSourceTimestamp(status.DoorOpenSourceTimestamp, capturedAt), DoorClosedSourceTimestamp = NormalizeStoredSourceTimestamp(status.DoorClosedSourceTimestamp, capturedAt), SourceReadAt = status.ReadAt ?? capturedAt, CapturedAt = capturedAt });
    }

    private static void AddMachineSample(AppDbContext db, string machine, OpcMachineStatusViewModel status, DateTime capturedAt)
    {
        db.MachineData.Add(new MachineDataEntity { Machine = machine, AutoStartLed = status.AutoStartLED, AutoStopLed = status.AutoStopLED, AutoStartPushButton = status.AutoStartPB, AutoStopPushButton = status.AutoStopPB, MaterialForce = (decimal)status.MaterialForce, MaterialTemperature = (decimal)status.MaterialTemperature, AutoStartLedSourceTimestamp = NormalizeStoredSourceTimestamp(status.AutoStartLEDSourceTimestamp, capturedAt), AutoStopLedSourceTimestamp = NormalizeStoredSourceTimestamp(status.AutoStopLEDSourceTimestamp, capturedAt), AutoStartPushButtonSourceTimestamp = NormalizeStoredSourceTimestamp(status.AutoStartPBSourceTimestamp, capturedAt), AutoStopPushButtonSourceTimestamp = NormalizeStoredSourceTimestamp(status.AutoStopPBSourceTimestamp, capturedAt), SourceReadAt = status.ReadAt ?? capturedAt, CapturedAt = capturedAt });
    }

    private static void SetSignals(MachineTimingStateEntity state, OpcUaStatusViewModel furnaceStatus, OpcMachineStatusViewModel machineStatus)
    {
        if (furnaceStatus.ConnectionStatus == "Connected") { state.LastDoorOpen = furnaceStatus.DoorOpen; state.LastDoorClosed = furnaceStatus.DoorClosed; }
        if (machineStatus.ConnectionStatus == "Connected") { state.LastAutoStartLED = machineStatus.AutoStartLED; state.LastAutoStartPB = machineStatus.AutoStartPB; state.LastAutoStopLED = machineStatus.AutoStopLED; state.LastAutoStopPB = machineStatus.AutoStopPB; state.LastMaterialForce = (decimal)machineStatus.MaterialForce; }
        state.LastUpdatedAt = IndiaTime.Now;
    }

    private static async Task<(DateTime OpenAt, DateTime CloseAt)?> GetLastProductionDoorTransitionAsync(
        AppDbContext db,
        string furnace,
        DateTime before,
        CancellationToken cancellationToken)
    {
        var samples = await db.FurnaceData
            .AsNoTracking()
            .Where(x => x.Furnace == furnace && x.CapturedAt <= before)
            .OrderByDescending(x => x.CapturedAt)
            .Take(300)
            .ToListAsync(cancellationToken);

        var intervals = FurnaceDoorTiming.GetCompletedIntervals(samples);
        return intervals.Count == 0 ? null : intervals[^1];
    }

    private async Task<TimingTransferIntervalEntity?> ReconcileDoorIntervalAsync(
        AppDbContext db,
        string furnace,
        string machine,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var transition = await GetLastProductionDoorTransitionAsync(
            db, furnace, now, cancellationToken);
        if (transition is null || transition.Value.CloseAt > now ||
            now - transition.Value.CloseAt > TimeSpan.FromHours(24))
        {
            return null;
        }

        var from = transition.Value.OpenAt.AddMinutes(-2);
        var to = transition.Value.OpenAt.AddMinutes(2);
        var existing = await db.TimingTransferIntervals
            .Where(x => x.Furnace == furnace && x.Machine == machine &&
                        x.OpenAt >= from && x.OpenAt <= to)
            .OrderByDescending(x => x.OpenAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is not null)
        {
            if (existing.TimingCycleId is not null || existing.MachineStopAt is not null)
                return null;

            existing.CloseAt ??= transition.Value.CloseAt;
            return existing;
        }

        var recovered = new TimingTransferIntervalEntity
        {
            Furnace = furnace,
            Machine = machine,
            OpenAt = transition.Value.OpenAt,
            CloseAt = transition.Value.CloseAt
        };
        db.TimingTransferIntervals.Add(recovered);
        return recovered;
    }

    private async Task<TimingTransferIntervalEntity> GetOrCreateOpenDoorIntervalAsync(
        AppDbContext db,
        string furnace,
        string machine,
        DateTime openAt,
        CancellationToken cancellationToken)
    {
        var from = openAt.AddMinutes(-2);
        var to = openAt.AddMinutes(2);
        var existing = await db.TimingTransferIntervals
            .Where(x => x.Furnace == furnace && x.Machine == machine &&
                        x.CloseAt == null && x.OpenAt >= from && x.OpenAt <= to)
            .OrderByDescending(x => x.OpenAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
            return existing;

        var interval = new TimingTransferIntervalEntity
        {
            Furnace = furnace,
            Machine = machine,
            OpenAt = openAt
        };
        db.TimingTransferIntervals.Add(interval);
        return interval;
    }

    private IEnumerable<string> GetFurnaces() => _configuration.GetSection("OpcUa:FurnaceEndpoints").GetChildren().Select(x => x.Key).Where(x => AllowedFurnaces.Contains(x, StringComparer.OrdinalIgnoreCase)).DefaultIfEmpty(AllowedFurnaces[0]).Concat(AllowedFurnaces.Skip(1)).Distinct(StringComparer.OrdinalIgnoreCase);

    private IEnumerable<string> GetMachines() => _configuration.GetSection("OpcUa:RingRollingEndpoints").GetChildren().Select(x => x.Key).DefaultIfEmpty(DefaultMachines[0]).Concat(DefaultMachines.Skip(1)).Distinct(StringComparer.OrdinalIgnoreCase);
}
