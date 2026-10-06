using System.Globalization;
using System.Text;
using MapleKiosk.Web.Services;
using MapleKiosk.Web.Shop.Catalog;
using Quote = System.Collections.Generic.IReadOnlyList<(MapleKiosk.Web.Shop.Catalog.CatalogCategory Category, System.Collections.Generic.IReadOnlyList<MapleKiosk.Web.Shop.Catalog.AppProduct> Items)>;

namespace MapleKiosk.Web.Assistant;

/// <summary>
/// What MapleKiosk the company sells and how to reach it, as one Markdown document: the assistant's
/// grounding, and the text to give the phone agent (served at /assistant/knowledge.md).
///
/// <para>Built from what the site itself says — the English copy of the pages people can open (home, the
/// four trades, the AI voice agent), the catalog's prices (/shop/admin), the pricing pages' wording — so the assistant and the
/// website cannot disagree. Copy no page shows any more (the old SaaS/on-premise price table, the old FAQ)
/// is left out on purpose. Facts the site doesn't carry yet (a phone number, hours) are added by the team in
/// the keyvalue row Assistant/Knowledge (/chats → Settings; or env ASSISTANT_KNOWLEDGE), read every few minutes.</para>
/// </summary>
public sealed class SiteKnowledge : IAssistantGrounding
{
    private static readonly TimeSpan ExtraTtl = TimeSpan.FromMinutes(5);

    // Each product's page copy lives under its own translation prefix.
    private static readonly Dictionary<string, string> PagePrefix = new()
    {
        ["coffee"] = "cof", ["nails"] = "spa", ["resto"] = "rp", ["garage"] = "gar", ["agent"] = "ag",
    };

    // Free test-drive apps, from the product pages.
    private static readonly Dictionary<string, string> TestDrive = new()
    {
        ["coffee"] = "https://coffee.maplekiosk.ca", ["nails"] = "https://nails.maplekiosk.ca",
    };

    private readonly CatalogStore _catalog;

    private string? _site;
    private string? _extra;
    private DateTime _extraAtUtc;

    public SiteKnowledge(CatalogStore catalog) => _catalog = catalog;

    public async Task<string?> ForCustomerAsync(ConversationChannel channel, CancellationToken ct = default)
    {
        // Rebuilt with the extra facts, so a price changed in the catalog reaches the assistant within minutes.
        if (_site is null || _extra is null || DateTime.UtcNow - _extraAtUtc > ExtraTtl)
        {
            _site = Build(await _catalog.GetGroupsAsync(ct));
            _extra = await KeyValueTable.ResolveAsync("ASSISTANT_KNOWLEDGE", "Assistant", "Knowledge");
            _extraAtUtc = DateTime.UtcNow;
        }

        return string.IsNullOrWhiteSpace(_extra)
            ? _site
            : _site + "\n\n## More facts from the MapleKiosk team (these win over anything above)\n\n" + _extra.Trim();
    }

    /// <summary>Re-read the team's extra facts on the next message (after an admin saves them).</summary>
    public void Invalidate() => _extra = null;

    private static Dictionary<string, string> En => Translations.All["en"];

    private static string Tr(string key) => En.TryGetValue(key, out var v) ? v : "";

    // Pricing wording: pr.{product}.{key}, falling back to the salon wording pr.{key} (as PricingBuilder).
    private static string Pr(string product, string key) =>
        En.TryGetValue($"pr.{product}.{key}", out var own) ? own : Tr($"pr.{key}");

    private static string Money(decimal d) => "$" + d.ToString(d == decimal.Truncate(d) ? "#,0" : "#,0.00", CultureInfo.InvariantCulture);

    private static string Build(Quote quote)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# MapleKiosk — knowledge for the assistant");
        sb.AppendLine();

        sb.AppendLine("## The company and how to reach it");
        sb.AppendLine("- MapleKiosk is point-of-sale and front-desk software built one trade at a time, by Kiosque Érable Inc., a Canadian company in Longueuil, Québec.");
        sb.AppendLine("- " + Tr("hp.co.title") + " " + Tr("hp.co.text"));
        sb.AppendLine("- Address: 507 Rue Montcalm, Longueuil, Québec J4J 2L3, Canada.");
        sb.AppendLine("- Email: sales@maplekiosk.ca (sales and general questions). Privacy questions: dev@maplekiosk.ca.");
        sb.AppendLine("- Website: www.maplekiosk.ca, in English, French, Vietnamese and Russian.");
        sb.AppendLine("- Phone: no phone number is published on the website yet. Give the email instead.");
        sb.AppendLine("- To get started: the “Book a demo” button, on every page, opens a short form; the team follows up by email. Or write to sales@maplekiosk.ca.");
        sb.AppendLine("- Free test drives, no sign-up: MapleSPA at https://nails.maplekiosk.ca, MapleCoffee at https://coffee.maplekiosk.ca.");
        sb.AppendLine("- Legal pages: privacy policy /privacy, terms /terms.");
        sb.AppendLine();

        sb.AppendLine("## What the home page says (/)");
        foreach (var k in new[] { "hp.sub", "hp.coffee.line", "hp.nails.line", "hp.resto.line", "hp.garage.line", "hp.ai.title", "hp.ai.coffee.text", "hp.ai.nails.text" })
            if (Tr(k) is { Length: > 0 } v) sb.AppendLine("- " + v);
        sb.AppendLine();

