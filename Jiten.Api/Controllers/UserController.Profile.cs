using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jiten.Api.Dtos;
using Jiten.Api.Helpers;
using Jiten.Api.Jobs;
using Jiten.Api.Services;
using Jiten.Core.Data;
using Jiten.Core.Data.Billing;
using Jiten.Core.Data.CommunityLists;
using Jiten.Core.Data.JMDict;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Swashbuckle.AspNetCore.Annotations;

namespace Jiten.Api.Controllers;

public partial class UserController
{
    private const int MaxCurrentlyReading = 6;
    private const int OverviewGenreCount = 5;

    /// <summary>Palette keys the frontend maps to colours; raw colours are never stored.</summary>
    private static readonly HashSet<string> ProfilePalette =
        ["slate", "red", "orange", "amber", "green", "teal", "cyan", "blue", "indigo", "purple", "pink", "rose"];

    private static readonly string[] MilestoneTiers = ["ema", "zeni", "ichibu", "koban", "jade", "crystal", "lacquer", "shimenawa"];

    /// <summary>One threshold per tier in <see cref="MilestoneTiers"/>, lowest first.</summary>
    private static readonly long[] CharacterMilestones = [10_000, 50_000, 250_000, 1_000_000, 3_000_000, 10_000_000, 30_000_000, 100_000_000];
    private static readonly long[] TitleMilestones = [1, 5, 10, 25, 50, 100, 250, 1_000];
    private static readonly long[] WordMilestones = [100, 500, 1_000, 3_000, 6_000, 12_000, 30_000, 60_000];

    /// <summary>Crystal sits at 2,136, the size of the jōyō kanji list.</summary>
    private static readonly long[] KanjiMilestones = [50, 200, 500, 1_000, 1_500, 2_136, 3_000, 5_000];

    private static readonly long[] StreakMilestones = [3, 7, 30, 100, 200, 365, 730, 1_500];
    private static readonly long[] StudyDayMilestones = [3, 14, 50, 100, 365, 730, 1_000, 2_000];
    private static readonly long[] ReviewMilestones = [100, 1_000, 5_000, 25_000, 50_000, 100_000, 250_000, 1_000_000];
    private static readonly long[] DifficultyVoteMilestones = [3, 10, 25, 50, 100, 250, 500, 1_000];

    private const string ReadableCatalogueCacheKey = "profile:readable-catalogue";

    [GeneratedRegex(@"^(\p{IsCJKUnifiedIdeographs}|\p{IsHiragana}|\p{IsKatakana}|[\uD840-\uD87F][\uDC00-\uDFFF])$")]
    private static partial Regex AvatarCharacterRegex();

