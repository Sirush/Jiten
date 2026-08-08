using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class LegalAcceptanceTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.UserLegalDocumentStates.RemoveRange(userDb.UserLegalDocumentStates);
        await userDb.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<JsonElement> GetStatus(string userId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/legal/status").WithUser(userId);
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<HttpResponseMessage> Post(string userId, string path, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path).WithUser(userId).WithJsonContent(body);
        return await _client.SendAsync(request);
    }

    private async Task<UserLegalDocumentState?> GetRow(string userId, LegalDocument document)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        return await userDb.UserLegalDocumentStates.AsNoTracking()
                           .FirstOrDefaultAsync(s => s.UserId == userId && s.Document == document);
    }

    [Fact]
    public async Task Status_Unauthenticated_Returns401()
    {
        var response = await _client.GetAsync("/api/legal/status");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Status_FreshUser_IsPendingInNoticePhase()
    {
        var status = await GetStatus(TestUsers.UserA);

        var cgu = status.GetProperty("cgu");
        cgu.GetProperty("accepted").GetBoolean().Should().BeFalse();
        cgu.GetProperty("dismissed").GetBoolean().Should().BeFalse();
        cgu.GetProperty("phase").GetString().Should().Be("notice");
        cgu.GetProperty("version").GetString().Should().NotBeNullOrEmpty();

        status.GetProperty("cgv").GetProperty("accepted").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task NoticeShown_IsIdempotent_AndNeverMovesTheClock()
    {
        (await Post(TestUsers.UserA, "/api/legal/notice-shown", new { document = "cgu" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var first = (await GetRow(TestUsers.UserA, LegalDocument.Cgu))!.NoticeShownAt;
        first.Should().NotBeNull();

        await Task.Delay(30);
        (await Post(TestUsers.UserA, "/api/legal/notice-shown", new { document = "cgu" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await GetRow(TestUsers.UserA, LegalDocument.Cgu))!.NoticeShownAt.Should().Be(first);
    }

    [Fact]
    public async Task Accept_CurrentVersion_RecordsAcceptance()
    {
        var status = await GetStatus(TestUsers.UserA);
        var version = status.GetProperty("cgu").GetProperty("version").GetString();

        (await Post(TestUsers.UserA, "/api/legal/accept", new { document = "cgu", version }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var row = (await GetRow(TestUsers.UserA, LegalDocument.Cgu))!;
        row.AcceptedAt.Should().NotBeNull();
        row.NoticeShownAt.Should().NotBeNull();
        row.Source.Should().Be(LegalAcceptanceSource.Banner);

        (await GetStatus(TestUsers.UserA)).GetProperty("cgu").GetProperty("accepted").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Accept_SupersededVersion_IsRejected()
    {
        var response = await Post(TestUsers.UserA, "/api/legal/accept", new { document = "cgu", version = "1970-01-01" });
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await GetRow(TestUsers.UserA, LegalDocument.Cgu)).Should().BeNull();
    }

    [Fact]
    public async Task Accept_Cgv_RecordsCheckoutSource()
    {
        var status = await GetStatus(TestUsers.UserA);
        var version = status.GetProperty("cgv").GetProperty("version").GetString();

        (await Post(TestUsers.UserA, "/api/legal/accept", new { document = "cgv", version }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var row = (await GetRow(TestUsers.UserA, LegalDocument.Cgv))!;
        row.AcceptedAt.Should().NotBeNull();
        row.Source.Should().Be(LegalAcceptanceSource.Checkout);
    }

    [Fact]
    public async Task Dismiss_InsideNoticePeriod_IsRejected()
    {
        await Post(TestUsers.UserA, "/api/legal/notice-shown", new { document = "cgu" });

        var response = await Post(TestUsers.UserA, "/api/legal/dismiss", new { document = "cgu" });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await GetRow(TestUsers.UserA, LegalDocument.Cgu))!.DismissedAt.Should().BeNull();
    }

    // A fresh registration records versioned CGU acceptance, so the banner must never ask again.
    [Fact]
    public async Task Register_ThenStatus_IsAlreadyAccepted_NoBannerPending()
    {
        // Role seeding is skipped in the Testing environment; registration assigns the User role.
        using (var scope = factory.Services.CreateScope())
        {
            var roleManager = scope.ServiceProvider
                                   .GetRequiredService<Microsoft.AspNetCore.Identity.RoleManager<Microsoft.AspNetCore.Identity.IdentityRole>>();
            if (!await roleManager.RoleExistsAsync("User"))
                await roleManager.CreateAsync(new Microsoft.AspNetCore.Identity.IdentityRole("User"));
        }

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
            .WithJsonContent(new
            {
                username = "legal_fresh_user",
                email = "legal_fresh@test.dev",
                password = "Str0ngPassw0rd!",
                recaptchaResponse = "test",
                tosAccepted = true,
                receiveNewsletter = false
            }));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string newUserId;
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            newUserId = await userDb.Users.Where(u => u.Email == "legal_fresh@test.dev").Select(u => u.Id).SingleAsync();

            var row = await userDb.UserLegalDocumentStates.AsNoTracking()
                                  .SingleAsync(s => s.UserId == newUserId && s.Document == LegalDocument.Cgu);
            row.AcceptedAt.Should().NotBeNull();
            row.Source.Should().Be(LegalAcceptanceSource.Registration);
        }

        var cgu = (await GetStatus(newUserId)).GetProperty("cgu");
        cgu.GetProperty("accepted").GetBoolean().Should().BeTrue();
        cgu.GetProperty("phase").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Dismiss_AfterNoticePeriod_IsPermanent()
    {
        await Post(TestUsers.UserA, "/api/legal/notice-shown", new { document = "cgu" });

        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var row = await userDb.UserLegalDocumentStates.FirstAsync(s => s.UserId == TestUsers.UserA);
            row.NoticeShownAt = DateTime.UtcNow.AddDays(-31);
            await userDb.SaveChangesAsync();
        }

        (await GetStatus(TestUsers.UserA)).GetProperty("cgu").GetProperty("phase").GetString().Should().Be("elapsed");

        (await Post(TestUsers.UserA, "/api/legal/dismiss", new { document = "cgu" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await GetRow(TestUsers.UserA, LegalDocument.Cgu))!.DismissedAt.Should().NotBeNull();

        var after = (await GetStatus(TestUsers.UserA)).GetProperty("cgu");
        after.GetProperty("dismissed").GetBoolean().Should().BeTrue();
        after.GetProperty("phase").ValueKind.Should().Be(JsonValueKind.Null);
    }
}
