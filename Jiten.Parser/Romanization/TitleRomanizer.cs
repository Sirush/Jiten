using System.Globalization;
using System.Text;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Jiten.Parser.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Parser.Romanization;

/// <summary>Romanises a media title: parser words carry the word boundaries and JMdict readings, Sudachi tokens fill whatever the parser dropped.</summary>
public static class TitleRomanizer
{
    private enum UnitKind { Word, Particle, Literal, Open, Close, Trail }

    private enum Role { Word, Particle, Suffix, Prefix, Continuation }

    /// <summary>Words keep their kana until Build so suffixes join before romanisation (ほん+や gives hon'ya, not honya).</summary>
    private sealed class Unit(UnitKind kind, string text)
    {
        public UnitKind Kind { get; } = kind;
        public string Text { get; set; } = text;
        public string Kana { get; set; } = "";
        public bool AfterSeparator { get; set; }

        /// <summary>Decorative marks hug their neighbours unless the title spaced them (まどか☆マギカ → Madoka☆Magika).</summary>
        public bool IsSymbol => Kind == UnitKind.Literal && Text.Length > 0 &&
                                Text.All(c => char.GetUnicodeCategory(c) is UnicodeCategory.OtherSymbol or UnicodeCategory.MathSymbol);
    }

    private readonly record struct Span<T>(int Start, int End, T Item);

    private static readonly HashSet<string> TitleParticles = ["の", "は", "が", "を", "へ", "も", "と"];

    // Hyphenated (Kogitsune-tachi); other suffixes join the word (Saien).
    private static readonly HashSet<string> HyphenatedSuffixes =
        ["さん", "ちゃん", "くん", "君", "様", "さま", "たち", "達", "殿", "どの", "氏", "先輩", "せんぱい"];

    public static async Task<string> RomanizeAsync(IDbContextFactory<JitenDbContext> contextFactory, string title)
    {
        var diagnostics = new ParserDiagnostics();
        var words = await Parser.ParseText(contextFactory, title, diagnostics: diagnostics);
        var dictionary = await Parser.GetWordsAsync(words.Select(w => w.WordId).Distinct());
        return Romanize(title, words, diagnostics.Sudachi?.Tokens ?? [], dictionary);
    }

    public static string Romanize(string title, IReadOnlyList<DeckWord> words, IReadOnlyList<SudachiToken> tokens,
                                  IReadOnlyDictionary<int, JmDictWord> dictionary)
    {
        // The parser hands back full-width digits (2人 → ２人), so both sides are matched half-width.
        title = ToHalfWidth(title);
        var wordSpans = Locate(title, words, w => w.OriginalText);
        var tokenSpans = Locate(title, tokens, t => t.Surface);
        var wordAt = wordSpans.ToDictionary(s => s.Start);
        var tokenAt = new Dictionary<int, Span<SudachiToken>>();
        foreach (var span in tokenSpans) tokenAt.TryAdd(span.Start, span);

        var toggleCounts = title.Where(IsToggle).GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count());
        var toggleSeen = new Dictionary<char, int>();

        var builder = new UnitBuilder();
        int i = 0;
        while (i < title.Length)
        {
            if (tokenAt.TryGetValue(i, out var katakanaToken) && IsSplitKatakanaToken(katakanaToken, wordSpans))
            {
                builder.AddJapanese(katakanaToken.Item.Surface, Role.Word, katakanaToken.Item.Surface);
                i = katakanaToken.End;
                continue;
            }

            if (wordAt.TryGetValue(i, out var wordSpan))
            {
                AddWord(builder, title, wordSpan, tokenSpans, dictionary);
                i = wordSpan.End;
                continue;
            }

            var c = title[i];

            if (char.IsWhiteSpace(c) || c is '・' or '･')
            {
                builder.Separate();
                i++;
                continue;
            }

            if (IsJapanese(c))
            {
                i = AddUnparsedJapanese(builder, title, i, wordAt, tokenAt);
                continue;
            }

            if (!IsInsideLatinRun(title, i))
            {
                if (IsToggle(c))
                {
                    var seen = toggleSeen[c] = toggleSeen.GetValueOrDefault(c) + 1;
                    var kind = toggleCounts[c] % 2 != 0 ? UnitKind.Literal : seen % 2 == 1 ? UnitKind.Open : UnitKind.Close;
                    builder.Add(new Unit(kind, ToggleText(c)));
                    i++;
                    continue;
                }

                if (Punctuation(c) is { } punctuation)
                {
                    builder.Add(new Unit(punctuation.Kind, punctuation.Text));
                    i++;
                    continue;
                }
            }

            var literal = new StringBuilder();
            while (i < title.Length && !wordAt.ContainsKey(i))
            {
                var ch = title[i];
                if (char.IsWhiteSpace(ch) || ch is '・' or '･' || IsJapanese(ch)) break;
                if (literal.Length > 0 && (IsToggle(ch) || Punctuation(ch) != null) && !IsInsideLatinRun(title, i)) break;
                literal.Append(ch);
                i++;
            }

            builder.Add(new Unit(UnitKind.Literal, literal.ToString()));
        }

