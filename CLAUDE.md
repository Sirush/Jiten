# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Jiten is a free, open-source platform for Japanese immersion learners. It analyses Japanese media (visual novels, subtitles, books, manga, web novels) into per-deck statistics (character count, difficulty, vocabulary and frequency lists, user coverage), and turns them into Anki decks and an in-site FSRS review system. Around that sit accounts and known-word tracking, media requests and difficulty voting, notifications, word sets, kanji, immersion plans, deck similarity, a Jiten Plus subscription tier on Stripe, and TTS and card media for review cards. See https://jiten.moe for the live platform.

The parser is only half the product. See [Feature Subsystems](#feature-subsystems) for where each surface lives.

## Repository Structure

```
/
├── Jiten.Api/            # ASP.NET Core Web API
├── Jiten.Cli/            # Command-line tool for batch processing
├── Jiten.Core/           # Core library with domain models and data access
├── Jiten.Parser/         # Japanese text parsing engine
├── Jiten.Tests/          # xUnit test suite
├── Jiten.Web/            # Nuxt 4 frontend application (Vue 3.5, PrimeVue 4, TailwindCSS 4)
├── Shared/               # Shared resources (dictionaries, ML models, config)
└── Jiten.sln             # Main .NET solution file
```

Root `PLAN_*.md` files hold feature plans, rolled up by `PLANS_INDEX.md`; `TASKS.md` is the running bug and feature list.

## Build & Run Commands

```bash
# Backend (.NET 9.0) - run from root directory
dotnet build Jiten.sln
dotnet run --project Jiten.Api/Jiten.Api.csproj          # API at https://localhost:7299
dotnet run --project Jiten.Cli/Jiten.Cli.csproj -- [options]
dotnet test Jiten.Tests/Jiten.Tests.csproj
dotnet test --filter "FullyQualifiedName~DeconjugatorTests.DeconjugationTest"  # Single test

# Frontend - run from Jiten.Web/
pnpm install && pnpm dev    # Dev server at https://localhost:3000
pnpm build                  # Production build
pnpm test                   # vitest
pnpm lint / pnpm lintfix    # Lint
```

## Solution Architecture

**Jiten.Core** - Domain models (`Deck`, `DeckWord`, `JmDictWord`, `ExampleSentence`, etc.), data access (`JitenDbContext` for deck and dictionary data, `UserDbContext` for accounts, SRS and billing), PostgreSQL + EF Core, metadata providers (Anilist, VNDB, Google Books, IGDB, TMDB). `Data/FSRS/` is the scheduler, optimiser, load balancer, replay and export DTOs; `Data/Billing/` the Jiten Plus tiers, plans and promo credits. `Services/` holds cross-cutting engines: `RoadmapEngine` (immersion plans), `DeckVectorService` (similarity), `NotificationService`, `JitenPlusTierResolver`, `CoverageCurveFitter`.

**Jiten.Parser** - Japanese text parsing engine. MorphologicalAnalyser (Sudachi native interop), Deconjugator (rule-based from `deconjugator.json`), Parser (JMDict lookup pipeline), Redis-backed caching. See [Processing Pipeline](#japanese-text-processing-pipeline) below.

**Jiten.Cli** - Batch processing CLI. Format-specific extractors (KiriKiri, BGI, YuRis, PSB, etc.), bruteforce regex extractor, text/subtitle/manga extractors. Commands for parsing, extraction, metadata download, dictionary import.

**Jiten.Api** - ASP.NET Core Web API. JWT + API Key + Google OAuth authentication. ~40 controllers under `Controllers/` (admin surfaces are `AdminController.*.cs` partials) backed by ~60 services under `Services/`. Hangfire background jobs under `Jobs/` (parsing, difficulty, stats, coverage, SRS rollups, web novel sync, Stripe reconciliation, frequency lists, roadmaps). Rate limiting with tiered access. OpenTelemetry observability. Swagger at `/swagger`. See [Feature Subsystems](#feature-subsystems) for the controller map.

**Jiten.Tests** - xUnit + FluentAssertions. Parser tests (`DeconjugatorTests`, `MorphologicalAnalyserTests`, `FormSelectionTests`, `RewriteRuleEngineTests`), SRS tests (`FsrsTests`, `FsrsOptimizerTests`, `FsrsReplayTests`, `FsrsWorkloadSimulatorTests`, `RetentionCalculatorTests`, `ReviewLogPackerTests`), and product tests (`StripeWebhookHandlingTests`, `CardMediaResolverTests`, `RoadmapEngineTests`, `WebNovelScheduleTests`). Integration tests under `Integration/` use `WebApplicationFactory` with SQLite in-memory (see [Integration Testing](#integration-testing)). This is the backend suite only; the frontend has its own vitest suite in `Jiten.Web/test/` (`pnpm test`), so a change touching both needs both.

**Jiten.Web** - Nuxt 4 frontend (Vue 3.5, TypeScript, Pinia), all code under `app/`. PrimeVue 4 components with TailwindCSS 4. API calls via `useApiFetch` composable with JWT auto-refresh. File-based routing with `auth`/`authAdmin` middleware; main route groups are `decks/`, `srs/`, `vocabulary/`, `profile/`, `requests/`, `kanji/`, `jiten-plus/`, `settings/`, `guides/` and admin-only `dashboard/`. Stores: `authStore` (JWT + Google OAuth), `jitenStore` (preferences), `srsStore` (review session), `displayStyleStore` (UI), `ankiImportStore`, `legalStore`. Nuxt auto-imports composables, components, and utils.

### Dependency Flow
- Jiten.Api → Jiten.Core, Jiten.Parser
- Jiten.Cli → Jiten.Core, Jiten.Parser
- Jiten.Parser → Jiten.Core
- Jiten.Tests → Jiten.Api, Jiten.Parser

## Feature Subsystems

Where each product surface lives. Controllers are under `Jiten.Api/Controllers/`, services under `Jiten.Api/Services/` unless noted.

| Surface | Entry points |
|---|---|
| Media decks, browsing, downloads | `MediaDeckController`, `DeckDownloadService`, `DeckImportService`, `DeckMetadataService`, `ParseJob`/`ReparseJob` |
| Vocabulary, example sentences, reader | `VocabularyController`, `ReaderController`, `ExampleSentenceQueryService`, `DeckWordResolver` |
| SRS reviews (FSRS) | `SrsController` (rate, undo, settings, optimise, vocabulary state), `StudyController` (study decks, batches, forecasts, stats), `Jiten.Core/Data/FSRS/`, `StudySessionService`, `SrsRecomputeJob`, `ReviewRollupJob` |
| Card media and TTS | `CardMediaController`, `TtsController`, `AdminController.CardMedia.cs`, `CardMedia*` services, `CardMediaStorageOptions`, `TtsService` |
| Card archive / backup | `CardArchiveService`, `CardRestoreService`, `FsrsCardArchive` |
| Jiten Plus and billing | `JitenPlusController`, `StripeController`, `AdminController.JitenPlus.cs`, `Services/Stripe/`, `JitenPlusService`, `JitenPlusTierResolver`, `JitenPlusLimitsOptions`, `Jiten.Core/Data/Billing/`, `StripeReconcileJob`, `RenewalReminderJob` |
| Immersion plans (roadmaps) | `RoadmapController`, `RoadmapEngine`, `RoadmapDataLoader`, `RoadmapJob` |
| Custom frequency lists | `CustomFrequencyListController` + `FrequencyListJob` (user lists); `FrequencyListController` serves the static site-wide Yomitan lists and is separate |
| User coverage | `CoverageComputeService`, `CoverageJourneyService`/`Builder`, `PendingCoverageQueue`, `UserCoverage`/`UserCoverageChunk` |
| Deck similarity | `DeckVectorService`, `DeckEmbedding`, `RecomputeVectorsJob` |
| Media requests | `RequestController`, `RequestActivityService`, `MediaRequest*` models |
| Difficulty voting and ranking | `DifficultyVoteController`, `DifficultyRankingController`, `DifficultyRankingSync`, `DifficultyAdjustmentJob`, `DifficultyComputationJob` |
| Word sets, kanji, derivations | `WordSetController`, `KanjiController`, `DerivationController`, `DerivationLinkCache` |
| Web novels | `AdminController.WebNovels.cs`, `Jiten.Core/Data/WebNovel/`, `WebNovelFetchJob`/`ImportJob`/`SyncSweepJob` |
| Notifications, site updates | `NotificationController`, `SiteUpdateController`, `NotificationService`, `AdminController.Notifications.cs`/`Updates.cs` |
| Accounts, auth, API keys | `AuthController`, `AccountController`, `UserController`, `ApiKeyController`, `TokenService`, `CurrentUserService` |
| Corpus research, stats, legal | `CorpusController` (Researcher tier), `StatsController`, `LegalController`, `Services/Legal/` |

## Database Architecture

**JitenDbContext (schema: jiten)** - `Decks` (media entries with stats, parent-child relationships, external links), `DeckWords` (WordId + ReadingIndex + Occurrences), `DeckRawTexts`, `ExampleSentences`, `DeckEmbeddings` (similarity), `Tags`/`DeckTags`/`DeckGenres`, `WordSets`/`WordSetMembers`, `DifficultyVotes`/`DifficultyRatings`/`DifficultyRank*`, `MediaRequest*`, `WebNovelSources`/`WebNovelChapters`, `Notifications`, `SiteUpdates`.

**JitenDbContext (schema: jmdict)** - `Words` (JMDict entries with Readings[], PartsOfSpeech[], PitchAccents[]), `Definitions` (multilingual), `Lookups` (text → WordIds), `WordFrequencies`, `WordForms`/`WordFormFrequencies`, `WordCompositions`/`WordDerivations`, `Kanjis`/`WordKanjis`.

**UserDbContext** - ASP.NET Identity, `UserCoverages`/`UserCoverageChunks`, `UserMetadatas`, `UserProfiles`, `ApiKeys`, `RefreshTokens`, `UserDeckPreferences`, `UserWordSetStates`, `UserKanjiGrids`, `UserAccomplishments`. SRS: `FsrsCards`, `FsrsReviewLogs`, `FsrsCardArchives`, `UserFsrsSettings`, `UserReviewDailies`, `UserStudyDecks`/`UserStudyDeckWords`, `UserExampleSentences`, `UserCustomMeanings`, `UserHiddenDefinitions`, `UserCardMedia`. Billing: `PromoCodes`, `UserPromoCredits`, `BillingEmailLogs`, `UserFrequencyLists`, `UserRoadmaps`.

There is no `UserKnownWord` table. A word's `KnownState` (New/Young/Mature/Blacklisted/Due/Mastered/Redundant/Suspended) is derived per request in `CurrentUserService`: an existing `FsrsCard` wins, then `UserWordSetStates`, then a covering derivation sibling (which yields `Redundant`).

**Key indexes**: PGroonga full-text search on Decks titles, WordId + ReadingIndex composites, DeckId indexes.

### EF Migrations
```bash
dotnet ef database update --project Jiten.Core --startup-project Jiten.Core
dotnet ef migrations add MigrationName --project Jiten.Core --startup-project Jiten.Core --context JitenDbContext
```

## Japanese Text Processing Pipeline

1. **Morphological Analysis** (`Jiten.Parser/MorphologicalAnalyser.cs`): Sudachi tokenises text → WordInfo objects (Text, DictionaryForm, PartOfSpeech, NormalizedForm)
2. **Deconjugation** (`Jiten.Parser/Deconjugator.cs`): Applies rules from `deconjugator.json` → possible base forms with conjugation history
3. **JMDict Lookup** (`Jiten.Parser/Parser.cs`): Queries Lookups table → matches by POS compatibility → priority scoring for ambiguous matches
4. **Result** (`Jiten.Core/Data/DeckWord.cs`): WordId, ReadingIndex, OriginalText, Conjugations, Occurrences

**Caching**: Redis-backed word cache by (Text, POS, DictionaryForm) tuples. JMDict cache populated in 10K batches. Cache failures are non-fatal. **You MUST flush Redis after parser changes** (`dotnet run --project Jiten.Cli -- --flush-redis`).

## Autonomous Parser Testing

**Test commands:**
```bash
dotnet run --project Jiten.Cli -- --parse-test "飲んだから"                    # Single input diagnostics
dotnet run --project Jiten.Cli -- --parse-test "食べている" --parse-test-output diag.json
dotnet run --project Jiten.Cli -- --run-parser-tests                          # Batch segmentation tests
dotnet run --project Jiten.Cli -- --run-parser-tests --parse-test-output failures.json
dotnet run --project Jiten.Cli -- --run-form-tests                           # WordId/ReadingIndex correctness tests
dotnet run --project Jiten.Cli -- --run-form-tests --parse-test-output failures.json
dotnet run --project Jiten.Cli -- --deconjugate-test "飲んだ"                   # Show all deconjugation forms
dotnet run --project Jiten.Cli -- --deconjugate-test "食べさせられた" --parse-test-output deconj.json
```

**Database search (for diagnostics):**
```bash
dotnet run --project Jiten.Cli -- --search-word 2084700        # By WordId
dotnet run --project Jiten.Cli -- --search-word "そうする"      # By reading
dotnet run --project Jiten.Cli -- --search-lookup "そうする"    # Lookups table
```

**Diagnostic JSON output contains**: `sudachi.tokens` (raw Sudachi analysis), `sudachi.rawOutput`, `tokenStages` (processing stages with modifications), `results` (final parsed tokens), `formScoring` (per-token scoring breakdowns showing all evaluated (word, form) candidates with component scores: WordScore, FormPriorityScore, FormFlagScore, SurfaceMatchScore, ScriptScore).

**Failure types:**
1. **OverSegmentation** - Tokens split that should be combined → check `sudachi.tokens` and `tokenStages` → fix: add to `SpecialCases2`/`SpecialCases3`
2. **UnderSegmentation** - Tokens merged that should be separate → check for `"type": "merge"` in modifications → fix: add exclusion in Combine* method or `PreprocessText()`
3. **TokenMismatch** - Content differs → usually POS misclassification or wrong dictionary form

**Key files for parser fixes:**

- `Jiten.Parser/MorphologicalAnalyser.cs`: `SpecialCases2`/`SpecialCases3` (hardcoded token combinations), `MisparsesRemove` (tokens to filter), `Combine*` methods (merging logic), `PreprocessText()` (forced splits), `RepairNTokenisation()` (ん-form fixes)
- `Jiten.Parser/Stages/MorphologicalAnalyser.RewriteRules.cs`: declarative token-rewrite table + engine. **A new single-surface lexical fix (pin a WordId, or split/merge a specific surface into fixed tokens) should be a `RewriteRule` row here, not a hand-rolled `if`-block.** The engine owns offsets, readings-from-templates, cloning, and pin/conjugation recovery. Phases: `Cleanup` (runs before `FilterMisparse`), `Late` (mora-theft position), `Early` (ProcessSpecialCases position). A rule pin is soft by default (lookup-time compound matching may absorb the token into a longer attested span); set `HardPin` when the pin is a final word decision that compound matching must not swallow. Add a new pipeline **stage** only for a pattern-general mechanism, never a single lexical pattern. Context-heavy logic (wider/dynamic window scans, dynamic readings, POS-section edits, multi-token mutation, order-dependent-on-another-block) legitimately stays as code.
- `Shared/resources/deconjugator.json`: Rule-based deconjugation (~1500 rules). Types: `stdrule`, `rewriterule`, `onlyfinalrule`, `neverfinalrule`, `contextrule`. Fields: `dec_end`/`con_end` (endings), `dec_tag`/`con_tag` (grammar tags). Search for specific endings or `"detail": "past"` etc.
- `Shared/resources/user_dic.xml`: Custom Sudachi dictionary entries. Format: `surface,leftId,rightId,cost,display,pos1,pos2,pos3,pos4,conjType,conjForm,reading,normalised,dictFormId,splitType,splitA,splitB,unused`. Regenerate after editing: `sudachi ubuild "Y:\CODE\Jiten\Shared\resources\user_dic.xml" -s "S:\Jiten\sudachi.rs\resources\system_full.dic" -o "Y:\CODE\Jiten\Shared\resources\user_dic.dic"`

**Autonomous fix workflow:**
1. `--run-parser-tests` → identify failures
2. `--parse-test "input"` → full diagnostics per failure
3. Analyse `sudachi` and `tokenStages` to identify cause
4. Apply fix: Sudachi issue → `user_dic.xml`/`PreprocessText()`; missing combination → `SpecialCases2/3`; wrong merge → Combine* method; deconjugation → `deconjugator.json`; word matching → `FindValidCompoundWordId`/`GetBestReadingIndex`
5. **Flush Redis** with `--flush-redis`
6. Re-run failing test, then full suite for regressions

## Integration Testing

`Jiten.Tests/Integration/` contains API integration tests that hit the full HTTP pipeline against SQLite in-memory databases.

**Running:**
```bash
dotnet test Jiten.Tests --filter "FullyQualifiedName~Integration"
```

**Adding a new test class:**
```csharp
public class MyTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task MyTest()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/endpoint")
            .WithUser(TestUsers.UserA)              // authenticated as UserA
            .WithJsonContent(new { key = "value" }); // JSON body
        var response = await _client.SendAsync(request);
        // .WithAdmin() for admin-only endpoints
    }
}
```

**Infrastructure (`Integration/Infrastructure/`):**
- `JitenWebApplicationFactory` — replaces Postgres with SQLite, auth with header-based `TestAuthHandler`, CDN with `StubCdnService`, removes Hangfire/Redis/hosted services
- `TestUsers` — `UserA`, `UserB`, `Admin` (GUID constants)
- `HttpClientExtensions` — `.WithUser(id)`, `.WithAdmin()`, `.WithJsonContent(obj)`
- `StubCdnService` — records uploads/deletions, returns dummy URLs

**Provider-awareness:** `JitenDbContext` and `UserDbContext` guard Postgres-specific features (`text[]`, `jsonb`, computed columns, `IsDescending` indexes) behind `Database.ProviderName?.Contains("Npgsql")` checks. When adding new Postgres-specific model config, wrap it in the same `if (isNpgsql)` guard.

**Environment guard:** `Program.cs` skips Npgsql DbContext registration, role seeding, and Hangfire middleware when `ASPNETCORE_ENVIRONMENT=Testing`. If adding new startup-time infrastructure, guard it similarly with `builder.Environment.IsEnvironment("Testing")` or `app.Environment.IsEnvironment("Testing")`.

## Shared Resources

`Shared/` is copied to output directories at build time. Contains: Sudachi config and native binaries (`sudachi_lib.dll`/`libsudachi_lib.so`), custom user dictionary (`user_dic.dic`/`user_dic.xml`), `deconjugator.json`, ONNX difficulty models (novels + shows), Anki template (`lapis.apkg`), `sharedsettings.json`.

## Configuration

Layered: `Shared/sharedsettings.json` → `appsettings.json` → `appsettings.{Environment}.json` → env vars. Required: PostgreSQL, Redis, JWT secret, SMTP, BunnyCDN, Google OAuth. See `sharedsettings.example.json`.
