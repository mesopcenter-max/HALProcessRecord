namespace HALProcessRecord.Models;

using HALProcessRecord.Utilities;

public class MachineDataEntity
{
    public long Id { get; set; }
    public string Machine { get; set; } = string.Empty;
    public bool AutoStartLed { get; set; }
    public bool AutoStopLed { get; set; }
    public bool AutoStartPushButton { get; set; }
    public bool AutoStopPushButton { get; set; }
    public decimal MaterialForce { get; set; }
    public decimal MaterialTemperature { get; set; }
    public DateTime? AutoStartLedSourceTimestamp { get; set; }
    public DateTime? AutoStopLedSourceTimestamp { get; set; }
    public DateTime? AutoStartPushButtonSourceTimestamp { get; set; }
    public DateTime? AutoStopPushButtonSourceTimestamp { get; set; }
    public DateTime SourceReadAt { get; set; }
    public DateTime CapturedAt { get; set; } = IndiaTime.Now;
}
