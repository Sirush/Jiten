using System.Text;
using FluentAssertions;
using Jiten.Core.Data.JMDict;

namespace Jiten.Tests;

public class KanjiVgImporterTests
{
    private const string Header = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE svg PUBLIC "-//W3C//DTD SVG 1.0//EN" "http://www.w3.org/TR/2001/REC-SVG-20010904/DTD/svg10.dtd" [
        <!ATTLIST g
        xmlns:kvg CDATA #FIXED "http://kanjivg.tagaini.net"
        kvg:element CDATA #IMPLIED
        kvg:variant CDATA #IMPLIED
        kvg:partial CDATA #IMPLIED
        kvg:original CDATA #IMPLIED
        kvg:part CDATA #IMPLIED
        kvg:number CDATA #IMPLIED
        kvg:tradForm CDATA #IMPLIED
        kvg:radicalForm CDATA #IMPLIED
        kvg:position CDATA #IMPLIED
        kvg:radical CDATA #IMPLIED
        kvg:phon CDATA #IMPLIED >
        <!ATTLIST path
        xmlns:kvg CDATA #FIXED "http://kanjivg.tagaini.net"
        kvg:type CDATA #IMPLIED >
        ]>
        """;

    private static KanjiVgImporter.ParsedKanji Parse(string character, string body) =>
        KanjiVgImporter.Parse(character, new MemoryStream(Encoding.UTF8.GetBytes(Header + "\n" + body)));

    [Fact]
    public void Parse_Time_ReadsStrokesNumbersAndNestedComponents()
    {
        var parsed = Parse("時", """
            <svg xmlns="http://www.w3.org/2000/svg" width="109" height="109" viewBox="0 0 109 109">
            <g id="kvg:StrokePaths_06642" style="fill:none;">
            <g id="kvg:06642" kvg:element="時">
                <g id="kvg:06642-g1" kvg:element="日" kvg:position="left" kvg:radical="general">
                    <path id="kvg:06642-s1" kvg:type="㇑" d="M16,29.84c0.75,0.66"/>
                    <path id="kvg:06642-s2" kvg:type="㇕a" d="M17.78,30.74c4.65-0.63"/>
                </g>
                <g id="kvg:06642-g2" kvg:element="寺" kvg:position="right" kvg:phon="寺">
                    <g id="kvg:06642-g3" kvg:element="土">
                        <path id="kvg:06642-s3" kvg:type="㇐" d="M51.44,29.03c1.37,0.44"/>
                    </g>
                    <g id="kvg:06642-g4" kvg:element="寸">
                        <path id="kvg:06642-s4" kvg:type="㇐" d="M46,60.99c1.43,0.46"/>
                    </g>
                </g>
            </g>
            </g>
            <g id="kvg:StrokeNumbers_06642" style="font-size:8;fill:#808080">
                <text transform="matrix(1 0 0 1 9.75 39.13)">1</text>
                <text transform="matrix(1 0 0 1 18.50 27.50)">2</text>
                <text transform="matrix(1 0 0 1 45.50 27.13)">3</text>
                <text transform="matrix(1 0 0 1 44.25 57.13)">4</text>
            </g>
            </svg>
            """);

        parsed.Strokes.Character.Should().Be("時");
        parsed.Strokes.Paths.Should().Equal("M16,29.84c0.75,0.66", "M17.78,30.74c4.65-0.63", "M51.44,29.03c1.37,0.44", "M46,60.99c1.43,0.46");
        parsed.Strokes.NumberPositions.Should().Equal(9.75f, 39.13f, 18.5f, 27.5f, 45.5f, 27.13f, 44.25f, 57.13f);

        parsed.Components.Select(c => (c.NodeIndex, c.ParentIndex, c.Component, c.IsRadical, c.IsPhonetic))
              .Should().Equal(((short)0, (short?)null, "日", true, false),
                              ((short)1, (short?)null, "寺", false, true),
                              ((short)2, (short?)1, "土", false, false),
                              ((short)3, (short?)1, "寸", false, false));
        parsed.Components.Should().OnlyContain(c => c.KanjiCharacter == "時");
    }

    [Fact]
    public void Parse_SplitComponentAndPhoneticOnlyGroup_MergesPartsAndKeepsSoundComponent()
    {
        var parsed = Parse("衷", """
            <svg xmlns="http://www.w3.org/2000/svg" width="109" height="109" viewBox="0 0 109 109">
            <g id="kvg:StrokePaths_08877" style="fill:none;">
            <g id="kvg:08877" kvg:element="衷">
                <g id="kvg:08877-g1" kvg:position="top">
                    <g id="kvg:08877-g2" kvg:element="衣" kvg:part="1" kvg:partial="true" kvg:radical="tradit">
                        <g id="kvg:08877-g3" kvg:element="亠" kvg:partial="true">
                            <path id="kvg:08877-s1" d="M1,1"/>
                        </g>
                    </g>
                    <g id="kvg:08877-g4" kvg:phon="中">
                        <g id="kvg:08877-g5" kvg:element="口">
                            <path id="kvg:08877-s2" d="M2,2"/>
                        </g>
                    </g>
                </g>
                <g id="kvg:08877-g7" kvg:element="衣" kvg:part="2" kvg:partial="true" kvg:position="bottom" kvg:radical="tradit">
                    <path id="kvg:08877-s3" d="M3,3"/>
                </g>
            </g>
            </g>
            </svg>
            """);

        parsed.Components.Select(c => (c.Component, c.ParentIndex))
              .Should().Equal(("衣", (short?)null), ("亠", (short?)0), ("中", (short?)null), ("口", (short?)2));
        parsed.Components[0].IsRadical.Should().BeTrue();
        parsed.Components[2].IsPhonetic.Should().BeTrue();
        parsed.Strokes.Paths.Should().HaveCount(3);
        parsed.Strokes.NumberPositions.Should().BeEmpty();
    }

    [Fact]
    public void Parse_VariantComponent_KeepsOriginalForm()
    {
        var parsed = Parse("休", """
            <svg xmlns="http://www.w3.org/2000/svg" width="109" height="109" viewBox="0 0 109 109">
            <g id="kvg:StrokePaths_04f11" style="fill:none;">
            <g id="kvg:04f11" kvg:element="休">
                <g id="kvg:04f11-g1" kvg:element="亻" kvg:variant="true" kvg:original="人" kvg:position="left" kvg:radical="general">
                    <path id="kvg:04f11-s1" d="M1,1"/>
                </g>
                <g id="kvg:04f11-g2" kvg:element="木" kvg:position="right">
                    <path id="kvg:04f11-s2" d="M2,2"/>
                </g>
            </g>
            </g>
            </svg>
            """);

        parsed.Components.Select(c => (c.Component, c.Original)).Should().Equal(("亻", "人"), ("木", (string?)null));
    }

    [Theory]
    [InlineData("06642.svg", "時")]
    [InlineData("kanji/053ec.svg", "召")]
    [InlineData("20b9f.svg", "𠮟")]
    [InlineData("05b57-Kaisho.svg", null)]
    [InlineData("03042.svg", null)]
    [InlineData("00041.svg", null)]
    public void CharacterFromFileName_MapsIdeographsOnly(string fileName, string? expected)
    {
        KanjiVgImporter.CharacterFromFileName(fileName).Should().Be(expected);
    }
}
