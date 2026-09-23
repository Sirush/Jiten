namespace Jiten.Core.Data.FSRS;

public enum FsrsVersion
{
    V6 = 6,
    V7 = 7,
}

public static class FsrsVersions
{
    private const double DefaultTolerance = 1e-6;

    /// <summary>Version for default-parameter users (new accounts, never optimised).</summary>
    public static FsrsVersion Unoptimised { get; private set; } = FsrsVersion.V7;

    public static void ConfigureUnoptimised(FsrsVersion version) => Unoptimised = version;

    /// <summary>Parameter count is the version marker, matching fsrs-rs and Anki.</summary>
    public static FsrsVersion? FromParameterCount(int count) => count switch
    {
        21 => FsrsVersion.V6,
        34 => FsrsVersion.V7,
        _ => null,
    };

    public static double[] DefaultParameters(FsrsVersion version)
        => version == FsrsVersion.V7 ? FsrsConstants.DefaultParametersV7 : FsrsConstants.DefaultParameters;

    public static int ParameterCount(FsrsVersion version) => DefaultParameters(version).Length;

    /// <summary>Empty or the unoptimised version's defaults; the other version's defaults are a deliberate pin to that version.</summary>
    public static bool FollowsUnoptimised(double[] parameters)
        => parameters.Length == 0 || IsDefaultFor(parameters, Unoptimised);

    public static bool IsDefaultFor(double[] parameters, FsrsVersion version)
    {
        var defaults = DefaultParameters(version);
        if (parameters.Length != defaults.Length)
            return false;

        for (var i = 0; i < parameters.Length; i++)
            if (Math.Abs(parameters[i] - defaults[i]) > DefaultTolerance)
                return false;

        return true;
    }
}
