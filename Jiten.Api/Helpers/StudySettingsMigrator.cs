using Jiten.Api.Dtos;

namespace Jiten.Api.Helpers;

/// <summary>Brings a stored settings blob up to the current defaults for settings whose default has changed.</summary>
public static class StudySettingsMigrator
{
    public const int CurrentAudioDefaultsVersion = 1;

    /// <summary>
    /// Idempotent, and applied to incoming requests as well as stored blobs: a client running cached JS PUTs
    /// version 0 with its pre-migration values, which must not undo the rollout.
    /// </summary>
    public static StudySettingsDto Apply(StudySettingsDto settings)
    {
        if (settings.AudioDefaultsVersion < CurrentAudioDefaultsVersion)
        {
            settings.AutoPlayCustomAudio = true;
            settings.AutoPlayCustomAudioPosition = CardAudioAutoPlayPosition.Back;
            settings.CustomAudioReplacesHeadword = true;
            settings.CustomAudioReplacesSentence = true;
            settings.AudioDefaultsVersion = CurrentAudioDefaultsVersion;
        }

        return settings;
    }
}
