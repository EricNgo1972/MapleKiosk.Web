using Azure;
using Azure.Data.Tables;
using MapleKiosk.Web.Shop.Notifications;

namespace MapleKiosk.Web.Shop.Orders;

/// <summary>
/// Persists app-store orders to Azure Table Storage and owns the single
/// payment-confirmation choke-point, <see cref="MarkPaidAsync"/>. Stripe webhook,
/// VietQR aggregator webhook and manual reconciliation all funnel through it, and
/// it is the only place the three "payment finished" signals fire. The
/// Pending→Paid transition is ETag-guarded so concurrent webhook retries confirm
/// exactly once. Uses the site's existing STORAGE_CONNECTION_STRING.
/// </summary>
public sealed class AppOrderService
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
    /// the three signals. Returns true only for the single winning transition.</summary>
    public async Task<bool> MarkPaidAsync(string orderRef, string? providerTxnId, CancellationToken ct = default)
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

            if (string.Equals(entity.Status, nameof(AppOrderStatus.Paid), StringComparison.Ordinal))
            {
                _logger.LogInformation("MarkPaid: order {OrderRef} already paid; no-op.", orderRef);
                return false;
            }

            entity.Status = nameof(AppOrderStatus.Paid);
            entity.PaidAt = DateTimeOffset.UtcNow;
            entity.ProviderTxnId = providerTxnId;

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

    private async Task FireSignalsAsync(AppOrder order, CancellationToken ct)
    {
        var note = new OrderPaidNotification(
            order.OrderRef, order.Total, order.Currency, order.Method,
            order.CustomerEmail, order.ProviderTxnId, order.PaidAt ?? DateTimeOffset.UtcNow);

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
        if (_table is null) return null;

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
