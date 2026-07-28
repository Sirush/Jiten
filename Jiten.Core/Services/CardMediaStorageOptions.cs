using Jiten.Core.Data.Billing;

namespace Jiten.Core.Services;

/// <summary>
/// Per-tier card-media storage allowances, bound from the <c>JitenPlus:CardMediaStorage</c> config
/// section so they can be retuned per environment without a code change.
/// </summary>
public sealed class CardMediaStorageOptions
{
    public const string SectionName = "JitenPlus:CardMediaStorage";

    private const long Megabyte = 1024L * 1024;
    private const long Gigabyte = 1024L * Megabyte;

    /// <summary>Free and lapsed accounts keep read and delete access on existing media but cannot upload.</summary>
    public long NoneBytes { get; set; }

    public long TrialBytes { get; set; } = 300 * Megabyte;

    public long FullBytes { get; set; } = 10 * Gigabyte;

    public long ForTier(JitenPlusTier tier) => tier switch
    {
        JitenPlusTier.Full => FullBytes,
        JitenPlusTier.Trial => TrialBytes,
        _ => NoneBytes
    };
}
