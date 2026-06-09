using Jiten.Api.Helpers;
using Jiten.Core;
using Jiten.Core.Data.JMDict;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services;

public class WordFormSiblingCache : IWordFormSiblingCache
{
    private readonly IDbContextFactory<JitenDbContext> _contextFactory;
    private readonly ILogger<WordFormSiblingCache> _logger;
    private volatile Dictionary<int, WordFormInfo> _wordForms = new();

    public WordFormSiblingCache(IDbContextFactory<JitenDbContext> contextFactory, ILogger<WordFormSiblingCache> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
        TryLoadData();
    }

    public void Reload() => TryLoadData();

    private void TryLoadData()
    {
        try
        {
            LoadData();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WordFormSiblingCache failed to load data from DB - serving with empty cache");
        }
    }

    private void LoadData()
    {
        var result = new Dictionary<int, WordFormInfo>();
        using var context = _contextFactory.CreateDbContext();

        var groups = context.WordForms
            .AsNoTracking()
            .Select(wf => new { wf.WordId, wf.ReadingIndex, wf.Text, wf.RubyText, wf.FormType })
            .ToList()
            .GroupBy(wf => wf.WordId);

        foreach (var group in groups)
        {
            var forms = group
                .Select(wf => new JmDictWordForm
                {
                    WordId = wf.WordId,
                    ReadingIndex = wf.ReadingIndex,
                    Text = wf.Text,
                    RubyText = wf.RubyText,
                    FormType = wf.FormType
                })
                .ToList();

            RubyTextHelper.EnrichForms(forms);

            var edges = RedundancyGraphHelper.BuildEdges(forms);
            if (edges.Count == 0)
                continue;

            result[group.Key] = new WordFormInfo
            {
                RedundantBySource = edges
                    .GroupBy(e => e.Source)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.Target).Distinct().ToArray()),
                SourcesByRedundant = edges
                    .GroupBy(e => e.Target)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.Source).Distinct().ToArray())
            };
        }

        _wordForms = result;
        _logger.LogInformation("WordFormSiblingCache loaded redundancy graph for {Count} words", result.Count);
    }

    public byte[]? GetKanaIndexesForKanji(int wordId, byte readingIndex)
    {
        if (!_wordForms.TryGetValue(wordId, out var info))
            return null;
        return info.RedundantBySource.GetValueOrDefault(readingIndex);
    }

    public byte[]? GetKanjiIndexesForKana(int wordId, byte readingIndex)
    {
        if (!_wordForms.TryGetValue(wordId, out var info))
            return null;
        return info.SourcesByRedundant.GetValueOrDefault(readingIndex);
    }

    public WordFormInfo? GetWordFormInfo(int wordId)
    {
        return _wordForms.GetValueOrDefault(wordId);
    }
}
