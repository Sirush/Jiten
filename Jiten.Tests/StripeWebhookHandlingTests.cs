using FluentAssertions;
using Jiten.Api.Services;
using Jiten.Api.Services.Stripe;
using Jiten.Core;
using Jiten.Core.Data.Authentication;
using Jiten.Core.Data.Billing;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Jiten.Tests;

public class StripeWebhookHandlingTests : IDisposable
{
    private const string UserId = "user-1";
    private const string CustomerId = "cus_1";

    private readonly SqliteConnection _connection;
    private readonly UserDbContext _context;
    private readonly StubStripeGateway _gateway = new();
    private readonly RecordingEmailService _emails = new();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly JitenPlusService _jitenPlus;
    private readonly StripeService _service;

    public StripeWebhookHandlingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<UserDbContext>().UseSqlite(_connection).Options;
        _context = new UserDbContext(options);
        _context.Database.EnsureCreated();

        _context.Users.Add(new User
        {
            Id = UserId,
            UserName = "tester",
            Email = "tester@example.com",
            StripeCustomerId = CustomerId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        _context.SaveChanges();

        _jitenPlus = new JitenPlusService(_context, _cache);

        var stripeOptions = Options.Create(new StripeOptions
        {
            MonthlyPriceId = "price_monthly",
            YearlyPriceId = "price_yearly",
            LifetimePriceId = "price_lifetime",
            LifetimeWindowEnd = new DateTime(2999, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });

        _service = new StripeService(_gateway, _context, _jitenPlus, _emails, _cache, stripeOptions,
                                     NullLogger<StripeService>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private User Reload() => _context.Users.AsNoTracking().First(u => u.Id == UserId);

    private int EmailCount(string method) => _emails.Sent.Count(e => e.Method == method);

    private static StripeSubscriptionSnapshot Snapshot(string status, string priceId, DateTime? periodEnd, bool cancelAtEnd = false, DateTime? endedAt = null) =>
        new("sub_1", CustomerId, status, priceId, periodEnd, cancelAtEnd, endedAt);

    private static StripeWebhookEvent DeletedEvent(DateTime? storedIrrelevant = null, DateTime? endedAt = null, string eventId = "evt_del") =>
        new(StripeWebhookKind.SubscriptionDeleted, "customer.subscription.deleted", eventId, CustomerId, "sub_1",
            null, UserId, Snapshot("canceled", "price_yearly", null, endedAt: endedAt));

    private static StripeWebhookEvent CheckoutSubscription(string eventId = "evt_1") =>
        new(StripeWebhookKind.CheckoutCompleted, "checkout.session.completed", eventId, CustomerId, "sub_1",
            StripeCheckoutMode.Subscription, UserId, null);

    private static StripeWebhookEvent CheckoutLifetime(string eventId = "evt_life") =>
        new(StripeWebhookKind.CheckoutCompleted, "checkout.session.completed", eventId, CustomerId, null,
            StripeCheckoutMode.Payment, UserId, null);

    // ---- checkout.session.completed (subscription) --------------------------------------------

    [Fact]
    public async Task SubscriptionCheckout_ActivatesAndSetsPlan()
    {
        var periodEnd = DateTime.UtcNow.AddDays(365);
        _gateway.Subscriptions["sub_1"] = Snapshot("active", "price_yearly", periodEnd);

        await _service.HandleWebhookAsync(CheckoutSubscription());

        var user = Reload();
        user.StripeSubscriptionActive.Should().BeTrue();
        user.StripeSubscriptionId.Should().Be("sub_1");
        user.SubscriptionPlan.Should().Be(SubscriptionPlan.Yearly);
        user.SubscriptionPeriodEnd.Should().BeCloseTo(periodEnd, TimeSpan.FromSeconds(1));
        EmailCount(nameof(IEmailService.SendSubscriptionConfirmedAsync)).Should().Be(1);
    }

    [Fact]
    public async Task SubscriptionCheckout_ReplayWithNewEventId_DoesNotResendEmail()
    {
        _gateway.Subscriptions["sub_1"] = Snapshot("active", "price_yearly", DateTime.UtcNow.AddDays(365));

        await _service.HandleWebhookAsync(CheckoutSubscription("evt_a"));
        await _service.HandleWebhookAsync(CheckoutSubscription("evt_b"));

        EmailCount(nameof(IEmailService.SendSubscriptionConfirmedAsync)).Should().Be(1);
    }

    // ---- checkout.session.completed (lifetime) ------------------------------------------------

    [Fact]
    public async Task LifetimeCheckout_SetsLifetimeAndEmails()
    {
        await _service.HandleWebhookAsync(CheckoutLifetime());

        var user = Reload();
        user.IsLifetime.Should().BeTrue();
        user.LifetimeSource.Should().Be(LifetimeSource.WindowPurchase);
        EmailCount(nameof(IEmailService.SendLifetimeConfirmedAsync)).Should().Be(1);
    }

    [Fact]
    public async Task LifetimeCheckout_WithActiveSubscription_CancelsImmediately()
    {
        var user = _context.Users.First(u => u.Id == UserId);
        user.StripeSubscriptionActive = true;
        user.StripeSubscriptionId = "sub_1";
        await _context.SaveChangesAsync();

        await _service.HandleWebhookAsync(CheckoutLifetime());

        _gateway.ImmediatelyCanceledSubscriptions.Should().ContainSingle().Which.Should().Be("sub_1");
    }

    [Fact]
    public async Task LifetimeCheckout_ReplayWithNewEventId_IsIdempotent_NoSecondCancel()
    {
        var user = _context.Users.First(u => u.Id == UserId);
        user.StripeSubscriptionActive = true;
        user.StripeSubscriptionId = "sub_1";
        await _context.SaveChangesAsync();

        await _service.HandleWebhookAsync(CheckoutLifetime("evt_l1"));
        await _service.HandleWebhookAsync(CheckoutLifetime("evt_l2"));

        EmailCount(nameof(IEmailService.SendLifetimeConfirmedAsync)).Should().Be(1);
        _gateway.ImmediatelyCanceledSubscriptions.Should().ContainSingle(); // only the first call cancels
    }

    // ---- customer.subscription.updated --------------------------------------------------------

    [Fact]
    public async Task SubscriptionUpdated_SyncsFields_NoEmail()
    {
        var periodEnd = DateTime.UtcNow.AddDays(300);
        var evt = new StripeWebhookEvent(StripeWebhookKind.SubscriptionUpdated, "customer.subscription.updated",
            "evt_u", CustomerId, "sub_1", null, UserId, Snapshot("active", "price_monthly", periodEnd));

        await _service.HandleWebhookAsync(evt);

        var user = Reload();
        user.StripeSubscriptionActive.Should().BeTrue();
        user.SubscriptionPlan.Should().Be(SubscriptionPlan.Monthly);
        user.SubscriptionPeriodEnd.Should().BeCloseTo(periodEnd, TimeSpan.FromSeconds(1));
        _emails.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task SubscriptionUpdated_PastDue_MarksInactive()
    {
        var user = _context.Users.First(u => u.Id == UserId);
        user.StripeSubscriptionActive = true;
        await _context.SaveChangesAsync();

        var evt = new StripeWebhookEvent(StripeWebhookKind.SubscriptionUpdated, "customer.subscription.updated",
            "evt_u2", CustomerId, "sub_1", null, UserId, Snapshot("past_due", "price_monthly", DateTime.UtcNow.AddDays(1)));

        await _service.HandleWebhookAsync(evt);

        Reload().StripeSubscriptionActive.Should().BeFalse();
    }

    // ---- customer.subscription.deleted --------------------------------------------------------

    [Fact]
    public async Task SubscriptionDeleted_WhenActive_DeactivatesAndEmails()
    {
        var user = _context.Users.First(u => u.Id == UserId);
        user.StripeSubscriptionActive = true;
        user.StripeSubscriptionId = "sub_1";
        await _context.SaveChangesAsync();

        var evt = new StripeWebhookEvent(StripeWebhookKind.SubscriptionDeleted, "customer.subscription.deleted",
            "evt_d", CustomerId, "sub_1", null, UserId, Snapshot("canceled", "price_monthly", null));

        await _service.HandleWebhookAsync(evt);

        Reload().StripeSubscriptionActive.Should().BeFalse();
        EmailCount(nameof(IEmailService.SendSubscriptionEndedAsync)).Should().Be(1);
    }

    [Fact]
    public async Task SubscriptionDeleted_ImmediateCancel_ClampsPeriodEndToEndedAt()
    {
        var user = _context.Users.First(u => u.Id == UserId);
        user.StripeSubscriptionActive = true;
        user.StripeSubscriptionId = "sub_1";
        user.SubscriptionPeriodEnd = DateTime.UtcNow.AddDays(300); // original period end, ~10 months out
        await _context.SaveChangesAsync();

        var endedAt = DateTime.UtcNow;
        await _service.HandleWebhookAsync(DeletedEvent(endedAt: endedAt));

        // Period end pulled back to the real end → grace runs +3d from now, not from 10 months out.
        var reloaded = Reload();
        reloaded.SubscriptionPeriodEnd.Should().BeCloseTo(endedAt, TimeSpan.FromSeconds(2));

        // Tier drops as soon as the 3-day grace from the real end elapses (verified via resolver).
        (await _jitenPlus.GetTierAsync(UserId)).Should().Be(JitenPlusTier.Full); // still within 3d grace
    }

    [Fact]
    public async Task SubscriptionDeleted_NormalEndOfPeriod_LeavesPeriodEndUnchanged()
    {
        var periodEnd = DateTime.UtcNow.AddDays(2); // reached the period end; ended_at == period end
        var user = _context.Users.First(u => u.Id == UserId);
        user.StripeSubscriptionActive = true;
        user.StripeSubscriptionId = "sub_1";
        user.SubscriptionPeriodEnd = periodEnd;
        await _context.SaveChangesAsync();

        await _service.HandleWebhookAsync(DeletedEvent(endedAt: periodEnd));

        Reload().SubscriptionPeriodEnd.Should().BeCloseTo(periodEnd, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task SubscriptionDeleted_NoEndedAt_FallsBackToNow()
    {
        var user = _context.Users.First(u => u.Id == UserId);
        user.StripeSubscriptionActive = true;
        user.StripeSubscriptionId = "sub_1";
        user.SubscriptionPeriodEnd = DateTime.UtcNow.AddDays(200);
        await _context.SaveChangesAsync();

        await _service.HandleWebhookAsync(DeletedEvent(endedAt: null));

        Reload().SubscriptionPeriodEnd.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SubscriptionDeleted_ReplayWithNewEventId_IsIdempotent()
    {
        var user = _context.Users.First(u => u.Id == UserId);
        user.StripeSubscriptionActive = true;
        user.StripeSubscriptionId = "sub_1";
        user.SubscriptionPeriodEnd = DateTime.UtcNow.AddDays(100);
        await _context.SaveChangesAsync();

        var endedAt = DateTime.UtcNow;
        await _service.HandleWebhookAsync(DeletedEvent(endedAt: endedAt, eventId: "evt_del_1"));
        var afterFirst = Reload().SubscriptionPeriodEnd;

        await _service.HandleWebhookAsync(DeletedEvent(endedAt: endedAt.AddDays(5), eventId: "evt_del_2"));

        // Already inactive → no second ended email, and the clamp never moves period end back out.
        EmailCount(nameof(IEmailService.SendSubscriptionEndedAsync)).Should().Be(1);
        Reload().SubscriptionPeriodEnd.Should().BeCloseTo(afterFirst!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task SubscriptionDeleted_WhenAlreadyInactive_NoEmail()
    {
        var evt = new StripeWebhookEvent(StripeWebhookKind.SubscriptionDeleted, "customer.subscription.deleted",
            "evt_d2", CustomerId, "sub_1", null, UserId, Snapshot("canceled", "price_monthly", null));

        await _service.HandleWebhookAsync(evt);

        EmailCount(nameof(IEmailService.SendSubscriptionEndedAsync)).Should().Be(0);
    }

    // ---- invoice.payment_failed ---------------------------------------------------------------

    [Fact]
    public async Task PaymentFailed_EmailsButDoesNotChangeFlags()
    {
        var user = _context.Users.First(u => u.Id == UserId);
        user.StripeSubscriptionActive = true;
        user.SubscriptionPeriodEnd = DateTime.UtcNow.AddDays(10);
        await _context.SaveChangesAsync();

        var evt = new StripeWebhookEvent(StripeWebhookKind.PaymentFailed, "invoice.payment_failed",
            "evt_f", CustomerId, null, null, UserId, null);

        await _service.HandleWebhookAsync(evt);

        var reloaded = Reload();
        reloaded.StripeSubscriptionActive.Should().BeTrue();
        EmailCount(nameof(IEmailService.SendSubscriptionPaymentFailedAsync)).Should().Be(1);
    }

    // ---- tier cache invalidation --------------------------------------------------------------

    [Fact]
    public async Task HandledEvent_InvalidatesTierCache()
    {
        var user = _context.Users.First(u => u.Id == UserId);
        user.StripeSubscriptionActive = true;
        user.StripeSubscriptionId = "sub_1";
        // Period end already past (beyond the grace window) so only the active flag holds the tier at Full.
        user.SubscriptionPeriodEnd = DateTime.UtcNow.AddDays(-10);
        await _context.SaveChangesAsync();

        // Warm the tier cache to Full.
        (await _jitenPlus.GetTierAsync(UserId)).Should().Be(JitenPlusTier.Full);

        var evt = new StripeWebhookEvent(StripeWebhookKind.SubscriptionDeleted, "customer.subscription.deleted",
            "evt_inv", CustomerId, "sub_1", null, UserId, Snapshot("canceled", "price_monthly", DateTime.UtcNow.AddDays(-10)));
        await _service.HandleWebhookAsync(evt);

        // If the cache were still warm this would read Full; invalidation forces a re-resolve to None.
        (await _jitenPlus.GetTierAsync(UserId)).Should().Be(JitenPlusTier.None);
    }

    // ---- replay dedupe (same event id) --------------------------------------------------------

    [Fact]
    public async Task SameEventId_ProcessedOnce()
    {
        _gateway.Subscriptions["sub_1"] = Snapshot("active", "price_yearly", DateTime.UtcNow.AddDays(365));

        await _service.HandleWebhookAsync(CheckoutSubscription("evt_same"));
        await _service.HandleWebhookAsync(CheckoutSubscription("evt_same"));

        EmailCount(nameof(IEmailService.SendSubscriptionConfirmedAsync)).Should().Be(1);
    }
}
