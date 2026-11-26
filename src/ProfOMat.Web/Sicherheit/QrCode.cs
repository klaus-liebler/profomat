using QRCoder;

namespace ProfOMat.Web.Sicherheit;

/// <summary>QR-Code als SVG-Daten-URI (für &lt;img&gt;; die CSP erlaubt img-src data:).</summary>
internal static class QrCode
{
    public static string SvgDatenUri(string inhalt)
    {
        using var daten = QRCodeGenerator.GenerateQrCode(inhalt, QRCodeGenerator.ECCLevel.M);
        var svg = new SvgQRCode(daten).GetGraphic(4);
        return "data:image/svg+xml;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(svg));
    }
}
