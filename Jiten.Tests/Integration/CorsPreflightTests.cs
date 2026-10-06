using System.Net;
using FluentAssertions;
using Jiten.Parser.Tests.Integration.Infrastructure;

namespace Jiten.Parser.Tests.Integration;

public class CorsPreflightTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private const string AllowedOrigin = "https://jiten.moe";
    private const string DisallowedOrigin = "https://evil.example";

    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static HttpRequestMessage Preflight(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/srs/review");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");
        return request;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    [Fact]
    public async Task Preflight_FromAllowedOrigin_IsCacheableForTwoHours()
    {
        var response = await _client.SendAsync(Preflight(AllowedOrigin));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        Header(response, "Access-Control-Allow-Origin").Should().Be(AllowedOrigin);
        Header(response, "Access-Control-Max-Age").Should().Be("7200");
    }

    [Fact]
    public async Task Preflight_FromDisallowedOrigin_GetsNoCorsHeaders()
    {
        var response = await _client.SendAsync(Preflight(DisallowedOrigin));

        Header(response, "Access-Control-Allow-Origin").Should().BeNull();
        Header(response, "Access-Control-Max-Age").Should().BeNull();
    }

    [Fact]
    public async Task Request_FromAllowedOrigin_ExposesServerTimingWithRoute()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/notifications/unread-count").WithUser(TestUsers.UserA);
        request.Headers.Add("Origin", AllowedOrigin);

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Header(response, "Timing-Allow-Origin").Should().Be(AllowedOrigin);
        Header(response, "Server-Timing").Should().MatchRegex("""^app;dur=\d+\.\d;desc="api/notifications/unread-count"$""");
    }

    [Fact]
    public async Task Request_WithoutAllowedOrigin_GetsNoTimingHeaders()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/notifications/unread-count").WithUser(TestUsers.UserA);
        request.Headers.Add("Origin", DisallowedOrigin);

        var response = await _client.SendAsync(request);

        Header(response, "Timing-Allow-Origin").Should().BeNull();
        Header(response, "Server-Timing").Should().BeNull();
    }
}
