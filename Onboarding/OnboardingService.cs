using System.Net;
using System.Text;
using MapleKiosk.Web.Services;
using MapleKiosk.Web.Shop.Config;

namespace MapleKiosk.Web.Onboarding;

/// <summary>
/// The onboarding workflow: staff add the customer's email after the deposit, the
/// customer signs in with Google / Microsoft (that email is their access), fills
/// the form and uploads the filled template, and on submit the
/// implementation inbox gets everything in one email. Config is config-free via
/// partition <c>Onboarding</c> in the keyvalue table:
/// <list type="bullet">
/// <item><c>Inbox</c> (ONBOARDING_INBOX) — implementer inbox; falls back to AppStore/OrderInbox.</item>
/// <item><c>AccessEmail</c> (ONBOARDING_ACCESS_EMAIL) — email customers add as Google Business Profile manager.</item>
/// <item><c>MetaBusinessId</c> (ONBOARDING_META_BUSINESS_ID) — our Meta Business ID for partner access.</item>
/// </list>
/// </summary>
public sealed class OnboardingService
{
    private readonly OnboardingStore _store;
    private readonly EmailService _email;
    private readonly AppStoreConfig _appStore;
    private readonly ILogger<OnboardingService> _logger;
    private readonly Lazy<Task<string>> _inbox;
    private readonly Lazy<Task<string>> _accessEmail;
    private readonly Lazy<Task<string>> _metaBusinessId;

    public OnboardingService(OnboardingStore store, EmailService email, AppStoreConfig appStore, ILogger<OnboardingService> logger)
    {
        _store = store;
        _email = email;
        _appStore = appStore;
        _logger = logger;
        _inbox = new(async () =>
        {
            var v = await KeyValueTable.ResolveAsync("ONBOARDING_INBOX", "Onboarding", "Inbox");
            return string.IsNullOrWhiteSpace(v) ? await _appStore.GetOrderInboxAsync() : v;
        });
        _accessEmail = new(() => KeyValueTable.ResolveAsync("ONBOARDING_ACCESS_EMAIL", "Onboarding", "AccessEmail"));
        _metaBusinessId = new(() => KeyValueTable.ResolveAsync("ONBOARDING_META_BUSINESS_ID", "Onboarding", "MetaBusinessId"));
    }

    public Task<string> GetAccessEmailAsync() => _accessEmail.Value;
    public Task<string> GetMetaBusinessIdAsync() => _metaBusinessId.Value;

    /// <summary>The customer's entry point. Not a secret: it asks them to sign in,
    /// and the signed-in email decides what they see.</summary>
    public static string CustomerUrl(string baseUri, OnboardingRecord r) => $"{baseUri.TrimEnd('/')}/onboarding?lang={r.Language}";
    public static string FileUrl(string baseUri, OnboardingRecord r, OnboardingFile f) =>
        $"{baseUri.TrimEnd('/')}/onboarding/files/{r.Token}/{f.Id}";

    public async Task<OnboardingRecord> CreateAsync(string businessName, string contactEmail, string industry,
        string language, string? orderRef, bool includesWebsite, bool sendEmail, string baseUri)
    {
        var record = new OnboardingRecord
        {
            BusinessName = businessName.Trim(),
            ContactEmail = OnboardingRecord.CanonicalEmail(contactEmail),
            Industry = industry,
            Language = language,
            OrderRef = string.IsNullOrWhiteSpace(orderRef) ? null : orderRef.Trim(),
            IncludesWebsite = includesWebsite
        };
        record.Form.BusinessName = record.BusinessName;
        record.Form.NotifyEmail = record.ContactEmail;

        await _store.SaveAsync(record);
        _logger.LogInformation("Onboarding created for {Business} ({Token}).", record.BusinessName, record.Token);

        if (sendEmail) await SendInviteAsync(record, baseUri);
        return record;
    }

