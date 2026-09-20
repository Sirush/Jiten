namespace Jiten.Parser.Tests.Integration.Infrastructure;

/// <summary>Picks a real whole-hour zone so a test can fix the user's local time of day regardless of when it runs.</summary>
public static class TestZones
{
    public static string WithLocalHour(DateTime utcNow, int localHour)
    {
        var offsetHours = (localHour - utcNow.Hour + 24) % 24;
        if (offsetHours > 12) offsetHours -= 24;

        return offsetHours == 0 ? "Etc/UTC" : $"Etc/GMT{(offsetHours > 0 ? "-" : "+")}{Math.Abs(offsetHours)}";
    }
}
