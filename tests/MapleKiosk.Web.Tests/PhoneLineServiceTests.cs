using System.Runtime.CompilerServices;
using MapleKiosk.Web.Shop.Catalog;
using MapleKiosk.Web.Shop.Checkout;
using MapleKiosk.Web.Shop.Config;
using MapleKiosk.Web.Shop.Notifications;
using MapleKiosk.Web.Shop.Orders;
using MapleKiosk.Web.Shop.Payments;
using MapleKiosk.Web.Shop.Payments.Qr;
using MapleKiosk.Web.Shop.PhoneLines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
// Stripe also defines a CheckoutService; alias ours so the unqualified name is unambiguous.
using CheckoutService = MapleKiosk.Web.Shop.Checkout.CheckoutService;

namespace MapleKiosk.Web.Tests;

internal static class NoLiveStorage
{
    // A local run reads the PRODUCTION storage account (Windows' STORAGE_CONNECTION_STRING): the tests must never
    // reach it. Without it every store here serves defaults or nothing.
    [ModuleInitializer]
    internal static void Init() => Environment.SetEnvironmentVariable("STORAGE_CONNECTION_STRING", null);
}

/// <summary>
/// The phone line /phone-line sells: the invariants that protect the platform's Twilio money. A number is bought
/// only for a paid order, only once, only for a salon the buyer owns, and only pointed at the address that salon
/// reported. The order store, directory, Twilio and Stripe are in-memory fakes.
/// </summary>
public sealed class PhoneLineServiceTests
{
    private const string Owner = "owner@salon.ca";
    private const string Tenant = "tenant-a";
    private const string Number = "+14165550101";
    private const string Root = "https://www.maplekiosk.ca/";

    private readonly FakeOrders _orders = new();
    private readonly FakeCatalog _catalog = new(PhoneLineProduct());
    private readonly FakeDirectory _directory = new();
    private readonly FakeTelephony _telephony = new();
    private readonly FakeStripe _stripe = new();
    private readonly PhoneLineService _service;
    private readonly PhoneLineFulfilment _fulfilment;

    public PhoneLineServiceTests()
    {
        _directory.Own(Owner, Tenant);
        _directory.Report(Tenant, "Lotus Nails", "https://lotus.example.com");

        _service = new PhoneLineService(_orders, _catalog, _directory, _directory, _telephony, _stripe, NullLogger<PhoneLineService>.Instance);
        var services = new ServiceCollection().AddSingleton(_service).BuildServiceProvider();
        _fulfilment = new PhoneLineFulfilment(services);
    }

    [Fact]
    public async Task Paying_buys_the_chosen_number_once_pointed_at_the_salons_own_address()
    {
        var orderRef = await PlaceAsync();
        Assert.Empty(_telephony.Bought); // nothing is bought before the payment

        await PayAsync(orderRef);

        var buy = Assert.Single(_telephony.Bought);
        Assert.Equal(Number, buy.Number);
        Assert.Equal(new VoiceWebhooks("https://lotus.example.com/voice/in", "https://lotus.example.com/voice/fallback",
            "https://lotus.example.com/voice/status"), buy.Hooks);
        Assert.Equal($"Lotus Nails · {orderRef}", buy.Name);

        var order = (await _orders.GetAsync(orderRef))!;
        Assert.Equal(PhoneLineStatus.Active, order.PhoneLineStatus);
        Assert.Equal("PN-" + Number, order.PhoneLineSid);

        // A replayed payment, the buyer's retry, staff's retry and a direct call all buy nothing more.
        await PayAsync(orderRef);
        await _service.RetryAsync(orderRef, Owner, isStaff: false, null);
        await _service.RetryAsync(orderRef, "staff@spc-technology.com", isStaff: true, "+14165550199");
        await _service.FulfilAsync(orderRef);
        Assert.Single(_telephony.Bought);
    }

