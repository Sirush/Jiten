using Hangfire;
using Jiten.Api.Services;
using Jiten.Core;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Jobs;

/// <summary>
/// Daily countdown of promo credits. Each user with credit loses exactly one Jiten+ day per day, unless they
/// hold an active paid subscription or lifetime access (then the countdown pauses — promo days are a bonus,
/// never wasted). Full-tier credits are consumed before Trial-tier ones, FIFO within each class, so a user
/// never drops to Trial while Full credit remains. When a user is down to their last day, they get a heads-up
/// email.
/// </summary>
public class DecrementPromoCreditsJob(
    IDbContextFactory<UserDbContext> contextFactory,
    IJitenPlusService jitenPlus,
    IEmailService emailService,
    ILogger<DecrementPromoCreditsJob> logger)
{
    [Queue("default")]
    public Task Run() => RunForDate(DateOnly.FromDateTime(DateTime.UtcNow));

    /// <summary>Exposed for tests so "today" is deterministic.</summary>
    public async Task RunForDate(DateOnly today)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        var userIds = await context.UserPromoCredits
                                   .Where(c => c.RemainingDays > 0)
                                   .Select(c => c.UserId)
                                   .Distinct()
                                   .ToListAsync();

        var decremented = 0;

        foreach (var userId in userIds)
        {
            try
            {
                if (await DecrementUserAsync(context, userId, today))
                {
                    decremented++;
                    jitenPlus.InvalidateTier(userId);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "DecrementPromoCredits: failed for user {UserId}", userId);
            }
        }

        logger.LogInformation("DecrementPromoCredits: {Count} users had a day consumed", decremented);
    }

    private async Task<bool> DecrementUserAsync(UserDbContext context, string userId, DateOnly today)
    {
        var user = await context.Users
                                .Where(u => u.Id == userId)
                                .Select(u => new { u.StripeSubscriptionActive, u.IsLifetime, u.Email })
                                .FirstOrDefaultAsync();
        if (user is null)
            return false;

        // Raw flags only: a lapsed-but-in-grace subscription must not pause the countdown.
        if (user.StripeSubscriptionActive || user.IsLifetime)
            return false;

        var credits = await context.UserPromoCredits
                                   .Where(c => c.UserId == userId)
                                   .ToListAsync();

        // Idempotency: if anything was already decremented today (including a credit that just hit 0), stop.
        if (credits.Any(c => c.LastDecrementDate == today))
            return false;

        var target = credits.Where(c => c.RemainingDays > 0)
                            .OrderByDescending(c => c.GrantsFullTier)
                            .ThenBy(c => c.GrantedAt)
                            .ThenBy(c => c.UserPromoCreditId)
                            .FirstOrDefault();
        if (target is null)
            return false;

        target.RemainingDays -= 1;
        target.LastDecrementDate = today;
        if (target.RemainingDays == 0)
            target.FullyUsedAt = DateTime.UtcNow;

        await context.SaveChangesAsync();

        // One day left across all credits (and no paid sub, or we'd have returned above) → warn that access ends tomorrow.
        var totalRemaining = credits.Sum(c => c.RemainingDays);
        if (totalRemaining == 1)
            await emailService.SendPromoAccessEndsTomorrowAsync(user.Email);

        return true;
    }
}
