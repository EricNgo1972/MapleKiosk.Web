using System.Globalization;
using System.Text;
using MapleKiosk.Web.Services;

namespace MapleKiosk.Web.Assistant;

/// <summary>
/// What MapleKiosk the company sells and how to reach it, as one Markdown document: the assistant's
/// grounding, and the text to give the phone agent (served at /assistant/knowledge.md).
///
/// <para>Built from what the site itself says — the English copy of the pages people can open (home, the
/// four trades, the AI voice agent), the price book, the pricing pages' wording — so the assistant and the
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

    private static readonly Lazy<string> Site = new(Build);

    private string? _extra;
    private DateTime _extraAtUtc;

    public async Task<string?> ForCustomerAsync(ConversationChannel channel, CancellationToken ct = default)
    {
        if (_extra is null || DateTime.UtcNow - _extraAtUtc > ExtraTtl)
        {
            _extra = await KeyValueTable.ResolveAsync("ASSISTANT_KNOWLEDGE", "Assistant", "Knowledge");
            _extraAtUtc = DateTime.UtcNow;
        }

        return string.IsNullOrWhiteSpace(_extra)
            ? Site.Value
            : Site.Value + "\n\n## More facts from the MapleKiosk team (these win over anything above)\n\n" + _extra.Trim();
    }

    /// <summary>Re-read the team's extra facts on the next message (after an admin saves them).</summary>
    public void Invalidate() => _extra = null;

    private static Dictionary<string, string> En => Translations.All["en"];

    private static string Tr(string key) => En.TryGetValue(key, out var v) ? v : "";

    // Pricing wording: pr.{product}.{key}, falling back to the salon wording pr.{key} (as PricingBuilder).
    private static string Pr(string product, string key) =>
        En.TryGetValue($"pr.{product}.{key}", out var own) ? own : Tr($"pr.{key}");

    private static string Usd(decimal d) => "$" + d.ToString(d == decimal.Truncate(d) ? "#,0" : "#,0.00", CultureInfo.InvariantCulture);

    private static string Build()
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

        AppendPricing(sb);
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

    private static void AppendPricing(StringBuilder sb)
    {
        sb.AppendLine("## Pricing");
        sb.AppendLine("- Every trade (MapleSPA, MapleCoffee, MaplePOS, MapleGarage) has the same packages and the same prices; only the names and features are worded for the trade.");
        sb.AppendLine("- " + Tr("pr.doc.t1") + " " + Tr("pr.doc.t2") + " The visitor picks what they need on the pricing page; the bill adds up (one-time setup, the monthly total, and the first payment = setup + first month), and they can print it as a quote. A printed quote is valid 30 days.");
        sb.AppendLine("- MapleSPA's pricing page also offers the full 4-page salon quote brochure as a PDF (in Vietnamese): /docs/MapleSPA-salon-quote.pdf.");
        sb.AppendLine("- The AI Voice Agent has no price list of its own on the site: the AI plans below ($49.99/month) include the AI phone assistant; for the agent on its own, ask sales.");

        foreach (var p in SiteProducts.In(ProductGroup.Trade))
        {
            var k = p.Key;
            sb.AppendLine();
            sb.AppendLine($"### {p.Name} pricing (/{p.Slug}/pricing)");
            sb.AppendLine("One-time setup (pick any):");
            foreach (var s in PriceBook.SetupItems)
            {
                var d = string.Join(" ", Enumerable.Range(1, s.Points).Select(i => Pr(k, $"setup.{s.Key}.d{i}")));
                sb.AppendLine($"- {Pr(k, $"setup.{s.Key}.t")}: {Usd(s.Price)} one time. {d}");
            }
            sb.AppendLine($"Monthly software (pick one; {Pr(k, "soft.sub")}):");
            foreach (var plan in PriceBook.Software)
            {
                var has = string.Join("; ", Enumerable.Range(1, plan.Features).Select(i => Pr(k, $"soft.f{i}")));
                sb.AppendLine($"- {Pr(k, $"soft.{plan.Key}.t")}: {Usd(plan.Price)}/month{(plan.Recommended ? " (recommended)" : "")}. {Pr(k, $"soft.{plan.Key}.goal")} Includes: {has}.");
            }
            sb.AppendLine($"Text messages (SMS add-on, pick one): {Pr(k, "sms.sub")}");
            foreach (var plan in PriceBook.Sms)
                sb.AppendLine($"- {Pr(k, $"sms.{plan.Key}.t")}: {Usd(plan.Price)}/month — {Pr(k, $"sms.{plan.Key}.d")}");
            sb.AppendLine("- " + Pr(k, "sms.over"));
        }

        sb.AppendLine();
        sb.AppendLine("### Online marketing (every trade, pick one)");
        sb.AppendLine(Neutral(Tr("pr.mkt.sub")));
        foreach (var plan in PriceBook.Marketing)
        {
            var facts = string.Join("; ", PriceBook.MarketingFacts.Select(f => $"{Tr($"pr.mkt.l.{f}")}: {Neutral(Tr($"pr.mkt.{plan.Key}.{f}"))}"));
            sb.AppendLine($"- {Tr($"pr.mkt.{plan.Key}.t")}: {Usd(plan.Price)}/month{(plan.Recommended ? " (recommended)" : "")}. {Neutral(Tr($"pr.mkt.{plan.Key}.goal"))} {facts}.");
        }

        sb.AppendLine();
        sb.AppendLine("### Card payments (not part of the bill)");
        foreach (var r in new[] { "pr.pay.r1", "pr.pay.r2", "pr.pay.r3" }) sb.AppendLine("- " + Tr(r));
    }
}
