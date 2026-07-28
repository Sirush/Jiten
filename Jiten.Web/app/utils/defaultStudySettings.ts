import type { StudySettingsDto } from '~/types';

/**
 * Default values for the card-display toggles that {@link buildLayoutFromLegacySettings} reads. Shared
 * between the store's initial settings and the `Default` layout preset so both agree on what a default
 * card looks like without either hand-writing a block list.
 */
// Values hardcode the server defaults from Jiten.Api/Dtos/StudySettingsDto.cs and must be kept in sync
// with them, so the Default preset and a fresh account render the same card.
export const DEFAULT_CARD_DISPLAY_SETTINGS = {
  showCardStatus: true,
  showFuriganaOnFront: false,
  furiganaOnFrontNewOnly: false,
  exampleSentencePosition: 'Back',
  blurExampleSentence: false,
  showConfusableReadings: true,
  showFrequencyRank: true,
  showPitchAccent: true,
  showKanjiBreakdown: true,
  showWordComposition: true,
  showWordUsedIn: true,
} as const satisfies Partial<StudySettingsDto>;