    public async Task SendInviteAsync(OnboardingRecord r, string baseUri)
    {
        if (string.IsNullOrWhiteSpace(r.ContactEmail)) return;
        var url = CustomerUrl(baseUri, r);
        var html = new StringBuilder()
            .Append("<div style='font-family:Arial,Helvetica,sans-serif;color:#111;max-width:560px'>")
            .Append($"<h2>{Enc(T(r.Language, "onb.mail.invite.title"))}</h2>")
            .Append($"<p>{Enc(string.Format(T(r.Language, "onb.mail.invite.body"), r.BusinessName))}</p>")
            .Append($"<p style='margin:28px 0'><a href='{Enc(url)}' style='background:#c0392b;color:#fff;padding:12px 22px;border-radius:6px;text-decoration:none;font-weight:600'>{Enc(T(r.Language, "onb.mail.invite.cta"))}</a></p>")
            .Append($"<p style='color:#666;font-size:13px'>{Enc(string.Format(T(r.Language, "onb.mail.invite.note"), r.ContactEmail))}<br>{Enc(url)}</p>")
            .Append("<p style='color:#666;margin-top:24px'>MapleKiosk</p></div>")
            .ToString();
        await _email.SendAsync(r.ContactEmail, T(r.Language, "onb.mail.invite.subject"), html);
    }

    public async Task SubmitAsync(OnboardingRecord r, string baseUri)
    {
        r.Status = OnboardingStatus.Submitted;
        r.SubmittedAt = DateTimeOffset.UtcNow;
        r.UpdatedAt = r.SubmittedAt;
        await _store.SaveAsync(r);
        _logger.LogInformation("Onboarding submitted for {Business} ({Token}).", r.BusinessName, r.Token);

        var inbox = await _inbox.Value;
        if (!string.IsNullOrWhiteSpace(inbox))
        {
            var html = "<div style='font-family:Arial,Helvetica,sans-serif;color:#111'>" +
                       $"<p><a href='{Enc(baseUri.TrimEnd('/'))}/onboarding/admin'>Open in onboarding admin</a></p>" +
                       SummaryHtml(r, f => FileUrl(baseUri, r, f)) + "</div>";
            await _email.SendAsync(inbox, $"[onboarding] {r.Form.BusinessName} submitted setup info", html);
        }

        if (!string.IsNullOrWhiteSpace(r.ContactEmail))
        {
            var html = "<div style='font-family:Arial,Helvetica,sans-serif;color:#111;max-width:560px'>" +
                       $"<h2>{Enc(T(r.Language, "onb.mail.done.title"))}</h2>" +
                       $"<p>{Enc(T(r.Language, "onb.mail.done.body"))}</p>" +
                       "<p style='color:#666;margin-top:24px'>MapleKiosk</p></div>";
            await _email.SendAsync(r.ContactEmail, T(r.Language, "onb.mail.done.subject"), html);
        }
    }

    /// <summary>Customer pressed "Send brief": stamp it and email the design
    /// brief to the implementation inbox. Can be sent again after edits.</summary>
    public async Task SendBriefAsync(OnboardingRecord r, WebsiteBrief brief, string baseUri)
    {
        brief.CompletedAt = DateTimeOffset.UtcNow;
        brief.UpdatedAt = brief.CompletedAt;
        await _store.SaveBriefAsync(r.Token, brief);
        _logger.LogInformation("Website brief sent for {Business} ({Token}).", r.BusinessName, r.Token);

        var inbox = await _inbox.Value;
        if (string.IsNullOrWhiteSpace(inbox)) return;
        var html = "<div style='font-family:Arial,Helvetica,sans-serif;color:#111'>" +
                   $"<p><a href='{Enc(baseUri.TrimEnd('/'))}/onboarding/admin'>Open in onboarding admin</a></p>" +
                   $"<h2 style='margin:0 0 4px'>{Enc(r.Form.BusinessName)} — website brief</h2>" +
                   BriefHtml(r, brief, f => FileUrl(baseUri, r, f)) + "</div>";
        await _email.SendAsync(inbox, $"[website brief] {r.Form.BusinessName}", html);
    }

