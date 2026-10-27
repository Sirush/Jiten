using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration.Infrastructure;

public static class ProfileTestSetup
{
    /// <summary>
    /// Test users are seeded without NormalizedUserName, which profile endpoints resolve by. The flags are written directly so a test
    /// can also exercise the access guard against an invariant-violating row; a null media list flag leaves it untouched.
    /// </summary>
    public static async Task ConfigureProfileAsync(this JitenWebApplicationFactory factory, string userId, bool isPublic,
                                                   bool? isMediaListPublic = null)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        var user = await userDb.Users.FirstAsync(u => u.Id == userId);
        user.NormalizedUserName = (user.UserName ?? userId).ToUpperInvariant();

        var profile = await userDb.UserProfiles.FirstOrDefaultAsync(p => p.UserId == userId);
        if (profile == null)
        {
            profile = new UserProfile { UserId = userId };
            userDb.UserProfiles.Add(profile);
        }
        profile.IsPublic = isPublic;
        if (isMediaListPublic is { } mediaListPublic)
            profile.IsMediaListPublic = mediaListPublic;

        await userDb.SaveChangesAsync();
    }
}
