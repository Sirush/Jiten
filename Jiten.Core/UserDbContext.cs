using Jiten.Core.Data;
using Jiten.Core.Data.Authentication;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.User;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Jiten.Core;

public class UserDbContext : IdentityDbContext<User>
{
    public UserDbContext()
    {
    }

    public UserDbContext(DbContextOptions<UserDbContext> options)
        : base(options)
    {
    }

    public DbSet<UserCoverage> UserCoverages { get; set; }
    public DbSet<UserCoverageChunk> UserCoverageChunks { get; set; }
    public DbSet<RefreshToken> RefreshTokens { get; set; }
    public DbSet<UserMetadata> UserMetadatas { get; set; }
    public DbSet<ApiKey> ApiKeys { get; set; }
    public DbSet<UserDeckPreference> UserDeckPreferences { get; set; }
    public DbSet<UserFsrsSettings> UserFsrsSettings { get; set; }

    public DbSet<FsrsCard> FsrsCards { get; set; }
    public DbSet<FsrsReviewLog> FsrsReviewLogs { get; set; }

    public DbSet<UserAccomplishment> UserAccomplishments { get; set; }
    public DbSet<UserProfile> UserProfiles { get; set; }
    public DbSet<UserKanjiGrid> UserKanjiGrids { get; set; }

