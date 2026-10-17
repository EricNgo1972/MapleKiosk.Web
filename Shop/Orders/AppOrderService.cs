using Azure;
using Azure.Data.Tables;
using MapleKiosk.Web.Shop.Notifications;
using MapleKiosk.Web.Shop.PhoneLines;

namespace MapleKiosk.Web.Shop.Orders;

/// <summary>
/// Persists app-store orders to Azure Table Storage and owns the single
/// payment-confirmation choke-point, <see cref="MarkPaidAsync"/>. Stripe webhook,
/// VietQR aggregator webhook and manual reconciliation all funnel through it, and
/// it is the only place the three "payment finished" signals fire. The
/// Pending→Paid transition is ETag-guarded so concurrent webhook retries confirm
/// exactly once. Uses the site's existing STORAGE_CONNECTION_STRING.
/// </summary>
public sealed class AppOrderService : IPhoneLineOrders
{
    public const string TableName = "appstoreorders";

    private readonly IEnumerable<IOrderPaidSink> _sinks;
    private readonly ILogger<AppOrderService> _logger;
    private readonly TableClient? _table;

    public event Action<OrderPaidNotification>? OrderPaid;

    public AppOrderService(IEnumerable<IOrderPaidSink> sinks, ILogger<AppOrderService> logger)
    {
        _sinks = sinks;
        _logger = logger;

        var conn = Environment.GetEnvironmentVariable("STORAGE_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(conn))
        {
            _logger.LogWarning("STORAGE_CONNECTION_STRING not set — app-store orders cannot persist.");
            return;
        }

        _table = new TableClient(conn, TableName);
        try { _table.CreateIfNotExists(); }
        catch (Exception ex) { _logger.LogError(ex, "Could not ensure orders table exists."); }
    }

    public async Task<AppOrder> CreateAsync(AppOrder order, CancellationToken ct = default)
    {
        if (_table is null)
            throw new InvalidOperationException("Order storage is not configured.");

        await _table.AddEntityAsync(AppOrderEntity.FromOrder(order), ct).ConfigureAwait(false);
        _logger.LogInformation("App Store order created: {OrderRef}, {Method} {Total} {Currency}",
            order.OrderRef, order.Method, order.Total, order.Currency);
        return order;
    }

    public async Task<AppOrder?> GetAsync(string orderRef, CancellationToken ct = default)
    {
        var entity = await LoadAsync(orderRef, ct).ConfigureAwait(false);
        return entity?.ToOrder();
    }

    /// <summary>THE choke-point. Idempotently flips the order to Paid and fires
    /// the three signals. Returns true only for the single winning transition.
    /// A subscription order also records the Stripe customer and subscription it created.
    /// <paramref name="test"/> is whether the payment came from the Stripe test account (null = not Stripe): a test
    /// payment never confirms a live order, nor a live payment a test one.</summary>
    public async Task<bool> MarkPaidAsync(string orderRef, string? providerTxnId, CancellationToken ct = default,
        string? stripeCustomerId = null, string? stripeSubscriptionId = null, bool? test = null)
    {
        if (_table is null) return false;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var entity = await LoadAsync(orderRef, ct).ConfigureAwait(false);
            if (entity is null)
            {
                _logger.LogWarning("MarkPaid: order {OrderRef} not found.", orderRef);
                return false;
            }

            if (test is { } t && t != entity.IsTest)
            {
                _logger.LogError("MarkPaid: order {OrderRef} is {Mode} but the payment came from the Stripe {Account} account; ignored.",
                    orderRef, entity.IsTest ? "a test order" : "live", t ? "test" : "live");
                return false;
            }

            if (string.Equals(entity.Status, nameof(AppOrderStatus.Paid), StringComparison.Ordinal))
            {
                _logger.LogInformation("MarkPaid: order {OrderRef} already paid; no-op.", orderRef);
                return false;
            }

            entity.Status = nameof(AppOrderStatus.Paid);
            entity.PaidAt = DateTimeOffset.UtcNow;
            entity.ProviderTxnId = providerTxnId;
            entity.StripeCustomerId ??= stripeCustomerId;
            entity.StripeSubscriptionId ??= stripeSubscriptionId;

            try
            {
                await _table.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace, ct)
                    .ConfigureAwait(false);
            }
            catch (RequestFailedException ex) when (ex.Status == 412)
            {
                _logger.LogDebug("MarkPaid: ETag conflict on {OrderRef}; retrying.", orderRef);
                continue;
            }

            await FireSignalsAsync(entity.ToOrder(), ct).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    /// <summary>Mirrors a Stripe subscription change (renewed, card failing, cancelled...) onto its order.
    /// Stripe does the billing; this only keeps the order's view current. Last write wins.</summary>
    public async Task UpdateSubscriptionAsync(string orderRef, string subscriptionId, string? customerId, string status,
        DateTimeOffset? currentPeriodEnd, CancellationToken ct = default, bool test = false)
    {
        if (_table is null) return;
        var entity = await LoadAsync(orderRef, ct).ConfigureAwait(false);
        if (entity is null)
        {
            _logger.LogWarning("Subscription {SubscriptionId}: order {OrderRef} not found.", subscriptionId, orderRef);
            return;
        }
        if (entity.IsTest != test)
        {
            _logger.LogError("Subscription {SubscriptionId} is from the Stripe {Account} account but order {OrderRef} isn't; ignored.",
                subscriptionId, test ? "test" : "live", orderRef);
            return;
        }

        // Merge only the subscription columns, so this never races the Pending→Paid write
        // (Stripe sends subscription.created alongside checkout.session.completed).
        var patch = new TableEntity(entity.PartitionKey, entity.RowKey)
        {
            [nameof(AppOrderEntity.StripeSubscriptionId)] = subscriptionId,
            [nameof(AppOrderEntity.SubscriptionStatus)] = status
        };
        if (customerId is not null) patch[nameof(AppOrderEntity.StripeCustomerId)] = customerId;
        if (currentPeriodEnd is not null) patch[nameof(AppOrderEntity.CurrentPeriodEnd)] = currentPeriodEnd;
        await _table.UpdateEntityAsync(patch, ETag.All, TableUpdateMode.Merge, ct).ConfigureAwait(false);
        _logger.LogInformation("App Store order {OrderRef} subscription {SubscriptionId} is {Status}.", orderRef, subscriptionId, status);
    }

    public async Task MarkFailedAsync(string orderRef, string reason, CancellationToken ct = default)
    {
        if (_table is null) return;
        var entity = await LoadAsync(orderRef, ct).ConfigureAwait(false);
        if (entity is null || string.Equals(entity.Status, nameof(AppOrderStatus.Paid), StringComparison.Ordinal))
            return;

        entity.Status = nameof(AppOrderStatus.Failed);
        try
        {
            await _table.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace, ct).ConfigureAwait(false);
            _logger.LogInformation("App Store order {OrderRef} marked failed: {Reason}", orderRef, reason);
        }
        catch (RequestFailedException ex) when (ex.Status == 412) { /* lost race with confirmation */ }
    }

