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
using Moq;
using StackExchange.Redis;
using ApiProgram = Program;

namespace Jiten.Parser.Tests.Integration.Infrastructure;

public class JitenWebApplicationFactory : WebApplicationFactory<ApiProgram>, IAsyncLifetime
{
    private SqliteConnection _jitenConnection = null!;
    private SqliteConnection _userConnection = null!;

    public JitenWebApplicationFactory()
    {
        Environment.SetEnvironmentVariable("JwtSettings__Secret", "ThisIsATestSecretKeyThatIsLongEnoughForHS256!");
        Environment.SetEnvironmentVariable("JwtSettings__Issuer", "TestIssuer");
        Environment.SetEnvironmentVariable("JwtSettings__Audience", "TestAudience");
        Environment.SetEnvironmentVariable("ConnectionStrings__JitenDatabase", "DataSource=:memory:");
        Environment.SetEnvironmentVariable("ConnectionStrings__Redis", "localhost:6379");
        Environment.SetEnvironmentVariable("StaticFilesPath", Path.GetTempPath());
        Environment.SetEnvironmentVariable("UseBunnyCdn", "false");
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

            // Replace auth
            services.RemoveAll<IAuthenticationSchemeProvider>();
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

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

    public async Task ResetDatabaseAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();

        db.Notifications.RemoveRange(db.Notifications);
        db.RequestActivityLogs.RemoveRange(db.RequestActivityLogs);
        db.MediaRequestUploads.RemoveRange(db.MediaRequestUploads);
        db.MediaRequestComments.RemoveRange(db.MediaRequestComments);
        db.MediaRequestSubscriptions.RemoveRange(db.MediaRequestSubscriptions);
        db.MediaRequestUpvotes.RemoveRange(db.MediaRequestUpvotes);
        db.MediaRequests.RemoveRange(db.MediaRequests);
        db.Decks.RemoveRange(db.Decks);
        await db.SaveChangesAsync();

        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.UserDeckPreferences.RemoveRange(userDb.UserDeckPreferences);
        userDb.UserProfiles.RemoveRange(userDb.UserProfiles);
        await userDb.SaveChangesAsync();
    }
}
