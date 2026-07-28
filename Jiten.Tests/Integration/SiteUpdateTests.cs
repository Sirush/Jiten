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

public class SiteUpdateTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<int> CreateUpdate(string title = "New feature", string body = "## Heading\n\nSome **markdown**.",
                                         string? teaser = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/updates")
            .WithAdmin()
            .WithJsonContent(new { title, bodyMarkdown = body, notificationTeaser = teaser });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body_ = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body_.GetProperty("id").GetInt32();
    }

    private async Task<HttpResponseMessage> Publish(int id)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/updates/{id}/publish").WithAdmin();
        return await _client.SendAsync(request);
    }

    private async Task<List<Notification>> GetNotifications()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        return await db.Notifications.AsNoTracking().ToListAsync();
    }

    [Fact]
    public async Task AdminCrud_RoundTrips()
    {
        var id = await CreateUpdate("Draft title", "Draft body");

        var edit = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/updates/{id}")
            .WithAdmin()
            .WithJsonContent(new { title = "Edited title", bodyMarkdown = "Edited body", notificationTeaser = "Teaser" });
        (await _client.SendAsync(edit)).StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/admin/updates").WithAdmin());
        var body = await list.Content.ReadFromJsonAsync<JsonElement>();
        body.GetArrayLength().Should().Be(1);
        body[0].GetProperty("title").GetString().Should().Be("Edited title");
        body[0].GetProperty("notificationTeaser").GetString().Should().Be("Teaser");
        body[0].GetProperty("updatedAt").ValueKind.Should().NotBe(JsonValueKind.Null);
        body[0].GetProperty("publishedAt").ValueKind.Should().Be(JsonValueKind.Null);

        var delete = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/updates/{id}").WithAdmin());
        delete.StatusCode.Should().Be(HttpStatusCode.OK);

        var afterDelete = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/admin/updates").WithAdmin());
        (await afterDelete.Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task PublicList_ExcludesDrafts_AndAllowsAnonymous()
    {
        var draftId = await CreateUpdate("Still a draft");
        var publishedId = await CreateUpdate("Shipped");
        await Publish(publishedId);

        var response = await _client.GetAsync("/api/updates");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = body.GetProperty("data");
        data.GetArrayLength().Should().Be(1);
        data[0].GetProperty("id").GetInt32().Should().Be(publishedId);
        data[0].GetProperty("title").GetString().Should().Be("Shipped");

        (await _client.GetAsync($"/api/updates/{draftId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.GetAsync($"/api/updates/{publishedId}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Publish_StampsDate_AndNotifiesEveryUser()
    {
        var id = await CreateUpdate("Big release", teaser: "Three new things landed.");

        var response = await Publish(id);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("count").GetInt32().Should().Be(3);

        var notifications = await GetNotifications();
        notifications.Should().HaveCount(3);
        notifications.Select(n => n.UserId).Should()
                     .BeEquivalentTo(new[] { TestUsers.UserA, TestUsers.UserB, TestUsers.Admin });
        notifications.Should().OnlyContain(n => n.Type == NotificationType.SiteUpdate);
        notifications.Should().OnlyContain(n => n.Title == "Big release");
        notifications.Should().OnlyContain(n => n.Message == "Three new things landed.");
        notifications.Should().OnlyContain(n => n.LinkUrl == $"/updates#update-{id}");

        var single = await _client.GetFromJsonAsync<JsonElement>($"/api/updates/{id}");
        single.GetProperty("publishedAt").ValueKind.Should().NotBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Publish_UsesDefaultMessage_WhenNoTeaser()
    {
        var id = await CreateUpdate("No teaser here");
        await Publish(id);

        var notifications = await GetNotifications();
        notifications.Should().OnlyContain(n => n.Message == "A new site update has been published.");
    }

    [Fact]
    public async Task Publish_IsIdempotent()
    {
        var id = await CreateUpdate();

        await Publish(id);
        var firstPublishedAt = (await _client.GetFromJsonAsync<JsonElement>($"/api/updates/{id}"))
                               .GetProperty("publishedAt").GetDateTime();

        var second = await Publish(id);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("count").GetInt32().Should().Be(0);

        (await GetNotifications()).Should().HaveCount(3);
        (await _client.GetFromJsonAsync<JsonElement>($"/api/updates/{id}"))
            .GetProperty("publishedAt").GetDateTime().Should().Be(firstPublishedAt);
    }

    [Fact]
    public async Task EditAfterPublish_DoesNotNotifyAgain()
    {
        var id = await CreateUpdate();
        await Publish(id);

        var edit = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/updates/{id}")
            .WithAdmin()
            .WithJsonContent(new { title = "Corrected", bodyMarkdown = "Fixed a typo" });
        (await _client.SendAsync(edit)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await GetNotifications()).Should().HaveCount(3);

        var single = await _client.GetFromJsonAsync<JsonElement>($"/api/updates/{id}");
        single.GetProperty("title").GetString().Should().Be("Corrected");
        single.GetProperty("updatedAt").ValueKind.Should().NotBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task NonAdmin_IsForbiddenOnAllAdminEndpoints()
    {
        var id = await CreateUpdate();

        var calls = new[]
        {
            new HttpRequestMessage(HttpMethod.Get, "/api/admin/updates"),
            new HttpRequestMessage(HttpMethod.Get, $"/api/admin/updates/{id}"),
            new HttpRequestMessage(HttpMethod.Post, "/api/admin/updates")
                .WithJsonContent(new { title = "Nope", bodyMarkdown = "Nope" }),
            new HttpRequestMessage(HttpMethod.Put, $"/api/admin/updates/{id}")
                .WithJsonContent(new { title = "Nope", bodyMarkdown = "Nope" }),
            new HttpRequestMessage(HttpMethod.Post, $"/api/admin/updates/{id}/publish"),
            new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/updates/{id}")
        };

        foreach (var call in calls)
        {
            var response = await _client.SendAsync(call.WithUser(TestUsers.UserA));
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        (await GetNotifications()).Should().BeEmpty();
    }

    [Fact]
    public async Task Create_RejectsMissingTitleOrBody()
    {
        var noTitle = new HttpRequestMessage(HttpMethod.Post, "/api/admin/updates")
            .WithAdmin()
            .WithJsonContent(new { title = "", bodyMarkdown = "Body" });
        (await _client.SendAsync(noTitle)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var noBody = new HttpRequestMessage(HttpMethod.Post, "/api/admin/updates")
            .WithAdmin()
            .WithJsonContent(new { title = "Title", bodyMarkdown = "" });
        (await _client.SendAsync(noBody)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
