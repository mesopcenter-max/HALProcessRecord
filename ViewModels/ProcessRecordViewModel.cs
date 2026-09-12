using System.ComponentModel.DataAnnotations;

namespace HALProcessRecord.ViewModels;

public class ProcessRecordViewModel
{
    [Required]
    [DataType(DataType.Date)]
    [Display(Name = "Date")]
    public DateTime Date { get; set; } = DateTime.Today;

    [Required]
    [Display(Name = "Part No")]
    public string PartNo { get; set; } = string.Empty;

    [Required]
    [Range(1, int.MaxValue)]
    public int Qty { get; set; }

    [Required]
    [Display(Name = "Batch No")]
    public string BatchNo { get; set; } = string.Empty;

    [Required]
    [DataType(DataType.Date)]
    [Display(Name = "Loading Date")]
    public DateTime LoadingDate { get; set; } = DateTime.Today;

    [Required]
    [Display(Name = "Loading Time")]
    public TimeSpan LoadingTime { get; set; }

    [Required]
    public string Furnace { get; set; } = "RRP-10";

    [Required]
    [Range(0, 5000)]
    public decimal ForgingTemperature { get; set; }

    [Range(0, 1000)]
    public decimal TemperatureTolerance { get; set; }

    [Range(0, 5000)]
    public decimal SoakingStartTemperature { get; set; }

    [Required]
    public TimeSpan SoakingMin { get; set; }

    [Required]
    public TimeSpan SoakingMax { get; set; }

    [Required]
    public TimeSpan SoakingStartTime { get; set; }

    [Required]
    public TimeSpan MinimumSoakingComplete { get; set; }

    [Required]
    public TimeSpan OperationEndBefore { get; set; }

    [Required]
    public TimeSpan ActualOperationStartTime { get; set; }

    [Required]
    public TimeSpan ActualOperationEndTime { get; set; }

    [Required]
    public string OperatorName { get; set; } = string.Empty;

    [Required]
    public string Operation { get; set; } = string.Empty;

    [Required]
    public string Equipment { get; set; } = string.Empty;

    [Required]
    public string RingRollingStageNo { get; set; } = string.Empty;

    [Range(0, 10000)]
    public decimal? MandrelDiameter { get; set; }

    public string OtherRemarks { get; set; } = string.Empty;

    public string PreparedBy { get; set; } = string.Empty;
    public string VerifiedBy { get; set; } = string.Empty;

    public OpcUaStatusViewModel OpcUa { get; set; } = new();

    public List<OperationRowViewModel> Operations { get; set; } = new();
}

public class OperationRowViewModel
{
    public int SlNo { get; set; }

    [Range(0, 999)]
    public int TransferMinutes { get; set; }

    [Range(0, 59)]
    public int TransferSeconds { get; set; }

    [Range(0, 999)]
    public int TotalMinutes { get; set; }

    [Range(0, 59)]
    public int TotalSeconds { get; set; }

    public int OperationMinutes { get; set; }
    public int OperationSeconds { get; set; }

    public string Observation { get; set; } = string.Empty;
}
