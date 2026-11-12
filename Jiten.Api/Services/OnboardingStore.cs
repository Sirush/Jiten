using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jiten.Api.Helpers;
using Jiten.Api.Services.ReadingCheck;
using Jiten.Api.Services.WordCheck;
using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Jiten.Api.Services;

public sealed class OnboardingSettings
{
    public WordCheckLevel? Level { get; set; }
    public DateTime? OpenedAt { get; set; }
    public DateTime? WordCheckDoneAt { get; set; }
    public int? WordCheckWords { get; set; }
    public Guid? WordCheckBatchId { get; set; }
    public string? Path { get; set; }
    public DateTime? DismissedAt { get; set; }
    public ReadingCheckRunState? ReadingCheck { get; set; }
}

public sealed record OnboardingStateDto(
    WordCheckLevel? Level,
    DateTime? OpenedAt,
    DateTime? WordCheckDoneAt,
    int? WordCheckWords,
    Guid? WordCheckBatchId,
    ReadingCheckRunState? ReadingCheck,
    int BasicGrammarMissing,
    int KnownWords,
    bool ShouldPrompt,
    bool ShowSetupStrip);

public sealed record OnboardingUpdateRequest(WordCheckLevel? Level, string? Path, bool? Dismissed, bool? Opened);

public interface IOnboardingStore
{
    Task<OnboardingStateDto> GetStateAsync(string userId);
    Task<OnboardingStateDto> UpdateAsync(string userId, OnboardingUpdateRequest request);
    Task RecordWordCheckAsync(string userId, int words, Guid? batchId);
    Task RecordReadingCheckAsync(string userId, ReadingCheckRunState run);

    /// <summary>Forgets the word check or reading check result that <paramref name="batchId"/> seeded, so setup asks for it again.</summary>
    Task ForgetUndoneBatchAsync(string userId, Guid batchId);

    /// <summary>Forgets both check results so setup asks again; the chosen level and path stay.</summary>
    Task ForgetCheckResultsAsync(string userId);
}

