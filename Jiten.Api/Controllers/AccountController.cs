using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Jiten.Api.Dtos;
using Jiten.Api.Dtos.Requests;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Controllers;

[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("api/account")]
public class AccountController : ControllerBase
{
    private readonly UserManager<User> _userManager;
    private readonly TokenService _tokenService;
    private readonly IEmailService _emailService;
    private readonly UserDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AccountController> _logger;

    private static readonly TimeSpan EmailChangeCooldown = TimeSpan.FromMinutes(15);

    public AccountController(
        UserManager<User> userManager,
        TokenService tokenService,
        IEmailService emailService,
        UserDbContext context,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<AccountController> logger)
    {
        _userManager = userManager;
        _tokenService = tokenService;
        _emailService = emailService;
        _context = context;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    [HttpGet]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> GetAccount()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var user = await _userManager.FindByIdAsync(userId);
        if (user == null) return NotFound("User not found.");

        var roles = await _userManager.GetRolesAsync(user);

        return Ok(new AccountInfoResponse
                  {
                      UserId = user.Id, UserName = user.UserName, Email = user.Email, EmailConfirmed = user.EmailConfirmed,
                      HasPassword = user.PasswordHash != null, CreatedAt = user.CreatedAt, ReceivesNewsletter = user.ReceivesNewsletter,
                      RateLimitTier = user.RateLimitTier.ToString(), Roles = roles
                  });
    }

    [HttpPost("change-password")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest model)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var user = await _userManager.FindByIdAsync(userId);
        if (user == null) return NotFound("User not found.");

        var result = await _userManager.ChangePasswordAsync(user, model.CurrentPassword, model.NewPassword);
        if (!result.Succeeded)
        {
            _logger.LogWarning("Password change failed: UserId={UserId}", user.Id);
            return BadRequest(new { message = "Password change failed.", errors = result.Errors.Select(e => e.Description) });
        }

        var userRefreshTokens = await _context.RefreshTokens
                                              .Where(rt => rt.UserId == user.Id && !rt.IsRevoked && !rt.IsUsed)
                                              .ToListAsync();
        foreach (var rt in userRefreshTokens) rt.IsRevoked = true;
        _context.RefreshTokens.UpdateRange(userRefreshTokens);

        var tokens = await _tokenService.GenerateTokens(user);
        await _context.SaveChangesAsync();

        await _emailService.SendPasswordChangedNoticeAsync(user.Email!);

