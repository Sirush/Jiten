using Jiten.Core.Data.WebNovel;

namespace Jiten.Core.WebNovel;

public interface IWebNovelSourceResolver
{
    IWebNovelSource Resolve(WebNovelProvider provider);
    bool IsSupported(WebNovelProvider provider);
}

public class WebNovelSourceResolver(IEnumerable<IWebNovelSource> sources) : IWebNovelSourceResolver
{
    private readonly Dictionary<WebNovelProvider, IWebNovelSource> _sources =
        sources.ToDictionary(s => s.Provider);

    public IWebNovelSource Resolve(WebNovelProvider provider) =>
        _sources.TryGetValue(provider, out var source)
            ? source
            : throw new NotSupportedException($"Webnovel provider {provider} is not enabled.");

    public bool IsSupported(WebNovelProvider provider) => _sources.ContainsKey(provider);
}
