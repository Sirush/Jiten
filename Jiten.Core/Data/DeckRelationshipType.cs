namespace Jiten.Core.Data;

public enum DeckRelationshipType
{
    Sequel = 1,
    Fandisc = 2,
    Spinoff = 3,
    SideStory = 4,
    Adaptation = 5,
    Alternative = 6,
    SameSeries = 7,
    SameSetting = 8,

    // Inverse relationships
    Prequel = 101,
    HasFandisc = 102,
    HasSpinoff = 103,
    HasSideStory = 104,
    SourceMaterial = 105
}