    /// <summary>
    /// Claims a PAID phone-line order for buying its number: moves it to <see cref="PhoneLineStatus.Buying"/>
    /// with an ETag-conditional write, so of any number of concurrent callers (Stripe replays, a second tab,
    /// staff) exactly one wins and only that one may call Twilio. <paramref name="newNumber"/> replaces the
    /// chosen number (a retry after it was taken). False = not paid, not claimable, or someone else won.
    /// </summary>
    public async Task<bool> ClaimPhoneLineAsync(string orderRef, string? newNumber, CancellationToken ct = default)
    {
        if (_table is null) return false;

        // A Stripe subscription mirror can touch the row between our read and write (it lands alongside the
        // payment); that 412 isn't a rival claim, so read again and retry while the order is still claimable.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var entity = await LoadAsync(orderRef, ct).ConfigureAwait(false);
            if (entity is null
                || !string.Equals(entity.Status, nameof(AppOrderStatus.Paid), StringComparison.Ordinal)
                || string.IsNullOrEmpty(entity.PhoneLineTenantId)
                || !PhoneLineStatus.IsClaimable(entity.PhoneLineStatus))
                return false;

            var patch = new TableEntity(entity.PartitionKey, entity.RowKey)
            {
                [nameof(AppOrderEntity.PhoneLineStatus)] = PhoneLineStatus.Buying,
                [nameof(AppOrderEntity.PhoneLineError)] = ""
            };
            if (!string.IsNullOrEmpty(newNumber)) patch[nameof(AppOrderEntity.PhoneLineNumber)] = newNumber;

            try
            {
                await _table.UpdateEntityAsync(patch, entity.ETag, TableUpdateMode.Merge, ct).ConfigureAwait(false);
                _logger.LogInformation("Phone line: order {OrderRef} claimed for buying.", orderRef);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 412)
            {
                _logger.LogDebug("Phone line: ETag conflict claiming {OrderRef}; reading again.", orderRef);
            }
        }

