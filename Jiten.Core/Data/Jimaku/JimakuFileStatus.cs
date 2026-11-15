namespace Jiten.Core.Data.Jimaku;

public enum JimakuFileStatus
{
    /// <summary>Cleared for ingestion at the next sync, either by the planner or by an admin approval</summary>
    Queued = 0,
    Imported = 1,
    /// <summary>Waiting for an admin decision; re-evaluated at every sync</summary>
    Held = 2,
    Ignored = 3
}
