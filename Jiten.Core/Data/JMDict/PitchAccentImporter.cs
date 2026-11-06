using System.Text;
using System.Text.Json;
using WanaKanaShaapu;

namespace Jiten.Core.Data.JMDict;

public sealed class PitchAccentSource
{
    private readonly Dictionary<(string Surface, string Reading), List<int>> _bySurface = new();
    private readonly Dictionary<(string Surface, string Reading), List<int>> _byKanaSurface = new();

    public int Count => _bySurface.Count;

    public void Add(string surface, string reading, List<int> positions)
    {
        var normalisedReading = PitchAccentImporter.NormaliseReading(reading);
        if (normalisedReading.Length == 0 || positions.Count == 0)
            return;

        _bySurface.TryAdd((surface, normalisedReading), positions);

        if (WanaKana.IsKana(surface))
            _byKanaSurface.TryAdd((PitchAccentImporter.NormaliseReading(surface), normalisedReading), positions);
    }

    public List<int>? Find(string surface, string normalisedReading)
        => _bySurface.GetValueOrDefault((surface, normalisedReading));

    /// <summary>Script-insensitive lookup of a kana spelling: ヤツ finds the record written やつ.</summary>
    public List<int>? FindKana(string normalisedReading)
        => _byKanaSurface.GetValueOrDefault((normalisedReading, normalisedReading));

    /// <summary>Loads Yomitan pitch banks (term_meta_bank_*.json); the first record for a (surface, reading) pair wins.</summary>
    public static async Task<PitchAccentSource> LoadAsync(IEnumerable<string> files)
    {
        var source = new PitchAccentSource();
        foreach (var file in files)
        {
            await using var stream = File.OpenRead(file);
            using var doc = await JsonDocument.ParseAsync(stream);

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var surface = item[0].GetString();
                if (surface == null || item[1].GetString() != "pitch")
                    continue;

                var info = item[2];
                var reading = info.TryGetProperty("reading", out var r) ? r.GetString() : null;
                if (reading == null || !info.TryGetProperty("pitches", out var pitches))
                    continue;

                var positions = new List<int>();
                foreach (var pitch in pitches.EnumerateArray())
                {
                    var position = pitch.GetProperty("position").GetInt32();
                    if (!positions.Contains(position))
                        positions.Add(position);
                }

                source.Add(surface, reading, positions);
            }
        }

        return source;
    }
}

public static class PitchAccentImporter
{
    private const char KatakanaMiddleDot = '・';
    private const int CustomWordIdStart = 8000000;

    /// <summary>Hiragana with Kanjium's notation marks (∘ … ⁎ ・) dropped, so both sides of a match compare equal.</summary>
    public static string NormaliseReading(string reading)
    {
        var kana = new StringBuilder(reading.Length);
        foreach (var c in reading)
        {
            if (c is >= 'ぁ' and <= 'ヿ' && c != KatakanaMiddleDot)
                kana.Append(c);
        }

        return kana.Length == 0 ? "" : WanaKana.ToHiragana(kana.ToString());
    }

    /// <summary>
    /// Sets every form's accent from the record for its own spelling and reading, then lets forms without one take the
    /// accent another form of the word gives the same reading. Kanji spellings, paired with any of the word's readings,
    /// win over kana-spelling records because a kana spelling is shared by homophones (はし is 橋 and 箸).
    /// The word-level value is the accent of the lowest-index form that has one.
    /// Returns false, changing nothing, when the word is custom and no form matched.
    /// </summary>
    public static bool Assign(JmDictWord word, PitchAccentSource source)
    {
        var forms = word.Forms.OrderBy(f => f.ReadingIndex).ToList();
        var kanaReadings = forms.Where(f => f.FormType == JmDictFormType.KanaForm)
                                .Select(f => NormaliseReading(f.Text))
                                .Where(r => r.Length > 0)
                                .Distinct()
                                .ToList();

        var accents = new Dictionary<JmDictWordForm, List<int>>();
        var readingAccents = new Dictionary<string, List<int>>();
        var candidateReadings = new Dictionary<JmDictWordForm, List<string>>();

        foreach (var form in forms.Where(f => f.FormType == JmDictFormType.KanjiForm))
        {
            var rubyReading = RubyTextHelper.KanaFromRubyText(form.RubyText);
            var candidates = rubyReading != null ? [NormaliseReading(rubyReading)] : kanaReadings;
            candidateReadings[form] = candidates;

            foreach (var reading in candidates)
            {
                var found = source.Find(form.Text, reading);
                if (found == null) continue;

                accents[form] = found;
                readingAccents.TryAdd(reading, found);
                break;
            }
        }

        foreach (var form in forms.Where(f => f.FormType == JmDictFormType.KanjiForm))
        foreach (var reading in kanaReadings)
        {
            if (readingAccents.ContainsKey(reading)) continue;
            if (source.Find(form.Text, reading) is { } found)
                readingAccents[reading] = found;
        }

        foreach (var form in forms.Where(f => f.FormType == JmDictFormType.KanaForm))
        {
            var reading = NormaliseReading(form.Text);
            if (reading.Length == 0) continue;

            var found = readingAccents.GetValueOrDefault(reading)
                        ?? source.Find(form.Text, reading)
                        ?? source.FindKana(reading);
            if (found == null) continue;

            accents[form] = found;
            readingAccents.TryAdd(reading, found);
        }

        foreach (var (form, candidates) in candidateReadings)
        {
            if (accents.ContainsKey(form)) continue;

            var inherited = candidates.Select(r => readingAccents.GetValueOrDefault(r)).FirstOrDefault(a => a != null);
            if (inherited != null)
                accents[form] = inherited;
        }

        if (accents.Count == 0 && word.WordId >= CustomWordIdStart)
            return false;

        foreach (var form in forms)
            form.PitchAccents = accents.TryGetValue(form, out var a) ? [..a] : [];

        word.PitchAccents = forms.Select(f => f.PitchAccents).FirstOrDefault(a => a is { Count: > 0 }) is { } primary
            ? [..primary]
            : null;

        return true;
    }
}
