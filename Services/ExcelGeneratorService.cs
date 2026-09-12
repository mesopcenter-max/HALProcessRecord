using ClosedXML.Excel;
using HALProcessRecord.ViewModels;

namespace HALProcessRecord.Services;

public class ExcelGeneratorService : IExcelGeneratorService
{
    private readonly IWebHostEnvironment _environment;

    public ExcelGeneratorService(IWebHostEnvironment environment)
    {
        _environment = environment;
    }

    public byte[] Generate(ProcessRecordViewModel model)
    {
        var templatePath = Path.Combine(
            _environment.ContentRootPath,
            "Templates",
            "HAL_Ring_Rolling_Process_Record_GE1.xlsx");

        if (!File.Exists(templatePath))
            throw new FileNotFoundException("Excel template was not found.", templatePath);

        using var workbook = new XLWorkbook(templatePath);
        var ws = workbook.Worksheet("Process Record");

        // Header / basic information. These coordinates match the uploaded template.
        ws.Cell("D9").Value = model.Date;
        ws.Cell("D9").Style.DateFormat.Format = "dd-MM-yyyy";
        ws.Cell("D10").Value = model.PartNo;
        ws.Cell("D11").Value = model.Qty;
        ws.Cell("D12").Value = model.BatchNo;
        ws.Cell("D13").Value = model.LoadingDate;
        ws.Cell("D13").Style.DateFormat.Format = "dd-MM-yyyy";
        ws.Cell("D14").Value = model.LoadingTime;
        ws.Cell("D14").Style.DateFormat.Format = "hh:mm";

        ws.Cell("J9").Value = model.Furnace;
        ws.Cell("J10").Value = model.ForgingTemperature;
        ws.Cell("J11").Value = model.TemperatureTolerance;
        ws.Cell("J12").Value = model.SoakingStartTemperature;
        ws.Cell("J13").Value = model.SoakingMin;
        ws.Cell("J13").Style.DateFormat.Format = "hh:mm";
        ws.Cell("J14").Value = model.SoakingMax;
        ws.Cell("J14").Style.DateFormat.Format = "hh:mm";

        ws.Cell("D16").Value = model.SoakingStartTime;
        ws.Cell("D16").Style.DateFormat.Format = "hh:mm";
        ws.Cell("D17").Value = model.MinimumSoakingComplete;
        ws.Cell("D17").Style.DateFormat.Format = "hh:mm";
        ws.Cell("D18").Value = model.OperationEndBefore;
        ws.Cell("D18").Style.DateFormat.Format = "hh:mm";

        ws.Cell("J16").Value = model.ActualOperationStartTime;
        ws.Cell("J16").Style.DateFormat.Format = "hh:mm";
        ws.Cell("J17").Value = model.ActualOperationEndTime;
        ws.Cell("J17").Style.DateFormat.Format = "hh:mm";
        ws.Cell("J18").Value = model.OperatorName;

        // Operation check marks: F21:F23.
        foreach (var row in new[] { 21, 22, 23 })
            ws.Cell($"F{row}").Value = string.Empty;

        var operationRows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Upset & Pierce"] = 21,
            ["Axial Pressing"] = 22,
            ["Flattening"] = 23
        };

        if (operationRows.TryGetValue(model.Operation, out var operationRow))
            ws.Cell($"F{operationRow}").Value = "✓";

        // Equipment check marks: M21:M23.
        foreach (var row in new[] { 21, 22, 23 })
            ws.Cell($"M{row}").Value = string.Empty;

        var equipmentRows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["1500 Ton Press"] = 21,
            ["3000 Ton Press"] = 22,
            ["4000 Ton Press"] = 23
        };

        if (equipmentRows.TryGetValue(model.Equipment, out var equipmentRow))
            ws.Cell($"M{equipmentRow}").Value = "✓";

        ws.Cell("D25").Value = model.RingRollingStageNo;
        ws.Cell("D26").Value = model.MandrelDiameter;
        ws.Cell("C27").Value = "Door Open (Value / Source Timestamp)";
        ws.Cell("D27").Value = FormatDoorSignal(model.OpcUa.DoorOpen, model.OpcUa.DoorOpenSourceTimestamp);
        ws.Cell("I27").Value = "Door Close (Value / Source Timestamp)";
        ws.Cell("J27").Value = FormatDoorSignal(model.OpcUa.DoorClosed, model.OpcUa.DoorClosedSourceTimestamp);
        ws.Cell("D44").Value = model.OtherRemarks;

        // Keep the signature areas but replace the blank lines with names.
        ws.Cell("A46").Value = $"Prepared / Recorded by:  {model.PreparedBy}";
        ws.Cell("H46").Value = $"Verified by:  {model.VerifiedBy}";

        // The template has 13 operation rows, 30 through 42.
        for (var row = 30; row <= 42; row++)
        {
            ClearOperationRow(ws, row);
        }

        foreach (var op in model.Operations.Take(13))
        {
            var row = 29 + op.SlNo;
            if (row < 30 || row > 42)
                continue;

            var operation = CalculateOperationTime(
                op.TransferMinutes,
                op.TransferSeconds,
                op.TotalMinutes,
                op.TotalSeconds);

            ws.Cell($"A{row}").Value = op.SlNo;
            ws.Cell($"C{row}").Value = op.TransferMinutes;
            ws.Cell($"D{row}").Value = op.TransferSeconds;
            ws.Cell($"E{row}").Value = op.TotalMinutes;
            ws.Cell($"F{row}").Value = op.TotalSeconds;
            ws.Cell($"G{row}").Value = operation.minutes;
            ws.Cell($"H{row}").Value = operation.seconds;
            ws.Cell($"I{row}").Value = op.Observation;
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static void ClearOperationRow(IXLWorksheet ws, int row)
    {
        ws.Cell($"A{row}").Clear(XLClearOptions.Contents);
        ws.Cell($"C{row}").Clear(XLClearOptions.Contents);
        ws.Cell($"D{row}").Clear(XLClearOptions.Contents);
        ws.Cell($"E{row}").Clear(XLClearOptions.Contents);
        ws.Cell($"F{row}").Clear(XLClearOptions.Contents);
        ws.Cell($"G{row}").Clear(XLClearOptions.Contents);
        ws.Cell($"H{row}").Clear(XLClearOptions.Contents);
        ws.Cell($"I{row}").Clear(XLClearOptions.Contents);
    }

    private static (int minutes, int seconds) CalculateOperationTime(
        int transferMinutes,
        int transferSeconds,
        int totalMinutes,
        int totalSeconds)
    {
        var transfer = TimeSpan.FromMinutes(transferMinutes) + TimeSpan.FromSeconds(transferSeconds);
        var total = TimeSpan.FromMinutes(totalMinutes) + TimeSpan.FromSeconds(totalSeconds);
        var operation = total - transfer;

        if (operation < TimeSpan.Zero)
            throw new ArgumentException("Total Time must be greater than or equal to Transfer Time.");

        return ((int)operation.TotalMinutes, operation.Seconds);
    }

    private static string FormatDoorSignal(bool value, DateTime? sourceTimestamp)
    {
        var timestamp = sourceTimestamp?.ToString("dd-MM-yyyy HH:mm:ss") ?? "-";
        return $"{value} / {timestamp}";
    }
}
