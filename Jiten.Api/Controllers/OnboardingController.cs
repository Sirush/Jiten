using Jiten.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;

namespace Jiten.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/user/onboarding")]
[Produces("application/json")]
public class OnboardingController(IOnboardingStore onboarding, ICurrentUserService currentUserService) : ControllerBase
{
    [HttpGet]
    [SwaggerOperation(Summary = "Setup state", Description = "The saved level and results plus the facts that decide whether the setup prompt shows.")]
    [ProducesResponseType(typeof(OnboardingStateDto), StatusCodes.Status200OK)]
    public async Task<IResult> Get()
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();
        return Results.Ok(await onboarding.GetStateAsync(userId));
    }

    [HttpPut]
    [SwaggerOperation(Summary = "Update setup state", Description = "Sets the level or path, or records that the wizard was opened or dismissed.")]
    [ProducesResponseType(typeof(OnboardingStateDto), StatusCodes.Status200OK)]
    public async Task<IResult> Update([FromBody] OnboardingUpdateRequest request)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();
        return Results.Ok(await onboarding.UpdateAsync(userId, request));
    }
}
