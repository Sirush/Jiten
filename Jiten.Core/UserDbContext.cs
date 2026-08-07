using Jiten.Core.Data;
using Jiten.Core.Data.Authentication;
using Jiten.Core.Data.Billing;
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
    public DbSet<FsrsCardArchive> FsrsCardArchives { get; set; }
    public DbSet<UserReviewDaily> UserReviewDailies { get; set; }

    public DbSet<UserAccomplishment> UserAccomplishments { get; set; }
    public DbSet<UserProfile> UserProfiles { get; set; }
    public DbSet<UserKanjiGrid> UserKanjiGrids { get; set; }

    public DbSet<UserWordSetState> UserWordSetStates { get; set; }
    public DbSet<UserStudyDeck> UserStudyDecks { get; set; }
    public DbSet<UserStudyDeckWord> UserStudyDeckWords { get; set; }
    public DbSet<UserExampleSentence> UserExampleSentences { get; set; }
    public DbSet<UserCustomMeaning> UserCustomMeanings { get; set; }
    public DbSet<UserHiddenDefinition> UserHiddenDefinitions { get; set; }

    public DbSet<PromoCode> PromoCodes { get; set; }
    public DbSet<UserPromoCredit> UserPromoCredits { get; set; }
    public DbSet<UserFrequencyList> UserFrequencyLists { get; set; }
    public DbSet<UserRoadmap> UserRoadmaps { get; set; }
    public DbSet<UserCardMedia> UserCardMedia { get; set; }

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
            entity.Property(um => um.ReviewRollupDirty).HasDefaultValue(false);
            entity.Property(um => um.ReviewRollupRebuiltAt).IsRequired(false);
            if (isNpgsql)
            {
                entity.Property(um => um.UserId).HasConversion(guidToString).HasColumnType("uuid").IsRequired();

                // Partial: the sweep runs every 15 minutes and the flag is false for nearly every row.
                entity.HasIndex(um => um.ReviewRollupDirty)
                      .HasDatabaseName("IX_UserMetadatas_ReviewRollupDirty")
                      .HasFilter("\"ReviewRollupDirty\"");
            }

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

        modelBuilder.Entity<FsrsCardArchive>(entity =>
        {
            entity.HasKey(a => a.ArchiveId);
            if (isNpgsql)
                entity.Property(a => a.UserId).HasConversion(guidToString).HasColumnType("uuid").IsRequired();

            entity.HasIndex(a => new { a.UserId, a.WordId, a.ReadingIndex })
                  .IsUnique()
                  .HasDatabaseName("IX_FsrsCardArchive_UserId_WordId_ReadingIndex");
            entity.HasIndex(a => new { a.UserId, a.ArchivedAt })
                  .HasDatabaseName("IX_FsrsCardArchive_UserId_ArchivedAt");
            entity.Property(a => a.HistoryMerged).HasDefaultValue(false);
            entity.Property(a => a.HistoryTruncated).HasDefaultValue(false);

            entity.HasOne<User>()
                  .WithMany()
                  .HasForeignKey(a => a.UserId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserReviewDaily>(entity =>
        {
            entity.HasKey(d => new { d.UserId, d.LocalDate });
            if (isNpgsql)
                entity.Property(d => d.UserId).HasConversion(guidToString).HasColumnType("uuid").IsRequired();

            entity.HasOne<User>()
                  .WithMany()
                  .HasForeignKey(d => d.UserId)
                  .OnDelete(DeleteBehavior.Cascade);
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

        modelBuilder.Entity<UserCustomMeaning>(entity =>
        {
            entity.HasKey(e => e.UserCustomMeaningId);
            if (isNpgsql)
                entity.Property(e => e.UserId).HasConversion(guidToString).HasColumnType("uuid").IsRequired();
            entity.Property(e => e.Text).HasMaxLength(500).IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql(isNpgsql ? "now() at time zone 'utc'" : "datetime('now')");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql(isNpgsql ? "now() at time zone 'utc'" : "datetime('now')");

            entity.HasOne<User>()
                  .WithMany()
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.UserId, e.WordId })
                  .IsUnique()
                  .HasDatabaseName("IX_UserCustomMeaning_UserId_WordId");
        });

        modelBuilder.Entity<UserHiddenDefinition>(entity =>
        {
            entity.HasKey(e => new { e.UserId, e.WordId });
            if (isNpgsql)
                entity.Property(e => e.UserId).HasConversion(guidToString).HasColumnType("uuid").IsRequired();

            entity.HasOne<User>()
                  .WithMany()
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.Cascade);
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

        modelBuilder.Entity<PromoCode>(entity =>
        {
            entity.HasKey(pc => pc.CodeId);
            entity.Property(pc => pc.Code).IsRequired().HasMaxLength(12);
            entity.Property(pc => pc.Description).HasMaxLength(500);
            entity.Property(pc => pc.CurrentUses).HasDefaultValue(0);
            entity.Property(pc => pc.IsActive).HasDefaultValue(true);
            entity.Property(pc => pc.GrantsFullTier).HasDefaultValue(false);
            entity.Property(pc => pc.CreatedAt).IsRequired();

            entity.HasIndex(pc => pc.Code).IsUnique().HasDatabaseName("IX_PromoCode_Code");
        });

        modelBuilder.Entity<UserPromoCredit>(entity =>
        {
            entity.HasKey(upc => upc.UserPromoCreditId);
            if (isNpgsql)
                entity.Property(upc => upc.UserId).HasConversion(guidToString).HasColumnType("uuid").IsRequired();
            entity.Property(upc => upc.RemainingDays).IsRequired();
            entity.Property(upc => upc.GrantedAt).IsRequired();
            entity.Property(upc => upc.Source).HasDefaultValue(PromoCreditSource.Redemption);
            entity.Property(upc => upc.GrantsFullTier).HasDefaultValue(false);
            entity.Property(upc => upc.ThankYouMessage).HasMaxLength(1000);

            entity.HasOne<User>()
                  .WithMany()
                  .HasForeignKey(upc => upc.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            // PromoCodeId is null for admin grants; the FK is optional and non-cascading.
            entity.HasOne<PromoCode>()
                  .WithMany()
                  .HasForeignKey(upc => upc.PromoCodeId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(upc => upc.UserId).HasDatabaseName("IX_UserPromoCredit_UserId");
            entity.HasIndex(upc => new { upc.UserId, upc.PromoCodeId })
                  .IsUnique()
                  .HasDatabaseName("IX_UserPromoCredit_UserId_PromoCodeId");
        });

        modelBuilder.Entity<UserRoadmap>(entity =>
        {
            entity.HasKey(r => r.Id);
            if (isNpgsql)
            {
                entity.Property(r => r.UserId).HasConversion(guidToString).HasColumnType("uuid").IsRequired();
                entity.Property(r => r.DefinitionJson).HasColumnType("jsonb").IsRequired();
                entity.Property(r => r.StepsJson).HasColumnType("jsonb").IsRequired();
            }
            else
            {
                entity.Property(r => r.DefinitionJson).IsRequired();
                entity.Property(r => r.StepsJson).IsRequired();
            }

            entity.Property(r => r.Name).HasMaxLength(100).IsRequired();
            entity.Property(r => r.FailureReason).HasMaxLength(500);
            entity.Property(r => r.CreatedAt).IsRequired();
            entity.Ignore(r => r.Definition);
            entity.Ignore(r => r.Payload);

            entity.HasOne<User>()
                  .WithMany()
                  .HasForeignKey(r => r.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(r => r.UserId).HasDatabaseName("IX_UserRoadmap_UserId");
        });

        modelBuilder.Entity<UserFrequencyList>(entity =>
        {
            entity.HasKey(f => f.Id);
            if (isNpgsql)
            {
                entity.Property(f => f.UserId).HasConversion(guidToString).HasColumnType("uuid").IsRequired();
                entity.Property(f => f.DefinitionJson).HasColumnType("jsonb").IsRequired();
            }
            else
            {
                entity.Property(f => f.DefinitionJson).IsRequired();
            }
            entity.Property(f => f.Name).HasMaxLength(100).IsRequired();
            entity.Property(f => f.PublicSlug).HasMaxLength(32);
            entity.Property(f => f.ZipUrl).HasMaxLength(1024);
            entity.Property(f => f.CsvUrl).HasMaxLength(1024);
            entity.Property(f => f.CreatedAt).IsRequired();
            entity.Ignore(f => f.Definition);

            entity.HasOne<User>()
                  .WithMany()
                  .HasForeignKey(f => f.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(f => f.UserId).HasDatabaseName("IX_UserFrequencyList_UserId");

            // Unique share slug where set. On Postgres a filtered index keeps multiple NULLs legal;
            // SQLite already treats NULLs as distinct, so a plain unique index suffices there.
            if (isNpgsql)
            {
                entity.HasIndex(f => f.PublicSlug)
                      .IsUnique()
                      .HasFilter("\"PublicSlug\" IS NOT NULL")
                      .HasDatabaseName("IX_UserFrequencyList_PublicSlug");
            }
            else
            {
                entity.HasIndex(f => f.PublicSlug).IsUnique().HasDatabaseName("IX_UserFrequencyList_PublicSlug");
            }
        });

        modelBuilder.Entity<UserCardMedia>(entity =>
        {
            entity.HasKey(m => m.Id);
            if (isNpgsql)
                entity.Property(m => m.UserId).HasConversion(guidToString).HasColumnType("uuid").IsRequired();
            entity.Property(m => m.StoragePath).HasMaxLength(512).IsRequired();
            entity.Property(m => m.ContentType).HasMaxLength(100).IsRequired();
            entity.Property(m => m.CreatedAt).IsRequired();

            entity.HasOne<User>()
                  .WithMany()
                  .HasForeignKey(m => m.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(m => m.UserId).HasDatabaseName("IX_UserCardMedia_UserId");

            entity.HasIndex(m => new { m.UserId, m.WordId, m.ReadingIndex, m.Kind })
                  .IsUnique()
                  .HasDatabaseName("IX_UserCardMedia_UserId_WordId_ReadingIndex_Kind");
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
