namespace MapleKiosk.Web.Services;

/// <summary>
/// One product in the "MapleKiosk family" apps showcase — each row renders one card.
/// Backed by SQLite via <see cref="FamilyAppCatalog"/>.
/// </summary>
public class FamilyApp
{
    public int Id { get; set; }
    public int SortOrder { get; set; }
    public string Number { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>When true the card is highlighted (the flagship MapleKiosk app).</summary>
    public bool IsFeatured { get; set; }

    /// <summary>Monthly SaaS (hosted) price in USD. null ⇒ shown as "Custom".</summary>
    public decimal? SaasMonthlyUsd { get; set; }
    /// <summary>One-time on-premise (self-hosted) licence price in USD. null ⇒ shown as "Custom".</summary>
    public decimal? OnPremiseUsd { get; set; }
}
