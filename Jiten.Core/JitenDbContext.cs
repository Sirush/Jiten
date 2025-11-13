using Jiten.Cli.NGrams;
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

    public DbSet<JmDictWord> JMDictWords { get; set; }
    public DbSet<JmDictWordFrequency> JmDictWordFrequencies { get; set; }
    public DbSet<JmDictDefinition> Definitions { get; set; }
    public DbSet<JmDictLookup> Lookups { get; set; }

    public DbSet<ExampleSentence> ExampleSentences { get; set; }
    public DbSet<ExampleSentenceWord> ExampleSentenceWords { get; set; }

    public JitenDbContext()
    {
    }

    public JitenDbContext(DbContextOptions<JitenDbContext> options) : base(options)
    {
        DbOptions = options;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("fuzzystrmatch");

        modelBuilder.HasDefaultSchema("jiten"); // Set a default schema

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
        });

        modelBuilder.Entity<DeckTitle>(entity =>
        {
            entity.HasKey(dt => dt.DeckTitleId);
            entity.Property(dt => dt.Title).IsRequired().HasMaxLength(200);
            entity.Property(dt => dt.TitleType).IsRequired();

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
                  .HasDatabaseName("IX_WordReadingIndex");

            entity.HasIndex(dw => new { dw.WordId, dw.ReadingIndex, dw.DeckId })
                  .HasDatabaseName("IX_DeckWordReadingIndexDeck");

            entity.HasIndex(dw => dw.DeckId)
                  .HasDatabaseName("IX_DeckId");

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

            entity.Property(e => e.Readings)
                  .HasColumnType("text[]");

            entity.Property(e => e.ReadingTypes)
                  .HasColumnType("int[]");

            entity.Property(e => e.ObsoleteReadings)
                  .HasColumnType("text[]")
                  .IsRequired(false);

            entity.Property(e => e.PartsOfSpeech)
                  .HasColumnType("text[]");

            entity.Property(e => e.PitchAccents)
                  .HasColumnType("int[]")
                  .IsRequired(false);

            entity.Property(e => e.Origin)
                  .HasColumnType("int");
        });

        modelBuilder.Entity<JmDictDefinition>(entity =>
        {
            entity.ToTable("Definitions", "jmdict");
            entity.HasKey(e => e.DefinitionId);
            entity.Property(e => e.DefinitionId).ValueGeneratedOnAdd();
            entity.Property(e => e.WordId).IsRequired();

            entity.Property(e => e.PartsOfSpeech)
                  .HasColumnType("text[]");
            entity.Property(e => e.EnglishMeanings)
                  .HasColumnType("text[]");
            entity.Property(e => e.DutchMeanings)
                  .HasColumnType("text[]");
            entity.Property(e => e.FrenchMeanings)
                  .HasColumnType("text[]");
            entity.Property(e => e.GermanMeanings)
                  .HasColumnType("text[]");
            entity.Property(e => e.SpanishMeanings)
                  .HasColumnType("text[]");
            entity.Property(e => e.HungarianMeanings)
                  .HasColumnType("text[]");
            entity.Property(e => e.RussianMeanings)
                  .HasColumnType("text[]");
            entity.Property(e => e.SlovenianMeanings)
                  .HasColumnType("text[]");
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

        modelBuilder.Entity<ExampleSentence>(entity =>
        {
            entity.ToTable("ExampleSentences", "jiten");
            entity.HasKey(e => e.SentenceId);
            entity.Property(e => e.SentenceId).ValueGeneratedOnAdd();
            entity.Property(e => e.Text).IsRequired();

            entity.HasIndex(e => e.DeckId).HasDatabaseName("IX_ExampleSentence_DeckId");

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

        // PrecomputedNgrams configuration
        modelBuilder.Entity<PrecomputedNgram>(entity =>
        {
            entity.ToTable("PrecomputedNgrams", "jiten");

            entity.HasKey(e => e.NgramId);

            entity.Property(e => e.WordId)
                  .IsRequired();

            entity.Property(e => e.ReadingIndex)
                  .IsRequired();

            entity.Property(e => e.ContextBefore)
                  .IsRequired()
                  .HasMaxLength(500);

            entity.Property(e => e.ContextAfter)
                  .IsRequired()
                  .HasMaxLength(500);

            entity.Property(e => e.ContextSize)
                  .IsRequired();

            entity.Property(e => e.TokensBefore)
                  .IsRequired();

            entity.Property(e => e.TokensAfter)
                  .IsRequired();

            entity.Property(e => e.FullContext)
                  .IsRequired()
                  .HasMaxLength(1000);

            entity.Property(e => e.Occurrences)
                  .IsRequired()
                  .HasDefaultValue(1);

            entity.Property(e => e.SignificanceScore)
                  .IsRequired();

            entity.Property(e => e.BertEmbedding)
                  .HasColumnType("real[]"); // Store as array of floats

            entity.Property(e => e.BertEmbeddingComputed)
                  .IsRequired()
                  .HasDefaultValue(false);

            entity.Property(e => e.LastUpdated)
                  .IsRequired()
                  .HasDefaultValueSql("NOW()");

            // Indexes
            entity.HasIndex(e => new { e.WordId, e.ReadingIndex })
                  .HasDatabaseName("IX_PrecomputedNgrams_WordId_ReadingIndex");

            entity.HasIndex(e => new { e.WordId, e.SignificanceScore })
                  .HasDatabaseName("IX_PrecomputedNgrams_WordId_SignificanceScore");

            entity.HasIndex(e => e.BertEmbeddingComputed)
                  .HasDatabaseName("IX_PrecomputedNgrams_BertEmbeddingComputed")
                  .HasFilter("\"BertEmbeddingComputed\" = false");

            // Partial index for high-significance n-grams
            entity.HasIndex(e => new { e.WordId, e.ReadingIndex, e.SignificanceScore })
                  .HasDatabaseName("IX_PrecomputedNgrams_HighSignificance")
                  .HasFilter("\"SignificanceScore\" > 0.5");

            // Foreign key to JmDictWord
            entity.HasOne<JmDictWord>()
                  .WithMany()
                  .HasForeignKey(e => e.WordId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // NgramSources configuration
        modelBuilder.Entity<NgramSource>(entity =>
        {
            entity.ToTable("NgramSources", "jiten");

            entity.HasKey(e => new { e.NgramId, e.ExampleSentenceId });

            entity.Property(e => e.WordPosition)
                  .IsRequired();

            // Indexes
            entity.HasIndex(e => e.ExampleSentenceId)
                  .HasDatabaseName("IX_NgramSources_ExampleSentenceId");

            entity.HasIndex(e => e.NgramId)
                  .HasDatabaseName("IX_NgramSources_NgramId");

            // Foreign key to PrecomputedNgram
            entity.HasOne(e => e.Ngram)
                  .WithMany(n => n.Sources)
                  .HasForeignKey(e => e.NgramId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Foreign key to ExampleSentence
            entity.HasOne(e => e.ExampleSentence)
                  .WithMany()
                  .HasForeignKey(e => e.ExampleSentenceId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // NgramStatistics configuration
        modelBuilder.Entity<NgramStatistics>(entity =>
        {
            entity.ToTable("NgramStatistics", "jiten");

            entity.HasKey(e => e.WordId);

            entity.Property(e => e.TotalNgrams)
                  .IsRequired();

            entity.Property(e => e.SignificantNgrams)
                  .IsRequired();

            entity.Property(e => e.AvgSignificanceScore)
                  .IsRequired();

            entity.Property(e => e.BertEmbeddingsComputed)
                  .IsRequired();

            entity.Property(e => e.LastProcessed);

            entity.Property(e => e.AmbiguityScore);

            // Indexes
            entity.HasIndex(e => e.AmbiguityScore)
                  .HasDatabaseName("IX_NgramStatistics_AmbiguityScore");

            entity.HasIndex(e => e.LastProcessed)
                  .HasDatabaseName("IX_NgramStatistics_LastProcessed");

            // Foreign key to JmDictWord
            entity.HasOne<JmDictWord>()
                  .WithMany()
                  .HasForeignKey(e => e.WordId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // NgramProcessingQueue configuration
        modelBuilder.Entity<NgramProcessingQueue>(entity =>
        {
            entity.ToTable("NgramProcessingQueue", "jiten");

            entity.HasKey(e => e.QueueId);

            entity.Property(e => e.NgramId)
                  .IsRequired();

            entity.Property(e => e.Priority)
                  .IsRequired()
                  .HasDefaultValue(1);

            entity.Property(e => e.Status)
                  .IsRequired()
                  .HasMaxLength(20)
                  .HasDefaultValue(ProcessingStatus.Pending)
                  .HasConversion<string>(); // Store enum as string

            entity.Property(e => e.RetryCount)
                  .IsRequired()
                  .HasDefaultValue(0);

            entity.Property(e => e.ErrorMessage)
                  .HasMaxLength(2000);

            entity.Property(e => e.CreatedAt)
                  .IsRequired()
                  .HasDefaultValueSql("NOW()");

            entity.Property(e => e.ProcessedAt);

            // Indexes
            entity.HasIndex(e => new { e.Status, e.Priority })
                  .HasDatabaseName("IX_NgramProcessingQueue_Status_Priority")
                  .HasFilter("\"Status\" = 'Pending'");

            // Foreign key to PrecomputedNgram
            entity.HasOne(e => e.Ngram)
                  .WithMany()
                  .HasForeignKey(e => e.NgramId)
                  .OnDelete(DeleteBehavior.Cascade);
        });


        base.OnModelCreating(modelBuilder);
    }
}