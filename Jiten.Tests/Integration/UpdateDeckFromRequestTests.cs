using System.Net;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class UpdateDeckFromRequestTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    private const int DeckId = 1;
    private const int OtherDeckId = 2;

    public Task InitializeAsync() => factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<int> SeedAsync(int? targetDeckId, MediaRequestStatus status = MediaRequestStatus.Open)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();

        db.MediaRequests.RemoveRange(db.MediaRequests);
        db.Decks.RemoveRange(db.Decks);
        await db.SaveChangesAsync();

        db.Decks.AddRange(
            new Deck { DeckId = DeckId, OriginalTitle = "Main", MediaType = MediaType.Anime },
            new Deck { DeckId = OtherDeckId, OriginalTitle = "Other", MediaType = MediaType.Anime });
        await db.SaveChangesAsync();

        var request = new MediaRequest
                      {
                          Title = "Add season 2", Kind = MediaRequestKind.Update, MediaType = MediaType.Anime,
                          Status = status, TargetDeckId = targetDeckId, RequesterId = TestUsers.UserA
                      };
        db.MediaRequests.Add(request);
        await db.SaveChangesAsync();
        return request.Id;
    }

    private Task<HttpResponseMessage> UpdateDeckAsync(int deckId, int? requestId)
    {
        var form = new MultipartFormDataContent
                   {
                       { new StringContent(deckId.ToString()), "deckId" },
                       { new StringContent(((int)MediaType.Anime).ToString()), "mediaType" },
                       { new StringContent("Main"), "originalTitle" },
                       { new StringContent("2020-01-01"), "releaseDate" },
                       { new StringContent("false"), "reparse" }
                   };
        if (requestId.HasValue)
            form.Add(new StringContent(requestId.Value.ToString()), "requestId");

        var message = new HttpRequestMessage(HttpMethod.Post, "/api/admin/update-deck") { Content = form }.WithAdmin();
        return _client.SendAsync(message);
    }

    private async Task<MediaRequest> ReadRequestAsync(int requestId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        return await db.MediaRequests.AsNoTracking().SingleAsync(r => r.Id == requestId);
    }

    [Fact]
    public async Task UpdateWithRequestId_LinksRequestAndMovesItInProgress()
    {
        var requestId = await SeedAsync(targetDeckId: DeckId);

        var response = await UpdateDeckAsync(DeckId, requestId);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var request = await ReadRequestAsync(requestId);
        request.FulfilledDeckId.Should().Be(DeckId);
        request.Status.Should().Be(MediaRequestStatus.InProgress);
    }

    [Fact]
    public async Task UpdateWithRequestId_KeepsNonOpenStatus()
    {
        var requestId = await SeedAsync(targetDeckId: DeckId, status: MediaRequestStatus.Completed);

        var response = await UpdateDeckAsync(DeckId, requestId);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var request = await ReadRequestAsync(requestId);
        request.FulfilledDeckId.Should().Be(DeckId);
        request.Status.Should().Be(MediaRequestStatus.Completed);
    }

    [Fact]
    public async Task UpdateWithRequestTargetingAnotherDeck_IsRejectedAndLeavesRequestUntouched()
    {
        var requestId = await SeedAsync(targetDeckId: OtherDeckId);

        var response = await UpdateDeckAsync(DeckId, requestId);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var request = await ReadRequestAsync(requestId);
        request.FulfilledDeckId.Should().BeNull();
        request.Status.Should().Be(MediaRequestStatus.Open);
    }

    [Fact]
    public async Task UpdateWithoutRequestId_LeavesRequestUntouched()
    {
        var requestId = await SeedAsync(targetDeckId: DeckId);

        var response = await UpdateDeckAsync(DeckId, requestId: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var request = await ReadRequestAsync(requestId);
        request.FulfilledDeckId.Should().BeNull();
        request.Status.Should().Be(MediaRequestStatus.Open);
    }
}
