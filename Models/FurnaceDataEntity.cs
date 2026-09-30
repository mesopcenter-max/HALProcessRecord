namespace HALProcessRecord.Models;

using HALProcessRecord.Utilities;

public class FurnaceDataEntity
{
    public long Id { get; set; }
    public string Furnace { get; set; } = string.Empty;
    public bool DoorOpen { get; set; }
    public bool DoorClosed { get; set; }
    public bool HalfDoor { get; set; }
    public string Code { get; set; } = string.Empty;
    public DateTime? DoorOpenSourceTimestamp { get; set; }
    public DateTime? DoorClosedSourceTimestamp { get; set; }
    public DateTime SourceReadAt { get; set; }
    public DateTime CapturedAt { get; set; } = IndiaTime.Now;
}