    /// <summary>The designer's view of a website brief, in English.</summary>
    public static string BriefHtml(OnboardingRecord r, WebsiteBrief b, Func<OnboardingFile, string> fileUrl)
    {
        const string en = "en";
        string L(IEnumerable<BriefOption> opts, string id) => string.IsNullOrWhiteSpace(id) ? "" : WebsiteBriefOptions.Label(opts, id, en);
        string Ls(IEnumerable<BriefOption> opts, IEnumerable<string> ids) => string.Join(", ", ids.Select(id => L(opts, id)));
        var sb = new StringBuilder();

        sb.Append(b.CompletedAt is { } at
            ? $"<p style='color:#666;margin:0 0 12px'>Sent {at.UtcDateTime:yyyy-MM-dd HH:mm} UTC</p>"
            : "<p style='color:#b26a00;margin:0 0 12px'>Draft — not sent by the customer yet.</p>");

        Section(sb, "Starting point", [
            ("Current website", L(WebsiteBriefOptions.CurrentSite, b.CurrentSite)), ("URL", b.CurrentSiteUrl),
            ("Works well", b.CurrentLikes), ("Problems", b.CurrentProblems),
            ("Domain", L(WebsiteBriefOptions.Domain, b.Domain)), ("Domain name", b.DomainName),
            ("Email on domain", b.WantsEmail ? $"Yes — {b.EmailAddresses}" : "")]);

        Section(sb, "Goals & clients", [
            ("Goals", Ls(WebsiteBriefOptions.Goals, b.Goals)), ("Most important", L(WebsiteBriefOptions.Goals, b.PrimaryGoal)),
            ("Clients", Ls(WebsiteBriefOptions.Audience, b.Audience)), ("About clients", b.AudienceNotes),
            ("Why clients choose them", b.Difference), ("Three words", b.ThreeWords)]);

        Section(sb, "Pages & features", [
            ("Pages", Ls(WebsiteBriefOptions.Pages, b.Pages)), ("Features", Ls(WebsiteBriefOptions.Features, b.Features)),
            ("Languages", Ls(WebsiteBriefOptions.Languages, b.Languages)), ("Notes", b.PagesNotes)]);

        Section(sb, "Style",
            new (string, string)[]
            {
                ("Styles", string.Join(", ", b.Styles.Select(id => WebsiteBriefOptions.Styles.FirstOrDefault(x => x.Id == id)?.En ?? id))),
                ("Light / dark", L(WebsiteBriefOptions.Modes, b.Mode)),
                ("Lettering", L(WebsiteBriefOptions.Fonts, b.Fonts)),
            }.Concat(WebsiteBriefOptions.Sliders.Select(sl =>
                ($"{sl.Left.En} ↔ {sl.Right.En}", $"{new string('●', b.Slider(sl.Id))}{new string('○', 5 - b.Slider(sl.Id))} ({b.Slider(sl.Id)}/5)"))).ToArray());

        sb.Append(Heading("Colours")).Append("<table style='border-collapse:collapse;margin-bottom:8px'>");
        foreach (var (label, hex) in new[] { ("Background", b.Background), ("Accent", b.Accent) })
            sb.Append($"<tr><td style='padding:3px 16px 3px 0;color:#666'>{label}</td><td style='padding:3px 0;font-weight:600'>" +
                      (string.IsNullOrWhiteSpace(hex) ? "<span style='color:#aaa;font-weight:400'>Designer's choice</span>"
                          : $"<span style='display:inline-block;width:14px;height:14px;border-radius:3px;border:1px solid #ccc;vertical-align:-2px;background:{Enc(hex)}'></span> {Enc(WebsiteBriefOptions.ColorLabel(hex, en))} <span style='color:#666;font-weight:400'>{Enc(hex)}</span>") +
                      "</td></tr>");
        sb.Append("</table>");
        Section(sb, "", [
            ("Palette (older brief)", WebsiteBriefOptions.Palettes.FirstOrDefault(p => p.Id == b.Palette)?.En ?? ""),
            ("Brand colours", b.BrandColors), ("Avoid colours", b.AvoidColors)]);

        Section(sb, "Websites to copy",
            b.References.Where(x => !string.IsNullOrWhiteSpace(x.Url))
                .Select((x, i) => ($"Site {i + 1}",
                    x.Url + (x.Copy.Count > 0 ? $" — copy: {Ls(WebsiteBriefOptions.CopyAspects, x.Copy)}" : "")
                          + (string.IsNullOrWhiteSpace(x.Likes) ? "" : $" — likes: {x.Likes}")
                          + (string.IsNullOrWhiteSpace(x.Dislikes) ? "" : $" — dislikes: {x.Dislikes}")))
                .Append(("Competitors", b.Competitors)).Append(("Must-haves", b.MustHave)).Append(("Avoid", b.Avoid)).ToArray());

        Section(sb, "Logo, photos & assets", [
            ("Logo", L(WebsiteBriefOptions.Logo, b.Logo)), ("Photos", L(WebsiteBriefOptions.Photos, b.Photos)),
            ("Picture mood", Ls(WebsiteBriefOptions.ImageryMood, b.ImageryMood)),
            ("Assets they can share", Ls(WebsiteBriefOptions.AssetTypes, b.Assets)),
            ("Font names", b.FontNames), ("Shared folder", b.AssetsLink)]);

        Section(sb, "Voice & words", [
            ("Tone", Ls(WebsiteBriefOptions.Tone, b.Tone)), ("Who writes", L(WebsiteBriefOptions.Copy, b.Copy)),
            ("Key messages", b.KeyMessages)]);

        Section(sb, "Timeline & approval", [
            ("Launch date", b.LaunchDate), ("Why that date", b.LaunchReason),
            ("Approver", string.Join(" · ", new[] { b.Approver, b.ApproverContact }.Where(v => !string.IsNullOrWhiteSpace(v)))),
            ("Notes", b.Notes)]);

        sb.Append(Heading("Uploaded brand files & assets"));
        if (b.Files.Count == 0) sb.Append("<p style='color:#666'>None uploaded.</p>");
        else
        {
            sb.Append("<ul>");
            foreach (var file in b.Files)
                sb.Append($"<li><a href='{Enc(fileUrl(file))}'>{Enc(file.FileName)}</a> <span style='color:#666'>({file.Size / 1024:N0} KB)</span></li>");
            sb.Append("</ul>");
        }
        return sb.ToString();
    }

