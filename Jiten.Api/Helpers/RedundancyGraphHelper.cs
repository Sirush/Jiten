using Jiten.Core;
using Jiten.Core.Data.JMDict;

namespace Jiten.Api.Helpers;

/// <summary>
/// Builds the per-word redundancy graph used by <see cref="Jiten.Api.Services.WordFormSiblingCache"/>.
/// A directed edge (source -> target) means "if the form at <c>source</c> is known, the form at
/// <c>target</c> is redundant". Two sources of edges:
///   1. Kana-degradation: a kanji form dominates any form obtained by replacing a subset of its kanji
///      with their readings while keeping every other character (kept kanji + okurigana) identical.
///      e.g. 落ち着ける -> 落ちつける, おちつける  (never 落ち付ける / 落着ける / 落付ける).
///   2. Script permutation: forms whose hiragana-normalised text is identical (e.g. おちつける ↔
///      オチツケル) are mutually redundant.
/// </summary>
public static class RedundancyGraphHelper
{
    private const int MaxKanjiSegments = 10; // guard against pathological 2^n enumeration

    public static List<(byte Source, byte Target)> BuildEdges(IReadOnlyList<JmDictWordForm> forms)
    {
        var edges = new HashSet<(byte, byte)>();

        // Map hiragana-normalised text -> reading indexes that spell to it.
        var byNormalised = new Dictionary<string, List<byte>>();
        foreach (var form in forms)
        {
            var key = ToHiragana(form.Text);
            if (!byNormalised.TryGetValue(key, out var list))
                byNormalised[key] = list = new List<byte>();
            if (!list.Contains((byte)form.ReadingIndex))
                list.Add((byte)form.ReadingIndex);
        }

        // 1. Script-variant edges (bidirectional among same-reading forms).
        foreach (var group in byNormalised.Values)
        {
            if (group.Count < 2) continue;
            foreach (var a in group)
                foreach (var b in group)
                    if (a != b)
                        edges.Add((a, b));
        }

        // 2. Kana-degradation edges from each structured kanji form.
        foreach (var form in forms)
        {
            if (form.FormType != JmDictFormType.KanjiForm) continue;
            if (!form.RubyText.Contains('[')) continue;

            var segments = ParseRubySegments(form.RubyText);
            if (segments == null) continue;
            if (segments.Count(s => s.IsKanji) is 0 or > MaxKanjiSegments) continue;

            var sourceRi = (byte)form.ReadingIndex;
            var selfNorm = ToHiragana(form.Text);

            foreach (var degraded in EnumerateDegradations(segments))
            {
                var norm = ToHiragana(degraded);
                if (norm == selfNorm) continue; // the all-keep combination is the form itself
                if (!byNormalised.TryGetValue(norm, out var targets)) continue;
                foreach (var target in targets)
                    if (target != sourceRi)
                        edges.Add((sourceRi, target));
            }
        }

        return edges.ToList();
    }

    private record Segment(string Text, string? Reading, bool IsKanji);

    /// <summary>
    /// Parses a structured ruby annotation (e.g. "落[お]ち着[つ]ける") into an ordered list of literal
    /// kana segments and kanji segments (each carrying its reading). Each bracket group is one kanji
    /// toggle unit, so jukujikun like "今日[きょう]" stays a single segment. Returns null on malformed
    /// input (unbalanced brackets).
    /// </summary>
    private static List<Segment>? ParseRubySegments(string ruby)
    {
        var segments = new List<Segment>();
        var pending = new System.Text.StringBuilder();

        int i = 0;
        while (i < ruby.Length)
        {
            char c = ruby[i];
            if (c == '[')
            {
                int close = ruby.IndexOf(']', i);
                if (close < 0) return null;

                var reading = ruby.Substring(i + 1, close - i - 1);

                // The reading annotates the trailing kanji run of the pending text; any leading kana
                // are literal okurigana that precede it.
                var pendingStr = pending.ToString();
                int k = pendingStr.Length;
                while (k > 0 && JapaneseTextHelper.IsKanji(pendingStr[k - 1])) k--;

                if (k > 0)
                    segments.Add(new Segment(pendingStr[..k], null, false));
                var kanjiRun = pendingStr[k..];
                if (kanjiRun.Length == 0) return null; // bracket without a preceding kanji
                segments.Add(new Segment(kanjiRun, reading, true));

                pending.Clear();
                i = close + 1;
            }
            else
            {
                pending.Append(c);
                i++;
            }
        }

        if (pending.Length > 0)
            segments.Add(new Segment(pending.ToString(), null, false));

        return segments;
    }

    /// <summary>
    /// Yields every kana-degradation of the segmented form: each kanji segment is independently kept
    /// as its kanji text or replaced by its reading; literal segments are always kept.
    /// </summary>
    private static IEnumerable<string> EnumerateDegradations(List<Segment> segments)
    {
        var kanjiIndexes = new List<int>();
        for (int i = 0; i < segments.Count; i++)
            if (segments[i].IsKanji)
                kanjiIndexes.Add(i);

        int combinations = 1 << kanjiIndexes.Count;
        var sb = new System.Text.StringBuilder();
        for (int mask = 0; mask < combinations; mask++)
        {
            sb.Clear();
            int bit = 0;
            foreach (var seg in segments)
            {
                if (!seg.IsKanji)
                {
                    sb.Append(seg.Text);
                }
                else
                {
                    bool useReading = (mask & (1 << bit)) != 0;
                    sb.Append(useReading ? seg.Reading : seg.Text);
                    bit++;
                }
            }
            yield return sb.ToString();
        }
    }

    private static string ToHiragana(string text)
    {
        var chars = text.ToCharArray();
        bool changed = false;
        for (int i = 0; i < chars.Length; i++)
        {
            // Katakana ァ..ヶ -> hiragana via the fixed 0x60 offset.
            if (chars[i] is >= 'ァ' and <= 'ヶ')
            {
                chars[i] = (char)(chars[i] - 0x60);
                changed = true;
            }
        }
        return changed ? new string(chars) : text;
    }
}
