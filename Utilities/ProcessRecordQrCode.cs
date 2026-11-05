using QRCoder;

namespace HALProcessRecord.Utilities;

public static class ProcessRecordQrCode
{
    public static string GenerateDataUri(int recordId)
    {
        using var qrGenerator = new QRCodeGenerator();
        using var qrCodeData = qrGenerator.CreateQrCode(
            $"HAL Process Record ID: {recordId}",
            QRCodeGenerator.ECCLevel.Q);
        var pngQrCode = new PngByteQRCode(qrCodeData);
        var pngBytes = pngQrCode.GetGraphic(pixelsPerModule: 8);

        return $"data:image/png;base64,{Convert.ToBase64String(pngBytes)}";
    }
}
