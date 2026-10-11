using System.Text.RegularExpressions;
using MapleKiosk.Web.Shop.Catalog;
using MapleKiosk.Web.Shop.Orders;
using MapleKiosk.Web.Shop.Payments;

namespace MapleKiosk.Web.Shop.PhoneLines;

/// <summary>One salon a signed-in owner holds, as the phone-line page shows it.</summary>
/// <param name="CanTakeCalls">The salon has reported where it answers, so a number can be pointed at it.</param>
/// <param name="ActiveNumber">The number this salon already has from us, if any.</param>
public sealed record PhoneLineSalon(string TenantId, string Name, string? Address, bool CanTakeCalls, string? ActiveNumber);

/// <summary>What the phone line costs: the catalog item that sells it.</summary>
public sealed record PhoneLineOffer(string Sku, string Name, decimal Price, string Currency);

/// <summary><paramref name="Error"/> is a translation key (pl.err.*).</summary>
public sealed record PhoneLineCheckout(bool Success, string? CheckoutUrl, string? OrderRef, string? Error);

/// <summary>Where a phone-line order's number stands after an attempt. <paramref name="Error"/> is a translation key
/// for a refusal; how an attempt ended is the <paramref name="Status"/>.</summary>
public sealed record PhoneLineOutcome(string Status, string? Number, string? Error = null);

/// <summary>
/// Sells a salon its phone number: a monthly subscription, calls included, answered by the salon's AI voice
/// assistant. The owner signs in (central OAuth proves the email), picks one of THEIR salons, chooses a number and
/// subscribes; only once Stripe confirms the payment does <see cref="FulfilAsync"/> buy that number on the
/// platform's Twilio account, pointed at the address the salon itself reported.
///
/// <para>The rules that keep the platform's money safe live here, not in a page:</para>
/// <list type="bullet">
///   <item>Only for a salon the signed-in email owns (TenantActivation), and only pointed at the address that
///   salon reported (TenantPhoneEndpoints) — never anything from the browser.</item>
///   <item>A number is bought only for a PAID order, and only by whoever wins the order's Buying claim, so a
///   replayed or concurrent webhook can never buy twice.</item>
///   <item>A phone-line item with a free trial is never sold: a trial completes checkout with no payment.</item>
///   <item>One active line per salon.</item>
/// </list>
/// The generic cart (CheckoutService) and the quotes refuse the phone-line category, so this is the only way in.
/// </summary>
public sealed class PhoneLineService
{
    /// <summary><see cref="AppOrder.Source"/> of a phone-line order.</summary>
    public const string Source = "phone-line";

    /// <summary>The catalog item that sells the line (category <see cref="CatalogDefaults.PhoneLineCategory"/>).</summary>
    public const string Sku = "phone-line";

    // E.164 for the two countries sold: +1 and ten digits.
    private static readonly Regex NorthAmericanNumber = new(@"^\+1\d{10}$", RegexOptions.Compiled);
    private static readonly Regex AreaCode = new(@"^\d{3}$", RegexOptions.Compiled);

    private readonly IPhoneLineOrders _orders;
    private readonly IAppCatalog _catalog;
    private readonly ITenantDirectory _directory;
    private readonly ITelephonyGateway _telephony;
    private readonly IStripeCheckout _stripe;
    private readonly ILogger<PhoneLineService> _logger;

    public PhoneLineService(IPhoneLineOrders orders, IAppCatalog catalog, ITenantDirectory directory, ITelephonyGateway telephony,
        IStripeCheckout stripe, ILogger<PhoneLineService> logger)
    {
        _orders = orders;
        _catalog = catalog;
        _directory = directory;
        _telephony = telephony;
        _stripe = stripe;
        _logger = logger;
    }

    public static bool IsNumber(string? e164) => e164 is not null && NorthAmericanNumber.IsMatch(e164);

    /// <summary>+14805551234 → (480) 555-1234.</summary>
    public static string Pretty(string? e164) =>
        IsNumber(e164) ? $"({e164![2..5]}) {e164[5..8]}-{e164[8..]}" : e164 ?? "";

    /// <summary>The line for sale, or null when it isn't ready to sell: inactive, unpriced, not monthly, or with a trial.</summary>
    public async Task<PhoneLineOffer?> GetOfferAsync(CancellationToken ct = default)
    {
        var product = await _catalog.FindAsync(Sku, ct).ConfigureAwait(false);
        if (product is null || !product.IsPhoneLine || product.Price <= 0 || product.TrialDays > 0
            || !string.Equals(product.BillingInterval, BillingIntervals.Monthly, StringComparison.OrdinalIgnoreCase))
            return null;
        return new PhoneLineOffer(product.Sku, product.Name, product.Price, CatalogStore.Currency);
    }

