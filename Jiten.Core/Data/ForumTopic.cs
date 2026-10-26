namespace Jiten.Core.Data;

public enum ForumSubject
{
    Deck = 1,
    List = 2
}

/// <summary>Forum discussion topic of a parent deck or public list; the reply count is a cached copy of the forum's, kept by its webhook and by discussion reads.</summary>
public class ForumTopic
{
    public ForumSubject Subject { get; set; }
    public int SubjectId { get; set; }
    public int TopicId { get; set; }
    public int ReplyCount { get; set; }
    public DateTime UpdatedAt { get; set; }
}
