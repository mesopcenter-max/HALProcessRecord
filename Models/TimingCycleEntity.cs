namespace HALProcessRecord.Models;

using HALProcessRecord.Utilities;

public class TimingCycleEntity
{
    public int Id { get; set; }
    public string Furnace { get; set; } = string.Empty;
    public string Machine { get; set; } = string.Empty;
    public string StartMode { get; set; } = string.Empty;
    public DateTime? TransferStartAt { get; set; }
    public DateTime TotalStartAt { get; set; }
    public DateTime StoppedAt { get; set; }
    public int? TransferSeconds { get; set; }
    public int TotalSeconds { get; set; }
    public int OperationSeconds { get; set; }
    public DateTime CreatedAt { get; set; } = IndiaTime.Now;
}
