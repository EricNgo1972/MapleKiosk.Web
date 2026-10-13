using System.Text.RegularExpressions;
using MapleKiosk.Web.Services;
using MapleKiosk.Web.Shop.Catalog;
using MapleKiosk.Web.Shop.Orders;
using MapleKiosk.Web.Shop.Payments;

namespace MapleKiosk.Web.Shop.PhoneLines;

/// <summary>One business a signed-in customer holds, as the phone-line page shows it.</summary>
/// <param name="TenantId">The app install staff linked to it; null = not linked yet, so it can't have a line.</param>
/// <param name="CanTakeCalls">The linked app has reported a public address Twilio can dial, so a number can be pointed at it.</param>
/// <param name="ActiveNumber">The number this salon already has from us, if any.</param>
public sealed record PhoneLineSalon(string Key, string? TenantId, string Name, string? Address, bool CanTakeCalls, string? ActiveNumber);

/// <summary>What the phone line costs: the catalog item that sells it.</summary>
public sealed record PhoneLineOffer(string Sku, string Name, decimal Price, string Currency);

/// <summary><paramref name="Error"/> is a translation key (pl.err.*).</summary>
public sealed record PhoneLineCheckout(bool Success, string? CheckoutUrl, string? OrderRef, string? Error);

/// <summary>Where a phone-line order's number stands after an attempt. <paramref name="Error"/> is a translation key
/// for a refusal; how an attempt ended is the <paramref name="Status"/>.</summary>
public sealed record PhoneLineOutcome(string Status, string? Number, string? Error = null);

