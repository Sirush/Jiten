namespace Jiten.Api.Dtos;

public class RequestUserSummaryDto
{
    public int RequestCount { get; set; }
    public int UpvoteCount { get; set; }
    public int BoostCount { get; set; }
    public int SubscriptionCount { get; set; }
    public int UploadCount { get; set; }
    public int FulfilledCount { get; set; }
}
