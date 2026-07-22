using FluentAssertions;
using Jiten.Api.Services;
using Jiten.Core.Data.User;

namespace Jiten.Parser.Tests;

public class CardMediaResolverTests
{
    private static UserCardMedia Media(int wordId, byte ri, CardMediaKind kind, DateTime? created = null) => new()
    {
        Id = ri + (int)kind * 100,
        UserId = "u",
        WordId = wordId,
        ReadingIndex = ri,
        Kind = kind,
        StoragePath = $"card-media/u/{wordId}_{ri}_{kind.ToString().ToLowerInvariant()}",
        ContentType = kind == CardMediaKind.Image ? "image/png" : "audio/mpeg",
        FileSizeBytes = 100,
        CreatedAt = created ?? DateTime.UtcNow
    };

    [Fact]
    public void ExactImage_Wins_NotInherited()
    {
        var media = new[] { Media(1, 0, CardMediaKind.Image) };
        var (image, audio) = CardMediaResolver.Resolve(0, media, kanaFormCount: 1);

        image.Should().NotBeNull();
        image!.Inherited.Should().BeFalse();
        image.SourceReadingIndex.Should().Be(0);
        audio.Should().BeNull();
    }

    [Fact]
    public void MissingImage_InheritsMostRecentSibling()
    {
        var media = new[]
        {
            Media(1, 0, CardMediaKind.Image, new DateTime(2026, 1, 1)),
            Media(1, 2, CardMediaKind.Image, new DateTime(2026, 6, 1))
        };
        var (image, _) = CardMediaResolver.Resolve(5, media, kanaFormCount: 3);

        image.Should().NotBeNull();
        image!.Inherited.Should().BeTrue();
        image.SourceReadingIndex.Should().Be(2); // most recent
    }

    [Fact]
    public void Audio_InheritsOnlyWhenSingleKanaReading()
    {
        var media = new[] { Media(1, 0, CardMediaKind.Audio) };

        var single = CardMediaResolver.Resolve(1, media, kanaFormCount: 1);
        single.Audio.Should().NotBeNull();
        single.Audio!.Inherited.Should().BeTrue();
        single.Audio.SourceReadingIndex.Should().Be(0);

        var multi = CardMediaResolver.Resolve(1, media, kanaFormCount: 3);
        multi.Audio.Should().BeNull();
    }

    [Fact]
    public void ExactAudio_Wins_EvenWithMultipleKanaReadings()
    {
        var media = new[] { Media(1, 1, CardMediaKind.Audio) };
        var (_, audio) = CardMediaResolver.Resolve(1, media, kanaFormCount: 3);

        audio.Should().NotBeNull();
        audio!.Inherited.Should().BeFalse();
    }
}
