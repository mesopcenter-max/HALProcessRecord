namespace HALProcessRecord.Models;

using HALProcessRecord.Utilities;

public class TimingTransferIntervalEntity
{
    public long Id { get; set; }
    public string Furnace { get; set; } = string.Empty;
    public string Machine { get; set; } = string.Empty;
    public DateTime OpenAt { get; set; }
    public DateTime? CloseAt { get; set; }
    public DateTime? MachineStartAt { get; set; }
    public DateTime? MachineStopAt { get; set; }
    public int? TimingCycleId { get; set; }
    public DateTime CreatedAt { get; set; } = IndiaTime.Now;
}
