using FluentAssertions;
using Jiten.Core.Data;
using Jiten.Parser;

namespace Jiten.Tests;

// Exercises the declarative token-rewrite engine (MorphologicalAnalyser.RewriteRules.cs) through the
// ApplyRewriteRulesForTesting hook with synthetic rules — no DB, no real rule table.
public class RewriteRuleEngineTests
{
    private static WordInfo Tok(string text, PartOfSpeech pos = PartOfSpeech.Noun,
        string? dict = null, string? reading = null, int start = -1, int end = -1, int? pin = null) =>
        new()
        {
            Text = text,
            PartOfSpeech = pos,
            DictionaryForm = dict ?? text,
            NormalizedForm = dict ?? text,
            Reading = reading ?? text,
            StartOffset = start,
            EndOffset = end,
            PreMatchedWordId = pin,
        };

    private static List<WordInfo> Run(RewriteRule rule, List<WordInfo> input, RewritePhase phase = RewritePhase.Early) =>
        new MorphologicalAnalyser().ApplyRewriteRulesForTesting(input, [rule], phase);

    [Fact]
    public void OnePinKeepsSurfaceAndOffsets_SetsDictFormAndPin()
    {
        var rule = new RewriteRule("darou", RewritePhase.Early,
            [new TokenPattern(Text: "だろー")],
            [new TokenTemplate("", DictForm: "だろう", NormalizedForm: "だろう", Pin: 1928670)]);

        var output = Run(rule, [Tok("だろー", start: 5, end: 8)]);

        output.Should().HaveCount(1);
        output[0].Text.Should().Be("だろー");
        output[0].DictionaryForm.Should().Be("だろう");
        output[0].NormalizedForm.Should().Be("だろう");
        output[0].PreMatchedWordId.Should().Be(1928670);
        output[0].StartOffset.Should().Be(5);
        output[0].EndOffset.Should().Be(8);
    }

    [Fact]
    public void OnePin_LeavesUnspecifiedFieldsAsSource()
    {
        // A pin that sets only DictForm must not touch NormalizedForm/Reading/POS (fields are independent).
        var rule = new RewriteRule("dictonly", RewritePhase.Early,
            [new TokenPattern(Text: "x", RequireUnpinned: false)],
            [new TokenTemplate("", DictForm: "X", Pin: 7)]);

        var output = Run(rule, [Tok("x", PartOfSpeech.Adverb, dict: "src", reading: "リーディング", start: 0, end: 1)]);

        output[0].DictionaryForm.Should().Be("X");
        output[0].NormalizedForm.Should().Be("src", "NormalizedForm is not coupled to DictForm");
        output[0].Reading.Should().Be("リーディング");
        output[0].PartOfSpeech.Should().Be(PartOfSpeech.Adverb);
        output[0].PreMatchedWordId.Should().Be(7);
    }

    [Fact]
    public void Split_TilesOffsets_TakesReadingsFromTemplates_ResetsPins()
    {
        // かって → か + って (offsets 10..13 tile as 10..11 / 11..13)
        // RequireUnpinned:false so the rule fires on a pinned source, letting us assert the reset.
        var rule = new RewriteRule("katte", RewritePhase.Early,
            [new TokenPattern(Text: "かって", RequireUnpinned: false)],
            [
                new TokenTemplate("か", DictForm: "か", Pos: PartOfSpeech.Particle, Reading: "カ"),
                new TokenTemplate("って", DictForm: "って", Pos: PartOfSpeech.Particle, Reading: "ッテ"),
            ]);

        var input = new List<WordInfo> { Tok("かって", PartOfSpeech.Adverb, reading: "カッテ", start: 10, end: 13, pin: 999) };
        var output = Run(rule, input);

        output.Select(t => t.Text).Should().Equal("か", "って");
        output[0].Reading.Should().Be("カ");
        output[1].Reading.Should().Be("ッテ");
        output[0].StartOffset.Should().Be(10);
        output[0].EndOffset.Should().Be(11);
        output[1].StartOffset.Should().Be(11);
        output[1].EndOffset.Should().Be(13);
        output.Should().OnlyContain(t => t.PreMatchedWordId == null, "a re-cut clears the source pin");
        output.Should().OnlyContain(t => t.PartOfSpeech == PartOfSpeech.Particle);
    }

    [Fact]
    public void Merge_CombinesSurface_UsesTemplateReading_SpansOffsets()
    {
        var rule = new RewriteRule("youha", RewritePhase.Early,
            [new TokenPattern(Text: "よう"), new TokenPattern(Text: "は")],
            [new TokenTemplate("ようは", DictForm: "ようは", Pos: PartOfSpeech.Conjunction, Reading: "ヨウハ", Pin: 1914670)]);

        var input = new List<WordInfo>
        {
            Tok("よう", reading: "ヨウ", start: 0, end: 2),
            Tok("は", PartOfSpeech.Particle, reading: "ハ", start: 2, end: 3),
        };
        var output = Run(rule, input);

        output.Should().HaveCount(1);
        output[0].Text.Should().Be("ようは");
        output[0].Reading.Should().Be("ヨウハ");
        output[0].PreMatchedWordId.Should().Be(1914670);
        output[0].StartOffset.Should().Be(0);
        output[0].EndOffset.Should().Be(3);
    }