    /// <summary>The activated salons <paramref name="email"/> owns, with whether each can take a number.</summary>
    public async Task<IReadOnlyList<PhoneLineSalon>> ListSalonsAsync(string email, CancellationToken ct = default)
    {
        var canonical = Canonical(email);
        if (canonical.Length == 0) return [];

        var salons = new List<PhoneLineSalon>();
        foreach (var owned in await _directory.ListOwnedAsync(canonical, ct).ConfigureAwait(false))
        {
            var endpoint = await _directory.GetPhoneEndpointAsync(owned.TenantId, ct).ConfigureAwait(false);
            var active = (await _orders.ListPhoneLinesAsync(owned.TenantId, ct).ConfigureAwait(false))
                .FirstOrDefault(o => o.PhoneLineStatus == PhoneLineStatus.Active)?.PhoneLineNumber;
            salons.Add(new PhoneLineSalon(
                owned.TenantId,
                string.IsNullOrWhiteSpace(endpoint?.SalonName) ? owned.TenantId : endpoint.SalonName,
                string.IsNullOrWhiteSpace(endpoint?.BaseUrl) ? null : endpoint.BaseUrl,
                CanTakeCalls: Reachable(endpoint),
                active));
        }
        return salons;
    }

    /// <summary>Numbers for sale. Country is US or CA; the area code, when given, is three digits.</summary>
    public async Task<NumberSearchResult> SearchAsync(string? country, string? areaCode, CancellationToken ct = default)
    {
        var iso = (country ?? "").Trim().ToUpperInvariant();
        if (iso is not ("US" or "CA")) return new NumberSearchResult([], "pl.err.country");

        var area = (areaCode ?? "").Trim();
        if (area.Length > 0 && !AreaCode.IsMatch(area)) return new NumberSearchResult([], "pl.err.area");

        try
        {
            if (!await _telephony.IsConfiguredAsync(ct).ConfigureAwait(false)) return new NumberSearchResult([], "pl.err.search");
            var found = await _telephony.SearchAsync(iso, area.Length > 0 ? area : null, ct).ConfigureAwait(false);
            return found.Error is null
                ? new NumberSearchResult(found.Numbers.Where(n => IsNumber(n.Number)).ToList())
                : new NumberSearchResult([], "pl.err.search");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Phone line: number search failed ({Country} {Area}).", iso, area);
            return new NumberSearchResult([], "pl.err.search");
        }
    }

