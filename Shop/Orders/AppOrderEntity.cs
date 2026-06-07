using System.Text.Json;
using Azure;
using Azure.Data.Tables;

namespace MapleKiosk.Web.Shop.Orders;

/// <summary>Azure Table row for an <see cref="AppOrder"/>. PartitionKey buckets by
/// creation month (stable across status changes); RowKey is the OrderRef. The
/// ETag drives optimistic concurrency on Pending→Paid.</summary>
public sealed class AppOrderEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "";
    public string RowKey { get; set; } = "";
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string LinesJson { get; set; } = "[]";
    public string Method { get; set; } = AppPaymentMethods.Stripe;
    public string Currency { get; set; } = "USD";
    public double Total { get; set; }
    public string Interval { get; set; } = "OneTime";
    public int TrialDays { get; set; }
    public string Status { get; set; } = nameof(AppOrderStatus.Pending);
    public string? CustomerEmail { get; set; }
    public string? ProviderTxnId { get; set; }
    public string? ProviderRef { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    public static string PartitionFor(DateTimeOffset createdAt) => createdAt.ToString("yyyyMM");

    public static AppOrderEntity FromOrder(AppOrder o) => new()
    {
        PartitionKey = PartitionFor(o.CreatedAt),
        RowKey = o.OrderRef,
        LinesJson = JsonSerializer.Serialize(o.Lines),
        Method = o.Method,
        Currency = o.Currency,
        Total = (double)o.Total,
        Interval = o.Interval,
        TrialDays = o.TrialDays,
        Status = o.Status.ToString(),
        CustomerEmail = o.CustomerEmail,
        ProviderTxnId = o.ProviderTxnId,
        ProviderRef = o.ProviderRef,
        CreatedAt = o.CreatedAt,
        PaidAt = o.PaidAt,
        ExpiresAt = o.ExpiresAt
    };

    public AppOrder ToOrder() => new()
    {
        OrderRef = RowKey,
        Lines = JsonSerializer.Deserialize<List<AppOrderLine>>(LinesJson) ?? new(),
        Method = Method,
        Currency = Currency,
        Total = (decimal)Total,
        Interval = string.IsNullOrWhiteSpace(Interval) ? "OneTime" : Interval,
        TrialDays = TrialDays,
        Status = Enum.TryParse<AppOrderStatus>(Status, out var s) ? s : AppOrderStatus.Pending,
        CustomerEmail = CustomerEmail,
        ProviderTxnId = ProviderTxnId,
        ProviderRef = ProviderRef,
        CreatedAt = CreatedAt,
        PaidAt = PaidAt,
        ExpiresAt = ExpiresAt
    };
}
