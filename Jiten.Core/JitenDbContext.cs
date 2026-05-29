using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Microsoft.Extensions.Configuration;

namespace Jiten.Core;

using Microsoft.EntityFrameworkCore;

public class JitenDbContext : DbContext
{
    public DbContextOptions<JitenDbContext>? DbOptions { get; set; }

    public DbSet<Deck> Decks { get; set; }
    public DbSet<DeckWord> DeckWords { get; set; }
    public DbSet<DeckRawText> DeckRawTexts { get; set; }
    public DbSet<DeckTitle> DeckTitles { get; set; }
    public DbSet<DeckStats> DeckStats { get; set; }
    public DbSet<DeckDifficulty> DeckDifficulties { get; set; }
    public DbSet<DeckDictionaryEntry> DeckDictionaryEntries { get; set; }

    public DbSet<JmDictWord> JMDictWords { get; set; }
    public DbSet<JmDictWordFrequency> JmDictWordFrequencies { get; set; }
    public DbSet<JmDictDefinition> Definitions { get; set; }
    public DbSet<JmDictLookup> Lookups { get; set; }
    public DbSet<JmDictWordForm> WordForms { get; set; }
    public DbSet<JmDictWordFormFrequency> WordFormFrequencies { get; set; }
    public DbSet<Kanji> Kanjis { get; set; }
    public DbSet<WordKanji> WordKanjis { get; set; }
    public DbSet<KanjiReadingWord> KanjiReadingWords { get; set; }
    public DbSet<JmDictWordComposition> WordCompositions { get; set; }
    
    public DbSet<ExampleSentence> ExampleSentences { get; set; }
    public DbSet<ExampleSentenceWord> ExampleSentenceWords { get; set; }

    public DbSet<Tag> Tags { get; set; }
    public DbSet<DeckGenre> DeckGenres { get; set; }
    public DbSet<DeckTag> DeckTags { get; set; }

    public DbSet<ExternalGenreMapping> ExternalGenreMappings { get; set; }
    public DbSet<ExternalTagMapping> ExternalTagMappings { get; set; }

    public DbSet<DeckRelationship> DeckRelationships { get; set; }

    public DbSet<WordSet> WordSets { get; set; }
    public DbSet<WordSetMember> WordSetMembers { get; set; }

    public DbSet<DifficultyVote> DifficultyVotes { get; set; }
    public DbSet<DifficultyRating> DifficultyRatings { get; set; }
    public DbSet<SkippedComparison> SkippedComparisons { get; set; }
    public DbSet<BlacklistedComparisonDeck> BlacklistedComparisonDecks { get; set; }
    public DbSet<DifficultyRankGroup> DifficultyRankGroups { get; set; }
    public DbSet<DifficultyRankItem> DifficultyRankItems { get; set; }

    public DbSet<MediaRequest> MediaRequests { get; set; }
    public DbSet<MediaRequestUpvote> MediaRequestUpvotes { get; set; }
    public DbSet<MediaRequestSubscription> MediaRequestSubscriptions { get; set; }
    public DbSet<MediaRequestComment> MediaRequestComments { get; set; }
    public DbSet<MediaRequestUpload> MediaRequestUploads { get; set; }
    public DbSet<RequestActivityLog> RequestActivityLogs { get; set; }
    public DbSet<Notification> Notifications { get; set; }

    public JitenDbContext()
    {
    }

