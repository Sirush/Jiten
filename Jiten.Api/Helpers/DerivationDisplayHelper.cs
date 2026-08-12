using Jiten.Api.Dtos;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data.JMDict;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Helpers;

public static class DerivationDisplayHelper
{
    private static readonly Dictionary<DerivationCategory, string> Labels =
        DerivationCategories.Shipped.ToDictionary(c => c.Category, c => c.Label);

    private static bool IsShipped(DerivationLink link) => Labels.ContainsKey(link.Category);

    /// <summary>The word's derivation links for display. A non-null category set marks which of them currently
    /// confer knowledge, so the page can show the whole family and still be honest about what the user covers.</summary>
    public static async Task<(List<WordDerivationDto> DerivedFrom, List<WordDerivationDto> Derives)> Load(
        IDbContextFactory<JitenDbContext> contextFactory, IDerivationLinkCache cache, int wordId, byte readingIndex,
        IReadOnlySet<DerivationCategory>? enabledCategories = null)
    {
        var baseLinks = cache.GetBaseLinks(wordId, readingIndex).Where(IsShipped).ToList();
        var derivedLinks = cache.GetDerivedLinks(wordId, readingIndex).Where(IsShipped).ToList();
        if (baseLinks.Count == 0 && derivedLinks.Count == 0)
            return ([], []);

        var linkedWordIds = baseLinks.Select(l => l.WordId)
                                     .Concat(derivedLinks.Select(l => l.WordId))
                                     .Distinct()
                                     .ToList();

        await using var ctx = await contextFactory.CreateDbContextAsync();
        var forms = await WordFormHelper.LoadWordForms(ctx, linkedWordIds);

        return (ToDtos(baseLinks), ToDtos(derivedLinks));

        List<WordDerivationDto> ToDtos(List<DerivationLink> links) =>
            links.Select(link =>
                 {
                     var form = forms.GetValueOrDefault((link.WordId, link.ReadingIndex));
                     return new WordDerivationDto
                     {
                         WordId = link.WordId,
                         ReadingIndex = link.ReadingIndex,
                         Text = form?.Text ?? "",
                         RubyText = form?.RubyText ?? form?.Text ?? "",
                         CategoryKey = DerivationCategories.GetKey(link.Category),
                         CategoryLabel = Labels.GetValueOrDefault(link.Category, ""),
                         Enabled = enabledCategories?.Contains(link.Category)
                     };
                 })
                 .Where(dto => dto.Text.Length > 0)
                 .ToList();
    }

    public static async Task<DerivationCoverDto?> LoadCover(IDbContextFactory<JitenDbContext> contextFactory,
                                                             DerivationCover? cover)
    {
        if (cover == null) return null;

        await using var ctx = await contextFactory.CreateDbContextAsync();
        var forms = await WordFormHelper.LoadWordForms(ctx, [cover.Value.WordId]);
        var form = forms.GetValueOrDefault((cover.Value.WordId, cover.Value.ReadingIndex));
        if (form == null) return null;

        return new DerivationCoverDto
        {
            WordId = cover.Value.WordId,
            ReadingIndex = cover.Value.ReadingIndex,
            Text = form.Text,
            CategoryKey = DerivationCategories.GetKey(cover.Value.ViaCategory),
            CategoryLabel = Labels.GetValueOrDefault(cover.Value.ViaCategory, "")
        };
    }
}