        return false;
    }

    /// <summary>Records how buying the number ended. Only the claim's winner calls this.</summary>
    public async Task FinishPhoneLineAsync(string orderRef, string status, string? number, string? sid, string? error,
        CancellationToken ct = default, string? accountSid = null)
    {
        if (_table is null) return;
        var entity = await LoadAsync(orderRef, ct).ConfigureAwait(false);
        if (entity is null) return;

        var patch = new TableEntity(entity.PartitionKey, entity.RowKey)
        {
            [nameof(AppOrderEntity.PhoneLineStatus)] = status,
            [nameof(AppOrderEntity.PhoneLineError)] = error ?? ""
        };
        if (!string.IsNullOrEmpty(number)) patch[nameof(AppOrderEntity.PhoneLineNumber)] = number;
        if (!string.IsNullOrEmpty(sid)) patch[nameof(AppOrderEntity.PhoneLineSid)] = sid;
        if (!string.IsNullOrEmpty(accountSid)) patch[nameof(AppOrderEntity.PhoneLineAccountSid)] = accountSid;
        await _table.UpdateEntityAsync(patch, ETag.All, TableUpdateMode.Merge, ct).ConfigureAwait(false);
        _logger.LogInformation("Phone line: order {OrderRef} is {Status} ({Number}).", orderRef, status, number);
    }

    /// <summary>Sets (or with null clears) when an Active line's number goes back to Twilio. The subscription
    /// mirror calls it; the releaser acts on it.</summary>
    public async Task ScheduleReleaseAsync(string orderRef, DateTimeOffset? releaseAt, CancellationToken ct = default)
    {
        if (_table is null) return;
        var entity = await LoadAsync(orderRef, ct).ConfigureAwait(false);
        if (entity is null) return;

        var patch = new TableEntity(entity.PartitionKey, entity.RowKey)
        {
            [nameof(AppOrderEntity.PhoneLineReleaseAt)] = releaseAt?.ToString("O") ?? ""
        };
        await _table.UpdateEntityAsync(patch, ETag.All, TableUpdateMode.Merge, ct).ConfigureAwait(false);
        _logger.LogInformation("Phone line: order {OrderRef} number release {When}.", orderRef,
            releaseAt is { } at ? $"scheduled for {at:u}" : "cancelled");
    }

    public async Task SetCapStateAsync(string orderRef, string state, CancellationToken ct = default)
    {
        if (_table is null) return;
        var entity = await LoadAsync(orderRef, ct).ConfigureAwait(false);
        if (entity is null) return;
        var patch = new TableEntity(entity.PartitionKey, entity.RowKey) { [nameof(AppOrderEntity.PhoneLineCapState)] = state };
        await _table.UpdateEntityAsync(patch, ETag.All, TableUpdateMode.Merge, ct).ConfigureAwait(false);
        _logger.LogInformation("Phone line: order {OrderRef} cap state {State}.", orderRef, state.Length == 0 ? "cleared" : state);
    }

    /// <summary>Active → Releasing with an ETag-conditional write, so exactly one caller hands the number back.</summary>
    public async Task<bool> ClaimReleaseAsync(string orderRef, CancellationToken ct = default)
    {
        if (_table is null) return false;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var entity = await LoadAsync(orderRef, ct).ConfigureAwait(false);
            if (entity is null || entity.PhoneLineStatus != PhoneLineStatus.Active) return false;

            var patch = new TableEntity(entity.PartitionKey, entity.RowKey)
            {
                [nameof(AppOrderEntity.PhoneLineStatus)] = PhoneLineStatus.Releasing
            };
            try
            {
                await _table.UpdateEntityAsync(patch, entity.ETag, TableUpdateMode.Merge, ct).ConfigureAwait(false);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 412)
            {
                _logger.LogDebug("Phone line: ETag conflict claiming release of {OrderRef}; reading again.", orderRef);
            }
        }
        return false;
    }

    /// <summary>Phone-line orders, newest first: one salon's when <paramref name="tenantId"/> is given, else all (staff).</summary>
    public async Task<IReadOnlyList<AppOrder>> ListPhoneLinesAsync(string? tenantId, CancellationToken ct = default)
    {
        if (_table is null) return [];

        var filter = string.IsNullOrEmpty(tenantId)
            ? TableClient.CreateQueryFilter($"Source eq {PhoneLineService.Source}")
            : TableClient.CreateQueryFilter($"Source eq {PhoneLineService.Source} and PhoneLineTenantId eq {tenantId}");

        var orders = new List<AppOrder>();
        await foreach (var e in _table.QueryAsync<AppOrderEntity>(filter: filter, cancellationToken: ct).ConfigureAwait(false))
            orders.Add(e.ToOrder());
        return orders.OrderByDescending(o => o.CreatedAt).ToList();
    }

    private async Task FireSignalsAsync(AppOrder order, CancellationToken ct)
    {
        var note = new OrderPaidNotification(
            order.OrderRef, order.Total, order.Currency, order.Method,
            order.CustomerEmail, order.ProviderTxnId, order.PaidAt ?? DateTimeOffset.UtcNow,
            order.Lines, order.Company, order.CustomerName, order.CustomerPhone, order.Source, order.Culture, order.IsTest);

        try { OrderPaid?.Invoke(note); }
        catch (Exception ex) { _logger.LogError(ex, "OrderPaid handler threw for {OrderRef}", note.OrderRef); }

        foreach (var sink in _sinks)
        {
            try { await sink.OnOrderPaidAsync(note, ct).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogError(ex, "Sink {Sink} failed for {OrderRef}", sink.GetType().Name, note.OrderRef); }
        }
    }

    private async Task<AppOrderEntity?> LoadAsync(string orderRef, CancellationToken ct)
    {
        // Refs are alphanumeric; anything else (a quote, say) must never reach the OData filter below.
        if (_table is null || string.IsNullOrEmpty(orderRef) || !orderRef.All(char.IsAsciiLetterOrDigit)) return null;

        var partition = PartitionFromRef(orderRef);
        if (partition is not null)
        {
            var res = await _table.GetEntityIfExistsAsync<AppOrderEntity>(partition, orderRef, cancellationToken: ct)
                .ConfigureAwait(false);
            if (res.HasValue) return res.Value;
        }

        await foreach (var e in _table.QueryAsync<AppOrderEntity>(
            filter: $"RowKey eq '{orderRef}'", maxPerPage: 1, cancellationToken: ct).ConfigureAwait(false))
            return e;

        return null;
    }

    private static string? PartitionFromRef(string orderRef)
    {
        if (orderRef.Length < 6 || !orderRef.StartsWith("MK", StringComparison.Ordinal)) return null;
        var yy = orderRef.Substring(2, 2);
        var mm = orderRef.Substring(4, 2);
        if (!yy.All(char.IsDigit) || !mm.All(char.IsDigit)) return null;
        return $"20{yy}{mm}";
    }
}