/// <summary>
/// Sells a salon its phone number: a monthly subscription, calls included, answered by the salon's AI voice
/// assistant. The customer signs in (central OAuth proves the email), picks one of THEIR businesses, chooses a number and
/// subscribes; only once Stripe confirms the payment does <see cref="FulfilAsync"/> buy that number on the
/// platform's Twilio account, pointed at the address the salon itself reported.
///
/// <para>The rules that keep the platform's money safe live here, not in a page:</para>
/// <list type="bullet">
///   <item>Only for a business on the signed-in email's customer record (the onboarding list), through the app
///   install staff linked to it, and only pointed at the address that install reported (TenantPhoneEndpoints) —
///   never anything from the browser.</item>
///   <item>A number is bought only for a PAID order, and only by whoever wins the order's Buying claim, so a
///   replayed or concurrent webhook can never buy twice.</item>
///   <item>A phone-line item with a free trial is never sold: a trial completes checkout with no payment.</item>
///   <item>One active line per salon.</item>
///   <item>When Stripe stops billing for good, the number goes back to Twilio after a grace period
///   (<see cref="OnSubscriptionChangedAsync"/>, <see cref="ReleaseDueAsync"/>), and an ended order never buys one.</item>
///   <item>"Calls included" has a ceiling: past the monthly minute cap a line's calls are declined (Twilio doesn't
///   bill a declined call) until the month turns or staff lift it (<see cref="EnforceCapsAsync"/>).</item>
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

    /// <summary>Days a line keeps its number after its subscription ends, unless <c>PhoneLine/ReleaseGraceDays</c>
    /// (or env PHONE_LINE_RELEASE_GRACE_DAYS) says otherwise. Room for a card fix or a change of mind.</summary>
    public const int DefaultReleaseGraceDays = 14;

    /// <summary>Where a capped line's calls go: a fixed answer that declines them, served by this site.</summary>
    public const string DeclinedPath = "/api/checkout/phone-line/declined";
    public static string DeclinedUrl => $"https://{SiteHost.Public}{DeclinedPath}";

    private readonly IPhoneLineOrders _orders;
    private readonly IAppCatalog _catalog;
    private readonly IPhoneLineCustomers _customers;
    private readonly ITenantDirectory _directory;
    private readonly ITelephonyGateway _telephony;
    private readonly IStripeCheckout _stripe;
    private readonly ILogger<PhoneLineService> _logger;

    public PhoneLineService(IPhoneLineOrders orders, IAppCatalog catalog, IPhoneLineCustomers customers, ITenantDirectory directory,
        ITelephonyGateway telephony, IStripeCheckout stripe, ILogger<PhoneLineService> logger)
    {
        _orders = orders;
        _catalog = catalog;
        _customers = customers;
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

    /// <summary>The businesses <paramref name="email"/> holds as a customer of this site, with whether each can take a
    /// number: it needs the app install staff linked to it, and that install's reported public address.</summary>
    public async Task<IReadOnlyList<PhoneLineSalon>> ListSalonsAsync(string email, CancellationToken ct = default)
    {
        var canonical = Canonical(email);
        if (canonical.Length == 0) return [];

        var salons = new List<PhoneLineSalon>();
        foreach (var business in await _customers.ListAsync(canonical, ct).ConfigureAwait(false))
        {
            var tenantId = string.IsNullOrWhiteSpace(business.TenantId) ? null : business.TenantId.Trim();
            // Two records on one install would be one line: list it once.
            if (tenantId is not null && salons.Any(s => string.Equals(s.TenantId, tenantId, StringComparison.OrdinalIgnoreCase)))
                continue;

            SalonPhoneEndpoint? endpoint = null;
            string? active = null;
            if (tenantId is not null)
            {
                endpoint = await _directory.GetPhoneEndpointAsync(tenantId, ct).ConfigureAwait(false);
                active = (await _orders.ListPhoneLinesAsync(tenantId, ct).ConfigureAwait(false))
                    .FirstOrDefault(o => o.PhoneLineStatus is PhoneLineStatus.Active or PhoneLineStatus.Releasing)?.PhoneLineNumber;
            }
            salons.Add(new PhoneLineSalon(business.Key, tenantId, business.Name, business.Address, Reachable(endpoint), active));
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
            .FirstOrDefault(s => s.TenantId is not null && string.Equals(s.TenantId, tenantId?.Trim(), StringComparison.OrdinalIgnoreCase));
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
            PhoneLineTenantId = salon.TenantId!,
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

        // Stripe has stopped billing (cancelled before the number could be bought, say): nothing more to buy.
        if (SubscriptionStates.HasEnded(order.SubscriptionStatus)) return Current(order, "pl.err.ended");

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
                .FirstOrDefault(o => o.OrderRef != order.OrderRef && PhoneLineStatus.HoldsNumber(o.PhoneLineStatus));
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

            var bought = await _telephony.BuyAsync(null, number!,
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

    /// <summary>
    /// Follows the Stripe subscription mirror: once Stripe has stopped billing an Active line for good, its number is
    /// scheduled to go back to Twilio a grace period after the paid time runs out; if billing resumes first (an
    /// unpaid invoice settled), the release is called off. Never throws: the webhook must still answer Stripe.
    /// </summary>
    public async Task OnSubscriptionChangedAsync(string orderRef, DateTimeOffset now, CancellationToken ct = default)
    {
        try
        {
            var order = await _orders.GetAsync(orderRef, ct).ConfigureAwait(false);
            if (order is null || !IsPhoneLineOrder(order) || order.PhoneLineStatus != PhoneLineStatus.Active) return;

            var ended = SubscriptionStates.HasEnded(order.SubscriptionStatus);
            if (ended && order.PhoneLineReleaseAt is null)
            {
                var paidUntil = order.CurrentPeriodEnd is { } end && end > now ? end : now;
                var at = paidUntil.AddDays(await ReleaseGraceDaysAsync().ConfigureAwait(false));
                _logger.LogWarning("Phone line: order {OrderRef} subscription {Status}; {Number} ({Sid}) will be released {At:u}.",
                    order.OrderRef, order.SubscriptionStatus, order.PhoneLineNumber, order.PhoneLineSid, at);
                await _orders.ScheduleReleaseAsync(order.OrderRef, at, ct).ConfigureAwait(false);
            }
            else if (!ended && order.PhoneLineReleaseAt is not null)
            {
                _logger.LogWarning("Phone line: order {OrderRef} subscription is {Status} again; keeping {Number}.",
                    order.OrderRef, order.SubscriptionStatus, order.PhoneLineNumber);
                await _orders.ScheduleReleaseAsync(order.OrderRef, null, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Phone line: following the subscription of order {OrderRef} failed.", orderRef);
        }
    }

    /// <summary>
    /// Hands back to Twilio every number whose release date has passed and whose subscription is still ended.
    /// Each release is claimed (Active → Releasing) so two sweeps never both delete; a failed release goes back to
    /// Active with the error and is tried on the next sweep. Returns how many numbers were released.
    /// </summary>
    public async Task<int> ReleaseDueAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var released = 0;
        foreach (var order in await _orders.ListPhoneLinesAsync(null, ct).ConfigureAwait(false))
        {
            if (order.PhoneLineStatus != PhoneLineStatus.Active || order.PhoneLineReleaseAt is not { } at || at > now
                || !SubscriptionStates.HasEnded(order.SubscriptionStatus))
                continue;
            if (string.IsNullOrWhiteSpace(order.PhoneLineSid))
            {
                _logger.LogError("Phone line: order {OrderRef} is due for release but has no Twilio number id.", order.OrderRef);
                continue;
            }
            if (!await _orders.ClaimReleaseAsync(order.OrderRef, ct).ConfigureAwait(false)) continue;

            string? error;
            try { error = await _telephony.ReleaseAsync(order.PhoneLineAccountSid, order.PhoneLineSid, ct).ConfigureAwait(false); }
            catch (Exception ex) { error = ex.Message; }

            if (error is null)
            {
                _logger.LogWarning("Phone line: released {Number} ({Sid}) of tenant {TenantId}, order {OrderRef} (subscription {Status}).",
                    order.PhoneLineNumber, order.PhoneLineSid, order.PhoneLineTenantId, order.OrderRef, order.SubscriptionStatus);
                await _orders.FinishPhoneLineAsync(order.OrderRef, PhoneLineStatus.Released, null, null, null, ct).ConfigureAwait(false);
                released++;
            }
            else
            {
                _logger.LogError("Phone line: releasing {Number} ({Sid}) of order {OrderRef} failed: {Error}",
                    order.PhoneLineNumber, order.PhoneLineSid, order.OrderRef, error);
                await _orders.FinishPhoneLineAsync(order.OrderRef, PhoneLineStatus.Active, null, null, "Release failed: " + error, ct)
                    .ConfigureAwait(false);
            }
        }
        return released;
    }

    /// <summary>
    /// Holds every Active line to the monthly minute cap (<c>PhoneLine/MonthlyMinuteCap</c>; unset or 0 = no cap):
    /// a line past it this month has its calls declined; a line capped in an earlier month is pointed back at the
    /// salon. Re-pointing reads the salon's reported address again, exactly as buying does. Returns lines changed.
    /// </summary>
    public async Task<int> EnforceCapsAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var cap = await MonthlyMinuteCapAsync().ConfigureAwait(false);
        var month = PhoneLineCap.Month(now);
        var changed = 0;
        foreach (var order in await _orders.ListPhoneLinesAsync(null, ct).ConfigureAwait(false))
        {
            if (order.PhoneLineStatus != PhoneLineStatus.Active || string.IsNullOrWhiteSpace(order.PhoneLineSid)) continue;
            var state = order.PhoneLineCapState ?? "";
            try
            {
                if (PhoneLineCap.IsCapped(state) && state != PhoneLineCap.Capped(now))
                {
                    // A new month: the line answers again.
                    var endpoint = await _directory.GetPhoneEndpointAsync(order.PhoneLineTenantId!, ct).ConfigureAwait(false);
                    if (!Reachable(endpoint))
                    {
                        _logger.LogWarning("Phone line: {Number} (order {OrderRef}) can't be pointed back at tenant {TenantId}: no public address.",
                            order.PhoneLineNumber, order.OrderRef, order.PhoneLineTenantId);
                        continue;
                    }
                    var error = await _telephony.PointAsync(order.PhoneLineAccountSid, order.PhoneLineSid,
                        new VoiceWebhooks(endpoint!.IncomingUrl, endpoint.FallbackUrl, endpoint.StatusUrl), ct).ConfigureAwait(false);
                    if (error is not null)
                    {
                        _logger.LogError("Phone line: restoring {Number} (order {OrderRef}) failed: {Error}", order.PhoneLineNumber, order.OrderRef, error);
                        continue;
                    }
                    _logger.LogWarning("Phone line: {Number} (order {OrderRef}) answers again at {Incoming} for {Month}.",
                        order.PhoneLineNumber, order.OrderRef, endpoint.IncomingUrl, month);
                    await _orders.SetCapStateAsync(order.OrderRef, "", ct).ConfigureAwait(false);
                    changed++;
                    state = "";
                }

                if (cap <= 0 || PhoneLineCap.IsCapped(state) || state == PhoneLineCap.Lifted(now)) continue;

                var (minutes, readError) = await _telephony.MinutesUsedAsync(order.PhoneLineAccountSid, order.PhoneLineNumber!,
                    PhoneLineCap.MonthStart(now), ct).ConfigureAwait(false);
                if (minutes is null)
                {
                    _logger.LogWarning("Phone line: couldn't read {Number}'s minutes (order {OrderRef}): {Error}", order.PhoneLineNumber, order.OrderRef, readError);
                    continue;
                }
                if (minutes < cap) continue;

                var declined = new VoiceWebhooks(DeclinedUrl, DeclinedUrl, DeclinedUrl);
                var pointError = await _telephony.PointAsync(order.PhoneLineAccountSid, order.PhoneLineSid, declined, ct).ConfigureAwait(false);
                if (pointError is not null)
                {
                    _logger.LogError("Phone line: capping {Number} (order {OrderRef}) failed: {Error}", order.PhoneLineNumber, order.OrderRef, pointError);
                    continue;
                }
                _logger.LogWarning("Phone line: {Number} (order {OrderRef}, tenant {TenantId}) used {Minutes} of {Cap} minutes in {Month}; its calls are declined until next month.",
                    order.PhoneLineNumber, order.OrderRef, order.PhoneLineTenantId, minutes, cap, month);
                await _orders.SetCapStateAsync(order.OrderRef, PhoneLineCap.Capped(now), ct).ConfigureAwait(false);
                changed++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Phone line: holding order {OrderRef} to its cap failed.", order.OrderRef);
            }
        }
        return changed;
    }

    /// <summary>Staff only: lets a capped line answer again for the rest of this month (the cap returns next month).</summary>
    public async Task<string?> LiftCapAsync(string orderRef, DateTimeOffset now, CancellationToken ct = default)
    {
        var order = await _orders.GetAsync(orderRef, ct).ConfigureAwait(false);
        if (order is null || !IsPhoneLineOrder(order) || order.PhoneLineStatus != PhoneLineStatus.Active || string.IsNullOrWhiteSpace(order.PhoneLineSid))
            return "Not an active line.";
        var endpoint = await _directory.GetPhoneEndpointAsync(order.PhoneLineTenantId!, ct).ConfigureAwait(false);
        if (!Reachable(endpoint)) return "The salon hasn't reported a public https address.";

        var error = await _telephony.PointAsync(order.PhoneLineAccountSid, order.PhoneLineSid,
            new VoiceWebhooks(endpoint!.IncomingUrl, endpoint.FallbackUrl, endpoint.StatusUrl), ct).ConfigureAwait(false);
        if (error is not null) return error;
        _logger.LogWarning("Phone line: cap lifted on {Number} (order {OrderRef}) for {Month}.", order.PhoneLineNumber, order.OrderRef, PhoneLineCap.Month(now));
        await _orders.SetCapStateAsync(order.OrderRef, PhoneLineCap.Lifted(now), ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>Minutes a line may use per month; 0 = no cap. Key <c>PhoneLine/MonthlyMinuteCap</c> or env PHONE_LINE_MONTHLY_MINUTE_CAP.</summary>
    public static async Task<int> MonthlyMinuteCapAsync()
    {
        var raw = await KeyValueTable.ResolveAsync("PHONE_LINE_MONTHLY_MINUTE_CAP", "PhoneLine", "MonthlyMinuteCap").ConfigureAwait(false);
        return int.TryParse(raw, out var minutes) && minutes > 0 ? minutes : 0;
    }

    private static async Task<int> ReleaseGraceDaysAsync()
    {
        var raw = await KeyValueTable.ResolveAsync("PHONE_LINE_RELEASE_GRACE_DAYS", "PhoneLine", "ReleaseGraceDays").ConfigureAwait(false);
        return int.TryParse(raw, out var days) && days is >= 0 and <= 365 ? days : DefaultReleaseGraceDays;
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

    // Reserved names (RFC 2606/6761 and LAN suffixes) never resolve publicly: a test install's report, not a salon.
    private static readonly HashSet<string> PrivateTlds = new(StringComparer.OrdinalIgnoreCase)
        { "example", "test", "invalid", "localhost", "local", "internal", "lan", "home" };

    private static bool IsHttps(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && !u.IsLoopback
        && u.Host.Contains('.') && !PrivateTlds.Contains(u.Host[(u.Host.LastIndexOf('.') + 1)..]);

    private static string Canonical(string? email) => (email ?? "").Trim().ToLowerInvariant();
}