    /// <summary>
    /// Places the order for <paramref name="number"/> on <paramref name="tenantId"/> and opens Stripe Checkout
    /// (a monthly subscription, no trial). Refuses a salon the email doesn't own, one that can't take calls yet,
    /// or one that already has a line. Nothing is bought here — that waits for the payment.
    /// </summary>
    /// <param name="email">The SIGNED-IN email (from the auth cookie), never one typed on the page.</param>
    /// <param name="siteRoot">This site's root, e.g. https://www.maplekiosk.ca/ — where Stripe sends the buyer back.</param>
    public async Task<PhoneLineCheckout> StartCheckoutAsync(string email, string tenantId, string number, string culture,
        string siteRoot, CancellationToken ct = default)
    {
        var canonical = Canonical(email);
        var phone = (number ?? "").Trim();
        if (!IsNumber(phone)) return Refuse("pl.err.pick");

        var salon = (await ListSalonsAsync(canonical, ct).ConfigureAwait(false))
            .FirstOrDefault(s => string.Equals(s.TenantId, tenantId?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (salon is null) return Refuse("pl.err.notyours");
        if (!salon.CanTakeCalls) return Refuse("pl.err.unreachable");
        if (salon.ActiveNumber is not null) return Refuse("pl.err.hasline");

        var product = await _catalog.FindAsync(Sku, ct).ConfigureAwait(false);
        if (await GetOfferAsync(ct).ConfigureAwait(false) is null || product is null) return Refuse("pl.err.offsale");

        var lang = culture is "fr" or "vi" or "ru" ? culture : "en";
        var order = new AppOrder
        {
            Lines = [new AppOrderLine
            {
                Sku = product.Sku, Name = $"{product.Name} · {Pretty(phone)}", UnitPrice = product.Price, Quantity = 1,
                Interval = BillingIntervals.Monthly
            }],
            Method = AppPaymentMethods.Stripe,
            Currency = CatalogStore.Currency,
            Total = product.Price,
            Interval = BillingIntervals.Monthly,
            TrialDays = 0, // never: a trial completes checkout with no payment, and would buy a number for nothing
            CustomerEmail = canonical,
            Company = salon.Name,
            Source = Source,
            Culture = lang,
            PhoneLineTenantId = salon.TenantId,
            PhoneLineNumber = phone,
            PhoneLineStatus = PhoneLineStatus.None
        };

        var root = siteRoot.TrimEnd('/') + "/" + (lang == "en" ? "" : lang + "/");
        var session = await _stripe.CreateAsync(order, $"{root}phone-line/order", $"{root}phone-line?checkout=cancelled", ct, lang)
            .ConfigureAwait(false);
        if (!session.Success || string.IsNullOrWhiteSpace(session.Url)) return Refuse("pl.err.checkout");

        order.ProviderRef = session.SessionId;
        await _orders.CreateAsync(order, ct).ConfigureAwait(false);
        _logger.LogInformation("Phone line: order {OrderRef} by {Email} for tenant {TenantId} ({Salon}), number {Number}.",
            order.OrderRef, canonical, salon.TenantId, salon.Name, phone);
        return new PhoneLineCheckout(true, session.Url, order.OrderRef, null);

        static PhoneLineCheckout Refuse(string why) => new(false, null, null, why);
    }

    /// <summary>
    /// Buys the order's number and points it at the salon — the one place the platform's Twilio money is spent.
    /// Called when the order is paid (<see cref="PhoneLineFulfilment"/>) and again by <see cref="RetryAsync"/>.
    /// Never throws: every problem lands on the order as a status the buyer or staff can retry.
    /// </summary>
    /// <param name="newNumber">A replacement number (after the first was taken); null = the one on the order.</param>
    public async Task<PhoneLineOutcome> FulfilAsync(string orderRef, string? newNumber = null, CancellationToken ct = default)
    {
        AppOrder? order;
        try
        {
            order = await _orders.GetAsync(orderRef, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Phone line: order {OrderRef} could not be read.", orderRef);
            return new PhoneLineOutcome(PhoneLineStatus.Failed, null, "pl.err.notfound");
        }

        if (order is null || !IsPhoneLineOrder(order)) return new PhoneLineOutcome(PhoneLineStatus.None, null, "pl.err.notfound");
        if (order.TrialDays > 0)
        {
            _logger.LogError("Phone line: order {OrderRef} has a trial; refusing to buy a number for it.", orderRef);
            return Current(order);
        }

        var chosen = newNumber?.Trim();
        if (!string.IsNullOrEmpty(chosen) && !IsNumber(chosen)) return Current(order, "pl.err.pick");

        try
        {
            // The claim is the guard: a replay, a second tab, or an unpaid order all lose it here.
            if (!await _orders.ClaimPhoneLineAsync(order.OrderRef, chosen, ct).ConfigureAwait(false))
                return Current(await _orders.GetAsync(order.OrderRef, ct).ConfigureAwait(false) ?? order);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Phone line: claiming order {OrderRef} threw.", orderRef);
            return Current(order);
        }

        var number = string.IsNullOrEmpty(chosen) ? order.PhoneLineNumber : chosen;
        var tenantId = order.PhoneLineTenantId!;
        try
        {
            var other = (await _orders.ListPhoneLinesAsync(tenantId, ct).ConfigureAwait(false))
                .FirstOrDefault(o => o.OrderRef != order.OrderRef && o.PhoneLineStatus is PhoneLineStatus.Active or PhoneLineStatus.Buying);
            if (other is not null)
                return await FinishAsync(order, PhoneLineStatus.Failed, number, null,
                    $"Tenant {tenantId} already has a line on order {other.OrderRef} ({other.PhoneLineNumber}, {other.PhoneLineStatus}).", ct);

            var endpoint = await _directory.GetPhoneEndpointAsync(tenantId, ct).ConfigureAwait(false);
            if (!Reachable(endpoint))
                return await FinishAsync(order, PhoneLineStatus.WaitingForSalon, number, null, "The salon hasn't reported a public https address.", ct);

            if (!IsNumber(number))
                return await FinishAsync(order, PhoneLineStatus.NumberTaken, number, null, "No valid number on the order.", ct);

            if (!await _telephony.IsConfiguredAsync(ct).ConfigureAwait(false))
                return await FinishAsync(order, PhoneLineStatus.Failed, number, null, "Twilio is not configured.", ct);

            // The directory rows are claims any salon binary could have written: record exactly what this buy trusted.
            _logger.LogWarning("Phone line: BUYING {Number} for order {OrderRef}, owner {Email}, tenant {TenantId} ({Salon}) → {Incoming} " +
                               "(fallback {Fallback}, status {Status}), reported {ReportedAt}.",
                number, order.OrderRef, order.CustomerEmail, tenantId, endpoint!.SalonName, endpoint.IncomingUrl,
                endpoint.FallbackUrl, endpoint.StatusUrl, endpoint.ReportedAtUtc);

            var bought = await _telephony.BuyAsync(number!,
                new VoiceWebhooks(endpoint.IncomingUrl, endpoint.FallbackUrl, endpoint.StatusUrl),
                $"{(string.IsNullOrWhiteSpace(endpoint.SalonName) ? tenantId : endpoint.SalonName)} · {order.OrderRef}", ct).ConfigureAwait(false);

            if (bought.Success)
            {
                _logger.LogWarning("Phone line: bought {Number} ({Sid}) for tenant {TenantId}, order {OrderRef}.",
                    bought.Number, bought.Sid, tenantId, order.OrderRef);
                return await FinishAsync(order, PhoneLineStatus.Active, bought.Number ?? number, bought.Sid, null, ct);
            }

            return bought.Unavailable
                ? await FinishAsync(order, PhoneLineStatus.NumberTaken, number, null, bought.Error ?? "Number taken.", ct)
                : await FinishAsync(order, PhoneLineStatus.Failed, number, null, bought.Error ?? "Twilio refused the purchase.", ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Phone line: fulfilling order {OrderRef} threw; it stays at Buying unless this records Failed.", order.OrderRef);
            try { return await FinishAsync(order, PhoneLineStatus.Failed, number, null, ex.Message, ct).ConfigureAwait(false); }
            catch { return new PhoneLineOutcome(PhoneLineStatus.Buying, number); }
        }
    }

    /// <summary>The buyer's (or staff's) "try again": another number after it was taken, or the same one once the
    /// salon is reachable. Only the email that placed the order, or staff.</summary>
    public async Task<PhoneLineOutcome> RetryAsync(string orderRef, string? signedInEmail, bool isStaff, string? newNumber,
        CancellationToken ct = default)
    {
        var order = await GetForAsync(orderRef, signedInEmail, isStaff, ct).ConfigureAwait(false);
        if (order is null) return new PhoneLineOutcome(PhoneLineStatus.None, null, "pl.err.notfound");
        if (!PhoneLineStatus.IsRetryable(order.PhoneLineStatus)) return Current(order);
        return await FulfilAsync(order.OrderRef, newNumber, ct).ConfigureAwait(false);
    }

    /// <summary>A phone-line order, only for the email that placed it (or staff); null otherwise.</summary>
    public async Task<AppOrder?> GetForAsync(string? orderRef, string? signedInEmail, bool isStaff, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(orderRef)) return null;
        var order = await _orders.GetAsync(orderRef.Trim(), ct).ConfigureAwait(false);
        if (order is null || !IsPhoneLineOrder(order)) return null;
        var mine = Canonical(signedInEmail) is { Length: > 0 } me && me == Canonical(order.CustomerEmail);
        return mine || isStaff ? order : null;
    }

    /// <summary>Every phone-line order, newest first (staff).</summary>
    public Task<IReadOnlyList<AppOrder>> ListOrdersAsync(CancellationToken ct = default) => _orders.ListPhoneLinesAsync(null, ct);

    public static bool IsPhoneLineOrder(AppOrder order) =>
        order.Source == Source && !string.IsNullOrWhiteSpace(order.PhoneLineTenantId);

    private async Task<PhoneLineOutcome> FinishAsync(AppOrder order, string status, string? number, string? sid, string? error,
        CancellationToken ct)
    {
        if (error is not null)
            _logger.LogWarning("Phone line: order {OrderRef} → {Status}: {Error}", order.OrderRef, status, error);
        await _orders.FinishPhoneLineAsync(order.OrderRef, status, number, sid, error, ct).ConfigureAwait(false);
        return new PhoneLineOutcome(status, number);
    }

    private static PhoneLineOutcome Current(AppOrder order, string? error = null) =>
        new(string.IsNullOrEmpty(order.PhoneLineStatus) ? PhoneLineStatus.None : order.PhoneLineStatus, order.PhoneLineNumber, error);

    // Twilio can only dial a public https address; anything else the salon reported is treated as not reported.
    private static bool Reachable(SalonPhoneEndpoint? e) =>
        e is not null && IsHttps(e.IncomingUrl) && IsHttps(e.FallbackUrl) && IsHttps(e.StatusUrl);

    private static bool IsHttps(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && !u.IsLoopback;

    private static string Canonical(string? email) => (email ?? "").Trim().ToLowerInvariant();
}
