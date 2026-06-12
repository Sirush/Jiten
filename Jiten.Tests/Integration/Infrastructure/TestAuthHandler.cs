using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Jiten.Parser.Tests.Integration.Infrastructure;

/// <summary>
/// Dual-mode auth handler for integration tests:
///  - Header mode: an <c>X-Test-UserId</c> header (optionally <c>X-Test-Role</c>) authenticates as that user
///    without a real token. Used by the bulk of integration tests.
///  - Real-JWT mode: when no test header is present but a real <c>Authorization: Bearer &lt;jwt&gt;</c> is, the token
///    is validated with the same parameters as production and its claims (including the real <c>jti</c>) are used.
///    This lets tests that need genuine token/refresh flows (change-password revocation, revoke-token keepCurrent)
///    drive the real /api/auth endpoints and then call account endpoints with the issued bearer token.
///
/// The handler is registered under both the default test scheme name and the JWT bearer scheme name so that
/// controllers restricting to <c>JwtBearerDefaults.AuthenticationScheme</c> resolve to it.
/// </summary>
public class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration configuration)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "TestScheme";

    private readonly IConfiguration _configuration = configuration;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers.TryGetValue("X-Test-UserId", out var userIdValues))
        {
            var userId = userIdValues.FirstOrDefault();
            if (string.IsNullOrEmpty(userId))
                return Task.FromResult(AuthenticateResult.NoResult());

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, userId)
            };

            // Allow tests to pin a jti so keepCurrent-style flows can be exercised in pure header mode if needed.
            if (Request.Headers.TryGetValue("X-Test-Jti", out var jtiValues) && !string.IsNullOrEmpty(jtiValues.FirstOrDefault()))
                claims.Add(new Claim(JwtRegisteredClaimNames.Jti, jtiValues.First()!));

            if (Request.Headers.TryGetValue("X-Test-Role", out var roleValues))
            {
                var role = roleValues.FirstOrDefault();
                if (!string.IsNullOrEmpty(role))
                    claims.Add(new Claim(ClaimTypes.Role, role));
            }

            return Task.FromResult(Success(claims));
        }

        // Real JWT path: validate an Authorization: Bearer token exactly like production does.
        var authHeader = Request.Headers["Authorization"].FirstOrDefault();
        if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = authHeader["Bearer ".Length..].Trim();
            var jwtSettings = _configuration.GetSection("JwtSettings");
            var secret = jwtSettings["Secret"];
            if (string.IsNullOrEmpty(secret))
                return Task.FromResult(AuthenticateResult.Fail("JWT secret not configured for tests."));

            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = jwtSettings["Issuer"],
                ValidAudience = jwtSettings["Audience"],
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(secret)),
                ClockSkew = TimeSpan.Zero
            };

            try
            {
                var handler = new JwtSecurityTokenHandler();
                var principal = handler.ValidateToken(token, validationParameters, out _);
                var ticket = new AuthenticationTicket(principal, SchemeName);
                return Task.FromResult(AuthenticateResult.Success(ticket));
            }
            catch
            {
                return Task.FromResult(AuthenticateResult.Fail("Invalid bearer token."));
            }
        }

        return Task.FromResult(AuthenticateResult.NoResult());
    }

    private AuthenticateResult Success(List<Claim> claims)
    {
        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return AuthenticateResult.Success(ticket);
    }
}
