namespace HALProcessRecord.Models;

public class ProcessRecordEntity
{
    public int Id { get; set; }
    public DateTime Date { get; set; }
    public string PartNo { get; set; } = string.Empty;
    public int Qty { get; set; }
    public string BatchNo { get; set; } = string.Empty;
    public DateTime LoadingDate { get; set; }
    public TimeSpan LoadingTime { get; set; }
    public string Furnace { get; set; } = string.Empty;
    public decimal ForgingTemperature { get; set; }
    public decimal TemperatureTolerance { get; set; }
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
    public decimal? MandrelDiameter { get; set; }
    public string OtherRemarks { get; set; } = string.Empty;
    public string PreparedBy { get; set; } = string.Empty;
    public string VerifiedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public List<OperationRecordEntity> Operations { get; set; } = new();
}
