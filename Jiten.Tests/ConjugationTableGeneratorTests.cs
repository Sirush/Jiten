using FluentAssertions;
using Jiten.Core.Data.JMDict;
using Jiten.Parser;

namespace Jiten.Tests;

public class ConjugationTableGeneratorTests
{
    private static JmDictWord MakeWord(int id, string pos, params string[] forms)
    {
        var w = new JmDictWord
        {
            WordId = id,
            PartsOfSpeech = new List<string> { pos },
            Forms = new List<JmDictWordForm>()
        };
        for (short i = 0; i < forms.Length; i++)
        {
            w.Forms.Add(new JmDictWordForm
            {
                WordId = id,
                ReadingIndex = i,
                Text = forms[i],
                FormType = i == 0 ? JmDictFormType.KanjiForm : JmDictFormType.KanaForm
            });
        }
        return w;
    }

    [Fact]
    public void V1_TaberuProducesCommonInflections()
    {
        var gen = ConjugationTableGenerator.FromSharedResources();
        var word = MakeWord(1, "v1", "食べる", "たべる");

        var records = gen.Generate(word).ToList();

        records.Should().NotBeEmpty();
        var surfaces = records.Select(r => r.Surface).ToHashSet();

        // Identity forms
        surfaces.Should().Contain("食べる");
        surfaces.Should().Contain("たべる");

        // Common inflections (at least for kanji form)
        surfaces.Should().Contain("食べた");       // past
        surfaces.Should().Contain("食べて");       // te-form
        surfaces.Should().Contain("食べない");     // negative
        surfaces.Should().Contain("食べます");     // polite (v1 → stem-ren → masu, depth 2)
        surfaces.Should().Contain("食べたい");     // desiderative (v1 → stem-ren → tai, depth 2)
    }

    [Fact]
    public void V5u_KauProducesPastAndNegative()
    {
        var gen = ConjugationTableGenerator.FromSharedResources();
        var word = MakeWord(2, "v5u", "買う");
        var surfaces = gen.Generate(word).Select(r => r.Surface).ToHashSet();

        surfaces.Should().Contain("買う");
        surfaces.Should().Contain("買った");      // past (w/ onbin)
        surfaces.Should().Contain("買わない");    // negative
    }

    [Fact]
    public void NonConjugableWordProducesNothing()
    {
        var gen = ConjugationTableGenerator.FromSharedResources();
        // Particle が has no conjugable POS.
        var word = MakeWord(3, "prt", "が");

        var records = gen.Generate(word).ToList();
        records.Should().BeEmpty();
    }

    [Fact]
    public void AdjI_TakaiProducesPastAndNegative()
    {
        var gen = ConjugationTableGenerator.FromSharedResources();
        var word = MakeWord(4, "adj-i", "高い");
        var surfaces = gen.Generate(word).Select(r => r.Surface).ToHashSet();

        surfaces.Should().Contain("高い");
        surfaces.Should().Contain("高かった");    // past
        surfaces.Should().Contain("高くない");    // negative
    }

    [Fact]
    public void NoRepeatedDetail_KillsPassivePassiveEtc()
    {
        var gen = ConjugationTableGenerator.FromSharedResources();
        var word = MakeWord(5, "v1", "食べる");
        var records = gen.Generate(word).ToList();

        // No chain should contain the same detail twice (the no-repeat
        // constraint eliminates passive-passive, causative-causative, etc.).
        foreach (var rec in records)
        {
            var distinct = rec.Chain.Distinct().Count();
            distinct.Should().Be(rec.Chain.Length,
                $"surface {rec.Surface} has duplicate detail: [{string.Join(", ", rec.Chain)}]");
        }

        // Likewise the surface should be unique per (surface, formIdx) — one
        // row per surface with the shortest chain.
        var seen = new HashSet<(string, short)>();
        foreach (var rec in records)
        {
            seen.Add((rec.Surface, rec.FormIndex)).Should().BeTrue(
                $"(surface={rec.Surface}, formIdx={rec.FormIndex}) should be emitted only once");
        }
    }

    [Fact]
    public void V1_TaberuDepth3ProducesCausativePassiveFamily()
    {
        var gen = ConjugationTableGenerator.FromSharedResources();
        var word = MakeWord(6, "v1", "食べる", "たべる");
        var surfaces = gen.Generate(word, maxDepth: 3, perWordCap: 10_000)
                          .Select(r => r.Surface).ToHashSet();

        surfaces.Should().Contain("食べさせる");         // causative (depth 1)
        surfaces.Should().Contain("食べられる");         // passive/potential (depth 1)
        surfaces.Should().Contain("食べさせられる");     // caus-pass (depth 2)
        surfaces.Should().Contain("食べたい");           // desiderative (depth 2 via stem-ren)
    }
}
