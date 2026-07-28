using System.Net;
using FluentAssertions;
using Jiten.Parser.Tests.Integration.Infrastructure;

namespace Jiten.Parser.Tests.Integration;

/// <summary>
/// Swagger generation fails as a whole document, so one bad action or a duplicate schema id takes the entire
/// API reference offline with a 500 that nothing else surfaces.
/// </summary>
public class SwaggerDocumentTests(JitenWebApplicationFactory factory) : IClassFixture<JitenWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task DocumentGenerates()
    {
        var response = await _client.GetAsync("/swagger/v1/swagger.json");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body[..Math.Min(2000, body.Length)]);
    }

    [Theory]
    [InlineData("/api/frequency-list/download")]
    [InlineData("/api/user/vocabulary/known-ids")]
    public async Task DocumentsPublicApiSurface(string path)
    {
        var body = await _client.GetStringAsync("/swagger/v1/swagger.json");

        body.Should().Contain($"\"{path}\"");
    }
}
