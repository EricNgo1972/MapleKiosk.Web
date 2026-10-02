namespace MapleKiosk.Web.Onboarding;

public sealed class WebsiteReference
{
    public string Url { get; set; } = "";
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
    // 1. Starting point
    public string CurrentSite { get; set; } = "";
    public string CurrentSiteUrl { get; set; } = "";
    public string CurrentLikes { get; set; } = "";
    public string CurrentProblems { get; set; } = "";
    public string Domain { get; set; } = "";
    public string DomainName { get; set; } = "";
    public bool WantsEmail { get; set; }
    public string EmailAddresses { get; set; } = "";

    // 2. Goals & audience
    public List<string> Goals { get; set; } = new();
    public string PrimaryGoal { get; set; } = "";
    public List<string> Audience { get; set; } = new();
    public string AudienceNotes { get; set; } = "";
    public string Difference { get; set; } = "";
    public string ThreeWords { get; set; } = "";

    // 3. Pages & features
    public List<string> Pages { get; set; } = new();
    public List<string> Features { get; set; } = new();
    public List<string> Languages { get; set; } = new();
    public string PagesNotes { get; set; } = "";

    // 4. Look & feel
    public List<string> Styles { get; set; } = new();
    public Dictionary<string, int> Sliders { get; set; } = new();
    public string Palette { get; set; } = "";
    public string BrandColors { get; set; } = "";
    public string AvoidColors { get; set; } = "";
    public string Fonts { get; set; } = "";

    // 5. Logo & photos
    public string Logo { get; set; } = "";
    public string Photos { get; set; } = "";
    public List<string> ImageryMood { get; set; } = new();

    // 6. Voice & content
    public List<string> Tone { get; set; } = new();
    public string Copy { get; set; } = "";
    public string KeyMessages { get; set; } = "";

    // 7. Inspiration
    public List<WebsiteReference> References { get; set; } = [new(), new(), new()];
    public string Competitors { get; set; } = "";
    public string MustHave { get; set; } = "";
    public string Avoid { get; set; } = "";

    // 8. Timeline & approvals
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