    [HttpGet("profile")]
    [SwaggerOperation(Summary = "Get the signed-in user's profile header, including privacy settings")]
    public async Task<IResult> GetOwnProfile()
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var access = await profileAccess.ResolveByIdAsync(userId);
        return access == null ? Results.NotFound() : Results.Ok(await BuildProfileHeaderAsync(access));
    }

    [HttpGet("profile/{handle}")]
    [AllowAnonymous]
    [ProfileAccess]
    [SwaggerOperation(Summary = "Get a profile header by display name")]
    public async Task<IResult> GetProfileHeader(string handle) => Results.Ok(await BuildProfileHeaderAsync(RequestedProfile));

    [HttpPatch("profile")]
    [SwaggerOperation(Summary = "Update the signed-in user's profile and privacy settings")]
    public async Task<IResult> UpdateProfile([FromBody] UpdateProfileRequest request)
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var user = await userContext.Users.AsNoTracking()
                                      .Where(u => u.Id == userId)
                                      .Select(u => new { u.DisplayName, u.CreatedAt })
                                      .FirstAsync();
        var profile = await userContext.UserProfiles.FirstOrDefaultAsync(p => p.UserId == userId);
        if (profile == null)
        {
            profile = new UserProfile { UserId = userId };
            userContext.UserProfiles.Add(profile);
        }

        if (request.Bio != null)
        {
            var bio = UserTextSanitizer.Clean(request.Bio, multiline: true);
            if (bio?.Length > UserProfile.MaxBioLength)
                return Results.BadRequest(new { message = $"Bio can be up to {UserProfile.MaxBioLength} characters." });
            if (bio != null && DisplayNameValidator.ContainsOffensiveWord(bio))
                return Results.BadRequest(new { message = "Bio contains a word that isn't allowed." });
            profile.Bio = bio;
        }

        if (request.Links != null)
        {
            var links = request.Links.Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToList();
            if (links.Count > ProfileLinkValidator.MaxLinks)
                return Results.BadRequest(new { message = $"Add up to {ProfileLinkValidator.MaxLinks} links." });
            if (links.Select(ProfileLinkValidator.Validate).FirstOrDefault(error => error != null) is { } linkError)
                return Results.BadRequest(new { message = linkError });
            profile.LinksJson = JsonSerializer.Serialize(links);
        }

        if (request.AvatarKanji != null)
        {
            var kanji = request.AvatarKanji.Trim();
            if (kanji.Length > 0 && !AvatarCharacterRegex().IsMatch(kanji))
                return Results.BadRequest(new { message = "Pick a single kanji or kana." });
            profile.AvatarKanji = kanji.Length == 0 ? null : kanji;
        }

        if (!IsPaletteColour(request.AvatarColour) || !IsPaletteColour(request.HeaderColour))
            return Results.BadRequest(new { message = "Unknown colour." });
        if (request.AvatarColour != null)
            profile.AvatarColour = request.AvatarColour.Length == 0 ? null : request.AvatarColour;
        if (request.HeaderColour != null)
            profile.HeaderColour = request.HeaderColour.Length == 0 ? null : request.HeaderColour;

        if (request.AvatarStyle != null)
        {
            if (!UserProfile.AvatarStyles.Contains(request.AvatarStyle))
                return Results.BadRequest(new { message = "Unknown avatar style." });
            profile.AvatarStyle = request.AvatarStyle == UserProfile.DefaultAvatarStyle ? null : request.AvatarStyle;
        }

        var privacy = ProfilePrivacy.Parse(profile.PrivacyJson, profile.IsPublic, profile.IsMediaListPublic);
        if (request.Privacy != null)
        {
            privacy = request.Privacy.ApplyTo(privacy);
            var privacyJson = privacy.Serialize();
            if (privacyJson != profile.PrivacyJson)
            {
                if (privacy.Profile != ProfileVisibility.Private && user.DisplayName == null)
                    return Results.BadRequest(new { message = "Choose a display name before making your profile visible." });

                profile.PrivacyJson = privacyJson;
                profile.IsPublic = privacy.Profile == ProfileVisibility.Public;
                profile.IsMediaListPublic = privacy.CanSee(ProfileViewer.Everyone, ProfileSection.Library);
            }
        }

        await userContext.SaveChangesAsync();

        return Results.Ok(await BuildProfileHeaderAsync(new ProfileContext(userId, user.DisplayName, user.CreatedAt, ProfileViewer.Owner, privacy, profile)));
    }

    [HttpGet("profile/{handle}/overview")]
    [AllowAnonymous]
    [ProfileAccess]
    [SwaggerOperation(Summary = "Get a profile's Overview tab: snapshot, in-progress titles, pinned titles, genres, favourite words, milestones, study heatmap")]
    public async Task<IResult> GetProfileOverview(string handle)
    {
        var access = RequestedProfile;
        var userId = access.UserId;
        var dto = new ProfileOverviewDto();
        var cache = await profileCache.ReadAsync(access, ProfileCacheKind.Vocabulary, ProfileCacheKind.KanjiCount, ProfileCacheKind.Genres,
                                                 ProfileCacheKind.DifficultyVotes);

        var showPinned = access.CanSee(ProfileSection.Pinned);
        var pins = showPinned ? await LoadPinnedIdsAsync(userId) : [];
        var (statuses, hidden) = await profileAccess.GetVisibleDisplayStatusesAsync(access, pins);

        var lifetime = await LoadLifetimeTotalsAsync(access);
        if (access.Shows(ProfileField.TitlesFinished))
            dto.Snapshot.TitlesFinished = lifetime.CompletedDeckCount;
        if (access.Shows(ProfileField.Characters))
            dto.Snapshot.Characters = lifetime.CharactersRead;
        if (access.Shows(ProfileField.WordsKnown))
        {
            var vocab = await GetVocabularyStatsAsync(cache, userId);
            dto.Snapshot.WordsKnown = vocab.Young + vocab.Mature + vocab.Mastered;
        }
        if (access.Shows(ProfileField.KanjiKnown))
            dto.Snapshot.KanjiKnown = await cache.GetOrSetAsync(ProfileCacheKind.KanjiCount, () => CountKnownKanjiAsync(userId));

        var showReading = access.Shows(ProfileField.CurrentlyReading);
        var readingIds = showReading
            ? statuses.Where(kv => kv.Value.Status == DeckStatus.Ongoing)
                      .OrderByDescending(kv => kv.Value.UpdatedAt)
                      .Select(kv => kv.Key)
                      .Take(MaxCurrentlyReading)
                      .ToList()
            : [];

        var pinnedIds = pins.Where(id => !hidden.Contains(id)).ToList();

        var cardDeckIds = readingIds.Concat(pinnedIds).Distinct().ToList();
        var cards = await LoadDeckCardsAsync(cardDeckIds);
        var scores = await profileAccess.GetVisibleScoresAsync(access, cardDeckIds);
        var summaries = readingIds.Count == 0
            ? new Dictionary<int, MediaListEntrySummary>()
            : await MediaListEntryHelper.BuildSummariesAsync(userContext, jitenContext, userId, readingIds,
                                                             await jitenContext.Decks.AsNoTracking()
                                                                               .Where(d => d.ParentDeckId != null && readingIds.Contains(d.ParentDeckId.Value))
                                                                               .Select(d => d.ParentDeckId!.Value)
                                                                               .Distinct()
                                                                               .ToListAsync());

        var showStatus = access.CanSee(ProfileSection.Library);
        ProfileDeckDto? ToProfileDeck(int id)
        {
            if (!cards.TryGetValue(id, out var card)) return null;
            return new ProfileDeckDto
                   {
                       Deck = card,
                       Status = showStatus ? statuses.GetValueOrDefault(id).Status : DeckStatus.None,
                       Score = scores.TryGetValue(id, out var score) ? UserDeckPreference.ToDisplayScore(score) : null,
                       ListEntry = summaries.GetValueOrDefault(id)
                   };
        }

        if (showReading)
            dto.CurrentlyReading = readingIds.Select(ToProfileDeck).OfType<ProfileDeckDto>().ToList();
        if (showPinned)
        {
            dto.Pinned = pinnedIds.Select(ToProfileDeck).OfType<ProfileDeckDto>().ToList();
            dto.FavouriteWords = await LoadFavouriteWordsAsync(userId);
        }

        if (access.CanSee(ProfileSection.Library))
            dto.Genres = (await cache.GetOrSetAsync(ProfileCacheKind.Genres, async () =>
                         {
                             var ledger = await ReadingTally.LoadAsync(jitenContext, userContext, userId);
                             var reading = ReadingTally.CharactersByTitle(ledger, ReadingTally.CreditCompletedReading(ledger), withUnfinished: access.IsOwner);
                             var hiddenReading = await profileAccess.GetHiddenDeckIdsAsync(access, reading.Keys.ToList());
                             return await ComputeGenreSharesAsync(reading.Where(kv => !hiddenReading.Contains(kv.Key)).ToDictionary(), splitAcrossGenres: true);
                         })).Take(OverviewGenreCount).ToList();

        var study = access.CanSee(ProfileSection.StudyActivity) ? await BuildStudyActivityAsync(userId, year: null, rolling: true) : null;
        dto.Heatmap = study?.Heatmap;

        if (access.CanSee(ProfileSection.Milestones))
            dto.Milestones = await BuildMilestonesAsync(access, dto.Snapshot, cache, study);

        return Results.Ok(dto);
    }

    private async Task<List<ProfileMilestoneDto>> BuildMilestonesAsync(ProfileContext access, ProfileSnapshotDto snapshot, ProfileCacheBatch cache,
                                                                       StudyActivity? study)
    {
        var milestones = new List<ProfileMilestoneDto?>
                         {
                             snapshot.Characters is { } characters ? BuildMilestone("characters", characters, CharacterMilestones) : null,
                             snapshot.TitlesFinished is { } titles ? BuildMilestone("titles", titles, TitleMilestones) : null,
                             snapshot.WordsKnown is { } words ? BuildMilestone("words", words, WordMilestones) : null,
                             snapshot.KanjiKnown is { } kanji ? BuildMilestone("kanji", kanji, KanjiMilestones) : null,
                             study != null ? BuildMilestone("reviews", study.AllReviews, ReviewMilestones) : null,
                             study != null ? BuildMilestone("streak", study.Heatmap.LongestStreak, StreakMilestones) : null,
                             study != null ? BuildMilestone("studyDays", study.Heatmap.TotalReviewDays, StudyDayMilestones) : null,
                             access.CanSee(ProfileSection.Stats)
                                 ? BuildMilestone("votes", await cache.GetOrSetAsync(ProfileCacheKind.DifficultyVotes, () => CountDifficultyVotesAsync(access.UserId)),
                                                  DifficultyVoteMilestones)
                                 : null,
                         };
        return milestones.OfType<ProfileMilestoneDto>().ToList();
    }

    [HttpGet("profile/{handle}/stats")]
    [AllowAnonymous]
    [ProfileAccess(ProfileSection.Stats)]
    [SwaggerOperation(Summary = "Get a profile's Stats tab")]
    public async Task<IResult> GetProfileStats(string handle)
    {
        var access = RequestedProfile;
        var cache = await profileCache.ReadAsync(access, ProfileCacheKind.Stats);
        var stats = await cache.GetOrSetAsync(ProfileCacheKind.Stats, () => ComputeProfileStatsAsync(access));
        if (access.Shows(ProfileField.WordsKnown))
            stats.Readable = await GetReadableAsync(access.UserId);

        return Results.Ok(stats);
    }

    [HttpGet("profile/pinned")]
    [SwaggerOperation(Summary = "List the titles pinned to the signed-in user's profile, in order")]
    public async Task<IResult> GetPinnedDecks()
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var ids = await LoadPinnedIdsAsync(userId);
        var decks = await jitenContext.Decks.AsNoTracking()
                                      .Where(deck => ids.Contains(deck.DeckId))
                                      .Select(deck => new MediaSuggestionDto
                                                      {
                                                          DeckId = deck.DeckId, OriginalTitle = deck.OriginalTitle, RomajiTitle = deck.RomajiTitle,
                                                          EnglishTitle = deck.EnglishTitle, CoverName = deck.CoverName, MediaType = deck.MediaType
                                                      })
                                      .ToDictionaryAsync(deck => deck.DeckId);

        return Results.Ok(ids.Where(decks.ContainsKey).Select(id => decks[id]).ToList());
    }

    [HttpPut("profile/pinned")]
    [SwaggerOperation(Summary = "Replace the titles pinned to the signed-in user's profile, in order")]
    public async Task<IResult> SetPinnedDecks([FromBody] SetPinnedDecksRequest request)
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        if (request.DeckIds == null)
            return Results.BadRequest(new { message = "List the titles to pin." });

        var ids = request.DeckIds.Distinct().ToList();
        if (ids.Count > UserPinnedDeck.MaxPins)
            return Results.BadRequest(new { message = $"Pin up to {UserPinnedDeck.MaxPins} titles." });

        var displayIds = await ToDisplayDeckIdsAsync(ids);
        if (displayIds.Count != ids.Count || displayIds.Distinct().Count() != displayIds.Count)
            return Results.BadRequest(new { message = "Pin each title once, by its main entry." });

        var current = await LoadPinnedIdsAsync(userId);
        if (await AnyPrivateAsync(userId, displayIds.Except(current).ToList()))
            return PrivatePinRejected();

        await using var transaction = await userContext.Database.BeginTransactionAsync();
        await UnpinAsync(userId);
        userContext.UserPinnedDecks.AddRange(displayIds.Select((id, i) => new UserPinnedDeck { UserId = userId, DeckId = id, Position = (short)i }));
        await userContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return Results.Ok(new { deckIds = displayIds });
    }

    [HttpPost("profile/pinned/{deckId:int}")]
    [SwaggerOperation(Summary = "Pin a title to the end of the signed-in user's profile row")]
    public async Task<IResult> PinDeck(int deckId)
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var displayId = await ToDisplayDeckIdAsync(deckId);
        if (displayId == 0)
            return Results.NotFound();

        var pins = await userContext.UserPinnedDecks.AsNoTracking()
                                    .Where(p => p.UserId == userId)
                                    .OrderBy(p => p.Position)
                                    .Select(p => new { p.DeckId, p.Position })
                                    .ToListAsync();
        var pinnedIds = pins.Select(p => p.DeckId).ToList();
        if (pinnedIds.Contains(displayId))
            return Results.Ok(new { deckIds = pinnedIds });
        if (await AnyPrivateAsync(userId, [displayId]))
            return PrivatePinRejected();
        if (pins.Count >= UserPinnedDeck.MaxPins)
            return Results.Conflict(new { message = $"You already have {UserPinnedDeck.MaxPins} pinned titles.", deckIds = pinnedIds });

        var position = pins.Count == 0 ? 0 : pins[^1].Position + 1;
        userContext.UserPinnedDecks.Add(new UserPinnedDeck { UserId = userId, DeckId = displayId, Position = (short)position });
        try
        {
            await userContext.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return Results.Ok(new { deckIds = await LoadPinnedIdsAsync(userId) });
        }

        return Results.Ok(new { deckIds = pinnedIds.Append(displayId) });
    }

    [HttpDelete("profile/pinned/{deckId:int}")]
    [SwaggerOperation(Summary = "Unpin a title from the signed-in user's profile")]
    public async Task<IResult> UnpinDeck(int deckId)
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        await UnpinAsync(userId, [await ToDisplayDeckIdAsync(deckId)]);
        return Results.NoContent();
    }

    [HttpGet("profile/favourite-words")]
    [SwaggerOperation(Summary = "List the favourite words shown on the signed-in user's profile, in order")]
    public async Task<IResult> GetFavouriteWords()
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        return Results.Ok(await LoadFavouriteWordsAsync(userId));
    }

    [HttpPut("profile/favourite-words")]
    [SwaggerOperation(Summary = "Replace the favourite words shown on the signed-in user's profile, in order")]
    public async Task<IResult> SetFavouriteWords([FromBody] SetFavouriteWordsRequest request)
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        if (request.Words == null)
            return Results.BadRequest(new { message = "List the words to show." });

        var words = request.Words.Distinct().ToList();
        if (words.Count > UserFavouriteWord.MaxWords)
            return Results.BadRequest(new { message = $"Pick up to {UserFavouriteWord.MaxWords} words." });

        var wordIds = words.Select(w => w.WordId).Distinct().ToList();
        var existing = await jitenContext.WordForms.AsNoTracking()
                                         .Where(f => wordIds.Contains(f.WordId))
                                         .Select(f => new { f.WordId, f.ReadingIndex })
                                         .ToListAsync();
        if (words.Any(w => !existing.Any(e => e.WordId == w.WordId && e.ReadingIndex == w.ReadingIndex)))
            return Results.BadRequest(new { message = "Unknown word." });

        await using var transaction = await userContext.Database.BeginTransactionAsync();
        await userContext.UserFavouriteWords.Where(w => w.UserId == userId).ExecuteDeleteAsync();
        userContext.UserFavouriteWords.AddRange(words.Select((w, i) => new UserFavouriteWord
                                                                       {
                                                                           UserId = userId, WordId = w.WordId, ReadingIndex = w.ReadingIndex, Position = (short)i
                                                                       }));
        await userContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return Results.Ok(await LoadFavouriteWordsAsync(userId));
    }

    [HttpGet("deck-preferences/{deckId:int}/details")]
    [SwaggerOperation(Summary = "Get the signed-in user's score, private note, privacy and pin for a title")]
    public async Task<IResult> GetDeckDetails(int deckId)
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var displayId = await ToDisplayDeckIdAsync(deckId);
        if (displayId == 0)
            return Results.NotFound();

        var preference = await userContext.UserDeckPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId && p.DeckId == displayId);
        return Results.Ok(await BuildDeckDetailsAsync(userId, displayId, preference));
    }

    [HttpPatch("deck-preferences/{deckId:int}/details")]
    [SwaggerOperation(Summary = "Set the signed-in user's score, private note or privacy for a title; stored on the title's main entry")]
    public async Task<IResult> UpdateDeckDetails(int deckId, [FromBody] UpdateDeckDetailsRequest request)
    {
        var userId = userService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var displayId = await ToDisplayDeckIdAsync(deckId);
        if (displayId == 0)
            return Results.NotFound();

        if (request.SetScore && request.Score is < UserDeckPreference.MinScore or > UserDeckPreference.MaxScore)
            return Results.BadRequest(new { message = "Scores go from 1.0 to 10.0." });

        var note = request.Note?.Trim();
        if (note is { Length: > UserDeckPreference.MaxNoteLength })
            return Results.BadRequest(new { message = $"Notes can be up to {UserDeckPreference.MaxNoteLength} characters." });

        var preference = (await LoadPreferencesForUpdateAsync(userId, [displayId], addMissing: true))[displayId];
        if (request.SetScore)
            preference.Score = request.Score;
        if (note != null)
            preference.Note = note.Length == 0 ? null : note;
        if (request.IsPrivate is { } isPrivate)
            preference.IsPrivate = isPrivate;

        await userContext.SaveChangesAsync();

        return Results.Ok(await BuildDeckDetailsAsync(userId, displayId, preference));
    }

    private async Task<ProfileHeaderDto> BuildProfileHeaderAsync(ProfileContext access)
    {
        var profile = access.Profile;
        var avatarStyle = UserProfile.ResolveAvatarStyle(profile.AvatarStyle);
        return new ProfileHeaderDto
               {
                   DisplayName = access.DisplayName,
                   IsOwner = access.IsOwner,
                   PreviewAs = access.IsPreview ? ProfilePrivacy.Name(access.Viewer) : null,
                   IsJitenPlus = await jitenPlus.GetTierAsync(access.UserId) == JitenPlusTier.Full,
                   Bio = profile.Bio,
                   Links = access.Shows(ProfileField.Links) ? ParseLinks(profile.LinksJson) : [],
                   AvatarKanji = profile.AvatarKanji,
                   AvatarColour = profile.AvatarColour,
                   AvatarStyle = avatarStyle,
                   AvatarStrokes = avatarStyle == UserProfile.StrokeAvatarStyle ? await LoadAvatarStrokesAsync(profile.AvatarKanji, access.DisplayName) : null,
                   HeaderColour = profile.HeaderColour,
                   MemberSince = access.Shows(ProfileField.MemberSince) ? ToMemberSinceMonth(access.CreatedAt) : null,
                   Sections = Enum.GetValues<ProfileSection>().Where(access.CanSee).Select(ProfilePrivacy.Name).ToList(),
                   HasLists = await HasProfileListsAsync(access),
                   Privacy = access.IsOwner ? access.Privacy.ToNames() : null
               };
    }

    /// <summary>Visitors only ever see the join month; the exact sign-up time stays private.</summary>
    private static DateTime ToMemberSinceMonth(DateTime createdAt) =>
        new(createdAt.Year, createdAt.Month, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Mirrors the frontend's default avatar character: the chosen one, else the display name's first character.</summary>
    private async Task<List<string>?> LoadAvatarStrokesAsync(string? avatarKanji, string? displayName)
    {
        var character = avatarKanji ?? (string.IsNullOrWhiteSpace(displayName) ? null : StringInfo.GetNextTextElement(displayName.Trim()).ToUpperInvariant());
        if (character == null)
            return null;

        return await jitenContext.KanjiStrokes.AsNoTracking()
                                 .Where(k => k.Character == character)
                                 .Select(k => k.Paths)
                                 .FirstOrDefaultAsync();
    }

    private async Task<bool> HasProfileListsAsync(ProfileContext access)
    {
        var publicLists = jitenContext.CommunityLists.Where(l => l.Status == CommunityListStatus.Public);
        return await publicLists.AnyAsync(l => l.OwnerId == access.UserId)
               || (access.CanSee(ProfileSection.FavouriteLists)
                   && await jitenContext.CommunityListFavourites.AnyAsync(f => f.UserId == access.UserId && publicLists.Any(l => l.ListId == f.ListId)));
    }

    private static List<string> ParseLinks(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json)?.Where(ProfileLinkValidator.IsDisplayable).ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Null leaves the colour unchanged and an empty string clears it, so both pass.</summary>
    private static bool IsPaletteColour(string? colour) => string.IsNullOrEmpty(colour) || ProfilePalette.Contains(colour);

    /// <summary>Only set inside actions marked with <see cref="ProfileAccessAttribute"/>.</summary>
    private ProfileContext RequestedProfile => ProfileAccessAttribute.Get(HttpContext);

    private Task<ProfileVocabularyStatsDto> GetVocabularyStatsAsync(ProfileCacheBatch cache, string userId) =>
        cache.GetOrSetAsync(ProfileCacheKind.Vocabulary, async () =>
        {
            var amounts = await ComputeKnownWordAmountAsync(userId, includeDerivations: false);
            return new ProfileVocabularyStatsDto
                   {
                       Young = amounts.Young, Mature = amounts.Mature, Mastered = amounts.Mastered, WordSetMastered = amounts.WordSetMastered
                   };
        });

    private async Task<int> CountKnownKanjiAsync(string userId)
    {
        var grid = await userContext.UserKanjiGrids.AsNoTracking().FirstOrDefaultAsync(g => g.UserId == userId);
        return grid?.GetKanjiScoresOnce().Count ?? 0;
    }

    private sealed record LifetimeTotals(int CompletedDeckCount, long CharactersRead);

    /// <summary>Private titles count for everyone; reading in progress only for the owner, as counts typed on unfinished entries are private.</summary>
    private async Task<LifetimeTotals> LoadLifetimeTotalsAsync(ProfileContext access) =>
        await userContext.UserAccomplishments.AsNoTracking()
                         .Where(a => a.UserId == access.UserId && a.MediaType == null)
                         .Select(a => new LifetimeTotals(a.CompletedDeckCount, a.TotalCharacterCount + (access.IsOwner ? a.UnfinishedCharacterCount : 0)))
                         .FirstOrDefaultAsync()
        ?? new LifetimeTotals(0, 0);

    private Task<List<int>> LoadPinnedIdsAsync(string userId) =>
        userContext.UserPinnedDecks.AsNoTracking()
                   .Where(p => p.UserId == userId)
                   .OrderBy(p => p.Position)
                   .Select(p => p.DeckId)
                   .ToListAsync();

    private Task<bool> AnyPrivateAsync(string userId, List<int> displayIds) =>
        userContext.UserDeckPreferences.AnyAsync(p => p.UserId == userId && p.IsPrivate && displayIds.Contains(p.DeckId));

    private static IResult PrivatePinRejected() => Results.BadRequest(new { message = "Private titles can't be pinned." });

    /// <summary>Null <paramref name="deckIds"/> clears every pin.</summary>
    private Task<int> UnpinAsync(string userId, IReadOnlyCollection<int>? deckIds = null)
    {
        var pins = userContext.UserPinnedDecks.Where(p => p.UserId == userId);
        if (deckIds != null)
            pins = pins.Where(p => deckIds.Contains(p.DeckId));
        return pins.ExecuteDeleteAsync();
    }

    private async Task<Dictionary<int, UserDeckPreference>> LoadPreferencesForUpdateAsync(string userId, IReadOnlyCollection<int> deckIds, bool addMissing)
    {
        var preferences = await userContext.UserDeckPreferences
                                           .Where(p => p.UserId == userId && deckIds.Contains(p.DeckId))
                                           .ToDictionaryAsync(p => p.DeckId);
        if (!addMissing)
            return preferences;

        foreach (var deckId in deckIds.Where(id => !preferences.ContainsKey(id)))
        {
            var preference = new UserDeckPreference { UserId = userId, DeckId = deckId, UpdatedAt = DateTime.UtcNow };
            userContext.UserDeckPreferences.Add(preference);
            preferences[deckId] = preference;
        }

        return preferences;
    }

    /// <summary>The columns cards and the stats tab read; the difficulty row keeps its vote columns and leaves its JSON payloads behind.</summary>
    private IQueryable<Deck> CardDecks(IReadOnlyCollection<int> deckIds) =>
        jitenContext.Decks.AsNoTracking()
                    .Where(d => deckIds.Contains(d.DeckId))
                    .Select(d => new Deck
                                 {
                                     DeckId = d.DeckId, OriginalTitle = d.OriginalTitle, RomajiTitle = d.RomajiTitle,
                                     EnglishTitle = d.EnglishTitle, MediaType = d.MediaType, CoverName = d.CoverName,
                                     ParentDeckId = d.ParentDeckId, CharacterCount = d.CharacterCount, UniqueWordCount = d.UniqueWordCount,
                                     SpeechDuration = d.SpeechDuration, ReleaseDate = d.ReleaseDate, Difficulty = d.Difficulty,
                                     DifficultyOverride = d.DifficultyOverride,
                                     DeckDifficulty = d.DeckDifficulty == null
                                         ? null
                                         : new DeckDifficulty
                                           {
                                               DeckId = d.DeckId, UserAdjustment = d.DeckDifficulty.UserAdjustment,
                                               DistinctVoterCount = d.DeckDifficulty.DistinctVoterCount,
                                               AdjustmentConfidence = d.DeckDifficulty.AdjustmentConfidence
                                           }
                                 });

    private async Task<Dictionary<int, DeckCardDto>> LoadDeckCardsAsync(IReadOnlyCollection<int> deckIds) =>
        deckIds.Count == 0 ? [] : await CardDecks(deckIds).ToDictionaryAsync(d => d.DeckId, d => new DeckCardDto(d));

    private async Task<List<ProfileFavouriteWordDto>> LoadFavouriteWordsAsync(string userId)
    {
        var picks = await userContext.UserFavouriteWords.AsNoTracking()
                                     .Where(w => w.UserId == userId)
                                     .OrderBy(w => w.Position)
                                     .ToListAsync();
        if (picks.Count == 0)
            return [];

        var wordIds = picks.Select(p => p.WordId).Distinct().ToList();
        var forms = await jitenContext.WordForms.AsNoTracking()
                                      .Where(f => wordIds.Contains(f.WordId))
                                      .Select(f => new { f.WordId, f.ReadingIndex, f.Text, f.FormType })
                                      .ToListAsync();
        var definitions = await jitenContext.Definitions.AsNoTracking()
                                            .Where(d => wordIds.Contains(d.WordId) && d.IsActiveInLatestSource
                                                        && d.SenseIndex == jitenContext.Definitions
                                                                                       .Where(o => o.WordId == d.WordId && o.IsActiveInLatestSource)
                                                                                       .Min(o => o.SenseIndex))
                                            .Select(d => new { d.WordId, d.EnglishMeanings })
                                            .ToListAsync();

        return picks.Select(p =>
                    {
                        var form = forms.FirstOrDefault(f => f.WordId == p.WordId && f.ReadingIndex == p.ReadingIndex);
                        var kana = form?.FormType == JmDictFormType.KanaForm
                            ? null
                            : forms.Where(f => f.WordId == p.WordId && f.FormType == JmDictFormType.KanaForm)
                                   .OrderBy(f => f.ReadingIndex)
                                   .Select(f => f.Text)
                                   .FirstOrDefault();
                        var meaning = definitions.FirstOrDefault(d => d.WordId == p.WordId)?.EnglishMeanings.Take(2) ?? [];
                        return new ProfileFavouriteWordDto
                               {
                                   WordId = p.WordId, ReadingIndex = p.ReadingIndex, Text = form?.Text ?? "",
                                   Reading = kana, Meaning = string.Join(", ", meaning)
                               };
                    })
                    .Where(w => w.Text.Length > 0)
                    .ToList();
    }

    /// <summary>Weighted by characters read; split shares divide a title among its genres and sum to one, otherwise a share is the part of all reading in titles with that genre.</summary>
    private async Task<List<ProfileGenreShareDto>> ComputeGenreSharesAsync(IReadOnlyDictionary<int, long> charactersByTitle, bool splitAcrossGenres)
    {
        var titleIds = charactersByTitle.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList();
        if (titleIds.Count == 0)
            return [];

        var genresByTitle = (await jitenContext.DeckGenres.AsNoTracking()
                                               .Where(g => titleIds.Contains(g.DeckId) && g.Genre != Genre.AdultOnly)
                                               .Select(g => new { g.DeckId, g.Genre })
                                               .ToListAsync())
                            .GroupBy(g => g.DeckId)
                            .ToDictionary(g => g.Key, g => g.Select(r => r.Genre).Distinct().ToList());

        var total = (splitAcrossGenres ? genresByTitle.Keys.ToList() : titleIds).Sum(id => (double)charactersByTitle[id]);
        if (total == 0)
            return [];

        return genresByTitle.SelectMany(kv => kv.Value.Select(genre => (Genre: genre,
                                                                        Characters: (double)charactersByTitle[kv.Key] / (splitAcrossGenres ? kv.Value.Count : 1))))
                            .GroupBy(w => w.Genre)
                            .Select(g => new ProfileGenreShareDto
                                         {
                                             Genre = g.Key, Characters = (long)Math.Round(g.Sum(w => w.Characters)),
                                             Share = Math.Round(g.Sum(w => w.Characters) / total, 4)
                                         })
                            .OrderByDescending(g => g.Characters)
                            .ToList();
    }

    private static ProfileMilestoneDto BuildMilestone(string kind, long value, long[] thresholds)
    {
        var reached = Array.FindLastIndex(thresholds, t => value >= t);
        var next = reached + 1 < thresholds.Length ? reached + 1 : -1;
        return new ProfileMilestoneDto
               {
                   Kind = kind, Value = value,
                   Tier = reached >= 0 ? MilestoneTiers[reached] : null,
                   Threshold = reached >= 0 ? thresholds[reached] : null,
                   NextTier = next >= 0 ? MilestoneTiers[next] : null,
                   NextThreshold = next >= 0 ? thresholds[next] : null
               };
    }

    /// <summary>Comparisons the user made by hand plus per-title ratings; pairs generated from a saved ranking are left out, since one ranking yields many.</summary>
    private async Task<long> CountDifficultyVotesAsync(string userId) =>
        await jitenContext.DifficultyVotes.LongCountAsync(v => v.UserId == userId && v.IsValid && v.Source == DifficultyVoteSource.Manual)
        + await jitenContext.DifficultyRatings.LongCountAsync(r => r.UserId == userId);

    /// <summary>Leaves <see cref="ProfileStatsDto.Readable"/> out, since that is cached per user rather than per viewer.</summary>
    private async Task<ProfileStatsDto> ComputeProfileStatsAsync(ProfileContext access)
    {
        var userId = access.UserId;
        var ledger = await ReadingTally.LoadAsync(jitenContext, userContext, userId);
        var credits = ReadingTally.CreditCompletedReading(ledger);
        var reading = ReadingTally.CharactersByTitle(ledger, credits, withUnfinished: access.IsOwner);
        var hidden = await profileAccess.GetHiddenDeckIdsAsync(access, ledger.FinishedTitleIds.Union(reading.Keys).ToList());
        var finishedIds = ledger.FinishedTitleIds.Where(id => !hidden.Contains(id)).ToList();

        var decks = await CardDecks(finishedIds).ToListAsync();

        var entries = await userContext.UserMediaListEntries.AsNoTracking()
                                       .Where(e => e.UserId == userId && e.State == MediaListEntryState.Completed && e.FinishedOn != null)
                                       .Select(e => new { e.DeckId, FinishedOn = e.FinishedOn!.Value, e.CoverageAtFinish })
                                       .ToListAsync();
        var entryDeckIds = entries.Select(e => e.DeckId).Distinct().ToList();
        var entryDecks = await jitenContext.Decks.AsNoTracking()
                                           .Where(d => entryDeckIds.Contains(d.DeckId))
                                           .Select(d => new { d.DeckId, DisplayId = d.ParentDeckId ?? d.DeckId, d.CharacterCount })
                                           .ToDictionaryAsync(d => d.DeckId);
        var visibleEntries = entries.Where(e => entryDecks.TryGetValue(e.DeckId, out var d) && !hidden.Contains(d.DisplayId)).ToList();

        var lifetime = await LoadLifetimeTotalsAsync(access);
        var dto = new ProfileStatsDto
                  {
                      TitlesFinished = access.Shows(ProfileField.TitlesFinished) ? lifetime.CompletedDeckCount : null,
                      Characters = access.Shows(ProfileField.Characters) ? lifetime.CharactersRead : null
                  };

        var lastFinish = visibleEntries.GroupBy(e => entryDecks[e.DeckId].DisplayId).ToDictionary(g => g.Key, g => g.Max(e => e.FinishedOn));
        var showTitles = access.CanSee(ProfileSection.Library);
        dto.UndatedFinished = decks.Count(d => !lastFinish.ContainsKey(d.DeckId));
        dto.Ladder = decks.Where(d => lastFinish.ContainsKey(d.DeckId))
                          .Select(d => new ProfileLadderPointDto
                                       {
                                           DeckId = showTitles ? d.DeckId : null, Title = showTitles ? d.OriginalTitle : null,
                                           RomajiTitle = showTitles ? d.RomajiTitle : null, EnglishTitle = showTitles ? d.EnglishTitle : null,
                                           FinishedOn = lastFinish[d.DeckId], Difficulty = DifficultyMapper.GetAdjustedDifficulty(d)
                                       })
                          .OrderBy(p => p.FinishedOn)
                          .ToList();

        dto.CharactersByMonth = credits.Where(c => c.FinishedOn != null && !hidden.Contains(c.TitleId))
                                            .GroupBy(c => c.FinishedOn!.Value.ToString("yyyy-MM", CultureInfo.InvariantCulture))
                                            .Select(g => new ProfileMonthDto { Month = g.Key, Characters = g.Sum(c => c.Characters) })
                                            .OrderBy(m => m.Month)
                                            .ToList();

        var finishedByType = ledger.CompletedDecks.Where(d => d.ParentDeckId == null).GroupBy(d => d.MediaType).ToDictionary(g => g.Key, g => g.Count());
        var charactersByType = credits.Select(c => (c.MediaType, c.Characters))
                                      .Concat(access.IsOwner ? ledger.Unfinished.Values : [])
                                      .GroupBy(c => c.MediaType)
                                      .ToDictionary(g => g.Key, g => g.Sum(c => c.Characters));
        dto.MediaTypes = finishedByType.Keys.Union(charactersByType.Keys)
                                       .Select(type => new ProfileCountDto
                                                       {
                                                           Key = ((int)type).ToString(), Count = finishedByType.GetValueOrDefault(type),
                                                           Characters = charactersByType.GetValueOrDefault(type)
                                                       })
                                       .Where(c => c.Count > 0 || c.Characters > 0)
                                       .OrderByDescending(c => c.Characters)
                                       .ToList();

        if (showTitles)
            dto.Genres = await ComputeGenreSharesAsync(reading.Where(kv => !hidden.Contains(kv.Key)).ToDictionary(), splitAcrossGenres: false);

        dto.Difficulty = decks.GroupBy(d => Math.Clamp((int)Math.Floor(DifficultyMapper.GetAdjustedDifficulty(d)), 0, 5))
                              .Select(g => new ProfileCountDto { Key = g.Key.ToString(), Count = g.Count() })
                              .ToList();

        var finishedDeckIds = decks.Select(d => d.DeckId).ToHashSet();
        dto.CoverageAtFinish = visibleEntries.Where(e => e.CoverageAtFinish != null && finishedDeckIds.Contains(entryDecks[e.DeckId].DisplayId))
                                             .GroupBy(e => entryDecks[e.DeckId].DisplayId)
                                             .Select(g => TitleCoverageAtFinish(g.Key, g.Select(e => (e.DeckId, e.FinishedOn, e.CoverageAtFinish!.Value)),
                                                                                id => entryDecks[id].CharacterCount))
                                             .GroupBy(CoverageBucket)
                                             .Select(g => new ProfileCountDto { Key = g.Key, Count = g.Count() })
                                             .ToList();

        var scores = (await profileAccess.GetVisibleScoresAsync(access, finishedIds)).Values.ToList();
        dto.ScoredCount = scores.Count;
        if (scores.Count > 0)
        {
            dto.MeanScore = Math.Round(scores.Average(s => (decimal)s) / UserDeckPreference.ScoreScale, 2);
            dto.Scores = scores.GroupBy(s => Math.Clamp(s / UserDeckPreference.ScoreScale, 1, 10))
                               .Select(g => new ProfileCountDto { Key = g.Key.ToString(), Count = g.Count() })
                               .ToList();
        }

        dto.Lengths = decks.GroupBy(d => LengthBucket(d.CharacterCount))
                           .Select(g => new ProfileCountDto { Key = g.Key, Count = g.Count() })
                           .ToList();

        dto.ReleaseYears = decks.Where(d => d.ReleaseDate.Year > 1900)
                                .GroupBy(d => d.ReleaseDate.Year)
                                .Select(g => new ProfileCountDto { Key = g.Key.ToString(), Count = g.Count() })
                                .OrderBy(c => c.Key)
                                .ToList();

        return dto;
    }

    /// <summary>Null before the owner's coverage was first computed.</summary>
    private async Task<ProfileReadableDto?> GetReadableAsync(string userId)
    {
        var computedAt = await userContext.UserMetadatas.AsNoTracking()
                                          .Where(m => m.UserId == userId)
                                          .Select(m => m.CoverageRefreshedAt)
                                          .FirstOrDefaultAsync();
        if (computedAt is not { } stamp)
            return null;

        return await profileCache.GetOrSetAsync(ProfileCacheKind.Readable, userId, stamp.Ticks.ToString(CultureInfo.InvariantCulture),
                                                () => ComputeReadableAsync(userId, stamp));
    }

    /// <summary>Shared by every profile; a few minutes of staleness only delays a new deck joining the counts.</summary>
    private Task<List<(int DeckId, MediaType MediaType)>> LoadReadableCatalogueAsync() =>
        memoryCache.GetOrCreateOnceAsync(ReadableCatalogueCacheKey, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) },
                                         async () => (await jitenContext.Decks.AsNoTracking()
                                                                        .Where(d => d.ParentDeckId == null && d.WordCount > 0)
                                                                        .Select(d => new { d.DeckId, d.MediaType })
                                                                        .ToListAsync())
                                                     .Select(d => (d.DeckId, d.MediaType))
                                                     .ToList());

    /// <summary>Reads the stored full-catalogue coverage; a deck added since the last recompute counts as unread until the next one.</summary>
    private async Task<ProfileReadableDto> ComputeReadableAsync(string userId, DateTime computedAt)
    {
        var catalogue = await LoadReadableCatalogueAsync();
        var coverage = await UserCoverageChunkHelper.LoadAllBasisPointsAsync(userContext, userId);
        var thresholds = SiteStatsJob.CoverageLevels.Select(level => level * 100).ToArray();

        ProfileReadableTypeDto Count(MediaType? mediaType, IEnumerable<int> deckIds)
        {
            var row = new ProfileReadableTypeDto
                      {
                          MediaType = mediaType, Mature = [..new int[thresholds.Length]], WithYoung = [..new int[thresholds.Length]]
                      };
            foreach (var deckId in deckIds)
            {
                var (mature, young) = coverage(deckId);
                var withYoung = Math.Min(10_000, mature + young);
                row.Titles++;
                for (var i = 0; i < thresholds.Length; i++)
                {
                    if (mature >= thresholds[i]) row.Mature[i]++;
                    if (withYoung >= thresholds[i]) row.WithYoung[i]++;
                }
            }

            return row;
        }

        var listed = catalogue.Where(d => MediaTypes.IsListed(d.MediaType)).ToList();
        return new ProfileReadableDto
               {
                   ComputedAt = computedAt,
                   Levels = [..SiteStatsJob.CoverageLevels],
                   MediaTypes = listed.GroupBy(d => d.MediaType)
                                      .OrderBy(g => g.Key)
                                      .Select(g => Count(g.Key, g.Select(d => d.DeckId)))
                                      .Prepend(Count(null, listed.Select(d => d.DeckId)))
                                      .ToList()
               };
    }

    private static string LengthBucket(int characters) => characters switch
    {
        < 50_000 => "<50k",
        < 100_000 => "50k",
        < 250_000 => "100k",
        < 500_000 => "250k",
        < 1_000_000 => "500k",
        _ => "1M"
    };

    /// <summary>The title's own latest finish wins; otherwise each volume's latest finish, weighted by its length.</summary>
    private static float TitleCoverageAtFinish(int titleId, IEnumerable<(int DeckId, DateOnly FinishedOn, float Coverage)> finishes,
                                               Func<int, int> characterCount)
    {
        var latest = finishes.GroupBy(f => f.DeckId).Select(g => g.MaxBy(f => f.FinishedOn)).ToList();
        var own = latest.FirstOrDefault(f => f.DeckId == titleId);
        if (own.DeckId == titleId)
            return own.Coverage;

        var characters = latest.Sum(f => (double)characterCount(f.DeckId));
        return characters > 0
            ? (float)(latest.Sum(f => f.Coverage * (double)characterCount(f.DeckId)) / characters)
            : latest.Average(f => f.Coverage);
    }

    /// <summary>Coverage is stored as a percentage; buckets are 5 points wide from 70.</summary>
    private static string CoverageBucket(float coverage) =>
        coverage < 70 ? "<70" : (Math.Min(95, (int)Math.Floor(coverage / 5) * 5)).ToString();

    /// <summary>0 when the deck does not exist.</summary>
    private async Task<int> ToDisplayDeckIdAsync(int deckId) => (await ToDisplayDeckIdsAsync([deckId])).FirstOrDefault();

    private async Task<List<int>> ToDisplayDeckIdsAsync(List<int> deckIds)
    {
        var decks = await jitenContext.Decks.AsNoTracking()
                                      .Where(d => deckIds.Contains(d.DeckId))
                                      .Select(d => new { d.DeckId, DisplayId = d.ParentDeckId ?? d.DeckId })
                                      .ToDictionaryAsync(d => d.DeckId, d => d.DisplayId);
        return deckIds.Where(decks.ContainsKey).Select(id => decks[id]).ToList();
    }

    private async Task<DeckDetailsDto> BuildDeckDetailsAsync(string userId, int displayId, UserDeckPreference? preference) =>
        DeckDetailsDto.From(displayId, preference, await userContext.UserPinnedDecks.AnyAsync(p => p.UserId == userId && p.DeckId == displayId));
}
