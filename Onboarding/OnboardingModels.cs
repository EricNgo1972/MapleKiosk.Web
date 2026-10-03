using System.Security.Cryptography;

namespace MapleKiosk.Web.Onboarding;

/// <summary>Industries we hand out setup templates for. The id picks the
/// template file: <c>wwwroot/templates/onboarding/{id}-{lang}.xlsx</c>.</summary>
public static class OnboardingIndustries
{
    public const string Nails = "nails";
    public const string Coffee = "coffee";
    public const string Restaurant = "restaurant";
    public const string Garage = "garage";

    public static readonly IReadOnlyList<(string Id, string Label)> All =
    [
        (Nails, "Nails & Spa"),
        (Coffee, "Coffee shop"),
        (Restaurant, "Restaurant"),
        (Garage, "Garage"),
    ];

    public static string Label(string id) => All.FirstOrDefault(i => i.Id == id).Label ?? id;
}

public enum OnboardingStatus { Sent, InProgress, Submitted }

/// <summary>Whether the customer can reach the record. Set by staff; Suspended and
/// Archived both lock the customer out (data kept), Archived also leaves the main list.</summary>
public enum OnboardingAccess { Active, Suspended, Archived }

/// <summary>Answer to an "give us access" step. We never ask for passwords —
/// the owner invites us (Google Manager, Meta Partner) and ticks the result.</summary>
public static class AccessAnswers
{
    public const string Done = "done";
    public const string NotApplicable = "na";
    public const string NeedHelp = "help";
}

public sealed class DayHours
{
    public string Day { get; set; } = "";
    public bool Closed { get; set; }
    public string Open { get; set; } = "09:00";
    public string Close { get; set; } = "19:00";
}

/// <summary>The one-time answers on the web form. Lists (services, staff) live in
/// the uploaded template and are read by the implementer, not by this app.</summary>
public sealed class OnboardingForm
{
    // Business
    public string BusinessName { get; set; } = "";
    public string LegalName { get; set; } = "";
    public string Motto { get; set; } = "";
    /// <summary>The business logo, used on booking pages, receipts and the website.</summary>
    public OnboardingFile? Logo { get; set; }
    public string Address { get; set; } = "";
    public string City { get; set; } = "";
    public string PostalCode { get; set; } = "";
    public string Phone { get; set; } = "";
    public string NotifyEmail { get; set; } = "";
    public string ContactName { get; set; } = "";
    public string ContactMobile { get; set; } = "";

    // Hours
    public List<DayHours> Hours { get; set; } = DefaultHours();
    public string HoursNotes { get; set; } = "";

    /// <summary>Hours come pre-filled, so "done" needs the customer to touch or confirm them.</summary>
    public bool HoursConfirmed { get; set; }

    // Online presence
    public string Website { get; set; } = "";
    public string Facebook { get; set; } = "";
    public string Instagram { get; set; } = "";
    public string GoogleProfile { get; set; } = "";
    public string SmsNumber { get; set; } = "";
    public bool NoOnline { get; set; }

    // Existing hardware the tenant wants to keep using
    public List<HardwareItem> Hardware { get; set; } = new();
    public bool NoHardware { get; set; }

    // Access (no passwords)
    public string GoogleAccess { get; set; } = "";
    public string MetaAccess { get; set; } = "";

    public string Notes { get; set; } = "";

    public static readonly string[] Days = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    public static List<DayHours> DefaultHours() =>
        Days.Select(d => new DayHours { Day = d, Closed = d == "Sun" }).ToList();
}

/// <summary>A device the tenant already owns and wants to reuse. <see cref="Type"/>
/// is an id from <see cref="HardwareOptions.Types"/>.</summary>
public sealed class HardwareItem
{
    public string Type { get; set; } = "";
    public string Brand { get; set; } = "";
    public string Model { get; set; } = "";
    public int Quantity { get; set; } = 1;
    public string Notes { get; set; } = "";
}

public sealed class OnboardingFile
{
    public string Id { get; set; } = "";
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    public long Size { get; set; }
    public DateTimeOffset UploadedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One customer's setup intake, created by staff after the deposit. The customer
/// signs in (Google / Microsoft via the central OAuth host) with
/// <see cref="ContactEmail"/> — that email is their access. <see cref="Token"/> is
/// just the record id (/onboarding/{token} when one email owns several businesses).
/// </summary>
public sealed class OnboardingRecord
{
    public string Token { get; set; } = NewToken();
    public string BusinessName { get; set; } = "";
    public string ContactEmail { get; set; } = "";
    public string Industry { get; set; } = OnboardingIndustries.Nails;
    public string Language { get; set; } = "en";
    public string? OrderRef { get; set; }
    public OnboardingStatus Status { get; set; } = OnboardingStatus.Sent;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public OnboardingForm Form { get; set; } = new();
    public List<OnboardingFile> Files { get; set; } = new();

    /// <summary>Scope includes a website: the customer also gets the website
    /// brief sub-page (/onboarding/{token}/website). Set by staff.</summary>
    public bool IncludesWebsite { get; set; }

    public OnboardingAccess Access { get; set; } = OnboardingAccess.Active;
    public DateTimeOffset? AccessChangedAt { get; set; }

    /// <summary>The one access rule: staff see every record; a customer only an active
    /// record on their own (non-empty) signed-in email.</summary>
    public bool IsOpenTo(string? signedInEmail, bool isAdmin)
    {
        if (isAdmin) return true;
        var email = CanonicalEmail(signedInEmail);
        return Access == OnboardingAccess.Active && email.Length > 0 && email == CanonicalEmail(ContactEmail);
    }

    /// <summary>Take the staff-owned fields from the stored copy, so a customer's page
    /// that was open while staff changed them can't write the old values back.</summary>
    public void CopyStaffFieldsFrom(OnboardingRecord stored)
    {
        ContactEmail = stored.ContactEmail;
        Access = stored.Access;
        AccessChangedAt = stored.AccessChangedAt;
        IncludesWebsite = stored.IncludesWebsite;
        Industry = stored.Industry;
        OrderRef = stored.OrderRef;
    }

    // 128 bits, lowercase hex — safe in a URL and a blob path.
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static string CanonicalEmail(string? email) => (email ?? "").Trim().ToLowerInvariant();

    public static bool IsValidToken(string? token) =>
        token is { Length: 32 } && token.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
