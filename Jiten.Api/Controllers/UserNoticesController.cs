using Jiten.Api.Services;
using Jiten.Api.Services.Notices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;

namespace Jiten.Api.Controllers;

[ApiController]
[Route("api/user/notices")]
[Authorize]
public class UserNoticesController(IOneTimeNoticeService notices, ICurrentUserService currentUserService) : ControllerBase
{
    [HttpGet]
    [SwaggerOperation(Summary = "One-time notices for a page that the user qualifies for and has not dismissed")]
    public async Task<ActionResult<List<ActiveNoticeDto>>> GetActive([FromQuery] string surface)
    {
        if (string.IsNullOrWhiteSpace(surface)) return BadRequest(new { error = "surface is required" });

        return Ok(await notices.GetActive(currentUserService.UserId!, surface, DateTime.UtcNow));
    }

    [HttpPost("{key}/dismiss")]
    [SwaggerOperation(Summary = "Dismiss a one-time notice on every device")]
    public async Task<IActionResult> Dismiss(string key)
    {
        return await notices.Dismiss(currentUserService.UserId!, key) ? NoContent() : NotFound();
    }
}
