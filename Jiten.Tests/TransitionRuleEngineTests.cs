using FluentAssertions;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Jiten.Parser;
using Jiten.Parser.Grammar;
using Jiten.Parser.Scoring;
using Xunit;

namespace Jiten.Tests;

public class TransitionRuleEngineTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static (WordInfo word, int pos, int len) W(
        string text,
        PartOfSpeech pos,
        string? dictForm = null,
        PartOfSpeechSection section1 = PartOfSpeechSection.None)
        => (new WordInfo
        {
            Text = text,
            DictionaryForm = dictForm ?? text,
            NormalizedForm = text,
            PartOfSpeech = pos,
            PartOfSpeechSection1 = section1
        }, 0, text.Length);

    private static List<(WordInfo word, int pos, int len)> Sentence(
        params (WordInfo word, int pos, int len)[] tokens)
        => [.. tokens];

    // Lookup that always returns false (no merge possible)
    private static readonly Func<string, bool> NoLookup = _ => false;

    // Lookup that returns true for a given merged form
    private static Func<string, bool> WithLookup(string merged) => s => s == merged;

    private static FormCandidate MakeCandidate(string formText, params string[] pos)
    {
        var word = new JmDictWord { WordId = 1, PartsOfSpeech = [..pos] };
        var form = new JmDictWordForm { Text = formText };
        return new FormCandidate(word, form, 0, formText);
    }

    private static ScoringWindow MakeWindow(
        FormCandidate candidate,
        List<PartOfSpeech>? prevPOS = null, List<PartOfSpeech>? nextPOS = null,
        string? prevText = null, string? nextText = null)
        => new(candidate,
            prevPOS != null ? PosMask.FromList(prevPOS) : 0, prevPOS != null, prevText,
            nextPOS != null ? PosMask.FromList(nextPOS) : 0, nextPOS != null, nextText);

    private static (int bonus, List<string> rules) EvaluateSoftRules(ScoringWindow window)
    {
        var rules = new List<string>();
        var mask = TransitionRuleEngine.ComputeContextApplicableMask(
            window.PrevMask, window.HasPrev, window.PrevText,
            window.NextMask, window.HasNext, window.NextText);
        var bonus = TransitionRuleEngine.EvaluateSoftRules(window, mask, rules);
        return (bonus, rules);
    }

    // -----------------------------------------------------------------------
    // Phase 1: leading-aux-strip
    // -----------------------------------------------------------------------

    [Fact]
    public void LeadingAuxStrip_RemovesSingleLeadingVerbOnlyAux()
    {
        var words = Sentence(W("られる", PartOfSpeech.Auxiliary, "られる"));
        TransitionRuleEngine.ApplyHardRules(words, NoLookup);
        words.Should().BeEmpty();
    }

    [Fact]
    public void LeadingAuxStrip_RemovesAllLeadingVerbAttachingAux()
    {
        // られる ます → both stripped
        var words = Sentence(
            W("られる", PartOfSpeech.Auxiliary, "られる"),
            W("ます", PartOfSpeech.Auxiliary, "ます"));
        TransitionRuleEngine.ApplyHardRules(words, NoLookup);
        words.Should().BeEmpty();
    }

    [Fact]
    public void LeadingAuxStrip_RemovesLeadingVerbOrAdjAux()
    {
        // た at sentence start → stripped
        var words = Sentence(
            W("た", PartOfSpeech.Auxiliary, "た"),
            W("食べる", PartOfSpeech.Verb, "食べる"));
        TransitionRuleEngine.ApplyHardRules(words, NoLookup);
        words.Select(w => w.word.Text).Should().Equal("食べる");
    }

    [Fact]
    public void LeadingAuxStrip_DoesNotRemoveNonVerbAttachingAux()
    {
        // An auxiliary not in the aux dict forms should NOT be stripped
        var words = Sentence(W("だ", PartOfSpeech.Auxiliary, "だ"));
        TransitionRuleEngine.ApplyHardRules(words, NoLookup);
        words.Should().HaveCount(1);
    }

    [Fact]
    public void LeadingAuxStrip_PreservesNonAuxFirst()
    {
        var words = Sentence(
            W("食べる", PartOfSpeech.Verb),
            W("られる", PartOfSpeech.Auxiliary, "られる"));
        TransitionRuleEngine.ApplyHardRules(words, NoLookup);
        words.Select(w => w.word.Text).Should().Equal("食べる", "られる");
    }

    // -----------------------------------------------------------------------
    // Phase 2a: aux-must-follow-verb (VerbOnlyAuxDictForms)
    // -----------------------------------------------------------------------

    [Fact]
    public void AuxMustFollowVerb_RemovesAuxAfterParticle()
    {
        // 本/Noun + を/Particle + られる/Aux → られる removed
        var words = Sentence(
            W("本", PartOfSpeech.Noun),
            W("を", PartOfSpeech.Particle),
            W("られる", PartOfSpeech.Auxiliary, "られる"));
        TransitionRuleEngine.ApplyHardRules(words, NoLookup);
        words.Select(w => w.word.Text).Should().Equal("本", "を");
    }

    [Fact]
    public void AuxMustFollowVerb_KeepsAuxAfterVerb()
    {
        var words = Sentence(
            W("食べ", PartOfSpeech.Verb),
            W("られる", PartOfSpeech.Auxiliary, "られる"));
        TransitionRuleEngine.ApplyHardRules(words, NoLookup);
        words.Select(w => w.word.Text).Should().Equal("食べ", "られる");
    }

    [Fact]
    public void AuxMustFollowVerb_KeepsAuxAfterAux()
    {
        // Verb + causative-aux + passive-aux — られる follows an Auxiliary, which is valid
        var words = Sentence(
            W("食べ", PartOfSpeech.Verb),
            W("させ", PartOfSpeech.Auxiliary, "させる"),
            W("られる", PartOfSpeech.Auxiliary, "られる"));
        TransitionRuleEngine.ApplyHardRules(words, NoLookup);
        words.Select(w => w.word.Text).Should().Equal("食べ", "させ", "られる");
    }

    [Fact]
    public void AuxMustFollowVerb_MergesWhenLookupSucceeds()
    {
        // 食べ + られる → 食べられる (valid compound)
        var words = Sentence(
            W("食べ", PartOfSpeech.Verb),
            W("られる", PartOfSpeech.Auxiliary, "られる"));

        // Artificially make the rule fail by giving prev POS = Noun
        words[0] = (new WordInfo
        {
            Text = "本",
            DictionaryForm = "本",
            NormalizedForm = "本",
            PartOfSpeech = PartOfSpeech.Noun
        }, 0, 1);

        TransitionRuleEngine.ApplyHardRules(words, WithLookup("本られる"));
        words.Should().HaveCount(1);
        words[0].word.Text.Should().Be("本られる");
        words[0].word.PartOfSpeech.Should().Be(PartOfSpeech.Noun);
    }

    // -----------------------------------------------------------------------
    // Phase 2b: verb-or-adj-aux-must-follow-content (VerbOrAdjAuxDictForms)
    // -----------------------------------------------------------------------

    [Fact]
    public void VerbOrAdjAuxMustFollowContent_RemovesAfterParticle()
    {
        // で + た → た removed
        var words = Sentence(
            W("で", PartOfSpeech.Particle),
            W("た", PartOfSpeech.Auxiliary, "た"));
        TransitionRuleEngine.ApplyHardRules(words, NoLookup);
        words.Select(w => w.word.Text).Should().Equal("で");
    }

    [Fact]
    public void VerbOrAdjAuxMustFollowContent_KeepsAfterIAdj()
    {
        // 美味しかっ/IAdjective + た/Aux → kept
        var words = Sentence(
            W("美味しかっ", PartOfSpeech.IAdjective),
            W("た", PartOfSpeech.Auxiliary, "た"));
        TransitionRuleEngine.ApplyHardRules(words, NoLookup);
        words.Select(w => w.word.Text).Should().Equal("美味しかっ", "た");
    }

    [Fact]
    public void VerbOrAdjAuxMustFollowContent_KeepsAfterVerb()
    {
        var words = Sentence(
            W("飲ん", PartOfSpeech.Verb),
            W("だ", PartOfSpeech.Auxiliary, "た")); // だ is surface form of た
        TransitionRuleEngine.ApplyHardRules(words, NoLookup);
        words.Select(w => w.word.Text).Should().Equal("飲ん", "だ");
    }

    // -----------------------------------------------------------------------
    // Phase 3: counter-must-follow-numberlike
    // -----------------------------------------------------------------------

    [Fact]
    public void Counter_AtSentenceStart_ReclassifiedAsNoun()
    {
        var words = Sentence(W("個", PartOfSpeech.Counter, "個"));
        TransitionRuleEngine.ApplyHardRules(words, NoLookup);
        words.Should().HaveCount(1);
        words[0].word.PartOfSpeech.Should().Be(PartOfSpeech.Noun);
    }

    [Fact]
    public void Counter_AfterNonNumeric_ReclassifiedAsNoun()
    {
        var words = Sentence(
            W("美味しい", PartOfSpeech.IAdjective),
            W("個", PartOfSpeech.Counter, "個"));
        TransitionRuleEngine.ApplyHardRules(words, NoLookup);
        words[1].word.PartOfSpeech.Should().Be(PartOfSpeech.Noun);
    }

    [Fact]
    public void Counter_AfterNumeral_Unchanged()
    {
        var words = Sentence(
            W("三", PartOfSpeech.Numeral),
            W("個", PartOfSpeech.Counter, "個"));
        TransitionRuleEngine.ApplyHardRules(words, NoLookup);
        words[1].word.PartOfSpeech.Should().Be(PartOfSpeech.Counter);
    }

    [Fact]
    public void Counter_AfterNoun_Unchanged()
    {
        var words = Sentence(
            W("リンゴ", PartOfSpeech.Noun),
            W("個", PartOfSpeech.Counter, "個"));
        TransitionRuleEngine.ApplyHardRules(words, NoLookup);
        words[1].word.PartOfSpeech.Should().Be(PartOfSpeech.Counter);
    }

    [Fact]
    public void SuffixWithCounterSection_AtStart_ReclassifiedAsNoun()
    {
        var words = Sentence(W("枚", PartOfSpeech.Suffix, "枚", PartOfSpeechSection.Counter));
        TransitionRuleEngine.ApplyHardRules(words, NoLookup);
        words[0].word.PartOfSpeech.Should().Be(PartOfSpeech.Noun);
    }

    // -----------------------------------------------------------------------
    // Phase 4: sfp-must-be-near-clause-end
    // -----------------------------------------------------------------------

    [Fact]
    public void Sfp_FollowedByContentWord_MergedOrRemoved()
    {
        // 食べる + よ(SFP) + 明日(Noun) → よ should be merged/removed
        var words = Sentence(
            W("食べる", PartOfSpeech.Verb),
            W("よ", PartOfSpeech.Particle, "よ", PartOfSpeechSection.SentenceEndingParticle),
            W("明日", PartOfSpeech.Noun));
        TransitionRuleEngine.ApplyHardRules(words, NoLookup);
        words.Select(w => w.word.Text).Should().Equal("食べる", "明日");
    }

    [Fact]
    public void Sfp_AtSentenceEnd_Preserved()
    {
        var words = Sentence(
            W("行く", PartOfSpeech.Verb),
            W("よ", PartOfSpeech.Particle, "よ", PartOfSpeechSection.SentenceEndingParticle));
        TransitionRuleEngine.ApplyHardRules(words, NoLookup);
        words.Select(w => w.word.Text).Should().Equal("行く", "よ");
    }

    [Fact]
    public void Sfp_FollowedByAnotherParticle_Preserved()
    {
        // よ + ね → both are SFPs, valid chain
        var words = Sentence(
            W("行く", PartOfSpeech.Verb),
            W("よ", PartOfSpeech.Particle, "よ", PartOfSpeechSection.SentenceEndingParticle),
            W("ね", PartOfSpeech.Particle, "ね", PartOfSpeechSection.SentenceEndingParticle));
        TransitionRuleEngine.ApplyHardRules(words, NoLookup);
        words.Select(w => w.word.Text).Should().Equal("行く", "よ", "ね");
    }

    [Fact]
    public void Sfp_FollowedByAuxiliary_Preserved()
    {
        var words = Sentence(
            W("行く", PartOfSpeech.Verb),
            W("ぞ", PartOfSpeech.Particle, "ぞ", PartOfSpeechSection.SentenceEndingParticle),
            W("た", PartOfSpeech.Auxiliary, "た"));
        TransitionRuleEngine.ApplyHardRules(words, NoLookup);
        words.Select(w => w.word.Text).Should().Equal("行く", "ぞ", "た");
    }

    [Fact]
    public void Sfp_MergesWithPrevious_WhenLookupSucceeds()
    {
        // 食べ + よ(SFP) + 明日(Noun) → 食べよ exists in lookups → merged
        var words = Sentence(
            W("食べ", PartOfSpeech.Verb),
            W("よ", PartOfSpeech.Particle, "よ", PartOfSpeechSection.SentenceEndingParticle),
            W("明日", PartOfSpeech.Noun));
        TransitionRuleEngine.ApplyHardRules(words, WithLookup("食べよ"));
        words.Should().HaveCount(2);
        words[0].word.Text.Should().Be("食べよ");
        words[1].word.Text.Should().Be("明日");
    }

    [Fact]
    public void Sfp_AfterPlainPredicate_BeforeVocative_Preserved()
    {
        var words = Sentence(
            W("やべえ", PartOfSpeech.IAdjective),
            W("よ", PartOfSpeech.Particle, "よ", PartOfSpeechSection.SentenceEndingParticle),
            W("母ちゃん", PartOfSpeech.CommonNoun));
        TransitionRuleEngine.ApplyHardRules(words, NoLookup);
        words.Select(w => w.word.Text).Should().Equal("やべえ", "よ", "母ちゃん");
    }

    [Fact]
    public void Sfp_MergesWithNounPrev_PreservesNounPos_NotForcedToVerb()
    {
        // SFP merge must inherit the predecessor's POS, not force PartOfSpeech.Verb.
        // Previous word is a Noun; the merged token must remain a Noun.
        var words = Sentence(
            W("本", PartOfSpeech.Noun),
            W("よ", PartOfSpeech.Particle, "よ", PartOfSpeechSection.SentenceEndingParticle),
            W("読む", PartOfSpeech.Verb));
        TransitionRuleEngine.ApplyHardRules(words, WithLookup("本よ"));

        words.Should().HaveCount(2);
        words[0].word.Text.Should().Be("本よ");
        words[0].word.PartOfSpeech.Should().Be(PartOfSpeech.Noun,
            "SFP merge must preserve the predecessor's POS, not force PartOfSpeech.Verb");
        words[1].word.Text.Should().Be("読む");
    }

    [Fact]
    public void Sfp_AtSentenceStart_FollowedByContent_Removed()
    {
        // よ(SFP) + 食べる(Verb) at start → leading-aux-strip doesn't apply (not aux), but SFP rule fires
        var words = Sentence(
            W("よ", PartOfSpeech.Particle, "よ", PartOfSpeechSection.SentenceEndingParticle),
            W("食べる", PartOfSpeech.Verb));
        TransitionRuleEngine.ApplyHardRules(words, NoLookup);
        words.Select(w => w.word.Text).Should().Equal("食べる");
    }

    [Fact]
    public void Sfp_Ne_FollowedByVerb_Removed()
    {
        var words = Sentence(
            W("本", PartOfSpeech.Noun),
            W("ね", PartOfSpeech.Particle, "ね", PartOfSpeechSection.SentenceEndingParticle),
            W("読む", PartOfSpeech.Verb));
        TransitionRuleEngine.ApplyHardRules(words, NoLookup);
        words.Select(w => w.word.Text).Should().Equal("本", "読む");
    }

    [Fact]
    public void Sfp_NonSentenceEndingParticle_MidClause_NotAffected()
    {
        // Case-marking particle を mid-clause → rule should NOT fire
        var words = Sentence(
            W("本", PartOfSpeech.Noun),
            W("を", PartOfSpeech.Particle, "を", PartOfSpeechSection.CaseMarkingParticle),
            W("読む", PartOfSpeech.Verb));
        TransitionRuleEngine.ApplyHardRules(words, NoLookup);
        words.Select(w => w.word.Text).Should().Equal("本", "を", "読む");
    }

    [Fact]
    public void Sfp_AfterAuxiliary_FollowedByContent_Preserved()
    {
        // だ(Aux) + な(SFP) + 気をつける(Verb) → な must be preserved (valid terminal sub-clause expression)
        // This prevents "だな" from being incorrectly re-merged when Sudachi misparsed だな as 棚
        var words = Sentence(
            W("だ", PartOfSpeech.Auxiliary),
            W("な", PartOfSpeech.Particle, "な", PartOfSpeechSection.SentenceEndingParticle),
            W("気をつける", PartOfSpeech.Verb));
        TransitionRuleEngine.ApplyHardRules(words, WithLookup("だな"));
        words.Select(w => w.word.Text).Should().Equal("だ", "な", "気をつける");
    }

    // -----------------------------------------------------------------------
    // Phase 5: prefix-must-precede-content
    // -----------------------------------------------------------------------

    [Fact]
    public void Prefix_AtSentenceEnd_ReclassifiedAsNoun()
    {
        var words = Sentence(W("御", PartOfSpeech.Prefix));
        TransitionRuleEngine.ApplyHardRules(words, NoLookup);
        words.Should().HaveCount(1);
        words[0].word.PartOfSpeech.Should().Be(PartOfSpeech.Noun);
    }

    [Fact]
    public void Prefix_BeforeParticle_ReclassifiedAsNoun()
    {
        var words = Sentence(
            W("御", PartOfSpeech.Prefix),
            W("が", PartOfSpeech.Particle));
        TransitionRuleEngine.ApplyHardRules(words, NoLookup);
        words[0].word.PartOfSpeech.Should().Be(PartOfSpeech.Noun);
    }

    [Fact]
    public void Prefix_BeforeNoun_Unchanged()
    {
        var words = Sentence(
            W("御", PartOfSpeech.Prefix),
            W("飯", PartOfSpeech.Noun));
        TransitionRuleEngine.ApplyHardRules(words, NoLookup);
        words[0].word.PartOfSpeech.Should().Be(PartOfSpeech.Prefix);
    }

    [Fact]
    public void Prefix_BeforeVerb_Unchanged()
    {
        var words = Sentence(
            W("御", PartOfSpeech.Prefix),
            W("覧", PartOfSpeech.Verb));
        TransitionRuleEngine.ApplyHardRules(words, NoLookup);
        words[0].word.PartOfSpeech.Should().Be(PartOfSpeech.Prefix);
    }

    // -----------------------------------------------------------------------
    // Diagnostics
    // -----------------------------------------------------------------------

    [Fact]
    public void ApplyHardRules_LogsViolationsTodiagnostics()
    {
        var diag = new Jiten.Parser.Diagnostics.ParserDiagnostics();
        var words = Sentence(W("られる", PartOfSpeech.Auxiliary, "られる"));
        TransitionRuleEngine.ApplyHardRules(words, NoLookup, diag);
        diag.TransitionViolations.Should().ContainSingle(v => v.RuleId == "leading-aux-strip");
    }

    // -----------------------------------------------------------------------
    // Soft rules: synergies
    // -----------------------------------------------------------------------

    [Fact]
    public void NounParticleSynergy_FiresWhenNounFollowedByCommonParticle()
    {
        var candidate = MakeCandidate("本", "n");
        var window = MakeWindow(candidate,
            nextPOS: [PartOfSpeech.Particle], nextText: "が");

        var (bonus, rules) = EvaluateSoftRules(window);
        bonus.Should().Be(40);
        rules.Should().Contain("noun-particle-synergy");
    }

    [Fact]
    public void NounCopulaSynergy_FiresWhenNounFollowedByCopula()
    {
        var candidate = MakeCandidate("学生", "n");
        var window = MakeWindow(candidate, nextText: "です");

        var (bonus, rules) = EvaluateSoftRules(window);
        bonus.Should().Be(65);
        rules.Should().Contain("noun-copula-synergy");
    }

    [Fact]
    public void NaAdjConnectorSynergy_FiresWhenNaAdjFollowedByNa()
    {
        var candidate = MakeCandidate("静か", "adj-na");
        var window = MakeWindow(candidate, nextText: "な");

        var (bonus, rules) = EvaluateSoftRules(window);
        bonus.Should().Be(30);
        rules.Should().Contain("na-adj-connector-synergy");
    }

    [Fact]
    public void AdverbVerbSynergy_FiresWhenAdverbFollowedByVerb()
    {
        var candidate = MakeCandidate("とても", "adv");
        var window = MakeWindow(candidate,
            nextPOS: [PartOfSpeech.Verb], nextText: "食べる");

        var (bonus, rules) = EvaluateSoftRules(window);
        bonus.Should().Be(20);
        rules.Should().Contain("adverb-verb-synergy");
    }

    [Fact]
    public void VerbAuxSynergy_FiresWhenAuxFollowsVerb()
    {
        var candidate = MakeCandidate("た", "aux");
        var window = MakeWindow(candidate,
            prevPOS: [PartOfSpeech.Verb], prevText: "食べ");

        var (bonus, rules) = EvaluateSoftRules(window);
        bonus.Should().Be(30);
        rules.Should().Contain("verb-aux-synergy");
        rules.Should().Contain("verb-sentence-final-synergy");
    }

    // -----------------------------------------------------------------------
    // Soft rules: penalties
    // -----------------------------------------------------------------------

    [Fact]
    public void SingleKanaPenalty_FiresIndependentlyForLeftAndRight()
    {
        var candidate = MakeCandidate("あ", "n");
        var window = MakeWindow(candidate,
            prevPOS: [PartOfSpeech.Noun], prevText: "い",
            nextPOS: [PartOfSpeech.Noun], nextText: "う");

        var (bonus, rules) = EvaluateSoftRules(window);
        bonus.Should().Be(-80);
        rules.Should().Contain("single-kana-penalty-left");
        rules.Should().Contain("single-kana-penalty-right");
    }

    [Fact]
    public void SingleKanaPenalty_DoesNotFireForParticles()
    {
        var candidate = MakeCandidate("が", "prt");
        var window = MakeWindow(candidate,
            prevPOS: [PartOfSpeech.Noun], prevText: "あ",
            nextPOS: [PartOfSpeech.Noun], nextText: "い");

        var (bonus, rules) = EvaluateSoftRules(window);
        rules.Should().NotContain("single-kana-penalty-left");
        rules.Should().NotContain("single-kana-penalty-right");
    }

    [Fact]
    public void ParticleParticlePenalty_FiresForLeftAndRightIndependently()
    {
        var candidate = MakeCandidate("は", "prt");
        var window = MakeWindow(candidate,
            prevPOS: [PartOfSpeech.Particle], prevText: "が",
            nextPOS: [PartOfSpeech.Particle], nextText: "も");

        var (bonus, rules) = EvaluateSoftRules(window);
        bonus.Should().Be(-40);
        rules.Should().Contain("particle-particle-penalty-left");
        rules.Should().Contain("particle-particle-penalty-right");
    }

    [Fact]
    public void ParticleParticlePenalty_OnlyLeftFires()
    {
        var candidate = MakeCandidate("は", "prt");
        var window = MakeWindow(candidate,
            prevPOS: [PartOfSpeech.Particle], prevText: "が",
            nextPOS: [PartOfSpeech.Verb], nextText: "食べる");

        var (bonus, rules) = EvaluateSoftRules(window);
        bonus.Should().Be(-20);
        rules.Should().Contain("particle-particle-penalty-left");
        rules.Should().NotContain("particle-particle-penalty-right");
    }

    // -----------------------------------------------------------------------
    // Soft rules: combined scenarios
    // -----------------------------------------------------------------------

    [Fact]
    public void NounWithParticleAndCopula_BothSynergiesFire()
    {
        // na-adjective followed by な gets both noun-like synergies if context matches
        var candidate = MakeCandidate("静か", "adj-na");
        var window = MakeWindow(candidate,
            nextPOS: [PartOfSpeech.Particle], nextText: "な");

        var (bonus, rules) = EvaluateSoftRules(window);
        // CandidateIsNounLike matches adj-na, NextIsCommonParticle does NOT match (な not in CommonParticles)
        // But NextIsNaConnector matches
        rules.Should().Contain("na-adj-connector-synergy");
    }

    [Fact]
    public void NoDaSynergy_FiresWhenVerbPrecedesNoFollowedByDa()
    {
        // 行くのだ — verb + の + だ → explanatory pattern
        var candidate = MakeCandidate("の", "prt");
        var window = MakeWindow(candidate,
            prevPOS: [PartOfSpeech.Verb], prevText: "行く",
            nextText: "だ");

        var (bonus, rules) = EvaluateSoftRules(window);
        bonus.Should().Be(25);
        rules.Should().Contain("no-da-synergy");
    }

    [Fact]
    public void NoDaSynergy_FiresWithAuxPrevAndDesuNext()
    {
        // 食べているのです — auxiliary (いる) + の + です
        var candidate = MakeCandidate("の", "prt");
        var window = MakeWindow(candidate,
            prevPOS: [PartOfSpeech.Auxiliary], prevText: "いる",
            nextText: "です");

        var (bonus, rules) = EvaluateSoftRules(window);
        bonus.Should().Be(25);
        rules.Should().Contain("no-da-synergy");
    }

    [Fact]
    public void NoDaSynergy_FiresWithIAdjPrev()
    {
        // 寒いのだ — i-adjective + の + だ
        var candidate = MakeCandidate("の", "prt");
        var window = MakeWindow(candidate,
            prevPOS: [PartOfSpeech.IAdjective], prevText: "寒い",
            nextText: "だ");

        var (bonus, rules) = EvaluateSoftRules(window);
        bonus.Should().Be(25);
        rules.Should().Contain("no-da-synergy");
    }

    [Fact]
    public void NoDaSynergy_DoesNotFireForPossessiveNo()
    {
        // 私のだ — noun + の + だ (possessive, not explanatory)
        var candidate = MakeCandidate("の", "prt");
        var window = MakeWindow(candidate,
            prevPOS: [PartOfSpeech.Noun], prevText: "私",
            nextText: "だ");

        var (bonus, rules) = EvaluateSoftRules(window);
        rules.Should().NotContain("no-da-synergy");
    }

    [Fact]
    public void NoDaSynergy_DoesNotFireWithoutCopulaNext()
    {
        // 行くのに — の followed by に, not copula
        var candidate = MakeCandidate("の", "prt");
        var window = MakeWindow(candidate,
            prevPOS: [PartOfSpeech.Verb], prevText: "行く",
            nextText: "に");

        var (bonus, rules) = EvaluateSoftRules(window);
        rules.Should().NotContain("no-da-synergy");
    }

    // -----------------------------------------------------------------------
    // Soft rules: predicate-explanatory-n-synergy
    // -----------------------------------------------------------------------

    [Fact]
    public void PredicateExplanatoryN_FiresForVerbBeforeNda()
    {
        var candidate = MakeCandidate("できる", "v1");
        var window = MakeWindow(candidate, nextText: "んだ");

        var (bonus, rules) = EvaluateSoftRules(window);
        bonus.Should().Be(25);
        rules.Should().Contain("predicate-explanatory-n-synergy");
    }

    [Fact]
    public void PredicateExplanatoryN_FiresForIAdjBeforeN()
    {
        var candidate = MakeCandidate("高い", "adj-i");
        var window = MakeWindow(candidate, nextText: "ん");

        var (bonus, rules) = EvaluateSoftRules(window);
        bonus.Should().Be(25);
        rules.Should().Contain("predicate-explanatory-n-synergy");
    }

    [Fact]
    public void PredicateExplanatoryN_FiresForAuxBeforeNdesu()
    {
        var candidate = MakeCandidate("ない", "aux");
        var window = MakeWindow(candidate, nextText: "んです");

        var (bonus, rules) = EvaluateSoftRules(window);
        bonus.Should().Be(25);
        rules.Should().Contain("predicate-explanatory-n-synergy");
    }

    [Fact]
    public void PredicateExplanatoryN_DoesNotFireForNounBeforeNda()
    {
        var candidate = MakeCandidate("本", "n");
        var window = MakeWindow(candidate, nextText: "んだ");

        var (_, rules) = EvaluateSoftRules(window);
        rules.Should().NotContain("predicate-explanatory-n-synergy");
    }

    [Fact]
    public void PredicateExplanatoryN_DoesNotFireForVerbBeforeDa()
    {
        var candidate = MakeCandidate("できる", "v1");
        var window = MakeWindow(candidate, nextText: "だ");

        var (_, rules) = EvaluateSoftRules(window);
        rules.Should().NotContain("predicate-explanatory-n-synergy");
    }

    [Fact]
    public void PredicateExplanatoryN_FiresForVerbBeforeNja()
    {
        var candidate = MakeCandidate("行く", "v5k");
        var window = MakeWindow(candidate, nextText: "んじゃ");

        var (bonus, rules) = EvaluateSoftRules(window);
        bonus.Should().Be(25);
        rules.Should().Contain("predicate-explanatory-n-synergy");
    }

    // -----------------------------------------------------------------------
    // Soft rules: conjunctive-particle-verb-link
    // -----------------------------------------------------------------------

    [Fact]
    public void ConjunctiveParticleVerbLink_FiresForVerbBeforeTo()
    {
        var candidate = MakeCandidate("行く", "v5k");
        var window = MakeWindow(candidate,
            nextPOS: [PartOfSpeech.Particle], nextText: "と");

        var (bonus, rules) = EvaluateSoftRules(window);
        bonus.Should().Be(20);
        rules.Should().Contain("conjunctive-particle-verb-link");
    }

    [Fact]
    public void ConjunctiveParticleVerbLink_FiresForIAdjBeforeNara()
    {
        var candidate = MakeCandidate("高い", "adj-i");
        var window = MakeWindow(candidate,
            nextPOS: [PartOfSpeech.Particle], nextText: "なら");

        var (bonus, rules) = EvaluateSoftRules(window);
        bonus.Should().Be(20);
        rules.Should().Contain("conjunctive-particle-verb-link");
    }

    [Fact]
    public void ConjunctiveParticleVerbLink_FiresForAuxBeforeTo()
    {
        var candidate = MakeCandidate("た", "aux");
        var window = MakeWindow(candidate,
            nextPOS: [PartOfSpeech.Particle], nextText: "と");

        var (bonus, rules) = EvaluateSoftRules(window);
        bonus.Should().Be(20);
        rules.Should().Contain("conjunctive-particle-verb-link");
    }

    [Fact]
    public void ConjunctiveParticleVerbLink_DoesNotFireForNounBeforeTo()
    {
        var candidate = MakeCandidate("本", "n");
        var window = MakeWindow(candidate,
            nextPOS: [PartOfSpeech.Particle], nextText: "と");

        var (_, rules) = EvaluateSoftRules(window);
        rules.Should().NotContain("conjunctive-particle-verb-link");
    }

    [Fact]
    public void ConjunctiveParticleVerbLink_DoesNotFireForNonConditionalParticle()
    {
        var candidate = MakeCandidate("食べる", "v1");
        var window = MakeWindow(candidate,
            nextPOS: [PartOfSpeech.Particle], nextText: "が");

        var (_, rules) = EvaluateSoftRules(window);
        rules.Should().NotContain("conjunctive-particle-verb-link");
    }

    // -----------------------------------------------------------------------
    // Soft rules: na-adj-no-connector-penalty
    // -----------------------------------------------------------------------

    [Fact]
    public void NaAdjNoConnectorPenalty_FiresWhenFollowedByVerb()
    {
        var candidate = MakeCandidate("静か", "adj-na");
        var window = MakeWindow(candidate,
            nextPOS: [PartOfSpeech.Verb], nextText: "する");

        var (bonus, rules) = EvaluateSoftRules(window);
        bonus.Should().Be(-20);
        rules.Should().Contain("na-adj-no-connector-penalty");
    }

    [Fact]
    public void NaAdjNoConnectorPenalty_DoesNotFireWhenFollowedByNa()
    {
        var candidate = MakeCandidate("静か", "adj-na");
        var window = MakeWindow(candidate, nextText: "な");

        var (_, rules) = EvaluateSoftRules(window);
        rules.Should().NotContain("na-adj-no-connector-penalty");
    }

    [Fact]
    public void NaAdjNoConnectorPenalty_DoesNotFireWhenFollowedByNi()
    {
        var candidate = MakeCandidate("静か", "adj-na");
        var window = MakeWindow(candidate,
            nextPOS: [PartOfSpeech.Particle], nextText: "に");

        var (_, rules) = EvaluateSoftRules(window);
        rules.Should().NotContain("na-adj-no-connector-penalty");
    }

    [Fact]
    public void NaAdjNoConnectorPenalty_DoesNotFireWhenFollowedByCopula()
    {
        var candidate = MakeCandidate("元気", "adj-na");
        var window = MakeWindow(candidate, nextText: "だ");

        var (_, rules) = EvaluateSoftRules(window);
        rules.Should().NotContain("na-adj-no-connector-penalty");
    }

    [Fact]
    public void NaAdjNoConnectorPenalty_DoesNotFireWhenFollowedByParticle()
    {
        var candidate = MakeCandidate("元気", "adj-na");
        var window = MakeWindow(candidate,
            nextPOS: [PartOfSpeech.Particle], nextText: "が");

        var (_, rules) = EvaluateSoftRules(window);
        rules.Should().NotContain("na-adj-no-connector-penalty");
    }

    [Fact]
    public void NaAdjNoConnectorPenalty_DoesNotFireAtSentenceEnd()
    {
        var candidate = MakeCandidate("元気", "adj-na");
        var window = MakeWindow(candidate);

        var (_, rules) = EvaluateSoftRules(window);
        rules.Should().NotContain("na-adj-no-connector-penalty");
    }

    [Fact]
    public void NaAdjNoConnectorPenalty_CombinesWithConnectorSynergy()
    {
        // な follows → synergy fires (+30), penalty does NOT fire
        var candidate = MakeCandidate("好き", "adj-na");
        var window = MakeWindow(candidate, nextText: "な");

        var (bonus, rules) = EvaluateSoftRules(window);
        bonus.Should().Be(30);
        rules.Should().Contain("na-adj-connector-synergy");
        rules.Should().NotContain("na-adj-no-connector-penalty");
    }

    [Fact]
    public void NoRulesMatch_ReturnsZeroBonus()
    {
        var candidate = MakeCandidate("食べる", "v1");
        var window = MakeWindow(candidate,
            prevPOS: [PartOfSpeech.Noun], prevText: "本",
            nextPOS: [PartOfSpeech.Noun], nextText: "物");

        var (bonus, rules) = EvaluateSoftRules(window);
        bonus.Should().Be(0);
        rules.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // HasApplicableSoftRules
    // -----------------------------------------------------------------------

    // -----------------------------------------------------------------------
    // Soft rules: adverb-na-adj-synergy
    // -----------------------------------------------------------------------

    [Fact]
    public void AdverbNaAdjSynergy_FiresWhenAdverbFollowedByNaAdj()
    {
        var candidate = MakeCandidate("とても", "adv");
        var window = MakeWindow(candidate,
            nextPOS: [PartOfSpeech.NaAdjective], nextText: "静か");

        var (bonus, rules) = EvaluateSoftRules(window);
        rules.Should().Contain("adverb-na-adj-synergy");
    }

    [Fact]
    public void AdverbNaAdjSynergy_DoesNotFireForAdverbBeforeVerb()
    {
        var candidate = MakeCandidate("すぐ", "adv");
        var window = MakeWindow(candidate,
            nextPOS: [PartOfSpeech.Verb], nextText: "食べる");

        var (_, rules) = EvaluateSoftRules(window);
        rules.Should().NotContain("adverb-na-adj-synergy");
    }

    [Fact]
    public void AdverbNaAdjSynergy_DoesNotFireForNounBeforeNaAdj()
    {
        var candidate = MakeCandidate("本", "n");
        var window = MakeWindow(candidate,
            nextPOS: [PartOfSpeech.NaAdjective], nextText: "静か");

        var (_, rules) = EvaluateSoftRules(window);
        rules.Should().NotContain("adverb-na-adj-synergy");
    }

    // -----------------------------------------------------------------------
    // Soft rules: verb-after-quotation-to-synergy
    // -----------------------------------------------------------------------

    [Fact]
    public void VerbAfterQuotationTo_FiresWhenVerbFollowsToParticle()
    {
        var candidate = MakeCandidate("思う", "v5u");
        var window = MakeWindow(candidate,
            prevPOS: [PartOfSpeech.Particle], prevText: "と");

        var (bonus, rules) = EvaluateSoftRules(window);
        rules.Should().Contain("verb-after-quotation-to-synergy");
    }

    [Fact]
    public void VerbAfterQuotationTo_DoesNotFireForVerbAfterGaParticle()
    {
        var candidate = MakeCandidate("食べる", "v1");
        var window = MakeWindow(candidate,
            prevPOS: [PartOfSpeech.Particle], prevText: "が");

        var (_, rules) = EvaluateSoftRules(window);
        rules.Should().NotContain("verb-after-quotation-to-synergy");
    }

    [Fact]
    public void VerbAfterQuotationTo_DoesNotFireForNounAfterTo()
    {
        var candidate = MakeCandidate("本", "n");
        var window = MakeWindow(candidate,
            prevPOS: [PartOfSpeech.Particle], prevText: "と");

        var (_, rules) = EvaluateSoftRules(window);
        rules.Should().NotContain("verb-after-quotation-to-synergy");
    }

    // -----------------------------------------------------------------------
    // HasApplicableSoftRules
    // -----------------------------------------------------------------------

    [Fact]
    public void HasApplicableSoftRules_ReturnsTrueWhenRuleMatches()
    {
        var candidate = MakeCandidate("本", "n");
        var window = MakeWindow(candidate,
            nextPOS: [PartOfSpeech.Particle], nextText: "が");

        TransitionRuleEngine.HasApplicableSoftRules(window).Should().BeTrue();
    }

    [Fact]
    public void HasApplicableSoftRules_ReturnsFalseWhenNoRuleMatches()
    {
        var candidate = MakeCandidate("食べる", "v1");
        var window = MakeWindow(candidate,
            prevPOS: [PartOfSpeech.Noun], prevText: "本",
            nextPOS: [PartOfSpeech.Noun], nextText: "物");

        TransitionRuleEngine.HasApplicableSoftRules(window).Should().BeFalse();
    }

    // -----------------------------------------------------------------------
    // Confidence margin (CandidateSelectionResult)
    // -----------------------------------------------------------------------

    [Fact]
    public void CandidateSelectionResult_HighConfidence_At40()
    {
        var result = new CandidateSelectionResult(null,40);
        result.IsHighConfidence.Should().BeTrue();
        result.IsMediumConfidence.Should().BeFalse();
        result.IsLowConfidence.Should().BeFalse();
    }

    [Fact]
    public void CandidateSelectionResult_LowConfidence_Below15()
    {
        var result = new CandidateSelectionResult(null,14);
        result.IsLowConfidence.Should().BeTrue();
        result.IsHighConfidence.Should().BeFalse();
    }

    [Fact]
    public void CandidateSelectionResult_MediumConfidence_Between15And39()
    {
        var result = new CandidateSelectionResult(null,25);
        result.IsMediumConfidence.Should().BeTrue();
        result.IsLowConfidence.Should().BeFalse();
        result.IsHighConfidence.Should().BeFalse();
    }

    [Fact]
    public void CandidateSelectionResult_NullMargin_AllFalse()
    {
        var result = new CandidateSelectionResult(null,null);
        result.IsLowConfidence.Should().BeFalse();
        result.IsMediumConfidence.Should().BeFalse();
        result.IsHighConfidence.Should().BeFalse();
    }
}
