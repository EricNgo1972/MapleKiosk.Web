using MapleKiosk.Web.Shop.Catalog;
using MapleKiosk.Web.Shop.Config;
using Stripe;

namespace MapleKiosk.Web.Shop.Payments;

/// <summary>One thing we sell, as Stripe should know it: a Product with one active Price.</summary>
/// <param name="Sku">The catalog SKU.</param>
/// <param name="Interval">OneTime / Monthly / Yearly (<see cref="BillingIntervals"/>).</param>
public sealed record StripeItem(string Sku, string Name, string? Description, decimal Price, string Currency,
    string Interval, bool Active)
{
    /// <summary>A catalog item; unpriced or inactive items are archived in Stripe.</summary>
    public static StripeItem From(AppProduct p)
        => new(p.Sku, p.Name, p.Description, p.Price, CatalogStore.Currency, p.BillingInterval, p.Active && p.Price > 0);
}

/// <summary>Where a <see cref="StripeItem"/> stands in the Stripe account the site's key points at.</summary>
public enum StripeSyncState { NotConfigured, Missing, Outdated, InSync, Archived }

public sealed record StripeItemStatus(StripeItem Item, StripeSyncState State, string? ProductId, string? PriceId, string? Detail);

public sealed record StripeSyncResult(bool Success, string? ProductId, string? PriceId, string? Error);

/// <summary>
/// Keeps Stripe Products and Prices in step with the catalog (/shop/admin syncs an item when it's
/// saved, archives it when it's deleted, and can sync everything at once). Ported from the monorepo's eShop gateway (StripeEShopGateway.SyncProductAsync):
/// prices are immutable, so a changed amount, currency or interval mints a new Price and archives the
/// old one (live subscriptions keep billing on it). Unlike the eShop it stores nothing: the Product id
/// is derived from the SKU and the current Price carries a lookup_key, so a switched account or
/// test↔live key heals on the next sync. Checkout (<see cref="StripeCheckoutCreator"/>) resolves the
/// same lookup keys and bills the real Price, so Stripe reports revenue by product.
/// </summary>
public sealed class StripeProductSync
{
    private readonly AppStoreConfig _config;
    private readonly ILogger<StripeProductSync> _logger;

    public StripeProductSync(AppStoreConfig config, ILogger<StripeProductSync> logger)
    {
        _config = config;
        _logger = logger;
    }

    // Stripe ids allow letters, digits, '_' and '-': "quote:nails:setup.server" → "mk_quote_nails_setup_server".
    public static string ProductIdFor(string sku) => "mk_" + Safe(sku);
    public static string LookupKeyFor(string sku) => "mk_" + Safe(sku);

