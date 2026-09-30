using System.Security.Claims;
using Jiten.Api.Dtos.Requests;
using Jiten.Api.Services;
using Jiten.Core.Data.Authentication;
using Jiten.Core.Data.Billing;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Jiten.Api.Controllers;

[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("api/community")]
public class CommunityController(
    UserManager<User> userManager,
    IJitenPlusService jitenPlusService,
    IOptions<DiscourseOptions> discourseOptions,
    ILogger<CommunityController> logger) : ControllerBase
{
    public const string DisplayNameRequiredCode = "display_name_required";
    private const string JitenPlusGroup = "jiten-plus";

    /// <summary>Answers a DiscourseConnect login: returns the signed forum URL the browser must be sent to.</summary>
    [HttpPost("sso")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> DiscourseSso([FromBody] DiscourseSsoRequest body)
    {
        var options = discourseOptions.Value;
        if (!options.IsConfigured)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "Jiten Community isn't available right now." });

        var request = DiscourseConnect.ReadRequest(body.Sso, body.Sig, options, logger);
        if (request == null)
            return BadRequest(new { message = "This login link is invalid or has expired. Go back to Jiten Community and try again." });

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var user = await userManager.FindByIdAsync(userId);
        if (user == null) return Unauthorized();

        if (string.IsNullOrEmpty(user.DisplayName))
            return Conflict(new { code = DisplayNameRequiredCode, message = "Choose a display name before joining Jiten Community." });

        var isFullMember = await jitenPlusService.GetTierAsync(user.Id) == JitenPlusTier.Full;

        var fields = new List<KeyValuePair<string, string>>
        {
            new("external_id", user.Id),
            new("email", user.Email ?? string.Empty),
            new("username", user.DisplayName),
            new("name", user.DisplayName),
            new(isFullMember ? "add_groups" : "remove_groups", JitenPlusGroup)
        };

        // Discourse re-verifies the address itself instead of trusting an unconfirmed one.
        if (!user.EmailConfirmed)
            fields.Add(new("require_activation", "true"));

        // Sent only when true: an explicit false would revoke forum admin granted by hand on the forum.
        if (User.IsInRole("Administrator"))
            fields.Add(new("admin", "true"));

        logger.LogInformation("DiscourseConnect login: UserId={UserId}", user.Id);
        return Ok(new { returnUrl = DiscourseConnect.BuildReturnUrl(request.Value, fields, options) });
    }
}
