using FluentAssertions;
using Jiten.Core.Data.JMDict;
using Xunit;

namespace Jiten.Tests;

public class CompositionAlignmentTests
{
    private const int Pride = 1267740;
    private const int Dust = 1565750;
    private const int Avoid = 1;
    private const int Thing = 2;
    private const int Word = 3;
    private const int Ga = 4;

    private static readonly Dictionary<int, string[]> KanaSpellings = new()
    {
        [Dust] = ["ほこり", "ホコリ"],
        [Pride] = ["ほこり"],
        [Word] = ["こと"],
    };

    private static bool SpelledByKana(CompositionSlot current, CompositionSlot donor) =>
        KanaSpellings.TryGetValue(donor.WordId, out var spellings) && spellings.Contains(current.Surface);

    private static CompositionSlot Slot(int wordId, string surface, bool grammatical = false) => new(wordId, 0, surface, grammatical);

    [Fact]
    public void KanaSlot_TakesTheWordItsKanjiSiblingPins()
    {
        IReadOnlyList<CompositionSlot> kana = [Slot(Pride, "ほこり"), Slot(Avoid, "除け")];
        IReadOnlyList<CompositionSlot> kanji = [Slot(Dust, "埃"), Slot(Avoid, "除け")];

        CompositionAlignment.FindKanjiDonor(kana, 0, [kanji], SpelledByKana).Should().Be(Dust);
    }

    [Fact]
    public void KanjiSiblingWithTheCurrentWord_KeepsIt()
    {
        IReadOnlyList<CompositionSlot> kana = [Slot(Pride, "ほこり"), Slot(Avoid, "除け")];
        IReadOnlyList<CompositionSlot> dust = [Slot(Dust, "埃"), Slot(Avoid, "除け")];
        IReadOnlyList<CompositionSlot> pride = [Slot(Pride, "誇り"), Slot(Avoid, "除け")];

        CompositionAlignment.FindKanjiDonor(kana, 0, [dust, pride], SpelledByKana).Should().BeNull();
    }

    [Fact]
    public void KanaOnlySiblings_OfferNoDonor()
    {
        IReadOnlyList<CompositionSlot> hiragana = [Slot(Pride, "ほこり"), Slot(Avoid, "よけ")];
        IReadOnlyList<CompositionSlot> katakana = [Slot(Dust, "ホコリ"), Slot(Avoid, "よけ")];

        CompositionAlignment.FindKanjiDonor(hiragana, 0, [katakana], SpelledByKana).Should().BeNull();
    }

    [Fact]
    public void DonorPastAnAlignedSlot_IsRejected()
    {
        IReadOnlyList<CompositionSlot> kana = [Slot(Thing, "こと"), Slot(Ga, "は", grammatical: true), Slot(Avoid, "なし")];
        IReadOnlyList<CompositionSlot> kanji = [Slot(Ga, "は", grammatical: true), Slot(Word, "言")];

        CompositionAlignment.FindKanjiDonor(kana, 0, [kanji], SpelledByKana).Should().BeNull();
    }

    [Fact]
    public void SiblingsPinningDifferentWords_LeaveTheSlotAlone()
    {
        IReadOnlyList<CompositionSlot> kana = [Slot(Thing, "ほこり")];
        IReadOnlyList<CompositionSlot> dust = [Slot(Dust, "埃")];
        IReadOnlyList<CompositionSlot> pride = [Slot(Pride, "誇り")];

        CompositionAlignment.FindKanjiDonor(kana, 0, [dust, pride], SpelledByKana).Should().BeNull();
    }

    [Fact]
    public void KanjiAndGrammaticalSlots_AreNeverReplaced()
    {
        IReadOnlyList<CompositionSlot> form = [Slot(Pride, "誇り"), Slot(Ga, "が", grammatical: true)];
        IReadOnlyList<CompositionSlot> sibling = [Slot(Dust, "埃"), Slot(Word, "言")];

        CompositionAlignment.FindKanjiDonor(form, 0, [sibling], (_, _) => true).Should().BeNull();
        CompositionAlignment.FindKanjiDonor(form, 1, [sibling], (_, _) => true).Should().BeNull();
    }
}
