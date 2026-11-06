using System.Text.Json.Serialization;
using Jiten.Api.Helpers;
using Jiten.Core.Data.User;

namespace Jiten.Api.Dtos;

public class FsrsParametersResponse
{
    public string Parameters { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public double DesiredRetention { get; set; }
    public int ReviewCount { get; set; }
    public int MinimumReviewsForOptimize { get; set; }

    /// <summary>FSRS major version the parameters belong to (6 or 7); the parameter count follows from it.</summary>
    public int Version { get; set; }

    public double[] DefaultParameters { get; set; } = [];

    [JsonConverter(typeof(CamelCaseEnumConverter<FsrsParametersSource>))]
    public FsrsParametersSource Source { get; set; }

    public DateTime? FittedAtUtc { get; set; }
    public int FittedReviewCount { get; set; }
    public bool AutoOptimise { get; set; } = true;

    public long? UndoHistoryId { get; set; }
}

public class FsrsApplyResponse : FsrsParametersResponse
{
    public bool Rescheduled { get; set; }
}

public class FsrsParameterHistoryDto
{
    public long Id { get; set; }
    public string Parameters { get; set; } = string.Empty;
    public int Version { get; set; }

    [JsonConverter(typeof(CamelCaseEnumConverter<FsrsParametersSource>))]
    public FsrsParametersSource Source { get; set; }
    public double? Loss { get; set; }
    public double? PreviousLoss { get; set; }
    public int ReviewCount { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? RevertedAtUtc { get; set; }
    public bool IsCurrent { get; set; }
}
