using FluentAssertions;
using Jiten.Core.Data.JMDict;
using Jiten.Parser.Conjugation;

namespace Jiten.Tests;

public class ForwardConjugationGeneratorTests
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
    public void V1_TaberuParadigm()
    {
        var gen = ForwardConjugationGenerator.FromSharedResources();
        var word = MakeWord(1, "v1", "食べる", "たべる");

        var surfaces = gen.Generate(word).Select(r => r.Surface).ToHashSet();

        surfaces.Should().Contain("食べる");      // identity
        surfaces.Should().Contain("食べた");      // past
        surfaces.Should().Contain("食べて");      // te
        surfaces.Should().Contain("食べない");    // negative
        surfaces.Should().Contain("食べれば");    // provisional
        surfaces.Should().Contain("食べられる");  // potential/passive (same for v1)
        surfaces.Should().Contain("食べさせる");  // causative
        surfaces.Should().Contain("食べろ");      // imperative
        surfaces.Should().Contain("食べよう");    // volitional
        surfaces.Should().Contain("食べたら");    // conditional
    }

    [Fact]
    public void V5u_Kau()
    {
        var gen = ForwardConjugationGenerator.FromSharedResources();
        var word = MakeWord(2, "v5u", "買う");
        var surfaces = gen.Generate(word).Select(r => r.Surface).ToHashSet();

        surfaces.Should().Contain("買う");
        surfaces.Should().Contain("買った");
        surfaces.Should().Contain("買って");
        surfaces.Should().Contain("買わない");
        surfaces.Should().Contain("買える");     // potential (godan e-ru)
        surfaces.Should().Contain("買われる");   // passive
    }

    [Fact]
    public void AdjI_TakaiParadigm()
    {
        var gen = ForwardConjugationGenerator.FromSharedResources();
        var word = MakeWord(3, "adj-i", "高い", "たかい");
        var surfaces = gen.Generate(word).Select(r => r.Surface).ToHashSet();

        surfaces.Should().Contain("高い");
        surfaces.Should().Contain("高かった");
        surfaces.Should().Contain("高くない");
        surfaces.Should().Contain("高くて");
        surfaces.Should().Contain("高ければ");
    }

    [Fact]
    public void SecondaryConjugation_PassivePastNegative()
    {
        var gen = ForwardConjugationGenerator.FromSharedResources();
        var word = MakeWord(4, "v1", "食べる");
        var surfaces = gen.Generate(word).Select(r => r.Surface).ToHashSet();

        surfaces.Should().Contain("食べられた");       // passive + past
        surfaces.Should().Contain("食べられない");     // passive + negative
        surfaces.Should().Contain("食べられなかった"); // passive + past-negative
        surfaces.Should().Contain("食べさせた");       // causative + past
    }

    [Fact]
    public void IiClass_AdjIxUsesYoStem()
    {
        var gen = ForwardConjugationGenerator.FromSharedResources();
        var word = MakeWord(5, "adj-ix", "いい");
        var surfaces = gen.Generate(word).Select(r => r.Surface).ToHashSet();

        surfaces.Should().Contain("いい");
        surfaces.Should().Contain("よかった");
        surfaces.Should().Contain("よくない");
    }

    [Fact]
    public void PlainNoun_NotConjugable()
    {
        var gen = ForwardConjugationGenerator.FromSharedResources();
        var word = MakeWord(6, "n", "本");

        gen.IsConjugable(word).Should().BeFalse();
        gen.Generate(word).Should().BeEmpty();
    }
}
