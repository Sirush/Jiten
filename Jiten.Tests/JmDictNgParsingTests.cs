using FluentAssertions;
using Jiten.Core.Data.JMDict;
using Xunit;

namespace Jiten.Parser.Tests;

/// <summary>
/// Parses a self-contained JMdict NG fixture (its own DOCTYPE) through the real sync parser to lock the
/// behaviour the cutover depends on: attribute-less English glosses (DTD default), g_type, s_inf, field,
/// dial, misc, stagk, xref, lsource/wasei, entry &lt;info&gt;, and the root created/version attributes.
/// </summary>
public class JmDictNgParsingTests
{
    private const string Fixture = """
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE JMdict [
<!ELEMENT JMdict (entry*)>
<!ATTLIST JMdict created CDATA #IMPLIED>
<!ATTLIST JMdict version CDATA #IMPLIED>
<!ELEMENT entry (ent_seq, k_ele*, r_ele+, lsource*, info*, sense+)>
<!ELEMENT ent_seq (#PCDATA)>
<!ELEMENT k_ele (keb)>
<!ELEMENT keb (#PCDATA)>
<!ELEMENT r_ele (reb)>
<!ELEMENT reb (#PCDATA)>
<!ELEMENT lsource (#PCDATA)>
<!ATTLIST lsource xml:lang CDATA "eng">
<!ATTLIST lsource ls_type CDATA #IMPLIED>
<!ATTLIST lsource ls_wasei CDATA #IMPLIED>
<!ELEMENT info (#PCDATA)>
<!ATTLIST info inf_type CDATA #IMPLIED>
<!ELEMENT sense (stagk*, stagr*, pos*, xref*, field*, misc*, s_inf*, dial*, gloss*)>
<!ELEMENT stagk (#PCDATA)>
<!ELEMENT stagr (#PCDATA)>
<!ELEMENT pos (#PCDATA)>
<!ELEMENT xref (#PCDATA)*>
<!ATTLIST xref type CDATA #REQUIRED>
<!ATTLIST xref seq CDATA #IMPLIED>
<!ATTLIST xref sno CDATA #IMPLIED>
<!ATTLIST xref xk CDATA #IMPLIED>
<!ATTLIST xref xr CDATA #IMPLIED>
<!ATTLIST xref dict CDATA #IMPLIED>
<!ELEMENT field (#PCDATA)>
<!ELEMENT misc (#PCDATA)>
<!ELEMENT dial (#PCDATA)>
<!ELEMENT s_inf (#PCDATA)>
<!ELEMENT gloss (#PCDATA)>
<!ATTLIST gloss xml:lang CDATA "eng">
<!ATTLIST gloss g_type CDATA #IMPLIED>
<!ENTITY n "noun (common) (futsuumeishi)">
<!ENTITY food "food, cooking">
<!ENTITY uk "word usually written using kana alone">
<!ENTITY ksb "Kansai-ben">
]>
<JMdict created="2026-06-22" version="1.10">
<entry>
<ent_seq>1000001</ent_seq>
<k_ele><keb>寿司</keb></k_ele>
<r_ele><reb>すし</reb></r_ele>
<lsource xml:lang="eng" ls_wasei="y">sushi src</lsource>
<info inf_type="note">a test entry note</info>
<sense>
<stagk>寿司</stagk>
<pos>&n;</pos>
<field>&food;</field>
<misc>&uk;</misc>
<dial>&ksb;</dial>
<s_inf>usually written in kana</s_inf>
<xref type="see" seq="1000002" sno="1" xk="刺身" xr="さしみ">刺身</xref>
<gloss>sushi</gloss>
<gloss g_type="lit">vinegared rice</gloss>
</sense>
</entry>
</JMdict>
""";

    private static async Task<SyncEntry> ParseSingleAsync()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, Fixture);
        try
        {
            var result = await JmDictHelper.ParseSyncEntries(path, path);
            result.Created.Should().Be("2026-06-22");
            result.Version.Should().Be("1.10");
            result.Entries.Should().ContainSingle();
            return result.Entries[0];
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task EnglishGloss_WithoutLangAttribute_LandsInEnglishMeanings()
    {
        var entry = await ParseSingleAsync();
        var sense = entry.Senses.Single();
        // Both glosses are English (the first relies on the DTD-default xml:lang="eng").
        sense.EnglishMeanings.Should().Equal("sushi", "vinegared rice");
    }

    [Fact]
    public async Task GlossTypes_AreIndexAlignedWithMeanings()
    {
        var entry = await ParseSingleAsync();
        var sense = entry.Senses.Single();
        sense.GlossTypes.Should().Equal("", "lit");
    }

    [Fact]
    public async Task SenseAnnotations_AreParsed()
    {
        var entry = await ParseSingleAsync();
        var sense = entry.Senses.Single();
        sense.Pos.Should().Equal("n");
        sense.Field.Should().Equal("food");
        sense.Misc.Should().Equal("uk");
        sense.Dial.Should().Equal("ksb");
        sense.SenseInfo.Should().Equal("usually written in kana");
        sense.StagK.Should().Equal("寿司");
    }

    [Fact]
    public async Task Xref_AttributesAreParsed()
    {
        var entry = await ParseSingleAsync();
        var xref = entry.Senses.Single().Xrefs.Single();
        xref.Type.Should().Be("see");
        xref.Seq.Should().Be(1000002);
        xref.Sno.Should().Be(1);
        xref.Xk.Should().Be("刺身");
        xref.Xr.Should().Be("さしみ");
        xref.RawText.Should().Be("刺身");
    }

    [Fact]
    public async Task LanguageSource_AndEntryInfo_AreParsed()
    {
        var entry = await ParseSingleAsync();
        entry.LanguageSources.Should().ContainSingle();
        entry.LanguageSources[0].IsWasei.Should().BeTrue();
        entry.LanguageSources[0].Text.Should().Be("sushi src");
        entry.EntryInfos.Should().ContainSingle();
        entry.EntryInfos[0].Text.Should().Be("a test entry note");
    }
}