    [Fact]
    public async Task Concurrent_payment_signals_buy_once()
    {
        var orderRef = await PlaceAsync();
        _orders.MarkPaid(orderRef);
        _telephony.Delay = TimeSpan.FromMilliseconds(50);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => _service.FulfilAsync(orderRef))));

        Assert.Single(_telephony.Bought);
        Assert.Equal(PhoneLineStatus.Active, (await _orders.GetAsync(orderRef))!.PhoneLineStatus);
    }

    [Fact]
    public async Task An_unpaid_order_never_buys_a_number()
    {
        var orderRef = await PlaceAsync();

        var outcome = await _service.FulfilAsync(orderRef);

        Assert.Empty(_telephony.Bought);
        Assert.Equal(PhoneLineStatus.None, outcome.Status);
    }

    [Fact]
    public async Task A_salon_the_email_does_not_own_cannot_be_bought_for()
    {
        _directory.Own("someone@else.ca", "tenant-b");
        _directory.Report("tenant-b", "Other Spa", "https://other.example.com");

        var checkout = await _service.StartCheckoutAsync(Owner, "tenant-b", Number, "en", Root);

        Assert.False(checkout.Success);
        Assert.Equal("pl.err.notyours", checkout.Error);
        Assert.Empty(_stripe.Orders);
        Assert.Empty(_orders.All);
    }

    [Fact]
    public async Task A_salon_that_never_reported_a_public_https_address_cannot_be_sold_a_line()
    {
        _directory.Own(Owner, "tenant-b");                              // never reported
        _directory.Own(Owner, "tenant-c");
        _directory.Report("tenant-c", "Plain Http", "http://lan-box:5000"); // Twilio can't dial it
        _directory.Own(Owner, "tenant-d");
        _directory.Report("tenant-d", "Test Install", "https://book.maplenails.example"); // reserved name

        var salons = await _service.ListSalonsAsync(Owner);
        Assert.False(salons.Single(s => s.TenantId == "tenant-b").CanTakeCalls);
        Assert.False(salons.Single(s => s.TenantId == "tenant-c").CanTakeCalls);
        Assert.False(salons.Single(s => s.TenantId == "tenant-d").CanTakeCalls);

        Assert.Equal("pl.err.unreachable", (await _service.StartCheckoutAsync(Owner, "tenant-b", Number, "en", Root)).Error);
        Assert.Equal("pl.err.unreachable", (await _service.StartCheckoutAsync(Owner, "tenant-c", Number, "en", Root)).Error);
        Assert.Equal("pl.err.unreachable", (await _service.StartCheckoutAsync(Owner, "tenant-d", Number, "en", Root)).Error);
        Assert.Empty(_stripe.Orders);
    }

    [Fact]
    public async Task A_salon_with_an_active_line_cannot_buy_a_second()
    {
        await PayAsync(await PlaceAsync());

        var second = await _service.StartCheckoutAsync(Owner, Tenant, "+14165550102", "en", Root);

        Assert.False(second.Success);
        Assert.Equal("pl.err.hasline", second.Error);
        Assert.Equal(PhoneLineService.Pretty(Number), PhoneLineService.Pretty((await _service.ListSalonsAsync(Owner)).Single().ActiveNumber));
    }

    [Fact]
    public async Task Two_paid_orders_for_one_salon_buy_one_line()
    {
        // Both checkouts were opened before either was paid.
        var first = await PlaceAsync();
        var second = (await _service.StartCheckoutAsync(Owner, Tenant, "+14165550102", "en", Root)).OrderRef!;

        await PayAsync(first);
        await PayAsync(second);

        Assert.Single(_telephony.Bought);
        Assert.Equal(PhoneLineStatus.Failed, (await _orders.GetAsync(second))!.PhoneLineStatus);
    }

    [Fact]
    public async Task A_phone_line_with_a_free_trial_is_never_sold()
    {
        // A trial completes checkout with no payment: that would buy a number for nothing.
        _catalog.Product.TrialDays = 14;

        Assert.Null(await _service.GetOfferAsync());
        var checkout = await _service.StartCheckoutAsync(Owner, Tenant, Number, "en", Root);

        Assert.Equal("pl.err.offsale", checkout.Error);
        Assert.Empty(_stripe.Orders);
    }

    [Fact]
    public async Task The_order_opens_a_monthly_subscription_with_no_trial_for_the_signed_in_email()
    {
        var checkout = await _service.StartCheckoutAsync(" Owner@Salon.CA ", Tenant, Number, "vi", Root);

        var order = Assert.Single(_stripe.Orders);
        Assert.Equal(checkout.OrderRef, order.OrderRef);
        Assert.Equal(BillingIntervals.Monthly, order.Interval);
        Assert.Equal(0, order.TrialDays);
        Assert.Equal(Owner, order.CustomerEmail);
        Assert.Equal(Tenant, order.PhoneLineTenantId);
        Assert.Equal(Number, order.PhoneLineNumber);
        Assert.Equal(PhoneLineService.Source, order.Source);
        Assert.Equal($"{Root}vi/phone-line/order", _stripe.SuccessUrls.Single());
    }

    [Fact]
    public async Task A_number_taken_meanwhile_is_replaced_on_the_same_order_by_its_owner_only()
    {
        var orderRef = await PlaceAsync();
        _telephony.Taken.Add(Number);

        await PayAsync(orderRef);
        Assert.Equal(PhoneLineStatus.NumberTaken, (await _orders.GetAsync(orderRef))!.PhoneLineStatus);

        // Someone else's retry is refused, and buys nothing.
        var stranger = await _service.RetryAsync(orderRef, "stranger@example.com", isStaff: false, "+14165550103");
        Assert.Equal("pl.err.notfound", stranger.Error);
        Assert.Empty(_telephony.Bought);

        var mine = await _service.RetryAsync(orderRef, Owner, isStaff: false, "+14165550103");

        Assert.Equal(PhoneLineStatus.Active, mine.Status);
        Assert.Equal("+14165550103", Assert.Single(_telephony.Bought).Number);
        Assert.Single(_stripe.Orders); // no second charge
    }

    [Fact]
    public async Task A_salon_that_stops_reporting_waits_instead_of_buying()
    {
        var orderRef = await PlaceAsync();
        _directory.Forget(Tenant);

        await PayAsync(orderRef);
        Assert.Equal(PhoneLineStatus.WaitingForSalon, (await _orders.GetAsync(orderRef))!.PhoneLineStatus);
        Assert.Empty(_telephony.Bought);

        _directory.Report(Tenant, "Lotus Nails", "https://lotus.example.com");
        var retry = await _service.RetryAsync(orderRef, Owner, isStaff: false, null);

        Assert.Equal(PhoneLineStatus.Active, retry.Status);
        Assert.Single(_telephony.Bought);
    }

    [Fact]
    public async Task Other_orders_paying_never_buy_a_number()
    {
        var other = new AppOrder { Source = null, CustomerEmail = Owner, Lines = [new AppOrderLine { Sku = "soft-salon" }] };
        await _orders.CreateAsync(other);
        _orders.MarkPaid(other.OrderRef);

        await _fulfilment.OnOrderPaidAsync(Note(other.OrderRef, source: null));
        var direct = await _service.FulfilAsync(other.OrderRef);

        Assert.Equal(PhoneLineStatus.None, direct.Status);
        Assert.Empty(_telephony.Bought);
    }

    [Fact]
    public async Task The_shop_cart_refuses_the_phone_line()
    {
        var config = new AppStoreConfig(NullLogger<AppStoreConfig>.Instance);
        var cart = new CheckoutService(_catalog,
            new AppOrderService([], NullLogger<AppOrderService>.Instance),
            new StripeCheckoutCreator(config, new StripeProductSync(config, NullLogger<StripeProductSync>.Instance), NullLogger<StripeCheckoutCreator>.Instance),
            new VietQrCreator(config, new QrImageService(), NullLogger<VietQrCreator>.Instance),
            config);

        var result = await cart.CreateAsync(new CreateCheckoutRequest
        {
            Items = [new CheckoutItem { Sku = PhoneLineService.Sku }], Email = Owner,
            SuccessUrl = Root + "shop/success", CancelUrl = Root + "shop"
        });

        Assert.False(result.Success);
        Assert.Contains("phone-line page", result.Error);
    }

    [Fact]
    public void The_default_catalog_seeds_the_phone_line_off_sale_and_out_of_the_shop()
    {
        var item = CatalogDefaults.Products.Single(p => p.Sku == PhoneLineService.Sku);
        Assert.True(item.IsPhoneLine);
        Assert.False(item.Active);
        Assert.Equal(0, item.TrialDays);
        Assert.Equal(BillingIntervals.Monthly, item.BillingInterval);
    }

    // --- Phase 2: number release when billing ends ---

    [Fact]
    public async Task A_cancelled_line_keeps_its_number_through_the_paid_time_and_grace_then_releases_it_once()
    {
        var orderRef = await BuyLineAsync();
        var now = DateTimeOffset.UtcNow;
        var paidUntil = now.AddDays(10);

        _orders.Mirror(orderRef, "canceled", paidUntil);
        await _service.OnSubscriptionChangedAsync(orderRef, now);
        var releaseAt = (await _orders.GetAsync(orderRef))!.PhoneLineReleaseAt;
        Assert.Equal(paidUntil.AddDays(PhoneLineService.DefaultReleaseGraceDays), releaseAt);

        Assert.Equal(0, await _service.ReleaseDueAsync(releaseAt!.Value.AddMinutes(-1)));
        Assert.Empty(_telephony.Released);

        _telephony.Delay = TimeSpan.FromMilliseconds(20);
        var sweeps = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => _service.ReleaseDueAsync(releaseAt.Value.AddMinutes(1))));
        Assert.Equal(1, sweeps.Sum());
        Assert.Equal(["PN-" + Number], _telephony.Released);
        Assert.Equal(PhoneLineStatus.Released, (await _orders.GetAsync(orderRef))!.PhoneLineStatus);
    }

    [Fact]
    public async Task Billing_that_resumes_before_the_release_keeps_the_number()
    {
        var orderRef = await BuyLineAsync();
        var now = DateTimeOffset.UtcNow;

        _orders.Mirror(orderRef, "unpaid", now);
        await _service.OnSubscriptionChangedAsync(orderRef, now);
        Assert.NotNull((await _orders.GetAsync(orderRef))!.PhoneLineReleaseAt);

        _orders.Mirror(orderRef, "active", now.AddDays(30));
        await _service.OnSubscriptionChangedAsync(orderRef, now);
        Assert.Null((await _orders.GetAsync(orderRef))!.PhoneLineReleaseAt);

        Assert.Equal(0, await _service.ReleaseDueAsync(now.AddYears(1)));
        Assert.Empty(_telephony.Released);
    }

    [Fact]
    public async Task A_past_due_card_is_not_an_ended_subscription()
    {
        var orderRef = await BuyLineAsync();
        _orders.Mirror(orderRef, "past_due", DateTimeOffset.UtcNow);
        await _service.OnSubscriptionChangedAsync(orderRef, DateTimeOffset.UtcNow);
        Assert.Null((await _orders.GetAsync(orderRef))!.PhoneLineReleaseAt);
    }

    [Fact]
    public async Task A_release_Twilio_refuses_stays_active_and_is_tried_again()
    {
        var orderRef = await BuyLineAsync();
        var now = DateTimeOffset.UtcNow;
        _orders.Mirror(orderRef, "canceled", now);
        await _service.OnSubscriptionChangedAsync(orderRef, now);

        _telephony.ReleaseError = "Twilio is down";
        Assert.Equal(0, await _service.ReleaseDueAsync(now.AddYears(1)));
        var order = (await _orders.GetAsync(orderRef))!;
        Assert.Equal(PhoneLineStatus.Active, order.PhoneLineStatus);
        Assert.Contains("Twilio is down", order.PhoneLineError);

        _telephony.ReleaseError = null;
        Assert.Equal(1, await _service.ReleaseDueAsync(now.AddYears(1)));
    }

    [Fact]
    public async Task A_released_salon_can_buy_a_new_line()
    {
        var orderRef = await BuyLineAsync();
        var now = DateTimeOffset.UtcNow;
        _orders.Mirror(orderRef, "canceled", now);
        await _service.OnSubscriptionChangedAsync(orderRef, now);
        await _service.ReleaseDueAsync(now.AddYears(1));

        var salon = Assert.Single(await _service.ListSalonsAsync(Owner));
        Assert.Null(salon.ActiveNumber);
        await PayAsync(await PlaceAsync());
        Assert.Equal(2, _telephony.Bought.Count);
    }

    [Fact]
    public async Task An_order_whose_subscription_ended_never_buys_a_number()
    {
        var orderRef = await PlaceAsync();
        _directory.Forget(Tenant);
        await PayAsync(orderRef); // the salon stopped reporting: WaitingForSalon
        _orders.Mirror(orderRef, "canceled", DateTimeOffset.UtcNow);

        _directory.Report(Tenant, "Lotus Nails", "https://lotus.example.com");
        var outcome = await _service.RetryAsync(orderRef, Owner, isStaff: false, null);
        Assert.Equal("pl.err.ended", outcome.Error);
        Assert.Empty(_telephony.Bought);
    }

    // --- Phase 2: monthly usage cap ---

    [Fact]
    public async Task A_line_past_its_monthly_cap_has_calls_declined_until_the_month_turns()
    {
        var orderRef = await BuyLineAsync();
        var october = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        using var cap = Cap(500);

        _telephony.Minutes = 499;
        Assert.Equal(0, await _service.EnforceCapsAsync(october));
        Assert.Empty(_telephony.Pointed);

        _telephony.Minutes = 500;
        Assert.Equal(1, await _service.EnforceCapsAsync(october));
        Assert.Equal(PhoneLineService.DeclinedUrl, Assert.Single(_telephony.Pointed).Hooks.Incoming);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), _telephony.MeteredSince);
        Assert.Equal(0, await _service.EnforceCapsAsync(october.AddHours(1))); // stays capped, no re-pointing

        _telephony.Minutes = 0;
        Assert.Equal(1, await _service.EnforceCapsAsync(october.AddDays(12))); // November
        Assert.Equal("https://lotus.example.com/voice/in", _telephony.Pointed[^1].Hooks.Incoming);
        Assert.Equal("", (await _orders.GetAsync(orderRef))!.PhoneLineCapState ?? "");
    }

    [Fact]
    public async Task Staff_can_lift_the_cap_for_the_rest_of_the_month()
    {
        var orderRef = await BuyLineAsync();
        var october = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        using var cap = Cap(500);
        _telephony.Minutes = 900;
        await _service.EnforceCapsAsync(october);

        Assert.Null(await _service.LiftCapAsync(orderRef, october));
        Assert.Equal("https://lotus.example.com/voice/in", _telephony.Pointed[^1].Hooks.Incoming);
        Assert.Equal(0, await _service.EnforceCapsAsync(october.AddDays(1)));

        Assert.Equal(1, await _service.EnforceCapsAsync(october.AddDays(12))); // November counts again
        Assert.Equal(PhoneLineService.DeclinedUrl, _telephony.Pointed[^1].Hooks.Incoming);
    }

    [Fact]
    public async Task Without_a_cap_no_line_is_metered()
    {
        await BuyLineAsync();
        _telephony.Minutes = 1_000_000;
        Assert.Equal(0, await _service.EnforceCapsAsync(DateTimeOffset.UtcNow));
        Assert.Equal(0, _telephony.MeterReads);
    }

    [Fact]
    public void A_call_log_page_bills_each_call_rounded_up_to_the_minute()
    {
        var (minutes, next) = TwilioGateway.ParseCallMinutes("""
            {"calls":[{"duration":"61"},{"duration":"60"},{"duration":"0"},{"duration":null}],
             "next_page_uri":"/2010-04-01/Accounts/AC1/Calls.json?Page=1"}
            """);
        Assert.Equal(3, minutes);
        Assert.Equal("/2010-04-01/Accounts/AC1/Calls.json?Page=1", next);
        Assert.Equal((0, (string?)null), TwilioGateway.ParseCallMinutes("""{"calls":[],"next_page_uri":null}"""));
    }

    // --- Helpers ---

    private async Task<string> BuyLineAsync()
    {
        var orderRef = await PlaceAsync();
        await PayAsync(orderRef);
        Assert.Equal(PhoneLineStatus.Active, (await _orders.GetAsync(orderRef))!.PhoneLineStatus);
        return orderRef;
    }

    // The cap is read like the live site reads it (env first); the tests in this class run one at a time.
    private static IDisposable Cap(int minutes)
    {
        Environment.SetEnvironmentVariable("PHONE_LINE_MONTHLY_MINUTE_CAP", minutes.ToString());
        return new Undo(() => Environment.SetEnvironmentVariable("PHONE_LINE_MONTHLY_MINUTE_CAP", null));
    }

    private sealed class Undo(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    private static AppProduct PhoneLineProduct() => new()
    {
        Sku = PhoneLineService.Sku, Category = CatalogDefaults.PhoneLineCategory, Name = "Salon phone line", Price = 29m,
        BillingInterval = BillingIntervals.Monthly, Active = true
    };

    private async Task<string> PlaceAsync()
    {
        var checkout = await _service.StartCheckoutAsync(Owner, Tenant, Number, "en", Root);
        Assert.True(checkout.Success, checkout.Error);
        return checkout.OrderRef!;
    }

    // What Stripe's webhook does: MarkPaidAsync flips the order (once) and fires the sinks.
    private async Task PayAsync(string orderRef)
    {
        _orders.MarkPaid(orderRef);
        await _fulfilment.OnOrderPaidAsync(Note(orderRef, PhoneLineService.Source));
    }

    private static OrderPaidNotification Note(string orderRef, string? source) =>
        new(orderRef, 29m, "CAD", AppPaymentMethods.Stripe, Owner, "pi_1", DateTimeOffset.UtcNow, Source: source);

    // --- Fakes ---

    private sealed class FakeOrders : IPhoneLineOrders
    {
        private readonly Dictionary<string, AppOrder> _rows = new();
        private readonly object _gate = new();

        public IReadOnlyCollection<AppOrder> All { get { lock (_gate) return _rows.Values.ToList(); } }

        public void MarkPaid(string orderRef) { lock (_gate) _rows[orderRef].Status = AppOrderStatus.Paid; }

        // What UpdateSubscriptionAsync writes when Stripe reports a subscription change.
        public void Mirror(string orderRef, string status, DateTimeOffset periodEnd)
        {
            lock (_gate)
            {
                _rows[orderRef].SubscriptionStatus = status;
                _rows[orderRef].CurrentPeriodEnd = periodEnd;
            }
        }

        public Task<AppOrder> CreateAsync(AppOrder order, CancellationToken ct = default)
        {
            lock (_gate) _rows[order.OrderRef] = Copy(order);
            return Task.FromResult(order);
        }

        public Task<AppOrder?> GetAsync(string orderRef, CancellationToken ct = default)
        {
            lock (_gate) return Task.FromResult(_rows.TryGetValue(orderRef, out var o) ? Copy(o) : null);
        }

        // Same rule as AppOrderService's ETag-conditional claim: paid, a phone line, claimable — and one winner.
        public Task<bool> ClaimPhoneLineAsync(string orderRef, string? newNumber, CancellationToken ct = default)
        {
            lock (_gate)
            {
                if (!_rows.TryGetValue(orderRef, out var o) || o.Status != AppOrderStatus.Paid
                    || string.IsNullOrEmpty(o.PhoneLineTenantId) || !PhoneLineStatus.IsClaimable(o.PhoneLineStatus))
                    return Task.FromResult(false);
                o.PhoneLineStatus = PhoneLineStatus.Buying;
                if (!string.IsNullOrEmpty(newNumber)) o.PhoneLineNumber = newNumber;
                return Task.FromResult(true);
            }
        }

        public Task FinishPhoneLineAsync(string orderRef, string status, string? number, string? sid, string? error, CancellationToken ct = default,
            string? accountSid = null)
        {
            lock (_gate)
            {
                var o = _rows[orderRef];
                o.PhoneLineStatus = status;
                o.PhoneLineError = error;
                if (!string.IsNullOrEmpty(number)) o.PhoneLineNumber = number;
                if (!string.IsNullOrEmpty(sid)) o.PhoneLineSid = sid;
            }
            return Task.CompletedTask;
        }

        public Task ScheduleReleaseAsync(string orderRef, DateTimeOffset? releaseAt, CancellationToken ct = default)
        {
            lock (_gate) _rows[orderRef].PhoneLineReleaseAt = releaseAt;
            return Task.CompletedTask;
        }

        public Task SetCapStateAsync(string orderRef, string state, CancellationToken ct = default)
        {
            lock (_gate) _rows[orderRef].PhoneLineCapState = state;
            return Task.CompletedTask;
        }

        // Same rule as AppOrderService's ETag-conditional release claim: Active, and one winner.
        public Task<bool> ClaimReleaseAsync(string orderRef, CancellationToken ct = default)
        {
            lock (_gate)
            {
                if (!_rows.TryGetValue(orderRef, out var o) || o.PhoneLineStatus != PhoneLineStatus.Active) return Task.FromResult(false);
                o.PhoneLineStatus = PhoneLineStatus.Releasing;
                return Task.FromResult(true);
            }
        }

        public Task<IReadOnlyList<AppOrder>> ListPhoneLinesAsync(string? tenantId, CancellationToken ct = default)
        {
            lock (_gate)
                return Task.FromResult<IReadOnlyList<AppOrder>>(_rows.Values
                    .Where(o => o.Source == PhoneLineService.Source && (tenantId is null || o.PhoneLineTenantId == tenantId))
                    .Select(Copy).ToList());
        }

        // Through the table row, so the new columns round-trip as they do in storage.
        private static AppOrder Copy(AppOrder o) => AppOrderEntity.FromOrder(o).ToOrder();
    }

    private sealed class FakeCatalog(AppProduct product) : IAppCatalog
    {
        public AppProduct Product { get; } = product;
        public Task<IReadOnlyList<AppProduct>> GetActiveAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AppProduct>>([Product]);
        public Task<AppProduct?> FindAsync(string sku, CancellationToken ct = default) =>
            Task.FromResult(Product.Active && Product.Sku == sku ? Product : null);
    }

    [Fact]
    public async Task A_customer_business_not_yet_linked_to_an_app_is_listed_but_cannot_be_sold_a_line()
    {
        _directory.Own(Owner, null, "New Salon");

        var salons = await _service.ListSalonsAsync(Owner);
        var unlinked = salons.Single(s => s.Name == "New Salon");
        Assert.Null(unlinked.TenantId);
        Assert.False(unlinked.CanTakeCalls);
        Assert.Equal("pl.err.notyours", (await _service.StartCheckoutAsync(Owner, "", Number, "en", Root)).Error);
        Assert.Empty(_stripe.Orders);
    }

    // The site's customers (onboarding records) and the salons' reported endpoints, in one fake.
    private sealed class FakeDirectory : ITenantDirectory, IPhoneLineCustomers
    {
        private readonly List<(string Email, string? TenantId, string Name)> _owners = new();
        private readonly Dictionary<string, SalonPhoneEndpoint> _endpoints = new();

        public void Own(string email, string? tenantId, string? name = null) => _owners.Add((email, tenantId, name ?? tenantId ?? "Salon"));
        public void Forget(string tenantId) => _endpoints.Remove(tenantId);
        public void Report(string tenantId, string name, string root) =>
            _endpoints[tenantId] = new SalonPhoneEndpoint(tenantId, name, root, root + "/voice/in", root + "/voice/fallback",
                root + "/voice/status", DateTimeOffset.UtcNow);

        public Task<IReadOnlyList<CustomerBusiness>> ListAsync(string email, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CustomerBusiness>>(_owners.Where(o => o.Email == email)
                .Select((o, i) => new CustomerBusiness($"rec-{i}", o.Name, null, o.TenantId)).ToList());

        public Task<SalonPhoneEndpoint?> GetPhoneEndpointAsync(string tenantId, CancellationToken ct = default) =>
            Task.FromResult(_endpoints.GetValueOrDefault(tenantId));
    }

    private sealed class FakeTelephony : ITelephonyGateway
    {
        private readonly List<(string Number, VoiceWebhooks Hooks, string Name)> _bought = new();
        public HashSet<string> Taken { get; } = new();
        public TimeSpan Delay { get; set; }
        public IReadOnlyList<(string Number, VoiceWebhooks Hooks, string Name)> Bought { get { lock (_bought) return _bought.ToList(); } }

        public Task<bool> IsConfiguredAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<NumberSearchResult> SearchAsync(string country, string? areaCode, CancellationToken ct = default) =>
            Task.FromResult(new NumberSearchResult([new AvailableNumber(Number, "Toronto", "ON")]));

        public async Task<NumberPurchase> BuyAsync(string? accountSid, string phoneNumber, VoiceWebhooks webhooks, string friendlyName,
            CancellationToken ct = default)
        {
            await Task.Delay(Delay, ct);
            if (Taken.Contains(phoneNumber)) return NumberPurchase.Taken("21422");
            lock (_bought) _bought.Add((phoneNumber, webhooks, friendlyName));
            return NumberPurchase.Bought("PN-" + phoneNumber, phoneNumber);
        }

        public List<string> Released { get; } = new();
        public string? ReleaseError { get; set; }
        public List<(string Sid, VoiceWebhooks Hooks)> Pointed { get; } = new();
        public int Minutes { get; set; }
        public int MeterReads { get; private set; }
        public DateTimeOffset? MeteredSince { get; private set; }

        public async Task<string?> ReleaseAsync(string? accountSid, string numberSid, CancellationToken ct = default)
        {
            await Task.Delay(Delay, ct);
            if (ReleaseError is not null) return ReleaseError;
            lock (Released) Released.Add(numberSid);
            return null;
        }

        public Task<string?> PointAsync(string? accountSid, string numberSid, VoiceWebhooks webhooks, CancellationToken ct = default)
        {
            Pointed.Add((numberSid, webhooks));
            return Task.FromResult<string?>(null);
        }

        public Task<(int? Minutes, string? Error)> MinutesUsedAsync(string? accountSid, string number, DateTimeOffset since,
            CancellationToken ct = default)
        {
            MeterReads++;
            MeteredSince = since;
            return Task.FromResult<(int?, string?)>((Minutes, null));
        }
    }

    private sealed class FakeStripe : IStripeCheckout
    {
        public List<AppOrder> Orders { get; } = new();
        public List<string> SuccessUrls { get; } = new();

        public Task<StripeCheckoutCreator.Result> CreateAsync(AppOrder order, string successUrl, string cancelUrl, CancellationToken ct = default,
            string? locale = null)
        {
            Orders.Add(order);
            SuccessUrls.Add(successUrl);
            return Task.FromResult(new StripeCheckoutCreator.Result(true, "https://checkout.stripe.com/c/" + order.OrderRef, "cs_" + order.OrderRef, null));
        }
    }
}
