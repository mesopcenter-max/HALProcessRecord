namespace HALProcessRecord.Models;

public class OperationRecordEntity
{
    public int Id { get; set; }
    public int ProcessRecordId { get; set; }
    public int SlNo { get; set; }
    public int TransferMinutes { get; set; }
    public int TransferSeconds { get; set; }
    public int TotalMinutes { get; set; }
    public int TotalSeconds { get; set; }
    public int OperationMinutes { get; set; }
    public int OperationSeconds { get; set; }
    public string Observation { get; set; } = string.Empty;
    public ProcessRecordEntity? ProcessRecord { get; set; }
}
