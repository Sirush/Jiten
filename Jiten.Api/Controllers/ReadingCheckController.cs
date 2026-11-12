using Jiten.Api.Services;
using Jiten.Api.Services.ReadingCheck;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;

namespace Jiten.Api.Controllers;

public sealed record ReadingDecksRequest(List<int> DeckIds);

public sealed record ReadingVerifyRequest(string SessionId, int? Level);

public sealed record ReadingAnswerRequest(string SessionId, List<ReadingAnswerDto> Answers);

[ApiController]
[Authorize]
[Route("api/reading-check")]
[Produces("application/json")]
public class ReadingCheckController(IReadingCheckService readingCheck, IReadingCheckData data, ICurrentUserService currentUserService) : ControllerBase
{
    private const int MaxDecks = 500;

    [HttpGet("works")]
    [SwaggerOperation(Summary = "List the works and volumes on the media list, with finished units ticked")]
    [ProducesResponseType(typeof(List<ReadingWorkDto>), StatusCodes.Status200OK)]
    public Task<IResult> GetWorks()
        => CheckEndpoint.Run(currentUserService.UserId, data.GetWorksAsync);

    [HttpGet("works/{deckId:int}")]
    [SwaggerOperation(Summary = "Picker row for a title found by search")]
    [ProducesResponseType(typeof(ReadingWorkDto), StatusCodes.Status200OK)]
    public async Task<IResult> GetWork(int deckId)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();
        var work = await data.GetWorkAsync(userId, deckId);
        return work == null ? Results.NotFound() : Results.Ok(work);
    }

    [HttpPost("count")]
    [SwaggerOperation(Summary = "Count read units and candidate words for a selection")]
    [ProducesResponseType(typeof(ReadingCountDto), StatusCodes.Status200OK)]
    public Task<IResult> Count([FromBody] ReadingDecksRequest request)
        => request.DeckIds.Count > MaxDecks
            ? Task.FromResult(Results.BadRequest("Too many volumes selected."))
            : CheckEndpoint.Run(currentUserService.UserId, userId => readingCheck.CountAsync(userId, request.DeckIds));

    [HttpPost("start")]
    [SwaggerOperation(Summary = "Start a reading check over the selected volumes and works")]
    [ProducesResponseType(typeof(ReadingStepDto), StatusCodes.Status200OK)]
    public Task<IResult> Start([FromBody] ReadingDecksRequest request)
        => request.DeckIds.Count > MaxDecks
            ? Task.FromResult(Results.BadRequest("Too many volumes selected."))
            : CheckEndpoint.Run(currentUserService.UserId, userId => readingCheck.StartAsync(userId, request.DeckIds));

    [HttpPost("answer")]
    [SwaggerOperation(Summary = "Answer the current words")]
    [ProducesResponseType(typeof(ReadingStepDto), StatusCodes.Status200OK)]
    public Task<IResult> Answer([FromBody] ReadingAnswerRequest request)
        => request.Answers.Count is 0 or > CheckEndpoint.MaxAnswers
            ? Task.FromResult(Results.BadRequest("Invalid answers"))
            : CheckEndpoint.Run(currentUserService.UserId, userId => readingCheck.AnswerAsync(userId, request.SessionId, request.Answers));

    [HttpPost("verify")]
    [SwaggerOperation(Summary = "Lock in a risk level and get the final check words", Description = "Level 1 is the most careful, 5 the boldest; null keeps the default.")]
    [ProducesResponseType(typeof(ReadingStepDto), StatusCodes.Status200OK)]
    public Task<IResult> Verify([FromBody] ReadingVerifyRequest request)
        => CheckEndpoint.Run(currentUserService.UserId, userId => readingCheck.VerifyAsync(userId, request.SessionId, request.Level));

    [HttpPost("review")]
    [SwaggerOperation(Summary = "Settle the current review page and get the next one", Description = "Words not listed as unknown count as known. An empty page means no word is left to review.")]
    [ProducesResponseType(typeof(ReadingStepDto), StatusCodes.Status200OK)]
    public Task<IResult> Review([FromBody] CheckConfirmRequest request)
        => request.UnknownIds.Count > CheckEndpoint.MaxAnswers
            ? Task.FromResult(Results.BadRequest("Invalid answers"))
            : CheckEndpoint.Run(currentUserService.UserId, userId => readingCheck.ReviewAsync(userId, request.SessionId, request.UnknownIds));

    [HttpPost("confirm")]
    [SwaggerOperation(Summary = "Finish the reading check and mark the words under one undoable batch")]
    [ProducesResponseType(typeof(ReadingResultDto), StatusCodes.Status200OK)]
    public Task<IResult> Confirm([FromBody] CheckConfirmRequest request)
        => request.UnknownIds.Count > CheckEndpoint.MaxAnswers
            ? Task.FromResult(Results.BadRequest("Invalid answers"))
            : CheckEndpoint.Run(currentUserService.UserId, userId => readingCheck.ConfirmAsync(userId, request.SessionId, request.UnknownIds));
}