    private static string Safe(string sku)
        => new(sku.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '_').ToArray());

    public async Task<bool> IsConfiguredAsync() => !string.IsNullOrEmpty(await _config.GetStripeSecretKeyAsync().ConfigureAwait(false));

    private async Task<StripeClient?> ClientAsync()
    {
        var key = await _config.GetStripeSecretKeyAsync().ConfigureAwait(false);
        return string.IsNullOrEmpty(key) ? null : new StripeClient(key);
    }

    /// <summary>Compares each item with Stripe without changing anything.</summary>
    public async Task<IReadOnlyList<StripeItemStatus>> GetStatusAsync(IReadOnlyList<StripeItem> items, CancellationToken ct = default)
    {
        var client = await ClientAsync().ConfigureAwait(false);
        if (client is null) return items.Select(i => new StripeItemStatus(i, StripeSyncState.NotConfigured, null, null, null)).ToList();

        var prices = await FindPricesAsync(client, items.Select(i => LookupKeyFor(i.Sku)), ct).ConfigureAwait(false);
        var products = new ProductService(client);
        var result = new List<StripeItemStatus>();
        foreach (var item in items)
        {
            var productId = ProductIdFor(item.Sku);
            var product = await GetOrNullAsync(() => products.GetAsync(productId, cancellationToken: ct)).ConfigureAwait(false);
            prices.TryGetValue(LookupKeyFor(item.Sku), out var price);

            StripeItemStatus Status(StripeSyncState s, string? detail = null) => new(item, s, product?.Id, price?.Id, detail);
            if (product is null || price is null) { result.Add(Status(StripeSyncState.Missing)); continue; }
            if (!item.Active) { result.Add(Status(product.Active ? StripeSyncState.Outdated : StripeSyncState.Archived, product.Active ? "still active in Stripe" : null)); continue; }

            var diffs = new List<string>();
            if (!product.Active) diffs.Add("archived in Stripe");
            if (product.Name != item.Name) diffs.Add($"name “{product.Name}”");
            if (!Matches(price, item)) diffs.Add($"price {price.UnitAmount / 100m:0.00} {price.Currency.ToUpperInvariant()}{(price.Recurring is null ? "" : "/" + price.Recurring.Interval)}");
            result.Add(diffs.Count == 0 ? Status(StripeSyncState.InSync) : Status(StripeSyncState.Outdated, string.Join(", ", diffs)));
        }
        return result;
    }

    /// <summary>Creates or updates the item's Product and makes sure its lookup key points at a Price
    /// with the right amount, currency and interval. An inactive item archives its Product.</summary>
    public async Task<StripeSyncResult> SyncAsync(StripeItem item, CancellationToken ct = default)
    {
        var client = await ClientAsync().ConfigureAwait(false);
        if (client is null) return new StripeSyncResult(false, null, null, "Stripe is not configured.");

        var productId = ProductIdFor(item.Sku);
        var lookupKey = LookupKeyFor(item.Sku);
        try
        {
            var products = new ProductService(client);
            var priceService = new PriceService(client);
            var metadata = new Dictionary<string, string> { ["sku"] = item.Sku, ["source"] = "maplekiosk-www" };

            // 1. Product: our id, so it's found again without storing anything.
            var product = await GetOrNullAsync(() => products.GetAsync(productId, cancellationToken: ct)).ConfigureAwait(false);
            if (product is null)
            {
                product = await products.CreateAsync(new ProductCreateOptions
                {
                    Id = productId,
                    Name = item.Name,
                    Description = NullIfEmpty(item.Description),
                    Active = item.Active,
                    Metadata = metadata
                }, cancellationToken: ct).ConfigureAwait(false);
            }
            else
            {
                await products.UpdateAsync(productId, new ProductUpdateOptions
                {
                    Name = item.Name,
                    Description = NullIfEmpty(item.Description) ?? "",
                    Active = item.Active,
                    Metadata = metadata
                }, cancellationToken: ct).ConfigureAwait(false);
            }

            if (!item.Active)
            {
                _logger.LogInformation("Stripe product archived: {Sku} -> {ProductId}", item.Sku, productId);
                return new StripeSyncResult(true, productId, null, null);
            }

            // 2. Price: reuse the lookup key's price only if nothing billing-relevant changed (and it
            //    belongs to this product); else mint a new one, move the lookup key to it, archive the old.
            var current = (await FindPricesAsync(client, [lookupKey], ct).ConfigureAwait(false)).GetValueOrDefault(lookupKey);
            if (current is not null && current.ProductId == productId && Matches(current, item))
            {
                if (product.DefaultPriceId != current.Id)
                    await products.UpdateAsync(productId, new ProductUpdateOptions { DefaultPrice = current.Id }, cancellationToken: ct).ConfigureAwait(false);
                return new StripeSyncResult(true, productId, current.Id, null);
            }

            var options = new PriceCreateOptions
            {
                Product = productId,
                UnitAmount = Cents(item.Price),
                Currency = item.Currency.ToLowerInvariant(),
                LookupKey = lookupKey,
                TransferLookupKey = true,
                Metadata = new Dictionary<string, string> { ["sku"] = item.Sku }
            };
            if (BillingIntervals.StripeInterval(item.Interval) is { } interval)
                options.Recurring = new PriceRecurringOptions { Interval = interval };

            var price = await priceService.CreateAsync(options, cancellationToken: ct).ConfigureAwait(false);
            await products.UpdateAsync(productId, new ProductUpdateOptions { DefaultPrice = price.Id }, cancellationToken: ct).ConfigureAwait(false);

            // Live subscriptions keep billing on the old price; that is correct, not a bug.
            if (current is not null)
            {
                try { await priceService.UpdateAsync(current.Id, new PriceUpdateOptions { Active = false }, cancellationToken: ct).ConfigureAwait(false); }
                catch (StripeException ex) when (IsResourceMissing(ex)) { }
            }

            _logger.LogInformation("Stripe product synced: {Sku} -> {ProductId}/{PriceId}", item.Sku, productId, price.Id);
            return new StripeSyncResult(true, productId, price.Id, null);
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Stripe product sync failed: {Sku}", item.Sku);
            return new StripeSyncResult(false, productId, null, ex.StripeError?.Message ?? ex.Message);
        }
    }

    /// <summary>The live Price ids for these SKUs whose amount/currency/interval still match, for
    /// checkout. Anything missing or stale is left out (checkout then prices that line inline).</summary>
    public async Task<IReadOnlyDictionary<string, string>> ResolvePriceIdsAsync(IEnumerable<StripeItem> items, CancellationToken ct = default)
    {
        var list = items.ToList();
        var resolved = new Dictionary<string, string>();
        var client = await ClientAsync().ConfigureAwait(false);
        if (client is null || list.Count == 0) return resolved;
        try
        {
            var prices = await FindPricesAsync(client, list.Select(i => LookupKeyFor(i.Sku)), ct).ConfigureAwait(false);
            foreach (var item in list)
                if (prices.TryGetValue(LookupKeyFor(item.Sku), out var p) && p.ProductId == ProductIdFor(item.Sku) && Matches(p, item))
                    resolved[item.Sku] = p.Id;
        }
        catch (StripeException ex)
        {
            _logger.LogWarning(ex, "Stripe price lookup failed; checkout prices inline.");
        }
        return resolved;
    }

    // Active prices by lookup key (Stripe takes up to 10 keys per list call).
    private static async Task<Dictionary<string, Price>> FindPricesAsync(StripeClient client, IEnumerable<string> lookupKeys, CancellationToken ct)
    {
        var service = new PriceService(client);
        var found = new Dictionary<string, Price>();
        foreach (var chunk in lookupKeys.Distinct().Chunk(10))
        {
            var page = await service.ListAsync(new PriceListOptions { LookupKeys = chunk.ToList(), Active = true, Limit = 100 }, cancellationToken: ct).ConfigureAwait(false);
            foreach (var p in page.Data.Where(p => p.LookupKey is not null)) found[p.LookupKey] = p;
        }
        return found;
    }

    private static bool Matches(Price price, StripeItem item)
    {
        var interval = BillingIntervals.StripeInterval(item.Interval);
        var sameInterval = interval is null
            ? price.Recurring is null
            : string.Equals(price.Recurring?.Interval, interval, StringComparison.OrdinalIgnoreCase);
        return price.Active
            && price.UnitAmount == Cents(item.Price)
            && string.Equals(price.Currency, item.Currency, StringComparison.OrdinalIgnoreCase)
            && sameInterval;
    }

    public static long Cents(decimal amount) => (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static bool IsResourceMissing(StripeException ex) =>
        string.Equals(ex.StripeError?.Code, "resource_missing", StringComparison.Ordinal)
        || ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound;

    private static async Task<T?> GetOrNullAsync<T>(Func<Task<T>> get) where T : class
    {
        try { return await get().ConfigureAwait(false); }
        catch (StripeException ex) when (IsResourceMissing(ex)) { return null; }
    }
}
