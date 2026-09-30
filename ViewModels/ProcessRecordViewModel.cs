using System.ComponentModel.DataAnnotations;

namespace HALProcessRecord.ViewModels;

using HALProcessRecord.Utilities;

public class ProcessRecordViewModel
{
    [DataType(DataType.Date)]
    [Display(Name = "Date")]
    public DateTime Date { get; set; } = IndiaTime.Now.Date;

    [Display(Name = "Part No")]
    public string PartNo { get; set; } = string.Empty;

    [Range(1, int.MaxValue)]
    public int Qty { get; set; }

    [Display(Name = "Batch No")]
    public string BatchNo { get; set; } = string.Empty;

    [DataType(DataType.Date)]
    [Display(Name = "Loading Date")]
    public DateTime LoadingDate { get; set; } = IndiaTime.Now.Date;

    [Display(Name = "Loading Time")]
    public TimeSpan LoadingTime { get; set; }

    public string Furnace { get; set; } = "RRP_10";

    public string Machine { get; set; } = "MRB_3000";

    [Range(0, 5000)]
    public decimal ForgingTemperature { get; set; }

    [Range(0, 1000)]
    public decimal TemperatureTolerance { get; set; }

    [Range(0, 5000)]
    public decimal SoakingStartTemperature { get; set; }

    public TimeSpan SoakingMin { get; set; }

    public TimeSpan SoakingMax { get; set; }

    public TimeSpan SoakingStartTime { get; set; }

    public TimeSpan MinimumSoakingComplete { get; set; }

    public TimeSpan OperationEndBefore { get; set; }

    public TimeSpan ActualOperationStartTime { get; set; }

    public TimeSpan ActualOperationEndTime { get; set; }

    public string OperatorName { get; set; } = string.Empty;

    public string Operation { get; set; } = string.Empty;

    public string Equipment { get; set; } = string.Empty;

    public string RingRollingStageNo { get; set; } = string.Empty;

    [Range(0, 10000)]
    public decimal? MandrelDiameter { get; set; }

    public string? OtherRemarks { get; set; }

    public string PreparedBy { get; set; } = string.Empty;
    public string VerifiedBy { get; set; } = string.Empty;

    public OpcUaStatusViewModel OpcUa { get; set; } = new();

    public OpcMachineStatusViewModel RingRollingOpcUa { get; set; } = new();

    public List<OperationRowViewModel> Operations { get; set; } = new();
}

public class OperationRowViewModel
{
    public int SlNo { get; set; }

    [Range(0, 999)]
    public int TransferMinutes { get; set; }

    [Range(0, 59)]
    public int TransferSeconds { get; set; }

    [Range(0, 999)]
    public int TotalMinutes { get; set; }

    [Range(0, 59)]
    public int TotalSeconds { get; set; }

    public int OperationMinutes { get; set; }
    public int OperationSeconds { get; set; }

    public string? Observation { get; set; }

    public DateTime? FurnaceDoorOpenTime { get; set; }
    public DateTime? FurnaceDoorCloseTime { get; set; }
    public DateTime? AutoStartTime { get; set; }
    public DateTime? StopTime { get; set; }
    public decimal? Temperature { get; set; }
    public decimal? HighestTemperature { get; set; }
}

public class FurnaceSignalChangeViewModel
{
    public DateTime CapturedAt { get; set; }
    public bool DoorOpen { get; set; }
    public bool DoorClosed { get; set; }
    public bool HalfDoor { get; set; }
    public string Change { get; set; } = string.Empty;
}

public class MachineSignalChangeViewModel
{
    public DateTime CapturedAt { get; set; }
    public bool AutoStartLed { get; set; }
    public bool AutoStartPushButton { get; set; }
    public bool AutoStopLed { get; set; }
    public bool AutoStopPushButton { get; set; }
    public decimal MaterialForce { get; set; }
    public decimal MaterialTemperature { get; set; }
    public string Change { get; set; } = string.Empty;
}