    /// <summary>The implementer's view of a submission: plain labelled tables, in
    /// English, used by the submit email and the admin page alike.</summary>
    public static string SummaryHtml(OnboardingRecord r, Func<OnboardingFile, string> fileUrl)
    {
        var f = r.Form;
        var sb = new StringBuilder();
        sb.Append($"<h2 style='margin:0 0 4px'>{Enc(f.BusinessName)}</h2>");
        sb.Append($"<p style='color:#666;margin:0 0 16px'>{Enc(OnboardingIndustries.Label(r.Industry))} · {Enc(r.Language.ToUpperInvariant())}" +
                  (r.OrderRef is null ? "" : $" · order {Enc(r.OrderRef)}") +
                  (r.SubmittedAt is { } at ? $" · submitted {at.UtcDateTime:yyyy-MM-dd HH:mm} UTC" : "") + "</p>");

        Section(sb, "Business", [
            ("Name shown to clients", f.BusinessName), ("Legal name", f.LegalName), ("Motto / slogan", f.Motto),
            ("Address", string.Join(", ", new[] { f.Address, f.City, f.PostalCode }.Where(s => !string.IsNullOrWhiteSpace(s)))),
            ("Business phone", f.Phone), ("Notifications / invoices email", f.NotifyEmail),
            ("Contact person", f.ContactName), ("Contact mobile", f.ContactMobile)]);

        Section(sb, "Opening hours",
            f.Hours.Select(h => (h.Day, h.Closed ? "Closed" : $"{h.Open} – {h.Close}"))
                   .Append(("Notes", f.HoursNotes)).ToArray());

        Section(sb, "Online", [
            ("Website", f.Website), ("Facebook", f.Facebook), ("Instagram", f.Instagram),
            ("Google Business Profile", f.GoogleProfile), ("SMS / WhatsApp number", f.SmsNumber)]);

        if (f.Logo is { } logo)
            sb.Append($"<p><img src='{Enc(fileUrl(logo))}' alt='Logo' style='max-height:72px;max-width:220px;display:block;margin:6px 0'>" +
                      $"<a href='{Enc(fileUrl(logo))}'>Logo: {Enc(logo.FileName)}</a></p>");

        sb.Append(Heading("Existing hardware"));
        if (f.NoHardware && f.Hardware.Count == 0) sb.Append("<p>None to reuse.</p>");
        else if (f.Hardware.Count == 0) sb.Append("<p style='color:#aaa'>—</p>");
        else
        {
            sb.Append("<table style='border-collapse:collapse;margin-bottom:8px'>");
            foreach (var h in f.Hardware)
                sb.Append($"<tr><td style='padding:3px 16px 3px 0;color:#666;vertical-align:top'>{Enc(HardwareOptions.Label(h.Type, "en"))}{(h.Quantity > 1 ? $" ×{h.Quantity}" : "")}</td>" +
                          $"<td style='padding:3px 0'><strong>{Enc($"{h.Brand} {h.Model}".Trim())}</strong>{(string.IsNullOrWhiteSpace(h.Notes) ? "" : $" <span style='color:#666'>— {Enc(h.Notes)}</span>")}</td></tr>");
            sb.Append("</table>");
        }

        Section(sb, "Access", [("Google Business Profile manager", AccessLabel(f.GoogleAccess)),
                               ("Meta (Facebook + Instagram) partner", AccessLabel(f.MetaAccess))]);

        sb.Append(Heading("Files"));
        if (r.Files.Count == 0) sb.Append("<p style='color:#c0392b'>No files uploaded.</p>");
        else
        {
            sb.Append("<ul>");
            foreach (var file in r.Files)
                sb.Append($"<li><a href='{Enc(fileUrl(file))}'>{Enc(file.FileName)}</a> <span style='color:#666'>({file.Size / 1024:N0} KB)</span></li>");
            sb.Append("</ul>");
        }

        if (!string.IsNullOrWhiteSpace(f.Notes))
            sb.Append(Heading("Notes")).Append($"<p style='white-space:pre-wrap'>{Enc(f.Notes)}</p>");
        return sb.ToString();
    }

