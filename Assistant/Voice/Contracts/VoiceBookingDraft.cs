// Vendored from the monorepo: MapleKiosk/MK.Chat/SPC.BO.Voice/Contracts/VoiceBookingDraft.cs
// Kept as close to the original as possible (it is tuned); only namespaces and dependencies the site
// lacks were changed. See Assistant/Voice/WebsiteVoiceEndpoints.cs for what was stripped.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace MapleKiosk.Web.Assistant.Voice;

/// <summary>
/// The booking the guest was asking for, as the assistant gathered it DURING the call — name, phone, the
/// service, when, and whether they come alone or with whom. The agent fills this in as it goes (see the head's draft tool), so a call that ends
/// before the guest confirms still leaves a structured request the front desk can turn into a real
/// appointment by hand — no re-listening to the recording, and no dependence on the AI having finished.
///
/// <para>Every field is optional: the agent notes what it has so far, and a half-filled draft ("she wants
/// a pedicure Tuesday afternoon" with no name yet) is still worth keeping. Merging keeps the newest
/// non-blank value per field, so repeated notes refine rather than overwrite.</para>
/// </summary>
public sealed record VoiceBookingDraft(
    [property: JsonPropertyName("guest_name")]  string? GuestName = null,
    [property: JsonPropertyName("guest_phone")] string? GuestPhone = null,
    [property: JsonPropertyName("service")]     string? Service = null,
    [property: JsonPropertyName("date_time")]   string? DateTime = null,
    [property: JsonPropertyName("preferred_tech")] string? PreferredTech = null,
    [property: JsonPropertyName("guest_names")] string? GuestNames = null,
    [property: JsonPropertyName("party_size")] [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] int? PartySize = null)
{
    public static readonly VoiceBookingDraft Empty = new();

    /// <summary>True when nothing has been noted — used to skip storing an empty draft.</summary>
    [JsonIgnore]
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(GuestName) && string.IsNullOrWhiteSpace(GuestPhone)
        && string.IsNullOrWhiteSpace(Service) && string.IsNullOrWhiteSpace(DateTime)
        && string.IsNullOrWhiteSpace(PreferredTech) && string.IsNullOrWhiteSpace(GuestNames)
        && PartySize is null;

    /// <summary>The guest said they come alone (asked and answered), as opposed to not yet asked.</summary>
    [JsonIgnore]
    public bool IsAlone => PartySize == 1;

    /// <summary>Overlay <paramref name="next"/>'s non-blank fields onto this one (newest note wins).</summary>
    public VoiceBookingDraft MergedWith(VoiceBookingDraft? next)
    {
        if (next is null) return this;
        return new VoiceBookingDraft(
            Pick(next.GuestName, GuestName),
            Pick(next.GuestPhone, GuestPhone),
            Pick(next.Service, Service),
            Pick(next.DateTime, DateTime),
            Pick(next.PreferredTech, PreferredTech),
            Pick(next.GuestNames, GuestNames),
            next.PartySize is > 0 ? next.PartySize : PartySize);
    }

    private static string? Pick(string? incoming, string? existing) =>
        string.IsNullOrWhiteSpace(incoming) ? existing : incoming.Trim();

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Parse a stored draft (or one tool call's arguments — same field names). Tolerant: bad or
    /// blank JSON yields null, never throws.</summary>
    public static VoiceBookingDraft? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var d = JsonSerializer.Deserialize<VoiceBookingDraft>(json, Json);
            return d is null || d.IsEmpty ? null : d;
        }
        catch (JsonException) { return null; }
    }
}
