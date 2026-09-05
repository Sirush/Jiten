using System.Data;
using Jiten.Api.Helpers;
using Jiten.Core;
using Jiten.Core.Data.JMDict;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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
        using var context = _contextFactory.CreateDbContext();
        var rows = context.Database.IsNpgsql() ? ReadFormsRaw(context) : ReadFormsEf(context);

        var result = new Dictionary<int, WordFormInfo>();
        var pending = new List<List<JmDictWordForm>>(WordsPerChunk);
        var current = new List<JmDictWordForm>();
        var currentWordId = int.MinValue;

        void Flush()
        {
            if (pending.Count == 0) return;
            var infos = new WordFormInfo?[pending.Count];
            Parallel.For(0, pending.Count, i => infos[i] = BuildInfo(pending[i]));
            for (var i = 0; i < pending.Count; i++)
                if (infos[i] != null)
                    result[pending[i][0].WordId] = infos[i]!;
            pending.Clear();
        }

        foreach (var row in rows)
        {
            if (row.WordId != currentWordId)
            {
                // A single form has nothing to be redundant with.
                if (current.Count > 1)
                {
                    pending.Add(current);
                    if (pending.Count == WordsPerChunk) Flush();
                }
                current = new List<JmDictWordForm>(2);
                currentWordId = row.WordId;
            }
            current.Add(row);
        }
        if (current.Count > 1) pending.Add(current);
        Flush();

        _wordForms = result;
        _logger.LogInformation("WordFormSiblingCache loaded redundancy graph for {Count} words", result.Count);
    }

    private const int WordsPerChunk = 4096;

    // EF materialisation costs about twice the raw read over 1.9M rows; the SQLite test provider keeps the EF path.
    private static IEnumerable<JmDictWordForm> ReadFormsRaw(JitenDbContext context)
    {
        var conn = (NpgsqlConnection)context.Database.GetDbConnection();
        var shouldClose = conn.State == ConnectionState.Closed;
        if (shouldClose) conn.Open();
        try
        {
            using var cmd = new NpgsqlCommand(
                """SELECT "WordId", "ReadingIndex", "Text", "RubyText", "FormType" FROM jmdict."WordForms" ORDER BY "WordId" """, conn);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                yield return new JmDictWordForm
                {
                    WordId = reader.GetInt32(0),
                    ReadingIndex = reader.GetInt16(1),
                    Text = reader.GetString(2),
                    RubyText = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    FormType = (JmDictFormType)reader.GetInt16(4)
                };
            }
        }
        finally
        {
            if (shouldClose) conn.Close();
        }
    }

    private static IEnumerable<JmDictWordForm> ReadFormsEf(JitenDbContext context) =>
        context.WordForms
            .AsNoTracking()
            .OrderBy(wf => wf.WordId)
            .Select(wf => new JmDictWordForm
            {
                WordId = wf.WordId,
                ReadingIndex = wf.ReadingIndex,
                Text = wf.Text,
                RubyText = wf.RubyText,
                FormType = wf.FormType
            })
            .AsEnumerable();

    private static WordFormInfo? BuildInfo(List<JmDictWordForm> forms)
    {
        RubyTextHelper.EnrichForms(forms);

        var edges = RedundancyGraphHelper.BuildEdges(forms);
        if (edges.Count == 0)
            return null;

        return new WordFormInfo
        {
            RedundantBySource = edges
                .GroupBy(e => e.Source)
                .ToDictionary(g => g.Key, g => g.Select(e => e.Target).Distinct().ToArray()),
            SourcesByRedundant = edges
                .GroupBy(e => e.Target)
                .ToDictionary(g => g.Key, g => g.Select(e => e.Source).Distinct().ToArray())
        };
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