    public DbSet<UserWordSetState> UserWordSetStates { get; set; }
    public DbSet<UserStudyDeck> UserStudyDecks { get; set; }
    public DbSet<UserStudyDeckWord> UserStudyDeckWords { get; set; }
    public DbSet<UserExampleSentence> UserExampleSentences { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var isNpgsql = Database.ProviderName?.Contains("Npgsql") == true;

        var guidToString = new ValueConverter<string, Guid>(
            v => Guid.Parse(v),
            v => v.ToString());

        if (isNpgsql)
            modelBuilder.HasDefaultSchema("user");

        modelBuilder.Entity<User>(entity =>
        {
            if (isNpgsql)
                entity.Property(e => e.Id).HasConversion(guidToString).HasColumnType("uuid").IsRequired();
        });

        modelBuilder.Entity<IdentityUserClaim<string>>(entity =>
        {
            if (isNpgsql)
                entity.Property(e => e.UserId).HasConversion(guidToString).HasColumnType("uuid").IsRequired();
        });

        modelBuilder.Entity<IdentityUserLogin<string>>(entity =>
        {
            if (isNpgsql)
                entity.Property(e => e.UserId).HasConversion(guidToString).HasColumnType("uuid").IsRequired();
        });

        modelBuilder.Entity<IdentityUserToken<string>>(entity =>
        {
            if (isNpgsql)
                entity.Property(e => e.UserId).HasConversion(guidToString).HasColumnType("uuid").IsRequired();
        });

        modelBuilder.Entity<IdentityUserRole<string>>(entity =>
        {
            if (isNpgsql)
                entity.Property(e => e.UserId).HasConversion(guidToString).HasColumnType("uuid").IsRequired();
        });


        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.HasKey(rt => rt.Token);
            entity.Property(rt => rt.JwtId).IsRequired();
            entity.Property(rt => rt.ExpiryDate).IsRequired();
            if (isNpgsql)
                entity.Property(rt => rt.UserId).HasConversion(guidToString).HasColumnType("uuid").IsRequired();
            entity.HasOne(rt => rt.User)
                  .WithMany()
                  .HasForeignKey(rt => rt.UserId);

            entity.HasIndex(rt => rt.UserId);
        });

        modelBuilder.Entity<UserCoverage>(entity =>
        {
            entity.HasKey(uc => new { uc.UserId, uc.DeckId }).HasName("PK_UserCoverages");
            entity.Property(uc => uc.Coverage).IsRequired();
            entity.Property(uc => uc.UniqueCoverage).IsRequired();
            if (isNpgsql)
                entity.Property(uc => uc.UserId).HasConversion(guidToString).HasColumnType("uuid").IsRequired();

            entity.HasOne<User>()
                  .WithMany()
                  .HasForeignKey(uc => uc.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(uc => uc.UserId).HasDatabaseName("IX_UserCoverage_UserId");
        });

        modelBuilder.Entity<UserCoverageChunk>(entity =>
        {
            entity.HasKey(uc => new { uc.UserId, uc.Metric, uc.ChunkIndex }).HasName("PK_UserCoverageChunks");
            if (isNpgsql)
            {
                entity.Property(uc => uc.UserId).HasConversion(guidToString).HasColumnType("uuid").IsRequired();
                entity.Property(uc => uc.Values).HasColumnType("smallint[]").IsRequired();
            }
            entity.Property(uc => uc.Metric).HasColumnType("smallint").IsRequired();
            entity.Property(uc => uc.ComputedAt).IsRequired();

            entity.HasOne<User>()
                  .WithMany()
                  .HasForeignKey(uc => uc.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(uc => uc.UserId).HasDatabaseName("IX_UserCoverageChunks_UserId");
        });

        modelBuilder.Entity<UserMetadata>(entity =>
        {
            entity.HasKey(um => um.UserId);
            entity.Property(um => um.CoverageRefreshedAt).IsRequired(false);
            entity.Property(um => um.CoverageDirty).IsRequired();
            entity.Property(um => um.CoverageDirtyAt).IsRequired(false);
            if (isNpgsql)
                entity.Property(um => um.UserId).HasConversion(guidToString).HasColumnType("uuid").IsRequired();

            entity.HasOne<User>()
                  .WithOne()
                  .HasForeignKey<UserMetadata>(um => um.UserId);
        });

        modelBuilder.Entity<ApiKey>(entity =>
        {
            entity.HasKey(k => k.Id);
            if (isNpgsql)
                entity.Property(k => k.UserId).HasConversion(guidToString).HasColumnType("uuid").IsRequired();
            entity.Property(k => k.Hash).IsRequired().HasMaxLength(88);
            entity.Property(k => k.CreatedAt).IsRequired();
            entity.Property(k => k.IsRevoked).HasDefaultValue(false);

            entity.HasOne(k => k.User)
                  .WithOne()
                  .HasForeignKey<ApiKey>(k => k.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(k => k.Hash)
                  .IsUnique()
                  .HasDatabaseName("IX_ApiKey_Hash");

            entity.HasIndex(k => k.UserId)
                  .HasDatabaseName("IX_ApiKey_UserId");

            entity.HasIndex(k => new { k.UserId, k.IsRevoked })
                  .HasDatabaseName("IX_ApiKey_UserId_IsRevoked");
        });

        modelBuilder.Entity<UserDeckPreference>(entity =>
        {
            entity.HasKey(udp => new { udp.UserId, udp.DeckId });
            if (isNpgsql)
                entity.Property(udp => udp.UserId).HasConversion(guidToString).HasColumnType("uuid").IsRequired();
            entity.Property(udp => udp.Status).IsRequired();
            entity.Property(udp => udp.IsFavourite).IsRequired();
            entity.Property(udp => udp.IsIgnored).IsRequired();

            entity.HasOne<User>()
                  .WithMany()
                  .HasForeignKey(udp => udp.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(udp => udp.UserId).HasDatabaseName("IX_UserDeckPreference_UserId");
            entity.HasIndex(udp => new { udp.UserId, udp.IsFavourite }).HasDatabaseName("IX_UserDeckPreference_UserId_IsFavourite");
            entity.HasIndex(udp => new { udp.UserId, udp.Status }).HasDatabaseName("IX_UserDeckPreference_UserId_Status");
            entity.HasIndex(udp => new { udp.UserId, udp.IsIgnored }).HasDatabaseName("IX_UserDeckPreference_UserId_IsIgnored");
        });

        // FSRS
        modelBuilder.Entity<FsrsCard>(entity =>
        {
            entity.HasKey(c => c.CardId);
            if (isNpgsql)
                entity.Property(c => c.UserId).HasConversion(guidToString).HasColumnType("uuid").IsRequired();
            entity.HasIndex(c => new { c.UserId, c.WordId, c.ReadingIndex }).IsUnique();
            entity.HasIndex(c => c.UserId);
            entity.HasIndex(c => new { c.UserId, c.State, c.Due }).HasDatabaseName("IX_FsrsCard_UserId_State_Due");
            entity.Property(c => c.CreatedAt).HasDefaultValueSql(isNpgsql ? "now() at time zone 'utc'" : "datetime('now')");

            entity.HasOne<User>()
                  .WithMany()
                  .HasForeignKey(c => c.UserId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FsrsReviewLog>(entity =>
        {
            entity.HasKey(l => l.ReviewLogId);
            entity.HasOne(r => r.Card)
                  .WithMany(c => c.ReviewLogs)
                  .HasForeignKey(r => r.CardId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(r => new { r.CardId, r.ReviewDateTime }).IsUnique();
        });

        modelBuilder.Entity<UserAccomplishment>(entity =>
        {
            entity.HasKey(ua => ua.AccomplishmentId);
            if (isNpgsql)
                entity.Property(ua => ua.UserId).HasConversion(guidToString).HasColumnType("uuid").IsRequired();

            entity.HasOne<User>()
                  .WithMany()
                  .HasForeignKey(ua => ua.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(ua => ua.UserId).HasDatabaseName("IX_UserAccomplishment_UserId");
            entity.HasIndex(ua => new { ua.UserId, ua.MediaType })
                  .IsUnique()
                  .HasDatabaseName("IX_UserAccomplishment_UserId_MediaType");
        });

        modelBuilder.Entity<UserProfile>(entity =>
        {
            entity.HasKey(up => up.UserId);
            if (isNpgsql)
                entity.Property(up => up.UserId).HasConversion(guidToString).HasColumnType("uuid").IsRequired();
            entity.Property(up => up.IsPublic).HasDefaultValue(false);
            entity.Property(up => up.IsMediaListPublic).HasDefaultValue(false);

            entity.HasOne<User>()
                  .WithOne()
                  .HasForeignKey<UserProfile>(up => up.UserId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserKanjiGrid>(entity =>
        {
            entity.HasKey(ukg => ukg.UserId);
            if (isNpgsql)
            {
                entity.Property(ukg => ukg.UserId).HasConversion(guidToString).HasColumnType("uuid").IsRequired();
                entity.Property(ukg => ukg.KanjiScoresJson).HasColumnType("jsonb").IsRequired();
            }
            else
            {
                entity.Property(ukg => ukg.KanjiScoresJson).IsRequired();
            }
            entity.Property(ukg => ukg.LastComputedAt).IsRequired();
            entity.Ignore(ukg => ukg.KanjiScores);

            entity.HasOne<User>()
                  .WithOne()
                  .HasForeignKey<UserKanjiGrid>(ukg => ukg.UserId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserFsrsSettings>(entity =>
        {
            entity.HasKey(ufs => ufs.UserId);
            if (isNpgsql)
            {
                entity.Property(ufs => ufs.UserId).HasConversion(guidToString).HasColumnType("uuid").IsRequired();
                entity.Property(ufs => ufs.ParametersJson).HasColumnType("jsonb").IsRequired();
            }
            else
            {
                entity.Property(ufs => ufs.ParametersJson).IsRequired();
            }
            entity.Property(ufs => ufs.DesiredRetention).HasColumnType("double precision");
            if (isNpgsql)
            {
                entity.Property(ufs => ufs.SettingsJson).HasColumnType("jsonb").HasDefaultValue("{}");
            }
            else
            {
                entity.Property(ufs => ufs.SettingsJson).HasDefaultValue("{}");
            }
            entity.Ignore(ufs => ufs.Parameters);

            entity.HasOne<User>()
                  .WithOne()
                  .HasForeignKey<UserFsrsSettings>(ufs => ufs.UserId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserStudyDeck>(entity =>
        {
            entity.HasKey(usd => usd.UserStudyDeckId);
            if (isNpgsql)
                entity.Property(usd => usd.UserId).HasConversion(guidToString).HasColumnType("uuid").IsRequired();
            entity.Property(usd => usd.DeckId).IsRequired(false);
            entity.Property(usd => usd.Name).HasMaxLength(200);
            entity.Property(usd => usd.Description).HasMaxLength(2000);
            entity.Property(usd => usd.CreatedAt).IsRequired();

            if (isNpgsql)
            {
                entity.HasIndex(usd => new { usd.UserId, usd.DeckId })
                      .IsUnique()
                      .HasFilter("\"DeckId\" IS NOT NULL");
            }

            entity.HasIndex(usd => usd.UserId).HasDatabaseName("IX_UserStudyDeck_UserId");

            entity.HasOne<User>()
                  .WithMany()
                  .HasForeignKey(usd => usd.UserId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserStudyDeckWord>(entity =>
        {
            entity.HasKey(w => new { w.UserStudyDeckId, w.WordId, w.ReadingIndex });
            entity.HasIndex(w => new { w.UserStudyDeckId, w.SortOrder });
            if (isNpgsql)
            {
                entity.HasIndex(w => new { w.UserStudyDeckId, w.Occurrences })
                      .IsDescending(false, true);
            }
            else
            {
                entity.HasIndex(w => new { w.UserStudyDeckId, w.Occurrences });
            }

            entity.HasOne(w => w.StudyDeck)
                  .WithMany(sd => sd.Words)
                  .HasForeignKey(w => w.UserStudyDeckId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserExampleSentence>(entity =>
        {
            entity.HasKey(e => e.UserExampleSentenceId);
            if (isNpgsql)
                entity.Property(e => e.UserId).HasConversion(guidToString).HasColumnType("uuid").IsRequired();
            entity.Property(e => e.Text).HasMaxLength(150).IsRequired();
            entity.Property(e => e.Source).HasMaxLength(150);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql(isNpgsql ? "now() at time zone 'utc'" : "datetime('now')");

            entity.HasOne<User>()
                  .WithMany()
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.UserId, e.WordId, e.ReadingIndex, e.SortOrder })
                  .IsUnique()
                  .HasDatabaseName("IX_UserExampleSentence_UserId_WordId_ReadingIndex_SortOrder");

            entity.HasIndex(e => new { e.UserId, e.WordId, e.ReadingIndex })
                  .HasDatabaseName("IX_UserExampleSentence_UserId_WordId_ReadingIndex");
        });

        modelBuilder.Entity<UserWordSetState>(entity =>
        {
            entity.HasKey(uwss => new { uwss.UserId, uwss.SetId });
            if (isNpgsql)
                entity.Property(uwss => uwss.UserId).HasConversion(guidToString).HasColumnType("uuid").IsRequired();
            entity.Property(uwss => uwss.CreatedAt).IsRequired();

            entity.HasOne<User>()
                  .WithMany()
                  .HasForeignKey(uwss => uwss.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(uwss => uwss.UserId).HasDatabaseName("IX_UserWordSetState_UserId");
        });

        base.OnModelCreating(modelBuilder);
    }

    public override int SaveChanges()
    {
        AddTimestamps();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        AddTimestamps();
        return base.SaveChangesAsync(cancellationToken);
    }

    private void AddTimestamps()
    {
        var userEntities = ChangeTracker.Entries()
                                        .Where(x => x is { Entity: User, State: EntityState.Added or EntityState.Modified });

        foreach (var entity in userEntities)
        {
            var now = DateTime.UtcNow;
            if (entity.State == EntityState.Added)
            {
                ((User)entity.Entity).CreatedAt = now;
            }

            ((User)entity.Entity).UpdatedAt = now;
        }
    }
}
