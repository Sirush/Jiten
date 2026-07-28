using System.Security.Claims;
using Jiten.Api.Services;
using Jiten.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

[ApiController]
[Route("api/api-key")]
public class ApiKeyController(
    UserDbContext context,
    ApiKeyService apiKeyService,
    ILogger<ApiKeyController> logger,
    ICurrentUserService currentUserService)
    : ControllerBase
{
    [HttpGet("info")]
    [Authorize]
    public async Task<IActionResult> GetApiKeyInfo()
    {
        var userId = currentUserService.UserId!;

        try
        {
            // Revoked keys are dead and offer the user no action; surfacing them leaves the settings tile
            // with nothing but a disabled Regenerate button after a half-failed regeneration.
            var apiKey = await context.ApiKeys
                                      .Where(k => k.UserId == userId && !k.IsRevoked)
                                      .OrderByDescending(k => k.Id)
                                      .Select(k => new
                                                   {
                                                       id = k.Id, createdAt = k.CreatedAt, lastUsedAt = k.LastUsedAt,
                                                       expiresAt = k.ExpiresAt, isRevoked = k.IsRevoked,
                                                       keyPreview = $"ak_...{k.Hash.Substring(Math.Max(0, k.Hash.Length - 8))}"
                                                   })
                                      .FirstOrDefaultAsync();

            return Ok(new { apiKey });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving API key info for user {UserId}", userId);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    [HttpPost("create")]
    [Authorize]
    public async Task<IActionResult> CreateApiKey()
    {
        var userId = currentUserService.UserId!;

        try
        {
            var existingCount = await context.ApiKeys
                                             .CountAsync(k => k.UserId == userId && !k.IsRevoked);

            if (existingCount >= 1)
            {
                return BadRequest(new { error = "Maximum number of API keys reached" });
            }

            var plainKey = apiKeyService.GenerateApiKey();
            var hash = apiKeyService.ComputeHash(plainKey);
            var now = DateTime.UtcNow;

            var apiKey = new ApiKey
                         {
                             UserId = userId, Hash = hash, CreatedAt = now, ExpiresAt = DateTime.UtcNow.AddYears(10), IsRevoked = false
                         };

            await context.ApiKeys.AddAsync(apiKey);
            await context.SaveChangesAsync();

            logger.LogInformation("API key created for user {UserId}", userId);

            // Return the plain API key once - it won't be stored
            return Ok(new
                      {
                          apiKey = plainKey, id = apiKey.Id,
                          message = "API key created successfully. Store it securely - it won't be shown again."
                      });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating API key for user {UserId}", userId);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    [HttpPost("{keyId}/revoke")]
    [Authorize]
    public async Task<IActionResult> RevokeApiKey(int keyId)
    {
        var userId = currentUserService.UserId!;

        try
        {
            var apiKey = await context.ApiKeys
                                      .FirstOrDefaultAsync(k => k.Id == keyId && k.UserId == userId);

            if (apiKey == null)
            {
                return NotFound(new { error = "API key not found" });
            }

            if (apiKey.IsRevoked)
            {
                // Idempotent: revoking an already-revoked key is a no-op
                logger.LogInformation("API key {KeyId} already revoked for user {UserId}", keyId, userId);
                return Ok(new { message = "API key revoked successfully" });
            }

            apiKey.IsRevoked = true;
            apiKey.RevokedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();

            logger.LogInformation("API key {KeyId} revoked for user {UserId}", keyId, userId);
            return Ok(new { message = "API key revoked successfully" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error revoking API key {KeyId} for user {UserId}", keyId, userId);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }
}