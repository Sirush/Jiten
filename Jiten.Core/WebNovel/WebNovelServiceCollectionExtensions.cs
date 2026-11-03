using System.Net;
using Jiten.Core.Data.WebNovel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jiten.Core.WebNovel;

public static class WebNovelServiceCollectionExtensions
{
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    /// <summary>
    /// Every enabled webnovel source plus the resolver. A provider in <see cref="WebNovelProviders.All"/> without a
    /// source registered here stays disabled: its URLs parse, but previews and imports refuse it.
    /// </summary>
    public static IServiceCollection AddWebNovelSources(this IServiceCollection services)
    {
        foreach (var site in WebNovelProviders.Sites)
            AddSiteClient(services, site.HttpClientName);

        foreach (var provider in new[] { WebNovelProvider.Syosetu, WebNovelProvider.SyosetuNovel18 })
        {
            services.AddSingleton<IWebNovelSource>(sp => new SyosetuSource(sp.GetRequiredService<IHttpClientFactory>(),
                                                                           sp.GetRequiredService<ILogger<SyosetuSource>>(),
                                                                           provider));
        }

        services.AddSingleton<IWebNovelSource, KakuyomuSource>();
        services.AddSingleton<IWebNovelSourceResolver, WebNovelSourceResolver>();

        return services;
    }

    /// <summary>
    /// Decompresses HTML pages; Syosetu's API body (gzip=5) is gzip regardless of headers and is inflated by hand.
    /// </summary>
    private static void AddSiteClient(IServiceCollection services, string name)
    {
        services.AddHttpClient(name, client =>
                {
                    client.DefaultRequestHeaders.Add("User-Agent", BrowserUserAgent);
                    client.Timeout = TimeSpan.FromSeconds(30);
                })
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
                });
    }
}