    public JitenDbContext(DbContextOptions<JitenDbContext> options) : base(options)
    {
        DbOptions = options;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var isNpgsql = Database.ProviderName?.Contains("Npgsql") == true;

        if (isNpgsql)
        {
            modelBuilder.HasPostgresExtension("fuzzystrmatch");
            modelBuilder.HasDefaultSchema("jiten");
        }

        modelBuilder.Entity<Deck>(entity =>
        {
            entity.Property(d => d.DeckId)
                  .ValueGeneratedOnAdd();

            entity.Property(d => d.ParentDeckId)
                  .HasDefaultValue(null);

            entity.Property(d => d.OriginalTitle)
                  .HasMaxLength(200);

            entity.Property(d => d.RomajiTitle)
                  .HasMaxLength(200);

            entity.Property(d => d.EnglishTitle)
                  .HasMaxLength(200);

            entity.HasMany(d => d.Links)
                  .WithOne(l => l.Deck)
                  .HasForeignKey(l => l.DeckId);

            entity.HasIndex(d => d.OriginalTitle).HasDatabaseName("IX_OriginalTitle");
            entity.HasIndex(d => d.RomajiTitle).HasDatabaseName("IX_RomajiTitle");
            entity.HasIndex(d => d.EnglishTitle).HasDatabaseName("IX_EnglishTitle");
            entity.HasIndex(d => d.MediaType).HasDatabaseName("IX_MediaType");
            entity.HasIndex(d => d.CharacterCount).HasDatabaseName("IX_CharacterCount");
            entity.HasIndex(d => d.ReleaseDate).HasDatabaseName("IX_ReleaseDate");
            entity.HasIndex(d => d.UniqueKanjiCount).HasDatabaseName("IX_UniqueKanjiCount");
            entity.HasIndex(d => d.Difficulty).HasDatabaseName("IX_Difficulty");
            entity.HasIndex(d => d.ExternalRating).HasDatabaseName("IX_ExternalRating");
            entity.HasIndex(d => new { d.ParentDeckId, d.MediaType }).HasDatabaseName("IX_ParentDeckId_MediaType");

            entity.HasOne(d => d.ParentDeck)
                  .WithMany(p => p.Children)
                  .HasForeignKey(d => d.ParentDeckId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasMany(d => d.DictionaryEntries)
                  .WithOne(e => e.Deck)
                  .HasForeignKey(e => e.DeckId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DeckDictionaryEntry>(entity =>
        {
            entity.HasKey(e => e.DeckDictionaryEntryId);
            entity.Property(e => e.Surface).IsRequired().HasMaxLength(100);
            entity.HasIndex(e => new { e.DeckId, e.Surface }).IsUnique()
                  .HasDatabaseName("IX_DeckDictionaryEntry_DeckId_Surface");
        });

        modelBuilder.Entity<DeckTitle>(entity =>
        {
              entity.HasKey(dt => dt.DeckTitleId);
              entity.Property(dt => dt.Title).IsRequired().HasMaxLength(200);
              entity.Property(dt => dt.TitleType).IsRequired();

              if (isNpgsql)
              {
                  entity.Property(dt => dt.TitleNoSpaces)
                        .HasComputedColumnSql("REPLACE(\"Title\", ' ', '')", stored: true);
              }
              else
              {
                  entity.Ignore(dt => dt.TitleNoSpaces);
              }

              entity.HasOne(dt => dt.Deck)
                    .WithMany(d => d.Titles)
                    .HasForeignKey(dt => dt.DeckId)
                    .OnDelete(DeleteBehavior.Cascade);

              entity.HasIndex(dt => dt.Title).HasDatabaseName("IX_DeckTitles_Title");
              entity.HasIndex(dt => new { dt.DeckId, dt.TitleType }).HasDatabaseName("IX_DeckTitles_DeckId_TitleType");
        });

        modelBuilder.Entity<DeckWord>(entity =>
        {
            entity.Property(d => d.DeckWordId)
                  .ValueGeneratedOnAdd();

            entity.HasKey(dw => new { Id = dw.DeckWordId, });

            entity.HasIndex(dw => new { dw.WordId, dw.ReadingIndex })
                  .HasDatabaseName("IX_DeckWords_WordId_ReadingIndex_IncDeckIdOcc");

            entity.HasIndex(dw => new { dw.DeckId, dw.WordId, dw.ReadingIndex })
                  .IsUnique()
                  .HasDatabaseName("IX_DeckWords_DeckId_WordId_ReadingIndex");

            entity.HasIndex(dw => dw.DeckId)
                  .HasDatabaseName("IX_DeckWords_DeckId_IncWordIdReadingIndexOcc");

            entity.HasOne(dw => dw.Deck)
                  .WithMany(d => d.DeckWords)
                  .HasForeignKey(dw => dw.DeckId);
        });

        modelBuilder.Entity<DeckRawText>(entity =>
        {
            entity.HasKey(drt => drt.DeckId);

            entity.HasIndex(dw => dw.DeckId)
                  .HasDatabaseName("IX_DeckRawText_DeckId");

            entity.HasOne(drt => drt.Deck)
                  .WithOne(d => d.RawText)
                  .HasForeignKey<DeckRawText>(drt => drt.DeckId);
        });

        modelBuilder.Entity<DeckStats>(entity =>
        {
            entity.ToTable("DeckStats", "jiten");
            entity.HasKey(ds => ds.DeckId);

            entity.HasOne(ds => ds.Deck)
                  .WithOne(d => d.DeckStats)
                  .HasForeignKey<DeckStats>(ds => ds.DeckId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(ds => ds.ComputedAt)
                  .HasDatabaseName("IX_DeckStats_ComputedAt");

            entity.Property(ds => ds.CoverageCurve)
                  .HasMaxLength(100);

            entity.Ignore(ds => ds.ParameterA);
            entity.Ignore(ds => ds.ParameterB);
            entity.Ignore(ds => ds.ParameterC);
            entity.Ignore(ds => ds.RSquared);
            entity.Ignore(ds => ds.RMSE);
            entity.Ignore(ds => ds.TotalUniqueWords);
        });

        modelBuilder.Entity<DeckDifficulty>(entity =>
        {
            entity.ToTable("DeckDifficulty", "jiten");
            entity.HasKey(dd => dd.DeckId);

            entity.Property(dd => dd.Difficulty)
                  .HasPrecision(4, 2)
                  .IsRequired();

            entity.Property(dd => dd.Peak)
                  .HasPrecision(4, 2)
                  .IsRequired();

            var decilesBuilder = entity.Property(dd => dd.DecilesJson).IsRequired();
            var progressionBuilder = entity.Property(dd => dd.ProgressionJson).IsRequired();
            if (isNpgsql)
            {
                decilesBuilder.HasColumnType("jsonb");
                progressionBuilder.HasColumnType("jsonb");
            }

            entity.Property(dd => dd.LastUpdated)
                  .IsRequired();

            entity.Property(dd => dd.EasierVoteCount).HasDefaultValue(0);
            entity.Property(dd => dd.HarderVoteCount).HasDefaultValue(0);
            entity.Property(dd => dd.DistinctVoterCount).HasDefaultValue(0);
            entity.Property(dd => dd.UserAdjustment).HasPrecision(4, 2).HasDefaultValue(0m);
            entity.Property(dd => dd.NEffective).HasPrecision(6, 2).HasDefaultValue(0m);

            entity.HasOne(dd => dd.Deck)
                  .WithOne(d => d.DeckDifficulty)
                  .HasForeignKey<DeckDifficulty>(dd => dd.DeckId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.Ignore(dd => dd.Deciles);
            entity.Ignore(dd => dd.Progression);
        });

        modelBuilder.Entity<Link>(entity =>
        {
            entity.ToTable("Links", "jiten");
            entity.HasKey(l => l.LinkId);
            entity.Property(l => l.Url).IsRequired();
            entity.Property(l => l.LinkType).IsRequired();
        });

        modelBuilder.Entity<JmDictWord>(entity =>
        {
            entity.ToTable("Words", "jmdict");
            entity.HasKey(e => e.WordId);
            entity.Property(e => e.WordId).ValueGeneratedNever();
            entity.HasMany(e => e.Definitions)
                  .WithOne()
                  .HasForeignKey(d => d.WordId);
            entity.HasMany(e => e.Lookups)
                  .WithOne()
                  .HasForeignKey(l => l.WordId);
            entity.HasMany(e => e.Forms)
                  .WithOne()
                  .HasForeignKey(f => f.WordId)
                  .OnDelete(DeleteBehavior.Cascade);

            if (isNpgsql)
            {
                entity.Property(e => e.PartsOfSpeech).HasColumnType("text[]");
                entity.Property(e => e.PitchAccents).HasColumnType("int[]").IsRequired(false);
            }
            else
            {
                entity.Property(e => e.PitchAccents).IsRequired(false);
            }

            entity.Property(e => e.Origin).HasColumnType("int");
        });

        modelBuilder.Entity<JmDictDefinition>(entity =>
        {
            entity.ToTable("Definitions", "jmdict");
            entity.HasKey(e => e.DefinitionId);
            entity.Property(e => e.DefinitionId).ValueGeneratedOnAdd();
            entity.Property(e => e.WordId).IsRequired();

            if (isNpgsql)
            {
                entity.Property(e => e.PartsOfSpeech).HasColumnType("text[]");
                entity.Property(e => e.EnglishMeanings).HasColumnType("text[]");
                entity.Property(e => e.DutchMeanings).HasColumnType("text[]");
                entity.Property(e => e.FrenchMeanings).HasColumnType("text[]");
                entity.Property(e => e.GermanMeanings).HasColumnType("text[]");
                entity.Property(e => e.SpanishMeanings).HasColumnType("text[]");
                entity.Property(e => e.HungarianMeanings).HasColumnType("text[]");
                entity.Property(e => e.RussianMeanings).HasColumnType("text[]");
                entity.Property(e => e.SlovenianMeanings).HasColumnType("text[]");
                entity.Property(e => e.Pos).HasColumnType("text[]").HasDefaultValueSql("'{}'");
                entity.Property(e => e.Misc).HasColumnType("text[]").HasDefaultValueSql("'{}'");
                entity.Property(e => e.Field).HasColumnType("text[]").HasDefaultValueSql("'{}'");
                entity.Property(e => e.Dial).HasColumnType("text[]").HasDefaultValueSql("'{}'");
                entity.Property(e => e.RestrictedToReadingIndices).HasColumnType("smallint[]").IsRequired(false);
            }
            else
            {
                entity.Property(e => e.RestrictedToReadingIndices).IsRequired(false);
            }

            entity.Property(e => e.SenseIndex).HasDefaultValue(0);
            entity.Property(e => e.IsActiveInLatestSource).HasDefaultValue(true);

            entity.HasIndex(e => new { e.WordId, e.SenseIndex })
                  .HasDatabaseName("IX_Definitions_WordId_SenseIndex");
        });

        modelBuilder.Entity<JmDictLookup>(entity =>
        {
            entity.ToTable("Lookups", "jmdict");
            entity.HasKey(e => new { EntrySequenceId = e.WordId, e.LookupKey });
            entity.Property(e => e.WordId).IsRequired();
            entity.Property(e => e.LookupKey).IsRequired();
        });

        modelBuilder.Entity<JmDictWordFrequency>(entity =>
        {
            entity.ToTable("WordFrequencies", "jmdict");
            entity.HasKey(e => e.WordId);
            entity.HasOne<JmDictWord>()
                  .WithMany()
                  .HasForeignKey(f => f.WordId);
        });

        modelBuilder.Entity<JmDictWordForm>(entity =>
        {
            entity.ToTable("WordForms", "jmdict");
            entity.HasKey(e => new { e.WordId, e.ReadingIndex });

            entity.Property(e => e.FormType).HasColumnType("smallint");

            if (isNpgsql)
            {
                entity.Property(e => e.Priorities).HasColumnType("text[]").IsRequired(false);
                entity.Property(e => e.InfoTags).HasColumnType("text[]").IsRequired(false);
            }
            else
            {
                entity.Property(e => e.Priorities).IsRequired(false);
                entity.Property(e => e.InfoTags).IsRequired(false);
            }

            entity.HasIndex(e => new { e.WordId, e.FormType, e.Text })
                  .IsUnique()
                  .HasDatabaseName("IX_WordForms_WordId_FormType_Text");

            entity.HasIndex(e => e.WordId)
                  .HasDatabaseName("IX_WordForms_WordId");
        });

        modelBuilder.Entity<JmDictWordFormFrequency>(entity =>
        {
            entity.ToTable("WordFormFrequencies", "jmdict");
            entity.HasKey(e => new { e.WordId, e.ReadingIndex });

            entity.Property(e => e.FrequencyRank)
                  .HasDefaultValue(0);
            entity.Property(e => e.FrequencyPercentage)
                  .HasDefaultValue(0.0);
            entity.Property(e => e.ObservedFrequency)
                  .HasDefaultValue(0.0);
            entity.Property(e => e.UsedInMediaAmount)
                  .HasDefaultValue(0);

            entity.HasIndex(e => e.FrequencyRank)
                  .HasDatabaseName("IX_WordFormFrequencies_FrequencyRank");
        });

        modelBuilder.Entity<Kanji>(entity =>
        {
            entity.ToTable("Kanji", "jmdict");
            entity.HasKey(e => e.Character);
            entity.Property(e => e.Character)
                  .HasColumnType("text")
                  .ValueGeneratedNever()
                  .IsRequired();

            if (isNpgsql)
            {
                entity.Property(e => e.OnReadings).HasColumnType("text[]");
                entity.Property(e => e.KunReadings).HasColumnType("text[]");
                entity.Property(e => e.Meanings).HasColumnType("text[]");
            }

            entity.Property(e => e.StrokeCount)
                  .IsRequired();

            entity.HasIndex(e => e.FrequencyRank)
                  .HasDatabaseName("IX_Kanji_FrequencyRank");

            entity.HasIndex(e => e.JlptLevel)
                  .HasDatabaseName("IX_Kanji_JlptLevel");

            entity.HasIndex(e => e.StrokeCount)
                  .HasDatabaseName("IX_Kanji_StrokeCount");
        });

        modelBuilder.Entity<WordKanji>(entity =>
        {
            entity.ToTable("WordKanji", "jmdict");
            entity.HasKey(e => new { e.WordId, e.ReadingIndex, e.KanjiCharacter, e.Position });

            entity.Property(e => e.KanjiCharacter)
                  .HasColumnType("text")
                  .IsRequired();

            entity.HasIndex(e => e.KanjiCharacter)
                  .HasDatabaseName("IX_WordKanji_KanjiCharacter");

            entity.HasIndex(e => new { e.WordId, e.ReadingIndex })
                  .HasDatabaseName("IX_WordKanji_WordId_ReadingIndex");

            entity.HasOne(e => e.Word)
                  .WithMany()
                  .HasForeignKey(e => e.WordId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Kanji)
                  .WithMany(k => k.WordKanjis)
                  .HasForeignKey(e => e.KanjiCharacter)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<KanjiReadingWord>(entity =>
        {
            entity.ToTable("KanjiReadingWords", "jmdict");
            entity.HasKey(e => new { e.KanjiCharacter, e.Reading, e.WordId, e.ReadingIndex });

            entity.Property(e => e.KanjiCharacter).HasColumnType("text").IsRequired();
            entity.Property(e => e.Reading).HasColumnType("text").IsRequired();

            entity.HasIndex(e => e.WordId)
                  .HasDatabaseName("IX_KanjiReadingWords_WordId");

            entity.HasIndex(e => new { e.KanjiCharacter, e.Reading })
                  .HasDatabaseName("IX_KanjiReadingWords_KanjiCharacter_Reading");
        });

        modelBuilder.Entity<JmDictWordComposition>(entity =>
        {
            entity.ToTable("WordCompositions", "jmdict");
            entity.HasKey(e => new { e.WordId, e.ReadingIndex, e.Position });

            entity.Property(e => e.ComponentSurface)
                  .HasColumnType("text")
                  .IsRequired();

            entity.HasIndex(e => new { e.WordId, e.ReadingIndex })
                  .HasDatabaseName("IX_WordComposition_WordId_ReadingIndex");

            entity.HasIndex(e => e.ComponentWordId)
                  .HasDatabaseName("IX_WordComposition_ComponentWordId");

            entity.HasOne(e => e.Word)
                  .WithMany()
                  .HasForeignKey(e => e.WordId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Component)
                  .WithMany()
                  .HasForeignKey(e => e.ComponentWordId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ExampleSentence>(entity =>
        {
            entity.ToTable("ExampleSentences", "jiten");
            entity.HasKey(e => e.SentenceId);
            entity.Property(e => e.SentenceId).ValueGeneratedOnAdd();
            entity.Property(e => e.Text).IsRequired();
            
            entity.HasIndex(e => e.DeckId).HasDatabaseName("IX_ExampleSentence_DeckId");
            entity.HasIndex(e => new { e.DeckId, e.Difficulty }).HasDatabaseName("IX_ExampleSentence_DeckId_Difficulty");
            
            entity.HasOne(e => e.Deck)
                  .WithMany(d => d.ExampleSentences)
                  .HasForeignKey(e => e.DeckId)
                  .OnDelete(DeleteBehavior.Cascade);
                  
            entity.HasMany(e => e.Words)
                  .WithOne(w => w.ExampleSentence)
                  .HasForeignKey(w => w.ExampleSentenceId);
        });
        
        modelBuilder.Entity<ExampleSentenceWord>(entity =>
        {
            entity.ToTable("ExampleSentenceWords", "jiten");
            entity.HasKey(e => new { e.ExampleSentenceId, e.WordId, e.Position });

            entity.HasIndex(dw => new { dw.WordId, dw.ReadingIndex }).HasDatabaseName("IX_ExampleSentenceWord_WordIdReadingIndex");

            entity.HasOne(e => e.Word)
                  .WithMany()
                  .HasForeignKey(e => e.WordId);
        });

        modelBuilder.Entity<Tag>(entity =>
        {
            entity.ToTable("Tags", "jiten");
            entity.HasKey(t => t.TagId);
            entity.Property(t => t.TagId).ValueGeneratedOnAdd();
            entity.Property(t => t.Name).IsRequired().HasMaxLength(50);

            entity.HasIndex(t => t.Name)
                  .IsUnique()
                  .HasDatabaseName("IX_Tags_Name");
        });

        modelBuilder.Entity<DeckGenre>(entity =>
        {
            entity.ToTable("DeckGenres", "jiten");
            entity.HasKey(dg => new { dg.DeckId, dg.Genre });

            entity.HasIndex(dg => dg.Genre)
                  .HasDatabaseName("IX_DeckGenres_Genre");

            entity.HasIndex(dg => dg.DeckId)
                  .HasDatabaseName("IX_DeckGenres_DeckId");

            entity.HasOne(dg => dg.Deck)
                  .WithMany(d => d.DeckGenres)
                  .HasForeignKey(dg => dg.DeckId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DeckTag>(entity =>
        {
            entity.ToTable("DeckTags", "jiten");
            entity.HasKey(dt => new { dt.DeckId, dt.TagId });

            entity.HasIndex(dt => dt.TagId)
                  .HasDatabaseName("IX_DeckTags_TagId");

            entity.HasIndex(dt => dt.Percentage)
                  .HasDatabaseName("IX_DeckTags_Percentage");

            entity.HasOne(dt => dt.Deck)
                  .WithMany(d => d.DeckTags)
                  .HasForeignKey(dt => dt.DeckId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(dt => dt.Tag)
                  .WithMany(t => t.DeckTags)
                  .HasForeignKey(dt => dt.TagId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.ToTable(t => t.HasCheckConstraint("CK_DeckTags_Percentage", "\"Percentage\" >= 0 AND \"Percentage\" <= 100"));
        });

        modelBuilder.Entity<ExternalGenreMapping>(entity =>
        {
            entity.ToTable("ExternalGenreMappings", "jiten");
            entity.HasKey(e => e.ExternalGenreMappingId);
            entity.Property(e => e.ExternalGenreMappingId).ValueGeneratedOnAdd();

            entity.Property(e => e.Provider).IsRequired();
            entity.Property(e => e.ExternalGenreName).IsRequired().HasMaxLength(100);
            entity.Property(e => e.JitenGenre).IsRequired();

            entity.HasIndex(e => new { e.Provider, e.ExternalGenreName, e.JitenGenre })
                  .IsUnique()
                  .HasDatabaseName("IX_ExternalGenreMapping_Provider_ExternalName_JitenGenre");

            entity.HasIndex(e => new { e.Provider, e.ExternalGenreName })
                  .HasDatabaseName("IX_ExternalGenreMapping_Provider_ExternalName");
        });

        modelBuilder.Entity<ExternalTagMapping>(entity =>
        {
            entity.ToTable("ExternalTagMappings", "jiten");
            entity.HasKey(e => e.ExternalTagMappingId);
            entity.Property(e => e.ExternalTagMappingId).ValueGeneratedOnAdd();

            entity.Property(e => e.Provider).IsRequired();
            entity.Property(e => e.ExternalTagName).IsRequired().HasMaxLength(100);
            entity.Property(e => e.TagId).IsRequired();

            entity.HasIndex(e => new { e.Provider, e.ExternalTagName, e.TagId })
                  .IsUnique()
                  .HasDatabaseName("IX_ExternalTagMapping_Provider_ExternalName_TagId");

            entity.HasIndex(e => new { e.Provider, e.ExternalTagName })
                  .HasDatabaseName("IX_ExternalTagMapping_Provider_ExternalName");

            entity.HasOne(e => e.Tag)
                  .WithMany()
                  .HasForeignKey(e => e.TagId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DeckRelationship>(entity =>
        {
            entity.ToTable("DeckRelationships", "jiten");
            entity.HasKey(dr => new { dr.SourceDeckId, dr.TargetDeckId, dr.RelationshipType });

            entity.HasOne(dr => dr.SourceDeck)
                  .WithMany(d => d.RelationshipsAsSource)
                  .HasForeignKey(dr => dr.SourceDeckId)
                  .HasConstraintName("FK_DeckRelationships_Decks_SourceDeckId")
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(dr => dr.TargetDeck)
                  .WithMany(d => d.RelationshipsAsTarget)
                  .HasForeignKey(dr => dr.TargetDeckId)
                  .HasConstraintName("FK_DeckRelationships_Decks_TargetDeckId")
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(dr => dr.SourceDeckId).HasDatabaseName("IX_DeckRelationships_SourceDeckId");
            entity.HasIndex(dr => dr.TargetDeckId).HasDatabaseName("IX_DeckRelationships_TargetDeckId");
        });

        modelBuilder.Entity<WordSet>(entity =>
        {
            entity.ToTable("WordSets", "jiten");
            entity.HasKey(ws => ws.SetId);
            entity.Property(ws => ws.Slug).HasMaxLength(50).IsRequired();
            entity.Property(ws => ws.Name).HasMaxLength(100).IsRequired();
            entity.Property(ws => ws.CreatedAt).IsRequired();
            entity.HasIndex(ws => ws.Slug).IsUnique().HasDatabaseName("IX_WordSet_Slug");
        });

        modelBuilder.Entity<WordSetMember>(entity =>
        {
            entity.ToTable("WordSetMembers", "jiten");
            entity.HasKey(wsm => new { wsm.SetId, wsm.WordId, wsm.ReadingIndex });
            entity.HasOne(wsm => wsm.Set)
                  .WithMany(ws => ws.Members)
                  .HasForeignKey(wsm => wsm.SetId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(wsm => new { wsm.WordId, wsm.ReadingIndex })
                  .HasDatabaseName("IX_WordSetMember_WordId_ReadingIndex");
        });

        modelBuilder.Entity<MediaRequest>(entity =>
        {
            entity.ToTable("MediaRequests", "jiten");
            entity.HasKey(mr => mr.Id);
            entity.Property(mr => mr.Id).ValueGeneratedOnAdd();
            entity.Property(mr => mr.Title).IsRequired().HasMaxLength(300);
            entity.Property(mr => mr.MediaType).IsRequired();
            entity.Property(mr => mr.ExternalUrl).HasMaxLength(500);
            entity.Property(mr => mr.Description).HasMaxLength(1000);
            entity.Property(mr => mr.Status).IsRequired();
            entity.Property(mr => mr.AdminNote).HasMaxLength(500);
            entity.Property(mr => mr.RequesterId).IsRequired().HasMaxLength(36);
            entity.Property(mr => mr.UpvoteCount).HasDefaultValue(0);
            entity.Property(mr => mr.CreatedAt).IsRequired();
            entity.Property(mr => mr.UpdatedAt).IsRequired();

            entity.HasOne(mr => mr.FulfilledDeck)
                  .WithMany()
                  .HasForeignKey(mr => mr.FulfilledDeckId)
                  .OnDelete(DeleteBehavior.SetNull);

            if (isNpgsql)
            {
                entity.HasIndex(mr => new { mr.Status, mr.UpvoteCount })
                      .IsDescending(false, true)
                      .HasDatabaseName("IX_MediaRequest_Status_UpvoteCount");
                entity.HasIndex(mr => new { mr.Status, mr.CreatedAt })
                      .IsDescending(false, true)
                      .HasDatabaseName("IX_MediaRequest_Status_CreatedAt");
            }
            else
            {
                entity.HasIndex(mr => new { mr.Status, mr.UpvoteCount })
                      .HasDatabaseName("IX_MediaRequest_Status_UpvoteCount");
                entity.HasIndex(mr => new { mr.Status, mr.CreatedAt })
                      .HasDatabaseName("IX_MediaRequest_Status_CreatedAt");
            }
            entity.HasIndex(mr => mr.MediaType)
                  .HasDatabaseName("IX_MediaRequest_MediaType");
            entity.HasIndex(mr => mr.RequesterId)
                  .HasDatabaseName("IX_MediaRequest_RequesterId");
            entity.HasIndex(mr => mr.Title)
                  .HasDatabaseName("IX_MediaRequest_Title");
        });

        modelBuilder.Entity<MediaRequestUpvote>(entity =>
        {
            entity.ToTable("MediaRequestUpvotes", "jiten");
            entity.HasKey(u => u.Id);
            entity.Property(u => u.Id).ValueGeneratedOnAdd();
            entity.Property(u => u.UserId).IsRequired().HasMaxLength(36);
            entity.Property(u => u.CreatedAt).IsRequired();

            entity.HasOne(u => u.MediaRequest)
                  .WithMany(mr => mr.Upvotes)
                  .HasForeignKey(u => u.MediaRequestId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(u => new { u.MediaRequestId, u.UserId })
                  .IsUnique()
                  .HasDatabaseName("IX_MediaRequestUpvote_RequestId_UserId");
        });

        modelBuilder.Entity<MediaRequestSubscription>(entity =>
        {
            entity.ToTable("MediaRequestSubscriptions", "jiten");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Id).ValueGeneratedOnAdd();
            entity.Property(s => s.UserId).IsRequired().HasMaxLength(36);
            entity.Property(s => s.CreatedAt).IsRequired();

            entity.HasOne(s => s.MediaRequest)
                  .WithMany(mr => mr.Subscriptions)
                  .HasForeignKey(s => s.MediaRequestId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(s => new { s.MediaRequestId, s.UserId })
                  .IsUnique()
                  .HasDatabaseName("IX_MediaRequestSubscription_RequestId_UserId");
        });

        modelBuilder.Entity<MediaRequestComment>(entity =>
        {
            entity.ToTable("MediaRequestComments", "jiten");
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Id).ValueGeneratedOnAdd();
            entity.Property(c => c.UserId).IsRequired().HasMaxLength(36);
            entity.Property(c => c.Text).HasMaxLength(500);
            entity.Property(c => c.CreatedAt).IsRequired();
            entity.Property(c => c.UpdatedAt);

            entity.HasOne(c => c.MediaRequest)
                  .WithMany(mr => mr.Comments)
                  .HasForeignKey(c => c.MediaRequestId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(c => c.MediaRequestId)
                  .HasDatabaseName("IX_MediaRequestComment_MediaRequestId");
            entity.HasIndex(c => c.UserId)
                  .HasDatabaseName("IX_MediaRequestComment_UserId");
        });

        modelBuilder.Entity<MediaRequestUpload>(entity =>
        {
            entity.ToTable("MediaRequestUploads", "jiten");
            entity.HasKey(u => u.Id);
            entity.Property(u => u.Id).ValueGeneratedOnAdd();
            entity.Property(u => u.FileName).IsRequired().HasMaxLength(255);
            entity.Property(u => u.StoragePath).IsRequired().HasMaxLength(500);
            entity.Property(u => u.FileSize).IsRequired();
            entity.Property(u => u.OriginalFileCount).HasDefaultValue(1);
            entity.Property(u => u.CreatedAt).IsRequired();
            entity.Property(u => u.AdminReviewed).HasDefaultValue(false);
            entity.Property(u => u.AdminNote).HasMaxLength(500);
            entity.Property(u => u.FileDeleted).HasDefaultValue(false);

            entity.HasOne(u => u.Comment)
                  .WithOne(c => c.Upload)
                  .HasForeignKey<MediaRequestUpload>(u => u.MediaRequestCommentId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(u => u.MediaRequest)
                  .WithMany(mr => mr.Uploads)
                  .HasForeignKey(u => u.MediaRequestId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(u => u.MediaRequestCommentId)
                  .IsUnique()
                  .HasDatabaseName("IX_MediaRequestUpload_CommentId");
            entity.HasIndex(u => u.MediaRequestId)
                  .HasDatabaseName("IX_MediaRequestUpload_RequestId");
        });

        modelBuilder.Entity<RequestActivityLog>(entity =>
        {
            entity.ToTable("RequestActivityLogs", "jiten");
            entity.HasKey(l => l.Id);
            entity.Property(l => l.Id).ValueGeneratedOnAdd();
            entity.Property(l => l.UserId).IsRequired().HasMaxLength(36);
            entity.Property(l => l.Action).IsRequired();
            entity.Property(l => l.TargetUserId).HasMaxLength(36);
            entity.Property(l => l.Detail).HasMaxLength(1000);
            entity.Property(l => l.IpAddress).HasMaxLength(45);
            entity.Property(l => l.CreatedAt).IsRequired();

            entity.HasIndex(l => new { l.MediaRequestId, l.CreatedAt })
                  .HasDatabaseName("IX_RequestActivityLog_RequestId_CreatedAt");
            entity.HasIndex(l => new { l.UserId, l.CreatedAt })
                  .HasDatabaseName("IX_RequestActivityLog_UserId_CreatedAt");
            entity.HasIndex(l => new { l.Action, l.CreatedAt })
                  .HasDatabaseName("IX_RequestActivityLog_Action_CreatedAt");
        });

        modelBuilder.Entity<Notification>(entity =>
        {
            entity.ToTable("Notifications", "jiten");
            entity.HasKey(n => n.Id);
            entity.Property(n => n.Id).ValueGeneratedOnAdd();
            entity.Property(n => n.UserId).IsRequired().HasMaxLength(36);
            entity.Property(n => n.Type).IsRequired();
            entity.Property(n => n.Title).IsRequired().HasMaxLength(200);
            entity.Property(n => n.Message).IsRequired().HasMaxLength(500);
            entity.Property(n => n.LinkUrl).HasMaxLength(300);
            entity.Property(n => n.IsRead).HasDefaultValue(false);
            entity.Property(n => n.CreatedAt).IsRequired();

            if (isNpgsql)
            {
                entity.HasIndex(n => new { n.UserId, n.IsRead, n.CreatedAt })
                      .IsDescending(false, false, true)
                      .HasDatabaseName("IX_Notification_UserId_IsRead_CreatedAt");
            }
            else
            {
                entity.HasIndex(n => new { n.UserId, n.IsRead, n.CreatedAt })
                      .HasDatabaseName("IX_Notification_UserId_IsRead_CreatedAt");
            }
            entity.HasIndex(n => n.CreatedAt)
                  .HasDatabaseName("IX_Notification_CreatedAt");
        });

        modelBuilder.Entity<DifficultyVote>(entity =>
        {
            entity.ToTable("DifficultyVotes", "jiten");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.UserId).IsRequired().HasMaxLength(450);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.IsValid).HasDefaultValue(true);
            entity.Property(e => e.Source).HasDefaultValue(DifficultyVoteSource.Manual);

            entity.HasOne(e => e.DeckLow)
                .WithMany().HasForeignKey(e => e.DeckLowId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.DeckHigh)
                .WithMany().HasForeignKey(e => e.DeckHighId).OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.UserId).HasDatabaseName("IX_DifficultyVotes_UserId");
            entity.HasIndex(e => e.DeckLowId).HasDatabaseName("IX_DifficultyVotes_DeckLowId");
            entity.HasIndex(e => e.DeckHighId).HasDatabaseName("IX_DifficultyVotes_DeckHighId");

            if (isNpgsql)
            {
                entity.HasIndex(e => new { e.UserId, e.DeckLowId, e.DeckHighId })
                    .IsUnique()
                    .HasFilter("\"IsValid\" = true")
                    .HasDatabaseName("IX_DifficultyVotes_Unique");
            }
            else
            {
                entity.HasIndex(e => new { e.UserId, e.DeckLowId, e.DeckHighId })
                    .IsUnique()
                    .HasDatabaseName("IX_DifficultyVotes_Unique");
            }
        });

        modelBuilder.Entity<DifficultyRating>(entity =>
        {
            entity.ToTable("DifficultyRatings", "jiten");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.UserId).IsRequired().HasMaxLength(450);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.Deck)
                .WithMany().HasForeignKey(e => e.DeckId).OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.UserId, e.DeckId })
                .IsUnique()
                .HasDatabaseName("IX_DifficultyRatings_UserDeck");
        });

        modelBuilder.Entity<SkippedComparison>(entity =>
        {
            entity.ToTable("SkippedComparisons", "jiten");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.UserId).IsRequired().HasMaxLength(450);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasIndex(e => new { e.UserId, e.DeckLowId, e.DeckHighId })
                .IsUnique()
                .HasDatabaseName("IX_SkippedComparisons_UserPair");
        });

        modelBuilder.Entity<BlacklistedComparisonDeck>(entity =>
        {
            entity.ToTable("BlacklistedComparisonDecks", "jiten");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.UserId).IsRequired().HasMaxLength(450);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.Deck)
                .WithMany().HasForeignKey(e => e.DeckId).OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.UserId, e.DeckId })
                .IsUnique()
                .HasDatabaseName("IX_BlacklistedComparisonDecks_UserDeck");
        });

        modelBuilder.Entity<DifficultyRankGroup>(entity =>
        {
            entity.ToTable("DifficultyRankGroups", "jiten");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.UserId).IsRequired().HasMaxLength(450);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasIndex(e => new { e.UserId, e.MediaTypeGroup, e.SortIndex })
                .IsUnique()
                .HasDatabaseName("IX_DifficultyRankGroups_UserGroupSort");
        });

        modelBuilder.Entity<DifficultyRankItem>(entity =>
        {
            entity.ToTable("DifficultyRankItems", "jiten");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.UserId).IsRequired().HasMaxLength(450);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.Group)
                .WithMany(g => g.Items)
                .HasForeignKey(e => e.GroupId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Deck)
                .WithMany()
                .HasForeignKey(e => e.DeckId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.UserId, e.DeckId })
                .IsUnique()
                .HasDatabaseName("IX_DifficultyRankItems_UserDeck");

            entity.HasIndex(e => e.GroupId)
                .HasDatabaseName("IX_DifficultyRankItems_GroupId");
        });

        base.OnModelCreating(modelBuilder);
    }
}
