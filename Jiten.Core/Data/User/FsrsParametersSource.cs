namespace Jiten.Core.Data.User;

/// <summary>Where a user's stored FSRS parameters came from; decides whether the nightly refit may replace them.</summary>
public enum FsrsParametersSource
{
    Default = 0,
    Optimised = 1,
    Auto = 2,

    /// <summary>Typed or pasted by the user; never replaced automatically.</summary>
    Edited = 3,

    /// <summary>Stored before provenance was tracked, or restored by a model switch.</summary>
    Legacy = 4
}

public enum FsrsAutoOptimiseOutcome
{
    None = 0,
    Accepted = 1,
    NoGain = 2,
    HardAsFail = 3,
    NotEnoughData = 4,
    Conflict = 5,
    Failed = 6
}
