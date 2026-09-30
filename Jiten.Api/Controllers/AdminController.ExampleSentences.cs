using Jiten.Api.Services;
using Jiten.Core.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Controllers;

public partial class AdminController
{
    /// <summary>i+1 sentences for a form, judged against the calling admin's own known words.</summary>
    [HttpGet("example-sentences/i-plus-one/{wordId:int}/{readingIndex:int}")]
    public async Task<IResult> GetIPlusOneSentences(int wordId, byte readingIndex, [FromServices] ISentenceTokenService sentenceTokens,
                                                    [FromQuery] int take = 10, [FromQuery] int maxUnknown = 0)
    {
        var sentences = await sentenceTokens.FindIPlusOneAsync(wordId, readingIndex, Math.Clamp(take, 1, 50), Math.Clamp(maxUnknown, 0, 3));
        return Results.Ok(sentences);
    }

    /// <summary>Rows still holding only the picked words converted from the old link table; a reparse of their deck fills them in.</summary>
    [HttpGet("example-sentences/token-coverage")]
    public async Task<IResult> GetSentenceTokenCoverage()
    {
        var total = await dbContext.ExampleSentences.LongCountAsync();
        var partial = await dbContext.Database
            .SqlQueryRaw<long>(@"SELECT count(*) AS ""Value"" FROM jiten.""ExampleSentences"" WHERE get_byte(""Tokens"", 0) >= {0}",
                               (int)ExampleSentenceTokens.PartialFlag)
            .SingleAsync();
        return Results.Ok(new { total, partial });
    }
}
