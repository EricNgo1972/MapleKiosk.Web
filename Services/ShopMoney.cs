using System.Globalization;

namespace MapleKiosk.Web.Services;

/// <summary>Shop amounts (CAD) as the pricing pages write them: "$" in the visitor's number format.</summary>
public static class ShopMoney
{
    public static string Format(decimal amount, string culture)
    {
        var locale = culture switch { "fr" => "fr-CA", "vi" => "vi-VN", "ru" => "ru-RU", _ => "en-CA" };
        var nf = (NumberFormatInfo)new CultureInfo(locale).NumberFormat.Clone();
        nf.CurrencySymbol = "$";
        nf.CurrencyDecimalDigits = amount == decimal.Truncate(amount) ? 0 : 2;
        return amount.ToString("C", nf);
    }
}
