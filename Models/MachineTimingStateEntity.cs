namespace HALProcessRecord.Models;

using HALProcessRecord.Utilities;

public class MachineTimingStateEntity
{
    public int Id { get; set; }
    public string Furnace { get; set; } = string.Empty;
    public string Machine { get; set; } = string.Empty;
    public bool? LastDoorOpen { get; set; }
    public bool? LastDoorClosed { get; set; }
    public int HalfDoorCloseTransitions { get; set; }
    public bool HalfDoor { get; set; }
    public bool? LastAutoStartLED { get; set; }
    public bool? LastAutoStartPB { get; set; }
    public bool? LastAutoStopLED { get; set; }
    public bool? LastAutoStopPB { get; set; }
    public decimal LastMaterialForce { get; set; }
    public DateTime? TransferStartAt { get; set; }
    public DateTime? TransferEndAt { get; set; }
    public DateTime? TotalStartAt { get; set; }
    public string? StartMode { get; set; }
    public DateTime LastUpdatedAt { get; set; } = IndiaTime.Now;
}