        sb.AppendLine("## Products");
        foreach (var p in SiteProducts.All)
        {
            sb.AppendLine();
            sb.AppendLine($"### {p.Name} — {Tr($"prod.{p.Key}.for")}");
            sb.AppendLine($"- Page: /{p.Slug}" + (p.Group == ProductGroup.Trade ? $" · Pricing and quote builder: /{p.Slug}/pricing" : ""));
            if (TestDrive.TryGetValue(p.Key, out var td)) sb.AppendLine($"- Free test drive: {td}");
            if (p.Group == ProductGroup.AddOn) sb.AppendLine("- Sold on its own, or already built into every MapleKiosk system.");
            if (PagePrefix.TryGetValue(p.Key, out var prefix))
                foreach (var line in PageCopy(prefix)) sb.AppendLine("- " + line);
        }
        sb.AppendLine();

        AppendPricing(sb, quote);
        return sb.ToString().TrimEnd();
    }

    // A page's copy as it reads, in key order, without the labels around it (picture descriptions,
    // "Watch" buttons, language pickers, titles for search engines).
    private static IEnumerable<string> PageCopy(string prefix)
    {
        static bool Skip(string key)
        {
            var last = key[(key.LastIndexOf('.') + 1)..];
            return last is "alt" or "watch" or "lang" or "only" || key.Contains(".cta") || key.Contains(".meta.title")
                || key.EndsWith(".hero.watch") || key.EndsWith(".product");
        }

        return En.Where(kv => kv.Key.StartsWith(prefix + ".") && !Skip(kv.Key) && !kv.Value.Contains("{0}"))
                 .Select(kv => kv.Value.Trim())
                 .Where(v => v.Length > 2)
                 .Distinct();
    }

    // The marketing plans are the same for every trade, but their copy was written for salons.
    private static string Neutral(string text) => text.Replace("salon", "business").Replace("clients", "customers");

    // The catalog: each category with its items, as the pricing pages and the shop sell them (CatalogStore.GetGroupsAsync).
    private static string Every(AppProduct i) => i.BillingInterval switch
    {
        BillingIntervals.Monthly => "/month",
        BillingIntervals.Yearly => "/year",
        _ => " one time",
    };

    private static void AppendPricing(StringBuilder sb, Quote quote)
    {
        sb.AppendLine("## Pricing");
        sb.AppendLine("- Every trade (MapleSPA, MapleCoffee, MaplePOS, MapleGarage) has the same packages and the same prices; only the names and features are worded for the trade.");
        sb.AppendLine("- " + Tr("pr.doc.t1") + " " + Tr("pr.doc.t2") + " The visitor picks what they need on the pricing page; the bill adds up (one-time setup, the monthly total, and the first payment = setup + first month). They can print it as a quote (valid 30 days) or buy it online by card right there: the first payment now, monthly plans renewing automatically.");
        sb.AppendLine("- MapleSPA's pricing page also offers the full 4-page salon quote brochure as a PDF (in Vietnamese): /docs/MapleSPA-salon-quote.pdf.");
        sb.AppendLine("- The AI Voice Agent has no price list of its own on the site: the AI plans below include the AI phone assistant; for the agent on its own, ask sales.");

        foreach (var p in SiteProducts.In(ProductGroup.Trade))
        {
            var k = p.Key;
            // Shared copy was written for salons: reword it for the other trades.
            string Neutral(string text) => k == "nails" ? text : SiteKnowledge.Neutral(text);
            sb.AppendLine();
            sb.AppendLine($"### {p.Name} pricing (/{p.Slug}/pricing)");
            foreach (var (cat, items) in quote)
            {
                var title = Pr(k, $"{cat.Key}.title") is { Length: > 0 } t ? t : cat.Name;
                var sub = Pr(k, $"{cat.Key}.sub");
                sb.AppendLine($"{title} ({(cat.PickOne ? "pick one" : "pick any")}){(sub.Length > 0 ? ": " + Neutral(sub) : ":")}");
                foreach (var i in items)
                {
                    var key = $"{cat.Key}.{i.CopyKey}";
                    var name = Pr(k, $"{key}.t") is { Length: > 0 } n ? n : i.Name;
                    var points = Enumerable.Range(1, 9).Select(x => Pr(k, $"{key}.d{x}")).TakeWhile(x => x.Length > 0).ToList();
                    var about = new[] { Pr(k, $"{key}.goal"), Pr(k, $"{key}.d") }.FirstOrDefault(x => x.Length > 0)
                                ?? (points.Count > 0 ? string.Join(" ", points) : i.Description ?? string.Join("; ", i.Features));
                    var facts = string.Join("; ", new[] { "freq", "format", "maps", "channels", "print", "report" }
                        .Where(f => Pr(k, $"{key}.{f}").Length > 0)
                        .Select(f => $"{Pr(k, $"{cat.Key}.l.{f}")}: {Neutral(Pr(k, $"{key}.{f}"))}"));
                    sb.AppendLine($"- {name}: {Money(i.Price)}{Every(i)}{(i.Recommended ? " (recommended)" : "")}. {Neutral(about)}{(facts.Length > 0 ? " " + facts + "." : "")}");
                }
                if (Pr(k, $"{cat.Key}.over") is { Length: > 0 } over) sb.AppendLine("- " + over);
            }
        }

        sb.AppendLine();
        sb.AppendLine("### Card payments (not part of the bill)");
        foreach (var r in new[] { "pr.pay.r1", "pr.pay.r2", "pr.pay.r3" }) sb.AppendLine("- " + Tr(r));
    }
}
