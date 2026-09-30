namespace HALProcessRecord.ViewModels;

public sealed class TimingStatusViewModel
{
    public string Furnace { get; set; } = string.Empty;
    public string Machine { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public string StartMode { get; set; } = string.Empty;
    public DateTime? TransferStartAt { get; set; }
    public DateTime? TotalStartAt { get; set; }
    public int TransferSeconds { get; set; }
    public int TotalSeconds { get; set; }
    public int OperationSeconds { get; set; }
    public DateTime? LastCompletedAt { get; set; }
    public bool? LastDoorOpen { get; set; }
    public bool? LastDoorClosed { get; set; }
    public bool? LastAutoStartLED { get; set; }
    public bool? LastAutoStartPB { get; set; }
    public bool? LastAutoStopLED { get; set; }
    public bool? LastAutoStopPB { get; set; }
    public decimal LastMaterialForce { get; set; }
    public decimal LastMaterialTemperature { get; set; }
    public DateTime? DoorOpenSourceTimestamp { get; set; }
    public DateTime? DoorClosedSourceTimestamp { get; set; }
    public DateTime? FurnaceCapturedAt { get; set; }
    public DateTime? MachineCapturedAt { get; set; }
    public DateTime? AutoStartLEDSourceTimestamp { get; set; }
    public DateTime? AutoStopLEDSourceTimestamp { get; set; }
    public DateTime? AutoStartPBSourceTimestamp { get; set; }
    public DateTime? AutoStopPBSourceTimestamp { get; set; }
}
