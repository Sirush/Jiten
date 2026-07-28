namespace Jiten.Api.Jobs;

/// <summary>
/// Coverage work is split by cost, not by kind: the full recompute scans the whole of DeckWords and
/// has to run alone, while the per-deck jobs are small enough to want parallelism.
/// </summary>
public static class CoverageQueues
{
    /// <summary>Light per-deck and per-user jobs. Multi-worker.</summary>
    public const string Incremental = "coverage";

    /// <summary>Whole-catalogue recomputes. Single worker — concurrent runs evict the shared buffer pool.</summary>
    public const string Full = "coverage-full";
}
