namespace HALProcessRecord.ViewModels;

public sealed class OpcMachineStatusViewModel
{
    public string EndpointUrl { get; set; } = string.Empty;
    public string ConnectionStatus { get; set; } = "Disconnected";
    public bool AutoStartLED { get; set; }
    public bool AutoStopLED { get; set; }
    public bool AutoStartPB { get; set; }
    public bool AutoStopPB { get; set; }
    public float MaterialForce { get; set; }
    public float MaterialTemperature { get; set; }
    public DateTime? AutoStartLEDSourceTimestamp { get; set; }
    public DateTime? AutoStopLEDSourceTimestamp { get; set; }
    public DateTime? AutoStartPBSourceTimestamp { get; set; }
    public DateTime? AutoStopPBSourceTimestamp { get; set; }
    public DateTime? ReadAt { get; set; }
    public string? ErrorMessage { get; set; }
}
