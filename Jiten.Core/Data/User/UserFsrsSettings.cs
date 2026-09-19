using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace Jiten.Core.Data.User;

public class UserFsrsSettings
{
    public string UserId { get; set; } = string.Empty;

    public string ParametersJson { get; set; } = "[]";

    public double? DesiredRetention { get; set; }

    public string SettingsJson { get; set; } = "{}";

    /// <summary>Study deck that supplies the next new card under round-robin gathering; null or a vanished deck restarts at the top.</summary>
    public int? NewCardCursorStudyDeckId { get; set; }

    [NotMapped]
    private double[]? _cachedParameters;

    [NotMapped]
    public double[] Parameters
    {
        get => GetParametersOnce();
        set
        {
            ParametersJson = JsonSerializer.Serialize(value);
            _cachedParameters = value;
        }
    }

    public double[] GetParametersOnce()
    {
        if (_cachedParameters != null)
        {
            return _cachedParameters;
        }

        try
        {
            _cachedParameters = JsonSerializer.Deserialize<double[]>(ParametersJson) ?? Array.Empty<double>();
        }
        catch (JsonException)
        {
            _cachedParameters = Array.Empty<double>();
        }

        return _cachedParameters;
    }
}
