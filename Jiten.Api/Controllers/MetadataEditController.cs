using System.Security.Claims;
using Jiten.Api.Services.MetadataEdits;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Jiten.Api.Controllers;

/// <summary>Every route needs a signed-in browser session; API keys and signed-out visitors get nothing, history included.</summary>
[ApiController]
[Route("api")]
[ApiExplorerSettings(IgnoreApi = true)]
[Authorize(Policy = "RequiresAccountSession")]
public class MetadataEditController(MetadataEditService edits) : ControllerBase
{
    private string? UserId => User.FindFirstValue(ClaimTypes.NameIdentifier);
    private string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();

    private IActionResult Respond<T>(ServiceResult<T> result) =>
        result.IsOk
            ? Ok(result.Value)
            : StatusCode(result.Status, result.Extra == null
                                            ? new { message = result.Error }
                                            : MergeExtra(result.Error!, result.Extra));

    private static Dictionary<string, object?> MergeExtra(string message, object extra)
    {
        var payload = new Dictionary<string, object?> { ["message"] = message };
        foreach (var property in extra.GetType().GetProperties())
            payload[property.Name] = property.GetValue(extra);
        return payload;
    }

    [HttpGet("media-deck/{id:int}/editable")]
    public async Task<IActionResult> GetEditable(int id, CancellationToken ct) =>
        Respond(await edits.GetEditableAsync(id, UserId!, ct));

    [HttpPost("media-deck/{id:int}/character-names/check")]
    [EnableRateLimiting("metadata-edit")]
    public async Task<IActionResult> CheckNames(int id, [FromBody] CheckNamesRequest request, CancellationToken ct) =>
        Respond(await edits.CheckNamesAsync(id, UserId!, request.Names, ct));

    [HttpPost("media-deck/{id:int}/metadata-edits")]
    [EnableRateLimiting("metadata-edit")]
    public async Task<IActionResult> Submit(int id, [FromBody] SubmitMetadataEditRequest request, CancellationToken ct) =>
        Respond(await edits.SubmitAsync(id, UserId!, Ip, request, ct));

    [HttpGet("media-deck/{id:int}/metadata-edits")]
    public async Task<IActionResult> DeckHistory(int id, [FromQuery] int offset = 0, [FromQuery] int limit = 30, CancellationToken ct = default) =>
        Respond(await edits.DeckHistoryAsync(id, UserId, offset, limit, ct));

    [HttpGet("metadata-edits/pending")]
    public async Task<IActionResult> Pending([FromQuery] string? state, [FromQuery] string? sort, [FromQuery] int? tier, [FromQuery] int? deckId,
                                             [FromQuery] int offset = 0, [FromQuery] int limit = 20, CancellationToken ct = default) =>
        Ok(await edits.QueueAsync(UserId!, state, sort, tier, deckId, offset, limit, ct));

    [HttpGet("metadata-edits/recent")]
    public async Task<IActionResult> Recent([FromQuery] string? status, [FromQuery] int offset = 0, [FromQuery] int limit = 50,
                                            CancellationToken ct = default) =>
        Respond(await edits.RecentAsync(UserId, status, offset, limit, ct));

    [HttpPut("metadata-edits/{id:long}/check")]
    public async Task<IActionResult> SetCheck(long id, [FromBody] MetadataEditCheckRequest request, CancellationToken ct) =>
        Respond(await edits.SetCheckAsync(id, UserId!, Ip, request, ct));

    [HttpDelete("metadata-edits/{id:long}/check")]
    public async Task<IActionResult> RemoveCheck(long id, CancellationToken ct)
    {
        var result = await edits.RemoveCheckAsync(id, UserId!, ct);
        return result.IsOk ? NoContent() : Respond(result);
    }

    [HttpGet("metadata-edits/{id:long}/name-preview")]
    public async Task<IActionResult> NamePreview(long id, CancellationToken ct) =>
        Respond(await edits.NamePreviewAsync(id, UserId!, ct));

