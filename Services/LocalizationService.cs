namespace MapleKiosk.Web.Services;

public class LocalizationService
{
    public string Culture { get; private set; } = "en";

    public void SetCulture(string? culture)
    {
        Culture = culture?.ToLowerInvariant() switch
        {
            "fr" => "fr",
            "vi" => "vi",
            "ru" => "ru",
            _ => "en",
        };
    }

    public string this[string key]
    {
        get
        {
            if (Translations.All.TryGetValue(Culture, out var dict) && dict.TryGetValue(key, out var v))
                return v;
            if (Translations.All["en"].TryGetValue(key, out var en))
                return en;
            return key;
        }
    }

    // Product names stay as-is (MapleCoffee, ...) unless the culture translates one (prod.{key}.name).
    public string ProductName(SiteProduct p)
    {
        var k = $"prod.{p.Key}.name";
        var v = this[k];
        return v == k ? p.Name : v;
    }

    public string Path(string culture) => culture == "en" ? "/" : $"/{culture}";
}
