using System.Collections.Frozen;
using Jiten.Api.Helpers;
using Jiten.Core.Data;

namespace Jiten.Api.Services;

public sealed class UserWordSets
{
    /// <summary>Generation changes whenever the set's membership is invalidated, so derived caches can key on it.</summary>
    public sealed record Subscription(
        int SetId,
        int Generation,
        WordSetStateType State,
        FrozenSet<long> Members,
        FrozenSet<long>? KanaRedundant,
        FrozenSet<long>? DerivationRedundant);

    public static readonly UserWordSets Empty = new([]);

    private readonly Subscription[] _sets;

    /// <summary>Mastered subscriptions come first so the first hit carries the winning state.</summary>
    public UserWordSets(IEnumerable<Subscription> sets)
    {
        _sets = sets.OrderByDescending(s => s.State == WordSetStateType.Mastered).ToArray();
    }

    public bool IsEmpty => _sets.Length == 0;

    public IReadOnlyList<Subscription> Sets => _sets;

    /// <summary>Mastered wins over Blacklisted when several subscribed sets hold the form.</summary>
    public bool TryGetState(long key, out WordSetStateType state)
    {
        foreach (var set in _sets)
        {
            if (!set.Members.Contains(key)) continue;
            state = set.State;
            return true;
        }

        state = default;
        return false;
    }

    public bool TryGetState((int WordId, byte ReadingIndex) key, out WordSetStateType state)
        => TryGetState(WordFormHelper.EncodeWordKey(key.WordId, key.ReadingIndex), out state);

    public bool Contains(long key) => TryGetState(key, out _);

    public bool Contains((int WordId, byte ReadingIndex) key) => TryGetState(key, out _);

    /// <summary>Member of a set, or made redundant by one through whichever extras were loaded.</summary>
    public bool Covers(long key)
    {
        foreach (var set in _sets)
        {
            if (set.Members.Contains(key)) return true;
            if (set.KanaRedundant?.Contains(key) == true) return true;
            if (set.DerivationRedundant?.Contains(key) == true) return true;
        }

        return false;
    }

    /// <summary>Identifies the subscription mix, for caches of results every user with the same mix shares.</summary>
    public string CacheKey => string.Join(',', _sets.OrderBy(s => s.SetId).Select(s => $"{s.SetId}.{s.Generation}:{(int)s.State}"));
}
