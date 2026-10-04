// Vendored from the monorepo: MapleKiosk/MK.Chat/SPC.BO.Voice/Contracts/IVoiceAgentProvider.cs
// Kept as close to the original as possible (it is tuned); only namespaces and dependencies the site
// lacks were changed. See Assistant/Voice/WebsiteVoiceEndpoints.cs for what was stripped.

namespace MapleKiosk.Web.Assistant.Voice;

/// <summary>
/// One vendor implementation, registered additively. Providers are peers: none is primary, none knows the
/// others exist, and each reads only the profile it is handed. Adding a vendor is a new class plus one
/// registration — no shared switch to extend, no shared configuration to widen.
/// </summary>
public interface IVoiceAgentProvider
{
    /// <summary>Matched against <see cref="VoiceProfileInfo.Kind"/>, case-insensitively.</summary>
    string Kind { get; }

    /// <summary>
    /// Build a service from this profile. Everything the vendor needs — keys, models, endpoints — is on
    /// the profile, and it validates itself; there is no central readiness rule to keep in sync.
    /// </summary>
    IVoiceAgentService Create(VoiceProfileInfo profile);
}
