using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace MapleKiosk.Web.Services;

/// <summary>
/// Validates a Canadian (A1A 1A1) or US (12345 or 12345-6789) postal code.
/// Error message is localized via <see cref="Translations"/> using the culture
/// in <see cref="ValidationCulture.Current"/>, which <c>TrialForm</c> sets.
/// </summary>
public class PostalCodeAttribute : ValidationAttribute
{
    private static readonly Regex Pattern = new(
        @"^\s*([A-Za-z]\d[A-Za-z][ -]?\d[A-Za-z]\d|\d{5}(-\d{4})?)\s*$",
        RegexOptions.Compiled);

    public override bool IsValid(object? value)
    {
        var s = value as string;
        if (string.IsNullOrWhiteSpace(s)) return true; // [Required] handles empty.
        return Pattern.IsMatch(s);
    }

    public override string FormatErrorMessage(string name)
    {
        var culture = ValidationCulture.Current.Value ?? "en";
        var dict = Translations.All.TryGetValue(culture, out var d) ? d : Translations.All["en"];
        return dict.TryGetValue("trial.v.postal", out var msg)
            ? msg
            : Translations.All["en"]["trial.v.postal"];
    }
}

/// <summary>AsyncLocal culture holder for validation attributes — set per request.</summary>
public static class ValidationCulture
{
    public static readonly System.Threading.AsyncLocal<string?> Current = new();
}
