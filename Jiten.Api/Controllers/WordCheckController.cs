using Jiten.Api.Services;
using Jiten.Api.Services.WordCheck;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;

namespace Jiten.Api.Controllers;

public sealed record WordCheckStartRequest(WordCheckLevel? Level);

public sealed record WordCheckAnswerRequest(string SessionId, List<WordCheckAnswerDto> Answers);

[ApiController]
[Authorize]
[Route("api/word-check")]
[Produces("application/json")]
public class WordCheckController(IWordCheckService wordCheck, ICurrentUserService currentUserService) : ControllerBase
{
    [HttpPost("start")]
    [SwaggerOperation(Summary = "Start a word check", Description = "Returns the first batch of words. The level only picks the starting band.")]
    [ProducesResponseType(typeof(WordCheckStepDto), StatusCodes.Status200OK)]
    public Task<IResult> Start([FromBody] WordCheckStartRequest request)
        => CheckEndpoint.Run(currentUserService.UserId, userId => wordCheck.StartAsync(userId, request.Level));

    [HttpPost("answer")]
    [SwaggerOperation(Summary = "Answer the current batch", Description = "Returns the next batch, the final check words, or a failed result.")]
    [ProducesResponseType(typeof(WordCheckStepDto), StatusCodes.Status200OK)]
    public Task<IResult> Answer([FromBody] WordCheckAnswerRequest request)
        => request.Answers.Count is 0 or > CheckEndpoint.MaxAnswers
            ? Task.FromResult(Results.BadRequest("Invalid answers"))
            : CheckEndpoint.Run(currentUserService.UserId, userId => wordCheck.AnswerAsync(userId, request.SessionId, request.Answers));

    [HttpPost("confirm")]
    [SwaggerOperation(Summary = "Finish the word check", Description = "Applies the final check and marks the common words as known under one undoable batch.")]
    [ProducesResponseType(typeof(WordCheckResultDto), StatusCodes.Status200OK)]
    public Task<IResult> Confirm([FromBody] CheckConfirmRequest request)
        => request.UnknownIds.Count > CheckEndpoint.MaxAnswers
            ? Task.FromResult(Results.BadRequest("Invalid answers"))
            : CheckEndpoint.Run(currentUserService.UserId, userId => wordCheck.ConfirmAsync(userId, request.SessionId, request.UnknownIds));
}
