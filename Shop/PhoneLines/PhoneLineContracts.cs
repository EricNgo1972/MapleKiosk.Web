using MapleKiosk.Web.Shop.Orders;

namespace MapleKiosk.Web.Shop.PhoneLines;

/// <summary>
/// Where buying a phone-line order's number stands, on <see cref="AppOrder.PhoneLineStatus"/>.
/// <code>
///   None ──(paid)──► Buying ──► Active
///                      │  ├──► NumberTaken      (someone else got it first → pick another, no new charge)
///                      │  ├──► WaitingForSalon  (the salon never reported its address → try again)
///                      │  └──► Failed           (anything else → try again / support)
///                      └── the three above go back to Buying on a retry
///   Active ──(subscription ended + grace)──► Releasing ──► Released   (number handed back to Twilio)
/// </code>
/// </summary>
public static class PhoneLineStatus
{
    public const string None = "None";
    public const string Buying = "Buying";
    public const string Active = "Active";
    public const string NumberTaken = "NumberTaken";
    public const string WaitingForSalon = "WaitingForSalon";
    public const string Failed = "Failed";
    public const string Releasing = "Releasing";
    public const string Released = "Released";

    /// <summary>States a buy may start (or start again) from. Empty = None.</summary>
    public static bool IsClaimable(string? status) =>
        string.IsNullOrEmpty(status) || status is None or NumberTaken or WaitingForSalon or Failed;

    public static bool IsRetryable(string? status) => status is NumberTaken or WaitingForSalon or Failed;

    /// <summary>States in which the salon holds (or is about to hold) a number: the one line it may have.</summary>
    public static bool HoldsNumber(string? status) => status is Buying or Active or Releasing;
}

/// <summary>A line's monthly usage cap, as recorded on <see cref="AppOrder.PhoneLineCapState"/>.</summary>
public static class PhoneLineCap
{
    public static string Month(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
    public static string Capped(DateTimeOffset at) => "capped:" + Month(at);
    public static string Lifted(DateTimeOffset at) => "lifted:" + Month(at);
    public static bool IsCapped(string? state) => state?.StartsWith("capped:", StringComparison.Ordinal) == true;
    /// <summary>The first instant of the month <paramref name="at"/> falls in (UTC): when its minutes start counting.</summary>
    public static DateTimeOffset MonthStart(DateTimeOffset at) => new(at.UtcDateTime.Year, at.UtcDateTime.Month, 1, 0, 0, 0, TimeSpan.Zero);
}

/// <summary>Stripe subscription states, as mirrored onto <see cref="AppOrder.SubscriptionStatus"/>.</summary>
public static class SubscriptionStates
{
    /// <summary>Stripe has stopped billing for good: cancelled, given up on an unpaid card, or never started.
    /// Empty (not mirrored yet) is NOT ended: the payment and the first mirror land together.</summary>
    public static bool HasEnded(string? status) => status is "canceled" or "unpaid" or "incomplete_expired";
}

/// <summary>The phone-line columns of the order store (<see cref="AppOrderService"/>); a seam for the tests.</summary>
public interface IPhoneLineOrders
{
    Task<AppOrder> CreateAsync(AppOrder order, CancellationToken ct = default);
    Task<AppOrder?> GetAsync(string orderRef, CancellationToken ct = default);
    Task<bool> ClaimPhoneLineAsync(string orderRef, string? newNumber, CancellationToken ct = default);
    Task FinishPhoneLineAsync(string orderRef, string status, string? number, string? sid, string? error, CancellationToken ct = default,
        string? accountSid = null);
    /// <summary>Records the line's usage-cap state for the month: "" (normal), "capped:yyyy-MM" or "lifted:yyyy-MM".</summary>
    Task SetCapStateAsync(string orderRef, string state, CancellationToken ct = default);
    /// <summary>Sets (or with null clears) when an Active line's number is released.</summary>
    Task ScheduleReleaseAsync(string orderRef, DateTimeOffset? releaseAt, CancellationToken ct = default);
    /// <summary>Active → Releasing, ETag-conditional: only one caller ever releases a number.</summary>
    Task<bool> ClaimReleaseAsync(string orderRef, CancellationToken ct = default);
    Task<IReadOnlyList<AppOrder>> ListPhoneLinesAsync(string? tenantId, CancellationToken ct = default);
}

/// <summary>An activated salon an owner's email holds (TenantActivation).</summary>
public sealed record OwnedSalon(string TenantId, string? Product);

/// <summary>Where a salon says its phone line answers (TenantPhoneEndpoints), reported by the salon itself.</summary>
public sealed record SalonPhoneEndpoint(
    string TenantId, string SalonName, string BaseUrl, string IncomingUrl, string FallbackUrl, string StatusUrl,
    DateTimeOffset? ReportedAtUtc);

/// <summary>The platform's tenant tables, read only: who owns which salon, and where each salon answers calls.</summary>
public interface ITenantDirectory
{
    Task<IReadOnlyList<OwnedSalon>> ListOwnedAsync(string ownerEmail, CancellationToken ct = default);
    Task<SalonPhoneEndpoint?> GetPhoneEndpointAsync(string tenantId, CancellationToken ct = default);
}

public sealed record AvailableNumber(string Number, string Locality, string Region);

public sealed record NumberSearchResult(IReadOnlyList<AvailableNumber> Numbers, string? Error = null);

public sealed record VoiceWebhooks(string Incoming, string Fallback, string Status);

public sealed record NumberPurchase(bool Success, bool Unavailable, string? Sid, string? Number, string? Error)
{
    public static NumberPurchase Bought(string sid, string number) => new(true, false, sid, number, null);
    public static NumberPurchase Taken(string? error) => new(false, true, null, null, error);
    public static NumberPurchase Failed(string? error) => new(false, false, null, null, error);
}

/// <summary>The platform's phone company (Twilio): search numbers for sale, buy one pointed at a salon.</summary>
public interface ITelephonyGateway
{
    Task<bool> IsConfiguredAsync(CancellationToken ct = default);
    Task<NumberSearchResult> SearchAsync(string country, string? areaCode, CancellationToken ct = default);
    /// <param name="accountSid">The Twilio account that will own the number; null = the platform account.</param>
    Task<NumberPurchase> BuyAsync(string? accountSid, string phoneNumber, VoiceWebhooks webhooks, string friendlyName,
        CancellationToken ct = default);
    /// <summary>Points a bought number's calls somewhere else (the salon again, or the over-cap answer). Null = done.</summary>
    Task<string?> PointAsync(string? accountSid, string numberSid, VoiceWebhooks webhooks, CancellationToken ct = default);
    /// <summary>Billable minutes of every call to or from <paramref name="number"/> since <paramref name="since"/>
    /// (each call rounded up to the minute, as Twilio bills); null with an error when the call log can't be read.</summary>
    Task<(int? Minutes, string? Error)> MinutesUsedAsync(string? accountSid, string number, DateTimeOffset since,
        CancellationToken ct = default);
    /// <summary>Hands a number back to Twilio. A number already gone counts as released. Null = done, else why not.</summary>
    Task<string?> ReleaseAsync(string? accountSid, string numberSid, CancellationToken ct = default);
}
