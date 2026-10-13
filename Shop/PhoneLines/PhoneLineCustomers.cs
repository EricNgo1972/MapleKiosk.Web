using MapleKiosk.Web.Onboarding;

namespace MapleKiosk.Web.Shop.PhoneLines;

/// <summary>
/// <see cref="IPhoneLineCustomers"/> over this site's customers: the onboarding records staff create after the
/// deposit. A record counts only while its access is Active and its login email is the signed-in one (the same
/// rule as its setup page); the business is named and placed by the customer's own answers, falling back to the
/// name staff gave it. Which app install it is comes from staff (the record's TenantId), never from the customer.
/// </summary>
public sealed class PhoneLineCustomers(OnboardingStore store) : IPhoneLineCustomers
{
    public async Task<IReadOnlyList<CustomerBusiness>> ListAsync(string email, CancellationToken ct = default)
    {
        var records = await store.FindByEmailAsync(email, ct).ConfigureAwait(false);
        return records
            .Where(r => r.IsOpenTo(email, isAdmin: false))
            .OrderBy(r => r.CreatedAt)
            .Select(r => new CustomerBusiness(r.Token, NameOf(r), AddressOf(r.Form), r.TenantId))
            .ToList();
    }

    private static string NameOf(OnboardingRecord r) =>
        !string.IsNullOrWhiteSpace(r.Form.BusinessName) ? r.Form.BusinessName.Trim() : r.BusinessName.Trim();

    private static string? AddressOf(OnboardingForm f)
    {
        var parts = new[] { f.Address, f.City }.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList();
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }
}
