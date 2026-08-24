using System.ComponentModel.DataAnnotations;

namespace Jiten.Api.Dtos.Requests;

public class UpdateStudyDeckRequest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    [Range(1, 6)]
    public int DownloadType { get; set; }
    [Range(1, 5)]
    public int Order { get; set; }
    [Range(0, int.MaxValue)]
    public int MinFrequency { get; set; }
    [Range(0, int.MaxValue)]
    public int MaxFrequency { get; set; }
    [Range(0f, 100f)]
    public float? TargetPercentage { get; set; }
    public bool StartFromKnown { get; set; }
    public int? MinOccurrences { get; set; }
    public int? MaxOccurrences { get; set; }
    public bool ExcludeKana { get; set; }
    public int? MinGlobalFrequency { get; set; }
    public int? MaxGlobalFrequency { get; set; }
    public string? PosFilter { get; set; }

    public int? FrequencyMediaType { get; set; }
    public long? FrequencyListId { get; set; }
}