        return builder.Build();
    }

    private static void AddWord(UnitBuilder builder, string title, Span<DeckWord> span, List<Span<SudachiToken>> tokenSpans,
                                IReadOnlyDictionary<int, JmDictWord> dictionary)
    {
        var word = span.Item;
        var surface = title[span.Start..span.End];
        var inner = tokenSpans.Where(t => t.Start >= span.Start && t.End <= span.End).ToList();
        var tiled = inner.Count > 0 && inner[0].Start == span.Start && inner[^1].End == span.End &&
                    inner.Zip(inner.Skip(1)).All(p => p.First.End == p.Second.Start);
        dictionary.TryGetValue(word.WordId, out var entry);

        // Parser words absorb a trailing copula or particle (幸せな); the title reads better with it split off (Shiawase na).
        if (tiled && inner.Count > 1 && word.ConjugationCount == 0 && IsParticlePos(inner[^1].Item.PartOfSpeech) &&
            !inner.All(t => IsParticlePos(t.Item.PartOfSpeech)) && entry != null && entry.Forms.All(f => f.Text != surface))
        {
            var tail = inner[^1];
            var head = inner[..^1];
            var headSurface = title[span.Start..tail.Start];
            var headReading = DictionaryReading(headSurface, word, entry) ?? string.Concat(head.Select(t => t.Item.Reading));
            builder.AddJapanese(headReading, RoleOf(head), headSurface);
            builder.AddJapanese(tail.Item.Reading, Role.Particle, tail.Item.Surface);
            return;
        }

        var reading = DictionaryReading(surface, word, entry)
                      ?? (tiled ? string.Concat(inner.Select(t => t.Item.Reading)) : null)
                      ?? (string.IsNullOrEmpty(word.SudachiReading) ? null : word.SudachiReading);

        if (reading == null || !IsReading(reading))
        {
            builder.Add(new Unit(UnitKind.Literal, surface));
            return;
        }

        var role = tiled ? RoleOf(inner) : FallbackRole(word);
        if (role == Role.Particle && tiled && inner.Count > 1)
        {
            foreach (var token in inner)
                builder.AddJapanese(token.Item.Reading, Role.Particle, token.Item.Surface);
            return;
        }

        if (role == Role.Word && word.ConjugationCount == 0 &&
            word.PartsOfSpeech.FirstOrDefault() is PartOfSpeech.Name or PartOfSpeech.Expression &&
            SplitTitleParticles(surface, reading) is { Count: > 1 } segments)
        {
            foreach (var (segmentSurface, segmentReading) in segments)
                builder.AddJapanese(segmentReading, TitleParticles.Contains(segmentSurface) ? Role.Particle : Role.Word, segmentSurface);
            return;
        }

        builder.AddJapanese(reading, role, surface);
    }

    /// <summary>Splits work titles that JMnedict or JMdict hold as one entry (君の名は → 君|の|名|は) at their particles; null when the reading can't be aligned.</summary>
    private static List<(string Surface, string Reading)>? SplitTitleParticles(string surface, string reading)
    {
        var runs = new List<string>();
        for (int i = 0; i < surface.Length;)
        {
            var isKana = JapaneseTextHelper.IsKana(surface[i]);
            int end = i;
            while (end < surface.Length && JapaneseTextHelper.IsKana(surface[end]) == isKana) end++;
            runs.Add(surface[i..end]);
            i = end;
        }

        if (!runs.Skip(1).Any(TitleParticles.Contains)) return null;

        var hiragana = KanaConverter.ToHiragana(reading, convertLongVowelMark: false);
        var aligned = new List<(string Surface, string Reading)>();
        return AlignRuns(runs, 0, hiragana, 0, aligned) ? SplitAtParticles(aligned) : null;
    }

    /// <summary>Gives each kanji run the shortest reading chunk that lets every later kana run match literally.</summary>
    private static bool AlignRuns(List<string> runs, int run, string reading, int position, List<(string Surface, string Reading)> aligned)
    {
        if (run == runs.Count) return position == reading.Length;

        var text = runs[run];
        if (JapaneseTextHelper.IsKana(text[0]))
        {
            var kana = KanaConverter.ToHiragana(text, convertLongVowelMark: false);
            if (!reading.AsSpan(position).StartsWith(kana, StringComparison.Ordinal)) return false;
            aligned.Add((text, reading.Substring(position, kana.Length)));
            if (AlignRuns(runs, run + 1, reading, position + kana.Length, aligned)) return true;
            aligned.RemoveAt(aligned.Count - 1);
            return false;
        }

        for (int length = 1; position + length <= reading.Length; length++)
        {
            aligned.Add((text, reading.Substring(position, length)));
            if (AlignRuns(runs, run + 1, reading, position + length, aligned)) return true;
            aligned.RemoveAt(aligned.Count - 1);
        }

        return false;
    }

    private static List<(string Surface, string Reading)> SplitAtParticles(List<(string Surface, string Reading)> aligned)
    {
        var segments = new List<(string, string)>();
        var surface = new StringBuilder();
        var reading = new StringBuilder();
        for (int i = 0; i < aligned.Count; i++)
        {
            if (i > 0 && TitleParticles.Contains(aligned[i].Surface))
            {
                if (surface.Length > 0) segments.Add((surface.ToString(), reading.ToString()));
                segments.Add(aligned[i]);
                surface.Clear();
                reading.Clear();
                continue;
            }

            surface.Append(aligned[i].Surface);
            reading.Append(aligned[i].Reading);
        }

        if (surface.Length > 0) segments.Add((surface.ToString(), reading.ToString()));
        return segments;
    }

    /// <summary>A katakana token the parser cut into several words (マギカ → マギ|カ) reads as the one loanword it is.</summary>
    private static bool IsSplitKatakanaToken(Span<SudachiToken> token, List<Span<DeckWord>> wordSpans)
    {
        if (!token.Item.Surface.All(JapaneseTextHelper.IsKatakanaWordChar)) return false;

        var inside = wordSpans.Where(w => w.End > token.Start && w.Start < token.End).ToList();
        return inside.Count > 1 && inside.All(w => w.Start >= token.Start && w.End <= token.End);
    }

    private static int AddUnparsedJapanese(UnitBuilder builder, string title, int start, Dictionary<int, Span<DeckWord>> wordAt,
                                           Dictionary<int, Span<SudachiToken>> tokenAt)
    {
        var nextWord = wordAt.Keys.Where(k => k > start).DefaultIfEmpty(title.Length).Min();

        if (tokenAt.TryGetValue(start, out var token) && token.End <= nextWord && IsReading(token.Item.Reading))
        {
            builder.AddJapanese(token.Item.Reading, RoleOf([token]), token.Item.Surface);
            return token.End;
        }

        int end = start;
        while (end < nextWord && JapaneseTextHelper.IsKana(title[end]) && title[end] != '・') end++;
        if (end > start)
        {
            // Kana the parser dropped is usually the tail of the previous word (告らせた|い).
            builder.AddJapanese(title[start..end], Role.Continuation, title[start..end]);
            return end;
        }

        builder.Add(new Unit(UnitKind.Literal, title[start].ToString()));
        return start + 1;
    }

    /// <summary>Surface reading from the chosen JMdict form's ruby, so 私 reads わたし rather than Sudachi's わたくし; null when the ruby doesn't cover every kanji.</summary>
    private static string? DictionaryReading(string surface, DeckWord word, JmDictWord? entry)
    {
        if (JapaneseTextHelper.IsAllKana(surface)) return surface;
        if (entry == null) return null;

        var candidates = entry.Forms.Where(f => f.ReadingIndex == word.ReadingIndex)
                              .Concat(entry.Forms.Where(f => f.ReadingIndex != word.ReadingIndex && f.FormType == JmDictFormType.KanjiForm)
                                           .OrderBy(f => f.ReadingIndex));

        foreach (var form in candidates)
        {
            var aligned = SentenceFurigana.Align(surface, form.RubyText);
            if (aligned.Count == 0) continue;

            var sb = new StringBuilder();
            int position = 0;
            var covered = true;
            foreach (var (offset, length, reading) in aligned)
            {
                if (!AppendKana(sb, surface, position, offset)) { covered = false; break; }
                sb.Append(reading);
                position = offset + length;
            }

            if (covered && AppendKana(sb, surface, position, surface.Length))
                return sb.ToString();
        }

        return null;
    }

    private static bool AppendKana(StringBuilder sb, string surface, int from, int to)
    {
        for (int i = from; i < to; i++)
        {
            if (!JapaneseTextHelper.IsKana(surface[i])) return false;
            sb.Append(surface[i]);
        }

        return true;
    }

    private static Role RoleOf(IReadOnlyList<Span<SudachiToken>> tokens)
    {
        if (tokens.All(t => IsParticlePos(t.Item.PartOfSpeech))) return Role.Particle;
        if (tokens.Count == 1 && tokens[0].Item.PartOfSpeech == nameof(PartOfSpeech.Prefix)) return Role.Prefix;
        if (tokens[0].Item.PartOfSpeech == nameof(PartOfSpeech.Suffix)) return Role.Suffix;
        return Role.Word;
    }

    private static Role FallbackRole(DeckWord word) =>
        word.PartsOfSpeech.FirstOrDefault() switch
        {
            PartOfSpeech.Particle => Role.Particle,
            PartOfSpeech.Suffix or PartOfSpeech.NounSuffix => Role.Suffix,
            _ => Role.Word
        };

    private static bool IsParticlePos(string pos) => pos is nameof(PartOfSpeech.Particle) or nameof(PartOfSpeech.Auxiliary);

    private static bool IsReading(string reading) =>
        reading.Length > 0 && reading.All(c => JapaneseTextHelper.IsKana(c) && c != '・');

    private static List<Span<T>> Locate<T>(string title, IEnumerable<T> items, Func<T, string> surfaceOf)
    {
        var spans = new List<Span<T>>();
        int cursor = 0;
        foreach (var item in items)
        {
            var surface = ToHalfWidth(surfaceOf(item));
            if (surface.Length == 0) continue;
            var index = title.IndexOf(surface, cursor, StringComparison.Ordinal);
            if (index < 0) continue;
            spans.Add(new Span<T>(index, index + surface.Length, item));
            cursor = index + surface.Length;
        }

        return spans;
    }

    private static bool IsJapanese(char c) =>
        (JapaneseTextHelper.IsKana(c) && c != '・') || JapaneseTextHelper.IsKanji(c) || c is '々' or '〆';

    /// <summary>Punctuation flanked by Latin letters or digits belongs to the run: 1,000, 3.5, Dr.STONE.</summary>
    private static bool IsInsideLatinRun(string title, int i) =>
        i > 0 && i + 1 < title.Length && IsLatinOrDigit(title[i - 1]) && IsLatinOrDigit(title[i + 1]);

    private static bool IsLatinOrDigit(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9');

    private static string ToHalfWidth(string text) =>
        string.Create(text.Length, text, static (span, src) =>
        {
            for (int i = 0; i < src.Length; i++)
            {
                span[i] = src[i] switch
                {
                    >= '\uFF01' and <= '\uFF5E' => (char)(src[i] - 0xFEE0),
                    '\u3000' => ' ',
                    _ => src[i]
                };
            }
        });

    // Same glyph opens and closes, so pairs alternate; an unpaired one stays a standalone mark.
    private static bool IsToggle(char c) => c is '~' or '〜' or '"' or '―' or '—' or '─';

    private static string ToggleText(char c) => c is '"' ? "\"" : c is '~' or '〜' ? "~" : "-";

    private static (UnitKind Kind, string Text)? Punctuation(char c) =>
        c switch
        {
            '「' or '『' or '“' or '《' or '〈' or '≪' or '«' => (UnitKind.Open, "\""),
            '」' or '』' or '”' or '》' or '〉' or '≫' or '»' => (UnitKind.Close, "\""),
            '(' or '（' => (UnitKind.Open, "("),
            ')' or '）' => (UnitKind.Close, ")"),
            '[' or '【' or '〔' or '〖' or '［' => (UnitKind.Open, "["),
            ']' or '】' or '〕' or '〗' or '］' => (UnitKind.Close, "]"),
            '!' => (UnitKind.Trail, "!"),
            '?' => (UnitKind.Trail, "?"),
            '‼' => (UnitKind.Trail, "!!"),
            '⁉' => (UnitKind.Trail, "!?"),
            '⁈' => (UnitKind.Trail, "?!"),
            ',' or '、' or '，' => (UnitKind.Trail, ","),
            '.' or '。' or '．' => (UnitKind.Trail, "."),
            ':' => (UnitKind.Trail, ":"),
            ';' => (UnitKind.Trail, ";"),
            '…' => (UnitKind.Trail, "..."),
            '‥' => (UnitKind.Trail, ".."),
            _ => null
        };

    private sealed class UnitBuilder
    {
        private readonly List<Unit> _units = [];
        private string? _pendingPrefix;
        private bool _separated = true;

        public void Separate()
        {
            FlushPrefix();
            _separated = true;
        }

        public void Add(Unit unit)
        {
            FlushPrefix();
            unit.AfterSeparator = _separated;
            _units.Add(unit);
            _separated = false;
        }

        public void AddJapanese(string kana, Role role, string surface)
        {
            if (role == Role.Prefix)
            {
                _pendingPrefix += kana;
                return;
            }

            if (_pendingPrefix != null)
            {
                kana = _pendingPrefix + kana;
                _pendingPrefix = null;
                if (role == Role.Suffix) role = Role.Word;
            }

            if (role == Role.Continuation)
            {
                if (!_separated && _units.Count > 0 && _units[^1].Kind == UnitKind.Word)
                {
                    _units[^1].Kana += kana;
                    return;
                }

                role = Role.Word;
            }

            // Counters on a numeral hyphenate like suffixes (3月 → 3-gatsu).
            if (role == Role.Word && !_separated && surface.Length == 1 && JapaneseTextHelper.IsKanji(surface[0]) &&
                _units.Count > 0 && _units[^1] is { Kind: UnitKind.Literal, Text: [.., >= '0' and <= '9'] })
                role = Role.Suffix;

            if (role == Role.Suffix && !_separated && _units.Count > 0)
            {
                var last = _units[^1];
                if (last.Kind == UnitKind.Word)
                {
                    last.Kana += (HyphenatedSuffixes.Contains(surface) ? "-" : "") + kana;
                    return;
                }

                if (last.Kind == UnitKind.Literal)
                {
                    last.Text += "-" + HepburnRomanizer.ToRomaji(kana);
                    return;
                }
            }

            _units.Add(role == Role.Particle
                ? new Unit(UnitKind.Particle, ParticleRomaji(surface, kana)) { AfterSeparator = _separated }
                : new Unit(UnitKind.Word, "") { Kana = kana, AfterSeparator = _separated });

            _separated = false;
        }

        public string Build()
        {
            FlushPrefix();

            var sb = new StringBuilder();
            Unit? previous = null;
            foreach (var unit in _units)
            {
                var text = unit.Kind == UnitKind.Word ? Capitalize(HepburnRomanizer.ToRomaji(unit.Kana)) : unit.Text;
                if (text.Length == 0) continue;
                if (previous == null && unit.Kind == UnitKind.Particle) text = Capitalize(text);

                var hugsSymbol = !unit.AfterSeparator && (unit.IsSymbol || previous?.IsSymbol == true);
                if (previous != null && previous.Kind != UnitKind.Open && unit.Kind is not (UnitKind.Close or UnitKind.Trail) && !hugsSymbol)
                    sb.Append(' ');

                sb.Append(text);
                previous = unit;
            }

            return sb.ToString();
        }

        private void FlushPrefix()
        {
            if (_pendingPrefix == null) return;
            var prefix = _pendingPrefix;
            _pendingPrefix = null;
            AddJapanese(prefix, Role.Word, prefix);
        }

        private static string ParticleRomaji(string surface, string kana) =>
            surface switch
            {
                "は" => "wa",
                "を" => "o",
                "へ" => "e",
                _ => HepburnRomanizer.ToRomaji(kana)
            };

        private static string Capitalize(string text) =>
            text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
    }
}
