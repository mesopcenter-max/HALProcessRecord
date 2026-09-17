namespace HALProcessRecord.ViewModels;

public sealed class OpcUaStatusViewModel
{
    public string EndpointUrl { get; set; } = string.Empty;
    public string ConnectionStatus { get; set; } = "Disconnected";
    public string Status { get; set; } = "Unavailable";
    public bool DoorOpen { get; set; }
    public bool DoorClosed { get; set; }
    public string Code { get; set; } = string.Empty;
    public string DoorOpenNodeId { get; set; } = string.Empty;
    public string DoorCloseNodeId { get; set; } = string.Empty;
    public string CodeNodeId { get; set; } = string.Empty;
    public DateTime? DoorOpenSourceTimestamp { get; set; }
    public DateTime? DoorClosedSourceTimestamp { get; set; }
    public DateTime? ReadAt { get; set; }
    public string? ErrorMessage { get; set; }
}
