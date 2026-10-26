using FluentAssertions;
using Jiten.Api.Helpers;
using Xunit;

namespace Jiten.Tests;

public class UserTextSanitizerTests
{
    [Theory]
    [InlineData("よつばと！")]
    [InlineData("Tiếng Việt")]
    [InlineData("Slice-of-life you can read at N4")]
    [InlineData("❤️ 👨‍👩‍👧 👍🏽")]
    public void Clean_KeepsOrdinaryText(string text) =>
        UserTextSanitizer.Clean(text, multiline: false).Should().Be(text);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("​​")]
    [InlineData("ㅤ ⠀")]
    [InlineData("‮⁦﻿")]
    [InlineData("́́")]
    public void Clean_ReturnsNullWhenNothingVisibleIsLeft(string? text) =>
        UserTextSanitizer.Clean(text, multiline: true).Should().BeNull();

    [Theory]
    [InlineData("  Hello   world  ", "Hello world")]
    [InlineData("tab\there\u0007", "tab here")]
    [InlineData("good‮evil", "goodevil")]
    [InlineData("zero​width", "zerowidth")]
    [InlineData("́́start", "start")]
    [InlineData("line1\nline2", "line1 line2")]
    public void Clean_SingleLine(string text, string expected) =>
        UserTextSanitizer.Clean(text, multiline: false).Should().Be(expected);

    [Theory]
    [InlineData("line1\r\nline2", "line1\nline2")]
    [InlineData("\n\nline1  \r\n\r\n\r\n\r\n  line2\n\n", "line1\n\nline2")]
    public void Clean_Multiline_KeepsBreaksButCapsBlankLines(string text, string expected) =>
        UserTextSanitizer.Clean(text, multiline: true).Should().Be(expected);

    [Fact]
    public void Clean_CapsStackedCombiningMarks()
    {
        var zalgo = "e" + new string('́', 10) + "x";
        UserTextSanitizer.Clean(zalgo, multiline: false).Should().Be("é́́́x");
    }
}