        _logger.LogInformation("Password changed: UserId={UserId}", user.Id);
        return Ok(tokens);
    }

    [HttpPost("set-password")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> SetPassword([FromBody] SetPasswordRequest model)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var user = await _userManager.FindByIdAsync(userId);
        if (user == null) return NotFound("User not found.");

        if (user.PasswordHash != null)
            return BadRequest(new { message = "A password is already set. Use change password instead." });

        var result = await _userManager.AddPasswordAsync(user, model.NewPassword);
        if (!result.Succeeded)
        {
            _logger.LogWarning("Set password failed: UserId={UserId}", user.Id);
            return BadRequest(new { message = "Failed to set password.", errors = result.Errors.Select(e => e.Description) });
        }

        await _emailService.SendPasswordSetNoticeAsync(user.Email!);

        _logger.LogInformation("Password set: UserId={UserId}", user.Id);
        return Ok(new { message = "Password set successfully." });
    }

    [HttpPost("change-email")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> ChangeEmail([FromBody] ChangeEmailRequest model)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var user = await _userManager.FindByIdAsync(userId);
        if (user == null) return NotFound("User not found.");

        if (user.PasswordHash == null)
            return BadRequest(new { message = "Please set a password before changing your email." });

        if (string.IsNullOrEmpty(model.CurrentPassword) || !await _userManager.CheckPasswordAsync(user, model.CurrentPassword))
            return BadRequest(new { message = "Current password is incorrect." });

        var newEmail = model.NewEmail.Trim();
        if (string.Equals(newEmail, user.Email, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "The new email is the same as your current email." });

        var emailExists = await _userManager.FindByEmailAsync(newEmail);
        if (emailExists != null)
            return BadRequest(new { message = "Email is already in use." });

        if (user.LastEmailChangeRequestedAt is { } lastChange && DateTime.UtcNow - lastChange < EmailChangeCooldown)
            return StatusCode(StatusCodes.Status429TooManyRequests,
                              new { message = "Please wait a moment before requesting another email change." });

        user.LastEmailChangeRequestedAt = DateTime.UtcNow;
        await _userManager.UpdateAsync(user);

        var code = await _userManager.GenerateChangeEmailTokenAsync(user, newEmail);
        code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));

        await _emailService.SendChangeEmailConfirmationAsync(newEmail, user.Id, code);
        await _emailService.SendEmailChangeNoticeAsync(user.Email!, newEmail);

        _logger.LogInformation("Email change requested: UserId={UserId}", user.Id);
        return Ok(new { message = "A confirmation link has been sent to your new email address. Your email changes once confirmed." });
    }

    [HttpPost("confirm-email-change")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> ConfirmEmailChange([FromBody] ConfirmEmailChangeRequest model)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        var user = await _userManager.FindByIdAsync(model.UserId);
        if (user == null) return BadRequest(new { message = "Email change confirmation failed." });

        string decodedToken;
        try
        {
            decodedToken = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(model.Code));
        }
        catch (FormatException)
        {
            return BadRequest(new { message = "Invalid token format." });
        }

        var oldEmail = user.Email;

        var result = await _userManager.ChangeEmailAsync(user, model.NewEmail, decodedToken);
        if (!result.Succeeded)
        {
            _logger.LogWarning("Email change confirmation failed: UserId={UserId}", user.Id);
            return BadRequest(new { message = "Email change confirmation failed.", errors = result.Errors.Select(e => e.Description) });
        }

        var userRefreshTokens = await _context.RefreshTokens
                                              .Where(rt => rt.UserId == user.Id && !rt.IsRevoked && !rt.IsUsed)
                                              .ToListAsync();
        foreach (var rt in userRefreshTokens) rt.IsRevoked = true;
        _context.RefreshTokens.UpdateRange(userRefreshTokens);
        await _context.SaveChangesAsync();

        if (!string.IsNullOrEmpty(oldEmail) && !string.Equals(oldEmail, model.NewEmail, StringComparison.OrdinalIgnoreCase))
            await _emailService.SendEmailChangedAwayNoticeAsync(oldEmail, model.NewEmail);
        await _emailService.SendEmailChangedConfirmationAsync(model.NewEmail);

        _logger.LogInformation("Email changed: UserId={UserId}", user.Id);
        return Ok(new { message = "Your email has been changed. Please log in again." });
    }

    [HttpPost("resend-confirmation")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> ResendConfirmation([FromBody] ResendConfirmationRequest model)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        if (!await ValidateRecaptcha(model.RecaptchaResponse))
            return BadRequest(new { message = "Recaptcha verification failed." });

        var user = await _userManager.FindByEmailAsync(model.Email);
        if (user == null || await _userManager.IsEmailConfirmedAsync(user))
        {
            return Ok(new { message = "If your email address is registered and not yet confirmed, a new confirmation link has been sent." });
        }

        var code = await _userManager.GenerateEmailConfirmationTokenAsync(user);
        code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));

        await _emailService.SendEmailConfirmationAsync(user.Email!, user.Id, code);

        return Ok(new { message = "If your email address is registered and not yet confirmed, a new confirmation link has been sent." });
    }

    [HttpPatch("preferences")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> UpdatePreferences([FromBody] UpdateAccountPreferencesRequest model)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var user = await _userManager.FindByIdAsync(userId);
        if (user == null) return NotFound("User not found.");

        user.ReceivesNewsletter = model.ReceivesNewsletter;
        await _userManager.UpdateAsync(user);

        return Ok(new { receivesNewsletter = user.ReceivesNewsletter });
    }

    private async Task<bool> ValidateRecaptcha(string recaptchaToken)
    {
        var recaptchaSecret = _configuration["Google:RecapatchaSecret"];
        if (string.IsNullOrWhiteSpace(recaptchaToken))
        {
            return false;
        }

        var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString();
        using (var http = _httpClientFactory.CreateClient())
        {
            var form = new FormUrlEncodedContent([
                new KeyValuePair<string, string>("secret", recaptchaSecret!),
                new KeyValuePair<string, string>("response", recaptchaToken),
                new KeyValuePair<string, string>("remoteip", remoteIp ?? string.Empty)
            ]);

            var verifyResponse = await http.PostAsync("https://www.google.com/recaptcha/api/siteverify", form);
            if (!verifyResponse.IsSuccessStatusCode)
            {
                return false;
            }

            var verifyJson = await verifyResponse.Content.ReadAsStringAsync();
            try
            {
                using var doc = JsonDocument.Parse(verifyJson);
                var root = doc.RootElement;
                if (!root.TryGetProperty("success", out var successProperty) || !successProperty.GetBoolean() ||
                    root.TryGetProperty("score", out var scoreProperty) &&
                    scoreProperty.ValueKind == JsonValueKind.Number &&
                    scoreProperty.GetDouble() < 0.5)
                {
                    return false;
                }
            }
            catch (JsonException)
            {
                return false;
            }
        }

        return true;
    }
}
