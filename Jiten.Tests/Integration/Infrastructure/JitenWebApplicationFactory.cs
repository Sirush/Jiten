using Hangfire;
using Jiten.Core;
using Jiten.Core.Data.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;
using ApiProgram = Program;

namespace Jiten.Parser.Tests.Integration.Infrastructure;

public class JitenWebApplicationFactory : WebApplicationFactory<ApiProgram>, IAsyncLifetime
{
    private SqliteConnection _jitenConnection = null!;
    private SqliteConnection _userConnection = null!;

    /// <summary>The recording email stub registered for IEmailService/IEmailSender. Singleton, so reads are reliable.</summary>
    public RecordingEmailService Emails => Services.GetRequiredService<RecordingEmailService>();

    /// <summary>The stub Stripe gateway. Singleton, so tests can configure canned responses and read recorded calls.</summary>
    public StubStripeGateway Stripe => Services.GetRequiredService<StubStripeGateway>();

    /// <summary>The stub external media list client. Singleton, so tests can set canned lists.</summary>
    public StubExternalMediaListClient ExternalLists => Services.GetRequiredService<StubExternalMediaListClient>();

    public JitenWebApplicationFactory()
    {
        Environment.SetEnvironmentVariable("JwtSettings__Secret", "ThisIsATestSecretKeyThatIsLongEnoughForHS256!");
        Environment.SetEnvironmentVariable("JwtSettings__Issuer", "TestIssuer");
        Environment.SetEnvironmentVariable("JwtSettings__Audience", "TestAudience");
        Environment.SetEnvironmentVariable("JwtSettings__AccessTokenExpirationMinutes", "15");
        Environment.SetEnvironmentVariable("JwtSettings__RefreshTokenExpirationDays", "30");
        Environment.SetEnvironmentVariable("ConnectionStrings__JitenDatabase", "DataSource=:memory:");
        Environment.SetEnvironmentVariable("ConnectionStrings__Redis", "localhost:6379");
        Environment.SetEnvironmentVariable("StaticFilesPath", Path.GetTempPath());
        Environment.SetEnvironmentVariable("UseBunnyCdn", "false");

        // Stripe config
        Environment.SetEnvironmentVariable("Stripe__SecretKey", "sk_test_placeholder");
        Environment.SetEnvironmentVariable("Stripe__WebhookSecret", StubStripeGateway.WebhookSecret);
        Environment.SetEnvironmentVariable("Stripe__MonthlyPriceId", "price_monthly");
        Environment.SetEnvironmentVariable("Stripe__YearlyPriceId", "price_yearly");
        Environment.SetEnvironmentVariable("Stripe__LifetimePriceId", "price_lifetime");
        Environment.SetEnvironmentVariable("Stripe__LifetimeWindowEnd", "2999-01-01T00:00:00Z");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            // Remove Hangfire registrations and add mock
            var hangfireDescriptors = services
                .Where(d => d.ServiceType.FullName?.Contains("Hangfire") == true
                          || d.ImplementationType?.FullName?.Contains("Hangfire") == true)
                .ToList();
            foreach (var d in hangfireDescriptors)
                services.Remove(d);
            services.AddSingleton(Mock.Of<IBackgroundJobClient>());

            // Remove hosted services (ParserWarmupService etc.)
            var hostedServices = services
                .Where(d => d.ServiceType == typeof(IHostedService))
                .ToList();
            foreach (var d in hostedServices)
                services.Remove(d);

            // Replace Redis with in-memory mock that supports GetDatabase()
            services.RemoveAll<IConnectionMultiplexer>();
            services.AddSingleton(InMemoryRedis.Create());

            // Register SQLite in-memory DbContexts (Npgsql is skipped via
            // environment guard in Program.cs, so no dual-provider conflict).
            _jitenConnection = new SqliteConnection("DataSource=:memory:");
            _jitenConnection.Open();

            _userConnection = new SqliteConnection("DataSource=:memory:");
            _userConnection.Open();

            services.AddDbContext<JitenDbContext>(options =>
                options.UseSqlite(_jitenConnection));
            services.AddDbContextFactory<JitenDbContext>(options =>
                options.UseSqlite(_jitenConnection), ServiceLifetime.Scoped);

            services.AddDbContext<UserDbContext>(options =>
                options.UseSqlite(_userConnection));
            services.AddDbContextFactory<UserDbContext>(options =>
                options.UseSqlite(_userConnection), ServiceLifetime.Scoped);

            // Replace auth. The test handler runs under the default test scheme (header-based identity for
            // most tests) AND under the JWT bearer scheme name, so controllers that restrict to
            // JwtBearerDefaults.AuthenticationScheme ("Bearer") resolve to it. In bearer mode the handler
            // validates real tokens issued by /api/auth, enabling genuine refresh/revocation flows.
            // The production AddJwtBearer/ApiKey/Smart scheme registrations are configured via
            // IConfigureOptions<AuthenticationOptions>; clear them so re-registering "Bearer" doesn't collide.
            services.RemoveAll<IAuthenticationSchemeProvider>();
            services.RemoveAll<IConfigureOptions<AuthenticationOptions>>();
            services.RemoveAll<IPostConfigureOptions<AuthenticationOptions>>();
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                    Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme, _ => { })
                // SignInManager.PasswordSignInAsync (used by /api/auth/login) signs into the Identity cookie
                // scheme; clearing the auth-options configs above dropped it, so re-register it. The cookie
                // itself is unused (we authenticate with JWTs) but the scheme must exist for sign-in to work.
                .AddCookie(Microsoft.AspNetCore.Identity.IdentityConstants.ApplicationScheme)
                .AddCookie(Microsoft.AspNetCore.Identity.IdentityConstants.ExternalScheme)
                .AddCookie(Microsoft.AspNetCore.Identity.IdentityConstants.TwoFactorUserIdScheme);