    [HttpPost("metadata-edits/{id:long}/approve")]
    public async Task<IActionResult> Approve(long id, [FromBody] ApproveMetadataEditRequest? request, CancellationToken ct) =>
        Respond(await edits.ApproveAsync(id, UserId!, request ?? new ApproveMetadataEditRequest(), ct));

    [HttpPost("metadata-edits/{id:long}/reject")]
    public async Task<IActionResult> Reject(long id, [FromBody] RejectMetadataEditRequest request, CancellationToken ct) =>
        Respond(await edits.RejectAsync(id, UserId!, request, ct));

    [HttpPost("metadata-edits/{id:long}/reopen")]
    public async Task<IActionResult> Reopen(long id, CancellationToken ct) =>
        Respond(await edits.ReopenAsync(id, UserId!, ct));

    [HttpPost("metadata-edits/{id:long}/withdraw")]
    public async Task<IActionResult> Withdraw(long id, CancellationToken ct) =>
        Respond(await edits.WithdrawAsync(id, UserId!, ct));

    [HttpPost("metadata-edits/{id:long}/revert")]
    public async Task<IActionResult> Revert(long id, CancellationToken ct) =>
        Respond(await edits.RevertAsync(id, UserId!, ct));

    [HttpPost("metadata-edits/{id:long}/confirm-applied")]
    public async Task<IActionResult> ConfirmApplied(long id, CancellationToken ct) =>
        Respond(await edits.ConfirmAppliedAsync(id, UserId!, ct));

    [HttpGet("metadata-edits/contributions/{userId}")]
    public async Task<IActionResult> Contributions(string userId, CancellationToken ct) =>
        Ok(await edits.ContributionsAsync(userId, UserId, ct));

    [HttpGet("metadata-edits/by-user/{userId}")]
    public async Task<IActionResult> ByUser(string userId, [FromQuery] int offset = 0, [FromQuery] int limit = 30, CancellationToken ct = default) =>
        Ok(await edits.ByUserAsync(userId, UserId, offset, limit, ct));

    [HttpGet("admin/metadata-edits/settings")]
    [Authorize("RequiresAdmin")]
    public async Task<IActionResult> GetSettings(CancellationToken ct) => Ok(await edits.GetSettingsAsync(ct));

    [HttpPut("admin/metadata-edits/settings")]
    [Authorize("RequiresAdmin")]
    public async Task<IActionResult> UpdateSettings([FromBody] MetadataEditSettingsDto dto, CancellationToken ct) =>
        Respond(await edits.UpdateSettingsAsync(dto, ct));

    [HttpGet("admin/metadata-edits/metrics")]
    [Authorize("RequiresAdmin")]
    public async Task<IActionResult> Metrics(CancellationToken ct) => Ok(await edits.MetricsAsync(ct));

    [HttpGet("admin/metadata-edits/contributors")]
    [Authorize("RequiresAdmin")]
    public async Task<IActionResult> Contributors([FromQuery] string? search, [FromQuery] string? level, [FromQuery] string? status,
                                                  [FromQuery] string? sort, [FromQuery] int offset = 0, [FromQuery] int limit = 50,
                                                  CancellationToken ct = default) =>
        Ok(await edits.ContributorsAsync(search, level, status, sort, offset, limit, ct));

    [HttpPut("admin/metadata-edits/contributors/{userId}")]
    [Authorize("RequiresAdmin")]
    public async Task<IActionResult> UpdateContributor(string userId, [FromBody] UpdateContributorRequest request, CancellationToken ct) =>
        Respond(await edits.UpdateContributorAsync(userId, request, ct));

    [HttpPost("admin/metadata-edits/contributors/{userId}/revert-all")]
    [Authorize("RequiresAdmin")]
    public async Task<IActionResult> RevertAll(string userId, CancellationToken ct) =>
        Respond(await edits.RevertAllAsync(userId, UserId!, ct));
}
