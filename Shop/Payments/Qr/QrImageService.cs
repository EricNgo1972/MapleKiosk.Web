using System.Text;
using Net.Codecrete.QrCodeGenerator;

namespace MapleKiosk.Web.Shop.Payments.Qr;

/// <summary>Renders a QR payload to an SVG data URI (pure-managed, no native libs).</summary>
public sealed class QrImageService
{
    public string ToDataUri(string payload, int border = 2)
    {
        var qr = QrCode.EncodeText(payload, QrCode.Ecc.Medium);
        var svg = qr.ToSvgString(border);
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
        return "data:image/svg+xml;base64," + base64;
    }
}
