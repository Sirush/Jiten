using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Parser.Tests.Integration.Infrastructure;

namespace Jiten.Parser.Tests.Integration;

public class PollTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<int> CreatePoll(string question = "Which feature next?", string[]? options = null,
                                       int maxSelections = 1, DateTime? closesAt = null)
    {
        options ??= ["Kanji grids", "Pitch accent"];

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/polls")
            .WithAdmin()
            .WithJsonContent(new
            {
                question,
                descriptionMarkdown = (string?)null,
                maxSelections,
                closesAt,
                options = options.Select((text, index) => new { text, sortOrder = index }).ToArray()
            });

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private Task<HttpResponseMessage> Publish(int id) =>
        _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/admin/polls/{id}/publish").WithAdmin());

    private Task<HttpResponseMessage> Close(int id) =>
        _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/admin/polls/{id}/close").WithAdmin());

    private async Task<int> CreatePublishedPoll(string question = "Which feature next?", string[]? options = null,
                                                int maxSelections = 1, DateTime? closesAt = null)
    {
        var id = await CreatePoll(question, options, maxSelections, closesAt);
        (await Publish(id)).StatusCode.Should().Be(HttpStatusCode.OK);
        return id;
    }

    private async Task<List<int>> OptionIds(int pollId)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/admin/polls/{pollId}").WithAdmin());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("id").GetInt32()).ToList();
    }

    private Task<HttpResponseMessage> Vote(string userId, int pollId, params int[] optionIds) =>
        _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, $"/api/polls/{pollId}/vote")
                          .WithUser(userId)
                          .WithJsonContent(new { optionIds }));

    private async Task<JsonElement> GetPollAs(string userId, int pollId)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/polls/{pollId}").WithUser(userId));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static int OptionCount(JsonElement poll, int optionId) =>
        poll.GetProperty("options").EnumerateArray().First(o => o.GetProperty("id").GetInt32() == optionId)
            .GetProperty("voteCount").GetInt32();

    [Fact]
    public async Task Lifecycle_DraftIsHidden_PublishNeedsTwoOptions()
    {
        var draftId = await CreatePoll("Still cooking");

        var detail = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/polls/{draftId}").WithUser(TestUsers.UserA));
        detail.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var list = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/polls").WithUser(TestUsers.UserA));
        (await list.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetArrayLength().Should().Be(0);

        var thinCreate = new HttpRequestMessage(HttpMethod.Post, "/api/admin/polls")
            .WithAdmin()
            .WithJsonContent(new
            {
                question = "Only one choice",
                descriptionMarkdown = (string?)null,
                maxSelections = 1,
                closesAt = (DateTime?)null,
                options = new[] { new { text = "Yes", sortOrder = 0 } }
            });
        (await _client.SendAsync(thinCreate)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await Publish(draftId)).StatusCode.Should().Be(HttpStatusCode.OK);

        var afterPublish = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/polls").WithUser(TestUsers.UserA));
        var data = (await afterPublish.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        data.GetArrayLength().Should().Be(1);
        data[0].GetProperty("id").GetInt32().Should().Be(draftId);
        data[0].GetProperty("resultsVisible").GetBoolean().Should().BeFalse();
        data[0].GetProperty("totalVoters").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Vote_RevealsResultsToVoterOnly()
    {
        var pollId = await CreatePublishedPoll();
        var options = await OptionIds(pollId);

        var voteResponse = await Vote(TestUsers.UserA, pollId, options[0]);
        voteResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var fromVote = await voteResponse.Content.ReadFromJsonAsync<JsonElement>();
        fromVote.GetProperty("resultsVisible").GetBoolean().Should().BeTrue();
        fromVote.GetProperty("myOptionIds").EnumerateArray().Select(o => o.GetInt32()).Should().Equal(options[0]);
        fromVote.GetProperty("totalVoters").GetInt32().Should().Be(1);
        OptionCount(fromVote, options[0]).Should().Be(1);
        OptionCount(fromVote, options[1]).Should().Be(0);

        var asA = await GetPollAs(TestUsers.UserA, pollId);
        asA.GetProperty("resultsVisible").GetBoolean().Should().BeTrue();
        asA.GetProperty("totalVoters").GetInt32().Should().Be(1);

        var asB = await GetPollAs(TestUsers.UserB, pollId);
        asB.GetProperty("resultsVisible").GetBoolean().Should().BeFalse();
        asB.GetProperty("totalVoters").ValueKind.Should().Be(JsonValueKind.Null);
        asB.GetProperty("myOptionIds").GetArrayLength().Should().Be(0);
        asB.GetProperty("options")[0].GetProperty("voteCount").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task ChangeVote_MovesCounts_AndRepeatIsNoOp()
    {
        var pollId = await CreatePublishedPoll();
        var options = await OptionIds(pollId);

        await Vote(TestUsers.UserA, pollId, options[0]);
        var changed = await Vote(TestUsers.UserA, pollId, options[1]);
        changed.StatusCode.Should().Be(HttpStatusCode.OK);

        var afterChange = await changed.Content.ReadFromJsonAsync<JsonElement>();
        OptionCount(afterChange, options[0]).Should().Be(0);
        OptionCount(afterChange, options[1]).Should().Be(1);
        afterChange.GetProperty("totalVoters").GetInt32().Should().Be(1);

        var repeat = await Vote(TestUsers.UserA, pollId, options[1]);
        repeat.StatusCode.Should().Be(HttpStatusCode.OK);

        var afterRepeat = await repeat.Content.ReadFromJsonAsync<JsonElement>();
        OptionCount(afterRepeat, options[1]).Should().Be(1);
        afterRepeat.GetProperty("totalVoters").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task MultiSelect_HonoursCap_AndCountsOneBallot()
    {
        var pollId = await CreatePublishedPoll("Pick two", ["A", "B", "C"], maxSelections: 2);
        var options = await OptionIds(pollId);

        var otherPollId = await CreatePublishedPoll("Other poll", ["X", "Y"]);
        var otherOptions = await OptionIds(otherPollId);

        (await Vote(TestUsers.UserA, pollId, options[0], options[1], options[2])).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
        (await Vote(TestUsers.UserA, pollId, options[0], otherOptions[0])).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
        (await Vote(TestUsers.UserA, pollId, options[0], options[0])).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
        (await Vote(TestUsers.UserA, pollId)).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);

        var accepted = await Vote(TestUsers.UserA, pollId, options[0], options[1]);
        accepted.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await accepted.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("totalVoters").GetInt32().Should().Be(1);
        body.GetProperty("myOptionIds").GetArrayLength().Should().Be(2);
        OptionCount(body, options[0]).Should().Be(1);
        OptionCount(body, options[1]).Should().Be(1);
        OptionCount(body, options[2]).Should().Be(0);
    }

    [Fact]
    public async Task Close_BlocksVoting_AndOpensResultsToEveryone()
    {
        var manualId = await CreatePublishedPoll("Closed by hand");
        var manualOptions = await OptionIds(manualId);
        await Vote(TestUsers.UserA, manualId, manualOptions[0]);
        (await Close(manualId)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Vote(TestUsers.UserB, manualId, manualOptions[0])).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var manualAsB = await GetPollAs(TestUsers.UserB, manualId);
        manualAsB.GetProperty("isClosed").GetBoolean().Should().BeTrue();
        manualAsB.GetProperty("resultsVisible").GetBoolean().Should().BeTrue();
        manualAsB.GetProperty("totalVoters").GetInt32().Should().Be(1);

        var expiredId = await CreatePublishedPoll("Ran out of time", closesAt: DateTime.UtcNow.AddDays(-1));
        var expiredOptions = await OptionIds(expiredId);

        (await Vote(TestUsers.UserB, expiredId, expiredOptions[0])).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var expiredAsB = await GetPollAs(TestUsers.UserB, expiredId);
        expiredAsB.GetProperty("isClosed").GetBoolean().Should().BeTrue();
        expiredAsB.GetProperty("resultsVisible").GetBoolean().Should().BeTrue();
        expiredAsB.GetProperty("totalVoters").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task NoResponseEverCarriesAVoterId()
    {
        var pollId = await CreatePublishedPoll();
        var options = await OptionIds(pollId);
        await Vote(TestUsers.UserA, pollId, options[0]);

        var payloads = new List<string>
        {
            await (await Vote(TestUsers.UserA, pollId, options[1])).Content.ReadAsStringAsync(),
            await (await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/polls").WithUser(TestUsers.UserA))).Content.ReadAsStringAsync(),
            await (await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/polls/{pollId}").WithUser(TestUsers.UserB))).Content.ReadAsStringAsync(),
            await (await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/polls/home").WithUser(TestUsers.UserB))).Content.ReadAsStringAsync(),
            await (await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/admin/polls").WithAdmin())).Content.ReadAsStringAsync(),
            await (await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/admin/polls/{pollId}").WithAdmin())).Content.ReadAsStringAsync()
        };

        foreach (var payload in payloads)
        {
            payload.Should().NotContain(TestUsers.UserA);
            payload.Should().NotContain(TestUsers.UserB);
        }
    }

    [Fact]
    public async Task HomePick_PrefersUnvoted_ThenMostRecent_ThenNothing()
    {
        var empty = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/polls/home").WithUser(TestUsers.UserA));
        empty.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var firstId = await CreatePublishedPoll("First poll");
        var secondId = await CreatePublishedPoll("Second poll");

        var firstOptions = await OptionIds(firstId);
        await Vote(TestUsers.UserA, firstId, firstOptions[0]);

        var unvoted = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/polls/home").WithUser(TestUsers.UserA));
        unvoted.StatusCode.Should().Be(HttpStatusCode.OK);
        (await unvoted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32().Should().Be(secondId);

        var secondOptions = await OptionIds(secondId);
        await Vote(TestUsers.UserA, secondId, secondOptions[0]);

        var allVoted = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/polls/home").WithUser(TestUsers.UserA));
        allVoted.StatusCode.Should().Be(HttpStatusCode.OK);
        (await allVoted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32().Should().Be(secondId);

        await Close(firstId);
        await Close(secondId);

        var noneActive = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/polls/home").WithUser(TestUsers.UserA));
        noneActive.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task HomePick_ExcludeSkipsPolls_AndNeverResurfacesThemAsFallback()
    {
        var firstId = await CreatePublishedPoll("First poll");
        var secondId = await CreatePublishedPoll("Second poll");

        var afterSkip = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/api/polls/home?exclude={firstId}").WithUser(TestUsers.UserA));
        afterSkip.StatusCode.Should().Be(HttpStatusCode.OK);
        (await afterSkip.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32().Should().Be(secondId);

        var bothSkipped = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/api/polls/home?exclude={firstId}&exclude={secondId}").WithUser(TestUsers.UserA));
        bothSkipped.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var secondOptions = await OptionIds(secondId);
        await Vote(TestUsers.UserA, secondId, secondOptions[0]);

        var votedFallback = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/api/polls/home?exclude={firstId}").WithUser(TestUsers.UserA));
        votedFallback.StatusCode.Should().Be(HttpStatusCode.OK);
        var fallback = await votedFallback.Content.ReadFromJsonAsync<JsonElement>();
        fallback.GetProperty("id").GetInt32().Should().Be(secondId);
        fallback.GetProperty("myOptionIds").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Reopen_RestoresVoting_ForBothClosePaths()
    {
        var manualId = await CreatePublishedPoll("Closed by hand");
        var manualOptions = await OptionIds(manualId);
        (await Close(manualId)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Vote(TestUsers.UserA, manualId, manualOptions[0])).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var reopen = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/admin/polls/{manualId}/reopen").WithAdmin());
        reopen.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Vote(TestUsers.UserA, manualId, manualOptions[0])).StatusCode.Should().Be(HttpStatusCode.OK);

        var expiredId = await CreatePublishedPoll("Ran out of time", closesAt: DateTime.UtcNow.AddDays(-1));
        var expiredOptions = await OptionIds(expiredId);
        (await Vote(TestUsers.UserB, expiredId, expiredOptions[0])).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var reopenExpired = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/admin/polls/{expiredId}/reopen").WithAdmin());
        reopenExpired.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Vote(TestUsers.UserB, expiredId, expiredOptions[0])).StatusCode.Should().Be(HttpStatusCode.OK);

        var poll = await GetPollAs(TestUsers.UserB, expiredId);
        poll.GetProperty("isClosed").GetBoolean().Should().BeFalse();
        poll.GetProperty("closesAt").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task AdminEdit_ProtectsVotedOptions_AndFreezesMaxSelections()
    {
        var pollId = await CreatePublishedPoll("Pick one", ["Voted on", "Untouched", "Third"]);
        var options = await OptionIds(pollId);
        await Vote(TestUsers.UserA, pollId, options[0]);

        var dropVoted = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/polls/{pollId}")
            .WithAdmin()
            .WithJsonContent(new
            {
                question = "Pick one",
                maxSelections = 1,
                options = new[]
                {
                    new { id = (int?)options[1], text = "Untouched", sortOrder = 0 },
                    new { id = (int?)options[2], text = "Third", sortOrder = 1 }
                }
            });
        (await _client.SendAsync(dropVoted)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var dropUnvoted = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/polls/{pollId}")
            .WithAdmin()
            .WithJsonContent(new
            {
                question = "Pick one, renamed",
                maxSelections = 1,
                options = new[]
                {
                    new { id = (int?)options[0], text = "Voted on, renamed", sortOrder = 0 },
                    new { id = (int?)options[2], text = "Third", sortOrder = 1 },
                    new { id = (int?)null, text = "Added later", sortOrder = 2 }
                }
            });
        (await _client.SendAsync(dropUnvoted)).StatusCode.Should().Be(HttpStatusCode.OK);

        var afterEdit = await GetPollAs(TestUsers.UserA, pollId);
        afterEdit.GetProperty("question").GetString().Should().Be("Pick one, renamed");
        afterEdit.GetProperty("options").GetArrayLength().Should().Be(3);
        OptionCount(afterEdit, options[0]).Should().Be(1);

        var tooFewOptions = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/polls/{pollId}")
            .WithAdmin()
            .WithJsonContent(new
            {
                question = "Pick one, renamed",
                maxSelections = 1,
                options = new[] { new { id = (int?)options[0], text = "Voted on, renamed", sortOrder = 0 } }
            });
        (await _client.SendAsync(tooFewOptions)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var changeCap = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/polls/{pollId}")
            .WithAdmin()
            .WithJsonContent(new
            {
                question = "Pick one, renamed",
                maxSelections = 2,
                options = new[]
                {
                    new { id = (int?)options[0], text = "Voted on, renamed", sortOrder = 0 },
                    new { id = (int?)options[2], text = "Third", sortOrder = 1 }
                }
            });
        (await _client.SendAsync(changeCap)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
}