public sealed class OnboardingStore(
    UserDbContext userContext,
    JitenDbContext jitenContext,
    IWordSetMemberCache wordSetCache,
    ICurrentUserService currentUserService,
    IKnownWordAmountService knownWordAmounts,
    IMemoryCache cache) : IOnboardingStore
{
    public const string BasicGrammarSlug = "particle-grammar";

    /// <summary>An account counts as set up once it knows this share of the basic grammar words and at least <see cref="SetUpKnownWords"/> words.</summary>
    private const double GrammarKnownShare = 0.9;
    public const int SetUpKnownWords = 2000;
    private static readonly TimeSpan SetUpCacheDuration = TimeSpan.FromHours(6);
    private static readonly HashSet<string> Paths = ["srs", "anki", "reader"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<OnboardingStateDto> GetStateAsync(string userId)
    {
        var json = await userContext.UserSettings.AsNoTracking().Where(s => s.UserId == userId).Select(s => s.OnboardingJson).FirstOrDefaultAsync();
        return await BuildStateAsync(userId, Parse(json));
    }

    /// <summary>Any account that isn't set up is prompted once, whatever its age; the strip ignores "Set up later" and keeps asking until the word check is done.</summary>
    private async Task<OnboardingStateDto> BuildStateAsync(string userId, OnboardingSettings settings)
    {
        var promptCandidate = settings.Level == null && settings.DismissedAt == null && settings.OpenedAt == null;
        var stripCandidate = settings.WordCheckDoneAt == null && settings.Level != WordCheckLevel.Starting
                             && (promptCandidate || settings.OpenedAt != null);
        if ((!promptCandidate && !stripCandidate) || cache.TryGetValue(SetUpCacheKey(userId), out _))
            return ToDto(settings, new SetUpFacts(0, 0, true), shouldPrompt: false, showStrip: false);

        var facts = await LoadSetUpFacts(userId);
        if (facts.SetUp) cache.Set(SetUpCacheKey(userId), true, SetUpCacheDuration);
        return ToDto(settings, facts, promptCandidate && !facts.SetUp, stripCandidate && !facts.SetUp);
    }

    public static string SetUpCacheKey(string userId) => $"onboarding-set-up:{userId}";

    private sealed record SetUpFacts(int GrammarMissing, int KnownWords, bool SetUp);

    private static OnboardingStateDto ToDto(OnboardingSettings settings, SetUpFacts facts, bool shouldPrompt, bool showStrip)
        => new(settings.Level, settings.OpenedAt, settings.WordCheckDoneAt, settings.WordCheckWords, settings.WordCheckBatchId, settings.ReadingCheck,
               facts.GrammarMissing, facts.KnownWords, shouldPrompt, showStrip);

    /// <summary>A grammar word counts as known when its form resolves to a learned state, as the vocabulary pages read it; the word total is the vocabulary page's, without derivations.</summary>
    private async Task<SetUpFacts> LoadSetUpFacts(string userId)
    {
        var setId = await jitenContext.WordSets.AsNoTracking().Where(s => s.Slug == BasicGrammarSlug).Select(s => (int?)s.SetId).FirstOrDefaultAsync();
        var grammar = setId is { } id ? await wordSetCache.GetMembersAsync(id) : FrozenSet<long>.Empty;

        var grammarMissing = 0;
        if (grammar.Count > 0)
        {
            var states = await currentUserService.GetKnownWordsState(grammar.Select(WordFormHelper.DecodeWordKey));
            grammarMissing = grammar.Count(k => !(states.TryGetValue(WordFormHelper.DecodeWordKey(k), out var tiers) && tiers.Any(KnownWordRules.IsLearned)));
        }

        var amounts = await knownWordAmounts.ComputeAsync(userId, includeDerivations: false);
        var knownWords = amounts.Young + amounts.Mature + amounts.Mastered + amounts.WordSetMastered;
        var grammarKnown = grammar.Count - grammarMissing >= grammar.Count * GrammarKnownShare;
        return new SetUpFacts(grammarMissing, knownWords, grammarKnown && knownWords >= SetUpKnownWords);
    }

    public async Task<OnboardingStateDto> UpdateAsync(string userId, OnboardingUpdateRequest request)
    {
        var saved = await Write(userId, settings =>
        {
            if (request.Level != null) settings.Level = request.Level;
            if (request.Path != null && Paths.Contains(request.Path)) settings.Path = request.Path;
            if (request.Dismissed == true) settings.DismissedAt ??= DateTime.UtcNow;
            if (request.Opened == true) settings.OpenedAt ??= DateTime.UtcNow;
        });
        return await BuildStateAsync(userId, saved);
    }

    public Task RecordWordCheckAsync(string userId, int words, Guid? batchId)
        => Write(userId, settings =>
        {
            settings.WordCheckDoneAt = DateTime.UtcNow;
            settings.WordCheckWords = words;
            settings.WordCheckBatchId = batchId;
        });

    public Task RecordReadingCheckAsync(string userId, ReadingCheckRunState run)
        => Write(userId, settings => settings.ReadingCheck = run);

    public async Task ForgetUndoneBatchAsync(string userId, Guid batchId)
    {
        var json = await userContext.UserSettings.AsNoTracking().Where(s => s.UserId == userId).Select(s => s.OnboardingJson).FirstOrDefaultAsync();
        var current = Parse(json);
        if (current.WordCheckBatchId != batchId && current.ReadingCheck?.BatchId != batchId) return;

        cache.Remove(SetUpCacheKey(userId));

        await Write(userId, settings =>
        {
            if (settings.WordCheckBatchId == batchId)
                ClearWordCheck(settings);

            if (settings.ReadingCheck?.BatchId == batchId)
                settings.ReadingCheck = null;
        });
    }

    public async Task ForgetCheckResultsAsync(string userId)
    {
        cache.Remove(SetUpCacheKey(userId));

        var json = await userContext.UserSettings.AsNoTracking().Where(s => s.UserId == userId).Select(s => s.OnboardingJson).FirstOrDefaultAsync();
        var current = Parse(json);
        if (current.WordCheckDoneAt == null && current.ReadingCheck == null) return;

        await Write(userId, settings =>
        {
            ClearWordCheck(settings);
            settings.ReadingCheck = null;
        });
    }

    private static void ClearWordCheck(OnboardingSettings settings)
    {
        settings.WordCheckDoneAt = null;
        settings.WordCheckWords = null;
        settings.WordCheckBatchId = null;
    }

    private async Task<OnboardingSettings> Write(string userId, Action<OnboardingSettings> change)
    {
        await using var transaction = await userContext.Database.BeginTransactionAsync();
        var row = await UserSettingsRows.LoadForUpdate(userContext, userId);
        var settings = Parse(row.OnboardingJson);
        change(settings);
        row.OnboardingJson = JsonSerializer.Serialize(settings, JsonOptions);
        await userContext.SaveChangesAsync();
        await transaction.CommitAsync();
        return settings;
    }

    private static OnboardingSettings Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new OnboardingSettings();
        try
        {
            return JsonSerializer.Deserialize<OnboardingSettings>(json, JsonOptions) ?? new OnboardingSettings();
        }
        catch (JsonException)
        {
            return new OnboardingSettings();
        }
    }
}
