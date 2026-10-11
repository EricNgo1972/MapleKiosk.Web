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

    /// <summary>States a buy may start (or start again) from. Empty = None.</summary>
    public static bool IsClaimable(string? status) =>
        string.IsNullOrEmpty(status) || status is None or NumberTaken or WaitingForSalon or Failed;

    public static bool IsRetryable(string? status) => status is NumberTaken or WaitingForSalon or Failed;
}

/// <summary>The phone-line columns of the order store (<see cref="AppOrderService"/>); a seam for the tests.</summary>
public interface IPhoneLineOrders
{
    Task<AppOrder> CreateAsync(AppOrder order, CancellationToken ct = default);
    Task<AppOrder?> GetAsync(string orderRef, CancellationToken ct = default);
    Task<bool> ClaimPhoneLineAsync(string orderRef, string? newNumber, CancellationToken ct = default);
    Task FinishPhoneLineAsync(string orderRef, string status, string? number, string? sid, string? error, CancellationToken ct = default);
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
    Task<NumberPurchase> BuyAsync(string phoneNumber, VoiceWebhooks webhooks, string friendlyName, CancellationToken ct = default);
}