            services.AddAuthorizationBuilder()
                .SetDefaultPolicy(new AuthorizationPolicyBuilder(TestAuthHandler.SchemeName)
                    .RequireAuthenticatedUser()
                    .Build())
                .AddPolicy("RequiresAdmin", policy =>
                    policy.AddAuthenticationSchemes(TestAuthHandler.SchemeName)
                          .RequireRole("Administrator"));

            // Replace debounce service with no-op for testing
            services.RemoveAll<Jiten.Api.Services.ISrsDebounceService>();
            services.AddSingleton<Jiten.Api.Services.ISrsDebounceService, NoOpSrsDebounceService>();

            // Replace study session service with in-memory for testing
            services.RemoveAll<Jiten.Api.Services.IStudySessionService>();
            services.AddSingleton<Jiten.Api.Services.IStudySessionService, InMemoryStudySessionService>();

            // Replace CDN
            services.RemoveAll<ICdnService>();
            services.AddSingleton<StubCdnService>();
            services.AddSingleton<ICdnService>(sp => sp.GetRequiredService<StubCdnService>());

            // Replace the external media list client so imports never hit AniList/VNDB.
            services.RemoveAll<Jiten.Api.Services.ExternalMediaList.IExternalMediaListClient>();
            services.AddSingleton<StubExternalMediaListClient>();
            services.AddSingleton<Jiten.Api.Services.ExternalMediaList.IExternalMediaListClient>(sp =>
                sp.GetRequiredService<StubExternalMediaListClient>());

            // Replace the Stripe gateway with a stub (canned network, real signature verification).
            services.RemoveAll<Jiten.Api.Services.Stripe.IStripeGateway>();
            services.AddSingleton<StubStripeGateway>();
            services.AddSingleton<Jiten.Api.Services.Stripe.IStripeGateway>(sp => sp.GetRequiredService<StubStripeGateway>());

            // Replace email (EmailService talks to SMTP) with a recording stub. It implements both the
            // IEmailService used by AccountController and the IEmailSender used by AuthController.
            services.RemoveAll<Jiten.Api.Services.IEmailService>();
            services.RemoveAll<Microsoft.AspNetCore.Identity.UI.Services.IEmailSender>();
            services.AddSingleton<RecordingEmailService>();
            services.AddSingleton<Jiten.Api.Services.IEmailService>(sp => sp.GetRequiredService<RecordingEmailService>());
            services.AddSingleton<Microsoft.AspNetCore.Identity.UI.Services.IEmailSender>(sp =>
                sp.GetRequiredService<RecordingEmailService>());

            // Stub outbound HTTP for the default client so reCAPTCHA siteverify never hits the network.
            services.AddHttpClient(string.Empty)
                .ConfigurePrimaryHttpMessageHandler(() => new StubRecaptchaHandler());
        });
    }

    public async Task InitializeAsync()
    {
        using var scope = Services.CreateScope();

        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        await jitenDb.Database.EnsureCreatedAsync();

        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        await userDb.Database.EnsureCreatedAsync();

        foreach (var id in new[] { TestUsers.UserA, TestUsers.UserB, TestUsers.Admin })
        {
            if (!await userDb.Users.AnyAsync(u => u.Id == id))
                userDb.Users.Add(new User { Id = id, UserName = id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        }
        await userDb.SaveChangesAsync();
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await _jitenConnection.DisposeAsync();
        await _userConnection.DisposeAsync();
    }

    /// <summary>Clears the jiten-side tables plus user deck preferences, profiles and study decks; every other user table (FSRS, promo, card media) and all jmdict word data is the test class's own job.</summary>
    public async Task ResetDatabaseAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();

        db.Notifications.RemoveRange(db.Notifications);
        db.SiteUpdates.RemoveRange(db.SiteUpdates);
        db.PollVotes.RemoveRange(db.PollVotes);
        db.PollOptions.RemoveRange(db.PollOptions);
        db.Polls.RemoveRange(db.Polls);
        db.RequestActivityLogs.RemoveRange(db.RequestActivityLogs);
        db.MediaRequestUploads.RemoveRange(db.MediaRequestUploads);
        db.MediaRequestComments.RemoveRange(db.MediaRequestComments);
        db.MediaRequestSubscriptions.RemoveRange(db.MediaRequestSubscriptions);
        db.MediaRequestUpvotes.RemoveRange(db.MediaRequestUpvotes);
        db.MediaRequestBoosts.RemoveRange(db.MediaRequestBoosts);
        db.MediaRequests.RemoveRange(db.MediaRequests);
        db.DeckActivityDailies.RemoveRange(db.DeckActivityDailies);
        db.Decks.RemoveRange(db.Decks);
        await db.SaveChangesAsync();

        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.UserDeckPreferences.RemoveRange(userDb.UserDeckPreferences);
        userDb.DeckDownloads.RemoveRange(userDb.DeckDownloads);
        userDb.UserProfiles.RemoveRange(userDb.UserProfiles);
        userDb.UserStudyDeckWords.RemoveRange(userDb.UserStudyDeckWords);
        userDb.UserStudyDecks.RemoveRange(userDb.UserStudyDecks);
        await userDb.SaveChangesAsync();
    }
}
