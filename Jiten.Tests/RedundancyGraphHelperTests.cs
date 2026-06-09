using FluentAssertions;
using Jiten.Api.Helpers;
using Jiten.Core.Data.JMDict;
using Xunit;

namespace Jiten.Tests;

public class RedundancyGraphHelperTests
{
    private static JmDictWordForm Form(short ri, string text, JmDictFormType type, string? ruby = null)
        => new() { WordId = 1, ReadingIndex = ri, Text = text, FormType = type, RubyText = ruby ?? text };

    [Fact]
    public void Degradation_MarksFewerKanjiAndFullKana_ButNotDifferentKanjiOrStructure()
    {
        var forms = new List<JmDictWordForm>
        {
            Form(0, "落ち着ける", JmDictFormType.KanjiForm, "落[お]ち着[つ]ける"), // source (known)
            Form(1, "落ちつける", JmDictFormType.KanjiForm),                       // 着 -> つ  => redundant
            Form(2, "おちつける", JmDictFormType.KanaForm),                         // full kana => redundant
            Form(3, "落ち付ける", JmDictFormType.KanjiForm),                       // 付 != 着  => NOT
            Form(4, "落着ける", JmDictFormType.KanjiForm),                          // dropped ち => NOT
            Form(5, "落付ける", JmDictFormType.KanjiForm),                          // 付 + dropped ち => NOT
        };

        var edges = RedundancyGraphHelper.BuildEdges(forms);

        var fromSource0 = edges.Where(e => e.Source == 0).Select(e => e.Target).OrderBy(x => x).ToArray();
        fromSource0.Should().Equal((byte)1, (byte)2);
    }

    [Fact]
    public void ScriptVariants_AreMutuallyRedundant()
    {
        var forms = new List<JmDictWordForm>
        {
            Form(0, "おちつける", JmDictFormType.KanaForm),
            Form(1, "オチツケル", JmDictFormType.KanaForm),
        };

        var edges = RedundancyGraphHelper.BuildEdges(forms);

        edges.Should().Contain(((byte)0, (byte)1));
        edges.Should().Contain(((byte)1, (byte)0));
    }

    [Fact]
    public void Kana_DoesNotDominate_Kanji()
    {
        var forms = new List<JmDictWordForm>
        {
            Form(0, "飲む", JmDictFormType.KanjiForm, "飲[の]む"),
            Form(1, "のむ", JmDictFormType.KanaForm),
        };

        var edges = RedundancyGraphHelper.BuildEdges(forms);

        edges.Should().Contain(((byte)0, (byte)1));      // kanji -> kana
        edges.Should().NotContain(((byte)1, (byte)0));   // kana -/-> kanji
    }
}
