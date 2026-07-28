using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Jiten.Api.Controllers;

[ApiController]
[Route("api/frequency-list")]
public class FrequencyListController(ILogger<FrequencyListController> logger) : ControllerBase
{
    private static readonly string[] DownloadTypes = ["yomitan", "csv"];

    /// <summary>
    /// DON'T TOUCH THIS
    /// IT'S BEING USED BY THE YOMITAN EXTENSION
    /// </summary>
    /// <param name="mediaType"></param>
    /// <param name="downloadType"></param>
    /// <returns></returns>
    [HttpGet("download")]
    [EnableRateLimiting("download")]
    public async Task<IResult> GetFrequencyList([FromQuery] MediaType? mediaType = null, string downloadType = "yomitan")
    {
        if (!DownloadTypes.Contains(downloadType))
            return Results.BadRequest($"downloadType must be one of: {string.Join(", ", DownloadTypes)}");

        var configuration = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        string path = Path.Join(configuration["StaticFilesPath"], "yomitan");

        bool isYomitan = downloadType == "yomitan";
        string fileName = (mediaType == null ? "jiten_freq_global" : $"jiten_freq_{mediaType.ToString()}") + (isYomitan ? ".zip" : ".csv");
        string filePath = Path.Join(path, fileName);

        if (!System.IO.File.Exists(filePath))
        {
            return Results.NotFound($"Frequency list not found: {fileName}");
        }

        byte[] bytes = await System.IO.File.ReadAllBytesAsync(filePath);
        logger.LogInformation("User downloaded frequency list: MediaType={MediaType}, DownloadType={DownloadType}, FileName={FileName}",
                              mediaType?.ToString() ?? "global", downloadType, fileName);
        return Results.File(bytes, isYomitan ? "application/zip" : "text/csv", fileName);
    }

    /// <summary>
    /// DON'T TOUCH THIS
    /// IT'S BEING USED BY THE YOMITAN EXTENSION
    /// </summary>
    /// <param name="mediaType"></param>
    /// <returns></returns>
    [HttpGet("index")]
    public async Task<IResult> GetFrequencyListIndex([FromQuery] MediaType? mediaType = null)
    {
        var configuration = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        string path = Path.Join(configuration["StaticFilesPath"], "yomitan");

        string fileName = mediaType == null ? "jiten_freq_global.json" : $"jiten_freq_{mediaType.ToString()}.json";
        string filePath = Path.Join(path, fileName);

        if (!System.IO.File.Exists(filePath))
        {
            return Results.NotFound($"Frequency list not found: {fileName}");
        }

        byte[] bytes = await System.IO.File.ReadAllBytesAsync(filePath);
        return Results.File(bytes, "text/json", fileName);
    }

    [HttpGet("download-kanji")]
    [EnableRateLimiting("download")]
    public async Task<IResult> GetKanjiFrequencyList(string downloadType = "yomitan")
    {
        if (!DownloadTypes.Contains(downloadType))
            return Results.BadRequest($"downloadType must be one of: {string.Join(", ", DownloadTypes)}");

        var configuration = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        string path = Path.Join(configuration["StaticFilesPath"], "yomitan");

        bool isYomitan = downloadType == "yomitan";
        string fileName = isYomitan ? "jiten_kanji_freq.zip" : "jiten_kanji_freq.csv";
        string filePath = Path.Join(path, fileName);

        if (!System.IO.File.Exists(filePath))
        {
            return Results.NotFound($"Kanji frequency list not found: {fileName}");
        }

        byte[] bytes = await System.IO.File.ReadAllBytesAsync(filePath);
        logger.LogInformation("User downloaded kanji frequency list: DownloadType={DownloadType}, FileName={FileName}",
                              downloadType, fileName);
        return Results.File(bytes, isYomitan ? "application/zip" : "text/csv", fileName);
    }

    [HttpGet("index-kanji")]
    public async Task<IResult> GetKanjiFrequencyListIndex()
    {
        var configuration = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        string path = Path.Join(configuration["StaticFilesPath"], "yomitan");

        string fileName = "jiten_kanji_freq.json";
        string filePath = Path.Join(path, fileName);

        if (!System.IO.File.Exists(filePath))
        {
            return Results.NotFound($"Kanji frequency list not found: {fileName}");
        }

        byte[] bytes = await System.IO.File.ReadAllBytesAsync(filePath);
        return Results.File(bytes, "text/json", fileName);
    }
}