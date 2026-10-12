# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Jiten (https://jiten.moe) analyses Japanese media (visual novels, subtitles, books, manga, web novels) into per-deck statistics, vocabulary and frequency lists and user coverage, then turns them into Anki decks and an in-site FSRS review system. Around that sit accounts and known-word tracking, media requests, difficulty voting, notifications, word sets, kanji, immersion plans, deck similarity, the Jiten Plus subscription (Stripe), and TTS and card media. The parser is only half the product.

| Project | Role |
|---|---|
| `Jiten.Api/` | ASP.NET Core Web API |
| `Jiten.Cli/` | Batch CLI: format-specific extractors, parsing, metadata download, dictionary import |
| `Jiten.Core/` | Domain models, EF Core data access (PostgreSQL), metadata providers |
| `Jiten.Parser/` | Japanese text parsing engine |
| `Jiten.Tests/` | xUnit + FluentAssertions, backend only |
| `Jiten.Web/` | Nuxt 4 frontend (Vue 3.5, PrimeVue 4, TailwindCSS 4) |
| `Shared/` | Dictionaries, ML models, Sudachi binaries, config; copied to build output |

Dependencies: Api and Cli use Core and Parser; Parser uses Core; Tests use Api and Parser.

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

# EF migrations
dotnet ef database update --project Jiten.Core --startup-project Jiten.Core
dotnet ef migrations add MigrationName --project Jiten.Core --startup-project Jiten.Core --context JitenDbContext
```

The frontend has its own vitest suite in `Jiten.Web/test/`; a change touching both sides needs both suites.

## Architecture Notes

- **Core:** `JitenDbContext` holds deck data (schema `jiten`) and dictionary data (schema `jmdict`); `UserDbContext` holds accounts, SRS and billing. `Data/FSRS/` is the scheduler, optimiser, load balancer and replay; `Data/Billing/` the Jiten Plus tiers. Cross-cutting engines live in `Services/`.
- **Api:** JWT + API key + Google OAuth. Admin surfaces are `AdminController.*.cs` partials. Hangfire jobs under `Jobs/`. Tiered rate limiting, OpenTelemetry, Swagger at `/swagger`.
- **Web:** all code under `app/`. API calls go through `useApiFetch` (JWT auto-refresh). `auth`/`authAdmin` route middleware. Nuxt auto-imports composables, components and utils.
- **Known state:** there is no `UserKnownWord` table. `KnownState` is derived per request in `CurrentUserService`: an existing `FsrsCard` wins, then `UserWordSetStates`, then a covering derivation sibling (which yields `Redundant`).

## Feature Subsystems

Controllers are under `Jiten.Api/Controllers/`, services under `Jiten.Api/Services/` unless noted.

| Surface | Entry points |
|---|---|
| Media decks, browsing, downloads | `MediaDeckController`, `DeckDownloadService`, `DeckImportService`, `DeckMetadataService`, `ParseJob`/`ReparseJob` |
| Vocabulary, example sentences, reader | `VocabularyController`, `ReaderController`, `ExampleSentenceQueryService`, `DeckWordResolver` |
| SRS reviews (FSRS) | `SrsController`, `StudyController`, `Jiten.Core/Data/FSRS/`, `StudySessionService`, `SrsRecomputeJob`, `ReviewRollupJob` |
| Card media and TTS | `CardMediaController`, `TtsController`, `AdminController.CardMedia.cs`, `CardMedia*` services, `TtsService` |
| Card archive / backup | `CardArchiveService`, `CardRestoreService`, `FsrsCardArchive` |
| Jiten Plus and billing | `JitenPlusController`, `StripeController`, `AdminController.JitenPlus.cs`, `Services/Stripe/`, `JitenPlusService`, `JitenPlusTierResolver`, `Jiten.Core/Data/Billing/`, `StripeReconcileJob`, `RenewalReminderJob` |
| Immersion plans (roadmaps) | `RoadmapController`, `RoadmapEngine`, `RoadmapDataLoader`, `RoadmapJob` |
| Custom frequency lists | `CustomFrequencyListController` + `FrequencyListJob` (user lists); `FrequencyListController` serves the static site-wide Yomitan lists |
| User coverage | `CoverageComputeService`, `CoverageJourneyService`/`Builder`, `PendingCoverageQueue`, `UserCoverage`/`UserCoverageChunk` |
| Deck similarity | `DeckVectorService`, `DeckEmbedding`, `RecomputeVectorsJob` |
| Media requests | `RequestController`, `RequestActivityService`, `MediaRequest*` models |
| Difficulty voting and ranking | `DifficultyVoteController`, `DifficultyRankingController`, `DifficultyRankingSync`, `DifficultyAdjustmentJob`, `DifficultyComputationJob` |
| Word sets, kanji, derivations | `WordSetController`, `KanjiController`, `DerivationController`, `DerivationLinkCache` |
| Franchises and series | `FranchiseController`, `MediaGroupController`, `AdminController.Franchise.cs`/`Series.cs`, `SeriesService` |
| Web novels | `AdminController.WebNovels.cs`, `Jiten.Core/Data/WebNovel/`, `WebNovelFetchJob`/`ImportJob`/`SyncSweepJob` |
| Notifications, site updates | `NotificationController`, `SiteUpdateController`, `NotificationService`, `AdminController.Notifications.cs`/`Updates.cs` |
| Accounts, auth, API keys | `AuthController`, `AccountController`, `UserController`, `ApiKeyController`, `TokenService`, `CurrentUserService` |
| Corpus research, stats, legal | `CorpusController` (Researcher tier), `StatsController`, `LegalController`, `Services/Legal/` |

## Parser

Sudachi tokenises (`MorphologicalAnalyser`), `Deconjugator` applies `Shared/resources/deconjugator.json`, then `Parser.cs` matches against JMDict lookups. Results are cached in Redis.

- **Flush Redis after any parser change:** `dotnet run --project Jiten.Cli -- --flush-redis`.
- A new single-surface lexical fix is a `RewriteRule` row in `Jiten.Parser/Stages/MorphologicalAnalyser.RewriteRules.cs`, not a hand-rolled `if`-block. Add a pipeline stage only for a pattern-general mechanism.
- Before parser work, read `Jiten.Parser/PARSER_TESTING.md`: CLI diagnostics, failure types, key files, the fix workflow and the user dictionary rebuild.

## Integration Tests

`dotnet test Jiten.Tests --filter "FullyQualifiedName~Integration"` runs the full HTTP pipeline against SQLite in-memory. Read `Jiten.Tests/Integration/README.md` before adding a test class.

- Postgres-only model config (`text[]`, `jsonb`, computed columns, `IsDescending` indexes) goes behind the `isNpgsql` guard in both DbContexts.
- New startup-time infrastructure must be skipped when the environment is `Testing`, as `Program.cs` does for Npgsql registration, role seeding and Hangfire.

## Configuration

Layered: `Shared/sharedsettings.json` → `appsettings.json` → `appsettings.{Environment}.json` → env vars. Required: PostgreSQL, Redis, JWT secret, SMTP, BunnyCDN, Google OAuth. See `sharedsettings.example.json`.
