using System.Collections.Frozen;
using Jiten.Core;
using Jiten.Core.Data.JMDict;

namespace Jiten.Api.Services;

public interface IWordSetMemberCache
{
    /// <summary>Forms outside the set that knowing its members makes kana-redundant.</summary>
    Task<FrozenSet<long>> GetKanaRedundantAsync(int setId);

    /// <summary>Forms outside the set that knowing its members makes derivation-redundant under these categories.</summary>
    Task<FrozenSet<long>> GetDerivationRedundantAsync(int setId, IReadOnlySet<DerivationCategory> categories);

    /// <summary>The user's subscribed sets with their members. Redundancy extras load only when asked for, since
    /// most callers check membership alone.</summary>
    Task<UserWordSets> LoadForUserAsync(UserDbContext userContext, string userId, bool withKanaRedundancy = false,
                                        IReadOnlySet<DerivationCategory>? derivationCategories = null);

    /// <summary>Bumps the set's generation; must follow every membership write, or readers keep the old members for hours.</summary>
    void Invalidate(int setId);
}
