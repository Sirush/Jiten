using Jiten.Core.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Controllers;

public partial class AdminController
{
    [HttpGet("sentence-profiles/coverage")]
    public async Task<IResult> GetSentenceProfileCoverage()
    {
        var withText = await dbContext.DeckRawTexts.CountAsync();
        var profiled = await dbContext.DeckSentenceProfiles.CountAsync(p => p.Profile != null && p.Version == SentenceProfileCodec.FormatVersion);
        var samples = await dbContext.DeckSentenceProfiles.CountAsync(p => p.Sample != null);
        return Results.Ok(new { withText, profiled, samples });
    }
}
