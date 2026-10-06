using System.Net;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class SignupSourceTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        // Role seeding is skipped in the Testing environment; registration assigns the User role.
        using var scope = factory.Services.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        if (!await roleManager.RoleExistsAsync("User"))
            await roleManager.CreateAsync(new IdentityRole("User"));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<string?> Register(string name, object? signupSource)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
            .WithJsonContent(new
            {
                username = name,
                email = $"{name}@test.dev",
                password = "Str0ngPassw0rd!",
                recaptchaResponse = "test",
                tosAccepted = true,
                receiveNewsletter = false,
                signupSource
            }));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        return await userDb.Users.Where(u => u.Email == $"{name}@test.dev").Select(u => u.SignupSourceJson).SingleAsync();
    }

    [Fact]
    public async Task Register_WithSource_StoresIt()
    {
        var json = await Register("source_full", new
        {
            route = "/decks/media/:id()/detail",
            referrer = "www.youtube.com",
            utm = "creator_x",
            age = "1-6",
            prompt = "download_dialog"
        });

        json.Should().NotBeNull();
        var stored = JsonDocument.Parse(json!).RootElement;
        stored.GetProperty("route").GetString().Should().Be("/decks/media/:id()/detail");
        stored.GetProperty("referrer").GetString().Should().Be("www.youtube.com");
        stored.GetProperty("utm").GetString().Should().Be("creator_x");
        stored.GetProperty("age").GetString().Should().Be("1-6");
        stored.GetProperty("prompt").GetString().Should().Be("download_dialog");
    }

    [Fact]
    public async Task Register_WithoutSource_StoresNothing()
    {
        (await Register("source_none", null)).Should().BeNull();
    }

    [Fact]
    public async Task Register_WithMalformedFields_DropsThem()
    {
        var json = await Register("source_bad", new
        {
            route = "https://evil.example/x",
            referrer = "<script>",
            utm = new string('a', 80),
            age = "forever",
            prompt = "download_dialog"
        });

        var stored = JsonDocument.Parse(json!).RootElement;
        stored.TryGetProperty("route", out _).Should().BeFalse();
        stored.TryGetProperty("referrer", out _).Should().BeFalse();
        stored.TryGetProperty("utm", out _).Should().BeFalse();
        stored.TryGetProperty("age", out _).Should().BeFalse();
        stored.GetProperty("prompt").GetString().Should().Be("download_dialog");
    }
}
