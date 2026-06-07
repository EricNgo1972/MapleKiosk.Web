using System.Globalization;
using System.Text;

namespace MapleKiosk.Web.Shop.Payments.Qr;

public record VietQrBuildInput(
    string BankCodeOrBin,
    string AccountNumber,
    string AccountName,
    decimal AmountVnd,
    string OrderReference,
    string? Note = null);

// EMVCo QR Code Specification + NAPAS VietQR overlay (public NAPAS spec v1.x).
// Payload is a sequence of (tag, length, value) triplets; tag 63 is CRC16-CCITT
// over everything up to and including its own "6304" header.
// Copied self-contained into the site so checkout has no monorepo dependency.
public static class VietQrPayloadBuilder
{
    private static readonly Dictionary<string, string> BankBinByAlias = new(StringComparer.OrdinalIgnoreCase)
    {
        ["VCB"] = "970436",
        ["TCB"] = "970407",
        ["MB"] = "970422",
        ["MBB"] = "970422",
        ["BIDV"] = "970418",
        ["ACB"] = "970416",
        ["VTB"] = "970415",
        ["CTG"] = "970415",
        ["TPB"] = "970423",
        ["VPB"] = "970432",
    };

    public static string Build(VietQrBuildInput input)
    {
        if (string.IsNullOrWhiteSpace(input.BankCodeOrBin))
            throw new ArgumentException("BankCodeOrBin required", nameof(input));
        if (string.IsNullOrWhiteSpace(input.AccountNumber))
            throw new ArgumentException("AccountNumber required", nameof(input));
        if (string.IsNullOrWhiteSpace(input.AccountName))
            throw new ArgumentException("AccountName required", nameof(input));
        if (input.AmountVnd <= 0)
            throw new ArgumentException("AmountVnd must be > 0", nameof(input));
        if (string.IsNullOrWhiteSpace(input.OrderReference))
            throw new ArgumentException("OrderReference required", nameof(input));

        var bin = ResolveBin(input.BankCodeOrBin);

        var beneficiaryOrg = Tlv("00", bin) + Tlv("01", input.AccountNumber);
        var merchantAccountInfo =
            Tlv("00", "A000000727") +
            Tlv("01", beneficiaryOrg) +
            Tlv("02", "QRIBFTTA");

        var additionalData = Tlv("01", Sanitize(input.OrderReference, 25));
        if (!string.IsNullOrWhiteSpace(input.Note))
            additionalData += Tlv("08", Sanitize(input.Note!, 50));

        var sb = new StringBuilder();
        sb.Append(Tlv("00", "01"));
        sb.Append(Tlv("01", "12"));
        sb.Append(Tlv("38", merchantAccountInfo));
        sb.Append(Tlv("53", "704"));
        sb.Append(Tlv("54", FormatAmount(input.AmountVnd)));
        sb.Append(Tlv("58", "VN"));
        sb.Append(Tlv("59", Sanitize(input.AccountName, 25)));
        sb.Append(Tlv("62", additionalData));

        sb.Append("6304");
        var crc = Crc16Ccitt(sb.ToString());
        sb.Append(crc);

        return sb.ToString();
    }

    private static string ResolveBin(string codeOrBin)
        => BankBinByAlias.TryGetValue(codeOrBin.Trim(), out var bin) ? bin : codeOrBin.Trim();

    private static string Tlv(string tag, string value)
    {
        if (value.Length > 99)
            throw new ArgumentException($"TLV value too long for tag {tag}: {value.Length} chars");
        return tag + value.Length.ToString("D2", CultureInfo.InvariantCulture) + value;
    }

    private static string FormatAmount(decimal amountVnd)
    {
        var whole = Math.Round(amountVnd, 0, MidpointRounding.AwayFromZero);
        return ((long)whole).ToString(CultureInfo.InvariantCulture);
    }

    private static string Sanitize(string input, int maxLen)
    {
        var trimmed = input.Trim();
        return trimmed.Length <= maxLen ? trimmed : trimmed[..maxLen];
    }

    // CRC-16/CCITT-FALSE: poly=0x1021, init=0xFFFF, refIn=false, refOut=false, xorOut=0x0000
    private static string Crc16Ccitt(string input)
    {
        ushort crc = 0xFFFF;
        foreach (var b in Encoding.ASCII.GetBytes(input))
        {
            crc ^= (ushort)(b << 8);
            for (var i = 0; i < 8; i++)
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc.ToString("X4", CultureInfo.InvariantCulture);
    }
}
