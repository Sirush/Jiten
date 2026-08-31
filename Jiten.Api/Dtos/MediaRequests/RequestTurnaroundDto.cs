namespace Jiten.Api.Dtos;

public class RequestTurnaroundDto
{
    public double? MedianDays { get; set; }
    public double? P75Days { get; set; }
    public int SampleSize { get; set; }
    public int ReadyToProcess { get; set; }
    public int AwaitingFile { get; set; }
    public double? MedianAwaitingFileDays { get; set; }
}