    [Fact]
    public void PrevContext_GatesTheRule()
    {
        var rule = new RewriteRule("ka-after-nani", RewritePhase.Early,
            [new TokenPattern(Text: "かって")],
            [
                new TokenTemplate("か", DictForm: "か", Pos: PartOfSpeech.Particle, Reading: "カ"),
                new TokenTemplate("って", DictForm: "って", Pos: PartOfSpeech.Particle, Reading: "ッテ"),
            ],
            Prev: new ContextCond(TextAnyOf: ["何", "誰"]));

        Run(rule, [Tok("何"), Tok("かって", start: 1, end: 4)]).Select(t => t.Text)
            .Should().Equal("何", "か", "って");
        // No qualifying prev → rule does not fire.
        Run(rule, [Tok("彼"), Tok("かって", start: 1, end: 4)]).Select(t => t.Text)
            .Should().Equal("彼", "かって");
    }

    [Fact]
    public void NextContext_Negate_GatesTheRule()
    {
        // Fires only when the next token is NOT と.
        var rule = new RewriteRule("nashi", RewritePhase.Early,
            [new TokenPattern(Text: "ナシ")],
            [new TokenTemplate("", DictForm: "なし", Pin: 1)],
            Next: new ContextCond(TextAnyOf: ["と"], Negate: true));

        Run(rule, [Tok("ナシ", start: 0, end: 2), Tok("を", PartOfSpeech.Particle)])[0]
            .PreMatchedWordId.Should().Be(1);
        Run(rule, [Tok("ナシ", start: 0, end: 2), Tok("と", PartOfSpeech.Particle)])[0]
            .PreMatchedWordId.Should().BeNull("と after ナシ blocks the pin");
    }

    [Fact]
    public void LookupGuard_ExpandsPattern_AndGatesOnDelegate()
    {
        var rule = new RewriteRule("kari", RewritePhase.Late,
            [new TokenPattern(Text: "貸"), new TokenPattern(Text: "り")],
            [new TokenTemplate("貸り", DictForm: "貸りる", Pos: PartOfSpeech.Verb, Reading: "カリ")],
            Guard: new LookupGuard(LookupGuardKind.CompoundExists, "{0}{1}る"));

        var analyser = new MorphologicalAnalyser { HasCompoundLookup = s => s == "貸りる" };
        var input = new List<WordInfo> { Tok("貸", start: 0, end: 1), Tok("り", start: 1, end: 2) };
        analyser.ApplyRewriteRulesForTesting(input, [rule], RewritePhase.Late)
            .Select(t => t.Text).Should().Equal("貸り");

        var noGuard = new MorphologicalAnalyser { HasCompoundLookup = _ => false };
        noGuard.ApplyRewriteRulesForTesting(input, [rule], RewritePhase.Late)
            .Select(t => t.Text).Should().Equal("貸", "り");
    }

    [Fact]
    public void RequireUnpinned_SkipsAlreadyPinnedToken()
    {
        var rule = new RewriteRule("pin", RewritePhase.Early,
            [new TokenPattern(Text: "だろー")],
            [new TokenTemplate("", DictForm: "だろう", Pin: 1928670)]);

        // Token already carries a pin → default RequireUnpinned means the rule leaves it alone.
        Run(rule, [Tok("だろー", start: 0, end: 3, pin: 42)])[0].PreMatchedWordId.Should().Be(42);
    }

    [Fact]
    public void NoMatch_ReturnsSameReference()
    {
        var rule = new RewriteRule("noop", RewritePhase.Early,
            [new TokenPattern(Text: " zzz ")],
            [new TokenTemplate("", Pin: 1)]);

        var input = new List<WordInfo> { Tok("犬"), Tok("猫") };
        var output = Run(rule, input);
        ReferenceEquals(input, output).Should().BeTrue();
    }

    [Fact]
    public void PhaseIsolation_RuleOnlyRunsInItsPhase()
    {
        var rule = new RewriteRule("late-only", RewritePhase.Late,
            [new TokenPattern(Text: "だろー")],
            [new TokenTemplate("", DictForm: "だろう", Pin: 1)]);

        // Asking for the Early phase must not fire a Late rule.
        Run(rule, [Tok("だろー", start: 0, end: 3)], RewritePhase.Early)[0].PreMatchedWordId.Should().BeNull();
        Run(rule, [Tok("だろー", start: 0, end: 3)], RewritePhase.Late)[0].PreMatchedWordId.Should().Be(1);
    }

    [Fact]
    public void Validation_RejectsDuplicateIds()
    {
        var a = new RewriteRule("dup", RewritePhase.Early, [new TokenPattern(Text: "x")], [new TokenTemplate("", Pin: 1)]);
        var act = () => new MorphologicalAnalyser().ApplyRewriteRulesForTesting([Tok("x")], [a, a], RewritePhase.Early);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Duplicate*dup*");
    }

    [Fact]
    public void Validation_RejectsNonConservingLiteralSplit()
    {
        var bad = new RewriteRule("bad", RewritePhase.Early,
            [new TokenPattern(Text: "ab")],
            [new TokenTemplate("a", Reading: "A"), new TokenTemplate("c", Reading: "C")]);
        var act = () => new MorphologicalAnalyser().ApplyRewriteRulesForTesting([Tok("ab")], [bad], RewritePhase.Early);
        act.Should().Throw<InvalidOperationException>().WithMessage("*conserve*");
    }

    [Fact]
    public void Validation_RequiresReadingOnSplitOutputs()
    {
        var bad = new RewriteRule("noreading", RewritePhase.Early,
            [new TokenPattern(Text: "ab")],
            [new TokenTemplate("a"), new TokenTemplate("b")]);
        var act = () => new MorphologicalAnalyser().ApplyRewriteRulesForTesting([Tok("ab")], [bad], RewritePhase.Early);
        act.Should().Throw<InvalidOperationException>().WithMessage("*must specify a Reading*");
    }
}
