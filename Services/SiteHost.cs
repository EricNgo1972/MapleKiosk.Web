namespace MapleKiosk.Web.Services;

/// <summary>
/// The site's public address, www.maplekiosk.ca: the one search engines and AI crawlers should index.
/// Every page names its address here as canonical, whichever host served it, and any host other than
/// this one or maplekiosk.ca (web.maplekiosk.ca is the development box, localhost) marks its pages noindex.
/// </summary>
public static class SiteHost
{
    public const string Public = "www.maplekiosk.ca";
    public const string Root = "https://" + Public + "/";

    public static bool IsPublic(string baseUri)
    {
        var host = new Uri(baseUri).Host;
        return host.Equals(Public, StringComparison.OrdinalIgnoreCase)
            || host.Equals("maplekiosk.ca", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The page's address on the public site: same path, no query or fragment.</summary>
    public static string Canonical(string baseRelativePath)
    {
        var path = baseRelativePath.Split('?', '#')[0];
        return Root + path;
    }
}
