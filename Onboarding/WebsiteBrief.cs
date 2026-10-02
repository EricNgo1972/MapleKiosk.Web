namespace MapleKiosk.Web.Onboarding;

public sealed class WebsiteReference
{
    public string Url { get; set; } = "";

    /// <summary>What to copy from it: ids from <see cref="WebsiteBriefOptions.CopyAspects"/>.</summary>
    public List<string> Copy { get; set; } = new();
    public string Likes { get; set; } = "";
    public string Dislikes { get; set; } = "";
}

/// <summary>
/// The website design interview for a tenant whose scope includes a website.
/// Stored apart from the setup form (table <c>onboardingwebsite</c>, RowKey =
/// record token) so the brief and the setup form can be edited independently
/// without overwriting each other. Option values are ids from
/// <see cref="WebsiteBriefOptions"/>.
/// </summary>
public sealed class WebsiteBrief
{
    // Starting point
    public string CurrentSite { get; set; } = "";
    public string CurrentSiteUrl { get; set; } = "";
    public string CurrentLikes { get; set; } = "";
    public string CurrentProblems { get; set; } = "";
    public string Domain { get; set; } = "";
    public string DomainName { get; set; } = "";
    public bool WantsEmail { get; set; }
    public string EmailAddresses { get; set; } = "";

    // Goals & audience
    public List<string> Goals { get; set; } = new();
    public string PrimaryGoal { get; set; } = "";
    public List<string> Audience { get; set; } = new();
    public string AudienceNotes { get; set; } = "";
    public string Difference { get; set; } = "";
    public string ThreeWords { get; set; } = "";

    // Pages & features
    public List<string> Pages { get; set; } = new();
    public List<string> Features { get; set; } = new();
    public List<string> Languages { get; set; } = new();
    public string PagesNotes { get; set; } = "";

    // Style
    public List<string> Styles { get; set; } = new();
    /// <summary>light / dark / both / any — see <see cref="WebsiteBriefOptions.Modes"/>.</summary>
    public string Mode { get; set; } = "";
    public Dictionary<string, int> Sliders { get; set; } = new();
    public string Fonts { get; set; } = "";

    // Colours — hex values ("#faf7f2"), from the presets or the custom picker; "" = designer's choice
    public string Background { get; set; } = "";
    public string Accent { get; set; } = "";
    /// <summary>Legacy palette pick (briefs from before background/accent existed).</summary>
    public string Palette { get; set; } = "";
    public string BrandColors { get; set; } = "";
    public string AvoidColors { get; set; } = "";

    // Logo & photos
    public string Logo { get; set; } = "";
    public string Photos { get; set; } = "";
    public List<string> ImageryMood { get; set; } = new();

    // Web assets they can share (ids from WebsiteBriefOptions.AssetTypes)
    public List<string> Assets { get; set; } = new();
    public string FontNames { get; set; } = "";
    /// <summary>Google Drive / Dropbox / OneDrive folder for files too big to upload.</summary>
    public string AssetsLink { get; set; } = "";

    // Voice & content
    public List<string> Tone { get; set; } = new();
    public string Copy { get; set; } = "";
    public string KeyMessages { get; set; } = "";

    // Websites to copy, competitors
    public List<WebsiteReference> References { get; set; } = [new(), new(), new()];
    public string Competitors { get; set; } = "";
    public string MustHave { get; set; } = "";
    public string Avoid { get; set; } = "";

    // Timeline & approvals
    public string LaunchDate { get; set; } = "";
    public string LaunchReason { get; set; } = "";
    public string Approver { get; set; } = "";
    public string ApproverContact { get; set; } = "";
    public string Notes { get; set; } = "";

    /// <summary>Logo, brand guide, photos — kept on the brief, not the setup form.</summary>
    public List<OnboardingFile> Files { get; set; } = new();
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>Last time the customer sent the brief to the design team.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    public int Slider(string id) => Sliders.TryGetValue(id, out var v) ? v : 3;
}
