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
        var ws = workbook.Worksheet("Process Record (2)");
        for (var column = 9; column <= 12; column++)
            ws.Column(column).Width = 22;

        var extraRows = Math.Max(0, model.Operations.Count - 20);
        if (extraRows > 0)
            ws.Row(51).InsertRowsAbove(extraRows);
        var lastOperationRow = 49 + extraRows;
        var remarksRow = 51 + extraRows;
        var signatureRow = 53 + extraRows;

        if (!string.IsNullOrWhiteSpace(model.QrCode))
        {
            const string pngDataUriPrefix = "data:image/png;base64,";
            var qrCodeBase64 = model.QrCode.StartsWith(pngDataUriPrefix, StringComparison.Ordinal)
                ? model.QrCode[pngDataUriPrefix.Length..]
                : throw new InvalidOperationException("The saved process record QR code is not a PNG data URI.");
            using var qrCodeStream = new MemoryStream(Convert.FromBase64String(qrCodeBase64));
            ws.Row(7).Height = 58;
            ws.AddPicture(qrCodeStream).MoveTo(ws.Cell("O7")).WithSize(75, 75);
        }

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

        ws.Cell("O9").Value = model.Furnace;
        ws.Cell("O10").Value = model.ForgingTemperature;
        ws.Cell("O11").Value = model.TemperatureTolerance;
        ws.Cell("O12").Value = model.SoakingStartTemperature;
        ws.Cell("O13").Value = model.SoakingMin;
        ws.Cell("O13").Style.DateFormat.Format = "hh:mm";
        ws.Cell("O14").Value = model.SoakingMax;
        ws.Cell("O14").Style.DateFormat.Format = "hh:mm";

        ws.Cell("D16").Value = model.SoakingStartTime;
        ws.Cell("D16").Style.DateFormat.Format = "hh:mm";
        ws.Cell("D17").Value = model.MinimumSoakingComplete;
        ws.Cell("D17").Style.DateFormat.Format = "hh:mm";
        ws.Cell("D18").Value = model.OperationEndBefore;
        ws.Cell("D18").Style.DateFormat.Format = "hh:mm";

        ws.Cell("O16").Value = model.ActualOperationStartTime;
        ws.Cell("O16").Style.DateFormat.Format = "hh:mm";
        ws.Cell("O17").Value = model.ActualOperationEndTime;
        ws.Cell("O17").Style.DateFormat.Format = "hh:mm";
        ws.Cell("O18").Value = model.OperatorName;

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

        // Equipment check marks: R21:R23.
        foreach (var row in new[] { 21, 22, 23 })
            ws.Cell($"R{row}").Value = string.Empty;

        var equipmentRows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["1500 Ton Press"] = 21,
            ["3000 Ton Press"] = 22,
            ["4000 Ton Press"] = 23
        };

        if (equipmentRows.TryGetValue(model.Equipment, out var equipmentRow))
            ws.Cell($"R{equipmentRow}").Value = "✓";

        ws.Cell("G25").Value = model.RingRollingStageNo;
        ws.Cell("G26").Value = model.MandrelDiameter;
        ws.Cell($"D{remarksRow}").Value = model.OtherRemarks;

        // Keep the signature areas but replace the blank lines with names.
        ws.Cell($"A{signatureRow}").Value = $"Prepared / Recorded by:  {model.PreparedBy}";
        ws.Cell($"H{signatureRow}").Value = $"Verified by:  {model.VerifiedBy}";

        for (var row = 30; row <= lastOperationRow; row++)
            ClearOperationRow(ws, row);

        foreach (var op in model.Operations)
        {
            var row = 29 + op.SlNo;
            if (row < 30 || row > lastOperationRow)
                continue;

            (int minutes, int seconds) operation = op.OperationMinutes > 0 || op.OperationSeconds > 0
                ? (op.OperationMinutes, op.OperationSeconds)
                : CalculateOperationTime(
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
            SetDateTimeCell(ws.Cell($"I{row}"), op.FurnaceDoorOpenTime);
            SetDateTimeCell(ws.Cell($"J{row}"), op.FurnaceDoorCloseTime);
            SetDateTimeCell(ws.Cell($"K{row}"), op.AutoStartTime);
            SetDateTimeCell(ws.Cell($"L{row}"), op.StopTime);
            ws.Cell($"M{row}").Value = op.Temperature;
            ws.Cell($"N{row}").Value = op.Observation;
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
        ws.Cell($"J{row}").Clear(XLClearOptions.Contents);
        ws.Cell($"K{row}").Clear(XLClearOptions.Contents);
        ws.Cell($"L{row}").Clear(XLClearOptions.Contents);
        ws.Cell($"M{row}").Clear(XLClearOptions.Contents);
        ws.Cell($"N{row}").Clear(XLClearOptions.Contents);
    }

    private static void SetDateTimeCell(IXLCell cell, DateTime? value)
    {
        if (value.HasValue)
        {
            cell.Value = value.Value;
            cell.Style.DateFormat.Format = "dd-MM-yyyy HH:mm:ss";
        }
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
}
