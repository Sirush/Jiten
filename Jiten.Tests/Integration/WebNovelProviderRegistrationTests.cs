using FluentAssertions;
using Jiten.Core.Data.WebNovel;
using Jiten.Core.WebNovel;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class WebNovelProviderRegistrationTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>
{
    [Theory]
    [InlineData(WebNovelProvider.Syosetu)]
    [InlineData(WebNovelProvider.SyosetuNovel18)]
    public void SyosetuProvidersAreEnabled(WebNovelProvider provider)
    {
        var resolver = factory.Services.GetRequiredService<IWebNovelSourceResolver>();

        resolver.IsSupported(provider).Should().BeTrue();
        resolver.Resolve(provider).Provider.Should().Be(provider);
    }
}
