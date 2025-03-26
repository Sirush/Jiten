using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Microsoft.Extensions.Configuration;

namespace Jiten.Core;

using Microsoft.EntityFrameworkCore;

public class JitenDbContext : DbContext
{
    public DbSet<Deck> Decks { get; set; }
    public DbSet<DeckWord> DeckWords { get; set; }

    public DbSet<JmDictWord> JMDictWords { get; set; }
    public DbSet<JmDictWordFrequency> JmDictWordFrequencies { get; set; }
    public DbSet<JmDictDefinition> Definitions { get; set; }
    public DbSet<JmDictLookup> Lookups { get; set; }

    public JitenDbContext()
    {
    }

    public JitenDbContext(DbContextOptions<JitenDbContext> options) : base(options)
    {
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        var configuration = new ConfigurationBuilder()
                            .SetBasePath(Directory.GetCurrentDirectory())
                            .AddJsonFile(Path.Combine(Environment.CurrentDirectory, "..", "Shared", "sharedsettings.json"), optional: true)
                            .AddJsonFile("sharedsettings.json", optional: true)
                            .AddJsonFile("appsettings.json", optional: true)
                            .Build();

        var connectionString = configuration.GetConnectionString("JitenDatabase");
        optionsBuilder.UseNpgsql(connectionString);
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

            entity.HasOne(d => d.ParentDeck)
                  .WithMany(p => p.Children)
                  .HasForeignKey(d => d.ParentDeckId)
                  .OnDelete(DeleteBehavior.Restrict);
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

        base.OnModelCreating(modelBuilder);
    }
}