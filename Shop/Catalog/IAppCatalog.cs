namespace MapleKiosk.Web.Shop.Catalog;

public interface IAppCatalog
{
    Task<IReadOnlyList<AppProduct>> GetActiveAsync(CancellationToken ct = default);
    Task<AppProduct?> FindAsync(string sku, CancellationToken ct = default);
}
