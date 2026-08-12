using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.User;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class VocabularyBackupExtrasTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        await userDb.UserExampleSentences.ExecuteDeleteAsync();
        await userDb.UserCustomMeanings.ExecuteDeleteAsync();
        await userDb.FsrsCards.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private Task<HttpResponseMessage> AddSentence(int wordId, byte readingIndex, string text, string? source = "Some deck")
        => _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/user/example-sentences/{wordId}/{readingIndex}")
                             .WithUser(TestUsers.UserA)
                             .WithJsonContent(new { text, source }));

    private Task<HttpResponseMessage> SetMeaning(int wordId, string text)
        => _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, $"/api/user/custom-meanings/{wordId}")
                             .WithUser(TestUsers.UserA)
                             .WithJsonContent(new { text }));

    private async Task<FsrsExportDto> Export()
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/user/vocabulary/export")
                                               .WithUser(TestUsers.UserA));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<FsrsExportDto>())!;
    }

    private Task<HttpResponseMessage> Import(FsrsExportDto export, bool overwrite = false)
        => _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/user/vocabulary/import?overwrite={overwrite}")
                             .WithUser(TestUsers.UserA)
                             .WithJsonContent(export));

    private async Task Wipe()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        await userDb.UserExampleSentences.ExecuteDeleteAsync();
        await userDb.UserCustomMeanings.ExecuteDeleteAsync();
    }

    private async Task<(List<UserExampleSentence> Sentences, List<UserCustomMeaning> Meanings)> Saved()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        return (await userDb.UserExampleSentences.Where(e => e.UserId == TestUsers.UserA)
                            .OrderBy(e => e.WordId).ThenBy(e => e.SortOrder).ToListAsync(),
                await userDb.UserCustomMeanings.Where(m => m.UserId == TestUsers.UserA)
                            .OrderBy(m => m.WordId).ToListAsync());
    }

    /// <summary>Sentences and notes belong to the word, not to a card, so a backup carries them with no card seeded.</summary>
    [Fact]
    public async Task Export_CarriesSentencesAndNotesForWordsWithNoCard()
    {
        (await AddSentence(901, 0, "これは**本**です")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SetMeaning(901, "the book I read")).StatusCode.Should().Be(HttpStatusCode.OK);

        var export = await Export();

        export.TotalCards.Should().Be(0);
        export.CustomSentences.Should().ContainSingle().Which.Text.Should().Be("これは**本**です");
        export.CustomSentences![0].Source.Should().Be("Some deck");
        export.CustomMeanings.Should().ContainSingle().Which.Text.Should().Be("the book I read");
    }

    [Fact]
    public async Task Import_RestoresSentencesAndNotesFromABackupWithNoCards()
    {
        await AddSentence(901, 0, "これは**本**です");
        await AddSentence(901, 0, "あの**本**を読んだ");
        await SetMeaning(901, "the book I read");

        var export = await Export();
        await Wipe();

        var response = await Import(export);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = (await response.Content.ReadFromJsonAsync<FsrsImportResultDto>())!;
        result.CustomSentencesImported.Should().Be(2);
        result.CustomMeaningsImported.Should().Be(1);

        var (sentences, meanings) = await Saved();
        sentences.Should().HaveCount(2);
        sentences.Select(s => s.SortOrder).Should().OnlyHaveUniqueItems();
        meanings.Should().ContainSingle().Which.Text.Should().Be("the book I read");
    }

    [Fact]
    public async Task Import_OfTheSameBackupTwice_DoesNotDuplicateSentences()
    {
        await AddSentence(901, 0, "これは**本**です");

        var export = await Export();

        (await Import(export)).StatusCode.Should().Be(HttpStatusCode.OK);
        var second = await Import(export);
        var result = (await second.Content.ReadFromJsonAsync<FsrsImportResultDto>())!;

        result.CustomSentencesImported.Should().Be(0);
        result.CustomSentencesSkipped.Should().Be(1);
        (await Saved()).Sentences.Should().ContainSingle();
    }

    [Fact]
    public async Task Import_WithoutOverwrite_KeepsTheExistingNote()
    {
        await SetMeaning(901, "backed up note");
        var export = await Export();
        await SetMeaning(901, "the note I have now");

        (await Import(export)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Saved()).Meanings.Single().Text.Should().Be("the note I have now");
    }

    [Fact]
    public async Task Import_WithOverwrite_ReplacesTheExistingNote()
    {
        await SetMeaning(901, "backed up note");
        var export = await Export();
        await SetMeaning(901, "the note I have now");

        (await Import(export, overwrite: true)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Saved()).Meanings.Single().Text.Should().Be("backed up note");
    }

    [Fact]
    public async Task Import_OfAnEmptyBackup_IsRejected()
    {
        var export = await Export();

        var response = await Import(export);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
