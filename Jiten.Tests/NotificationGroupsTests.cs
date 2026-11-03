using FluentAssertions;
using Jiten.Core.Data;
using Jiten.Core.Services;
using Xunit;

namespace Jiten.Tests;

public class NotificationGroupsTests
{
    public static TheoryData<NotificationType> AllTypes() => new(Enum.GetValues<NotificationType>());

    [Theory]
    [MemberData(nameof(AllTypes))]
    public void Every_type_has_a_group(NotificationType type)
    {
        var group = NotificationGroups.Of(type);

        NotificationGroups.FromJitenTypes.Contains(type).Should().Be(group == NotificationGroup.FromJiten);
    }

    [Theory]
    [InlineData("[1,2]", new[] { NotificationGroup.MediaRequests, NotificationGroup.MetadataEdits })]
    [InlineData("[0,2,2,99]", new[] { NotificationGroup.MetadataEdits })]
    [InlineData("not json", new NotificationGroup[0])]
    [InlineData(null, new NotificationGroup[0])]
    public void Muted_groups_parse_only_mutable_values(string? json, NotificationGroup[] expected)
    {
        NotificationService.ParseMutedGroups(json).Should().Equal(expected);
    }
}
