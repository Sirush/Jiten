using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Jiten.Parser.Tests.Integration.Infrastructure;

namespace Jiten.Parser.Tests.Integration;

public class GzipRequestBodyTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static readonly object BlankWordImport = new
    {
        cards = new[] { new { card = new { word = " " }, reviewLogs = Array.Empty<object>() } },
        overwrite = false,
        parseWords = false,
    };

    private static ByteArrayContent GzipJson(object payload, bool declareEncoding)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest))
            gzip.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));

        var content = new ByteArrayContent(buffer.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (declareEncoding)
            content.Headers.ContentEncoding.Add("gzip");
        return content;
    }

    [Fact]
    public async Task AnkiImport_AcceptsGzippedBody()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/user/vocabulary/import-from-anki")
            .WithUser(TestUsers.UserA);
        request.Content = GzipJson(BlankWordImport, declareEncoding: true);

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("No valid words found.");
    }

    [Fact]
    public async Task AnkiImport_RejectsBodyThatInflatesPastSizeLimit()
    {
        var bomb = new { cards = Array.Empty<object>(), padding = new string('a', 31_000_000) };
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/user/vocabulary/import-from-anki")
            .WithUser(TestUsers.UserA);
        request.Content = GzipJson(bomb, declareEncoding: true);
        (await request.Content.ReadAsByteArrayAsync()).Length.Should().BeLessThan(1_000_000);

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AnkiImport_RejectsCorruptGzipBody()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/user/vocabulary/import-from-anki")
            .WithUser(TestUsers.UserA);
        var content = new ByteArrayContent([0x1f, 0x8b, 0x08, 0x00, 0xde, 0xad, 0xbe, 0xef, 0x00, 0x00]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");
        request.Content = content;

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AnkiImport_Anonymous_IsRejectedBeforeInflating()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/user/vocabulary/import-from-anki");
        request.Content = GzipJson(BlankWordImport, declareEncoding: true);

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task EndpointWithoutOptIn_DoesNotInflateGzippedBody()
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/user/hidden-definitions/100")
            .WithUser(TestUsers.UserA);
        request.Content = GzipJson(new { hiddenIndices = new[] { 1 } }, declareEncoding: true);

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AnkiImport_GzippedBodyWithoutEncodingHeader_IsNotParsed()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/user/vocabulary/import-from-anki")
            .WithUser(TestUsers.UserA);
        request.Content = GzipJson(BlankWordImport, declareEncoding: false);

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("No valid words found.");
    }
}