    private static void Section(StringBuilder sb, string title, (string Label, string Value)[] rows)
    {
        if (!string.IsNullOrEmpty(title)) sb.Append(Heading(title));
        sb.Append("<table style='border-collapse:collapse;margin-bottom:8px'>");
        foreach (var (label, value) in rows)
            sb.Append($"<tr><td style='padding:3px 16px 3px 0;color:#666;vertical-align:top'>{Enc(label)}</td>" +
                      $"<td style='padding:3px 0;font-weight:600'>{(string.IsNullOrWhiteSpace(value) ? "<span style='color:#aaa;font-weight:400'>—</span>" : Enc(value))}</td></tr>");
        sb.Append("</table>");
    }

    private static string Heading(string title) =>
        $"<h3 style='margin:18px 0 6px;font-size:15px;border-bottom:1px solid #ddd;padding-bottom:4px'>{Enc(title)}</h3>";

    private static string AccessLabel(string answer) => answer switch
    {
        AccessAnswers.Done => "✓ Done",
        AccessAnswers.NotApplicable => "Don't have one",
        AccessAnswers.NeedHelp => "⚠ Needs help",
        _ => ""
    };

    private static string T(string lang, string key) =>
        (Translations.All.TryGetValue(lang, out var d) && d.TryGetValue(key, out var v)) ? v
        : Translations.All["en"].TryGetValue(key, out var en) ? en : key;

    private static string Enc(string? s) => WebUtility.HtmlEncode(s ?? "");
}
