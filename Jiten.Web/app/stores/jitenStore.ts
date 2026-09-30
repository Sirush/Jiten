import { defineStore } from 'pinia';
import { type DifficultyDisplayStyle, DifficultyValueDisplayStyle, type SentenceFuriganaMode, ThemeMode, TitleLanguage } from '~/types';
import type { KanjiScalePref } from '~/data/kanjiGroupings';
import { DEFAULT_TTS_VOLUME } from '~/utils/ttsVolume';
import type { CoverageScale } from '~/utils/coverageAxis';
import { DEFAULT_WORD_STATE_COLOURS, type WordStateColourKey, type WordStateColours } from '~/utils/wordState';
import {
  DEFAULT_READING_SPEEDS,
  type DifficultyPalette,
  type HeadwordFuriganaMode,
  type JapaneseFont,
  type PitchAccentDisplay,
  type ReducedMotionSetting,
  type TextSizeStep,
  type TtsVoice,
} from '~/utils/displayProfile';
import { AVERAGE_BAND, difficultyBand } from '~/utils/difficultyColours';
import { DEFAULT_MEDIA_CARD_COLUMNS, type MediaCardStatColumns } from '~/utils/mediaCardStats';
import { DEFAULT_MEDIA_CARD_SECTION_LAYOUT, copySectionLayout, type MediaCardSectionLayout } from '~/utils/mediaCardSections';
import { readJarCookie, registerSettingResync, sameSetting, settingChangedInOtherTab } from '~/utils/settingsTabSync';

export type WatchColourKey = WordStateColourKey;

export type WatchTranscriptLayout = 'auto' | 'below' | 'side';

export interface WatchPrefs {
  autoPause: boolean;
  pauseOffsetMs: number;
  blurKnown: boolean;
  pauseOnLookup: boolean;
  /** Neighbouring lines added on each side when mining a sentence */
  sentenceContext: number;
  /** Desktop transcript placement; auto puts it beside the player only on short screens */
  transcriptLayout: WatchTranscriptLayout;
  /** Superseded by the profile's stateColours; read once to carry old choices over. */
  colours?: Partial<WordStateColours>;
}

export const DEFAULT_WATCH_COLOURS = DEFAULT_WORD_STATE_COLOURS;

export const DEFAULT_WATCH_PREFS: WatchPrefs = {
  autoPause: false,
  pauseOffsetMs: -100,
  blurKnown: false,
  pauseOnLookup: true,
  sentenceContext: 0,
  transcriptLayout: 'auto',
};

const YEAR = 60 * 60 * 24 * 365;

export const DEFAULT_DICTIONARY_FONT_SIZE = 16;

export function createCookieState<T>(key: string, defaultValue: T): Ref<T> {
  const name = `jiten-${key}`;
  const cookie = useCookie<T>(name, {
    watch: true,
    maxAge: YEAR,
    path: '/',
  });

  const state = ref<T>(cookie.value ?? defaultValue) as Ref<T>;

  watch(state, (newValue) => {
    cookie.value = newValue;
  });

  if (import.meta.client) {
    // useCookie already relays other tabs' writes into `cookie`; anything differing from `state` came from another tab.
    watch(
      cookie,
      (value) => {
        const next = (value ?? defaultValue) as T;
        if (sameSetting(next, state.value)) return;
        state.value = next;
        settingChangedInOtherTab();
      },
      { deep: true }
    );
    registerSettingResync(() => {
      const jar = readJarCookie(name) as T | undefined;
      if (jar !== undefined && !sameSetting(jar, cookie.value)) cookie.value = jar;
    });
  }

  return state;
}

// For flags that only ever matter client-side, so they don't ride along on every request as a cookie.
function createLocalStorageState<T>(key: string, defaultValue: T): Ref<T> {
  const storageKey = `jiten-${key}`;
  const state = ref<T>(defaultValue) as Ref<T>;

  if (import.meta.client) {
    // Hydration reassigns object values before mount, which must not clobber the stored one
    let loaded = false;
    onMounted(() => {
      try {
        const stored = localStorage.getItem(storageKey);
        if (stored !== null) state.value = JSON.parse(stored) as T;
      } catch {
        // A corrupt entry just means the default stands.
      }
      loaded = true;
    });

    watch(state, (newValue) => {
      if (!loaded) return;
      try {
        localStorage.setItem(storageKey, JSON.stringify(newValue));
      } catch {
        // Private-mode quota failures must not break the setting itself.
      }
    });

    const adopt = (stored: string | null) => {
      if (!loaded || stored === null) return;
      try {
        const next = JSON.parse(stored) as T;
        if (sameSetting(next, state.value)) return;
        state.value = next;
        settingChangedInOtherTab();
      } catch {
      }
    };
    window.addEventListener('storage', (event) => {
      if (event.key === storageKey) adopt(event.newValue);
    });
    registerSettingResync(() => {
      try {
        adopt(localStorage.getItem(storageKey));
      } catch {
        // Storage can be unavailable in private mode.
      }
    });
  }

  return state;
}

export const useJitenStore = defineStore('jiten', () => {
  const titleLanguage = createCookieState<TitleLanguage>('title-language', TitleLanguage.Romaji);
  // Replaced by the two furigana modes; an old "off" carries over to both.
  const legacyFuriganaOff = useCookie<boolean | undefined>('jiten-display-furigana').value === false;
  const headwordFurigana = createCookieState<HeadwordFuriganaMode>('headword-furigana', legacyFuriganaOff ? 'hidden' : 'shown');
  const sentenceFurigana = createCookieState<SentenceFuriganaMode>('sentence-furigana', legacyFuriganaOff ? 'off' : 'all');
  const headwordSize = createCookieState<TextSizeStep>('headword-size', 'md');
  const sentenceSize = createCookieState<TextSizeStep>('sentence-size', 'md');
  const japaneseFont = createCookieState<JapaneseFont>('japanese-font', 'default');
  const japaneseCustomFont = createCookieState<string>('japanese-custom-font', '');
  const japaneseFontWordsOnly = createCookieState<boolean>('japanese-font-words-only', false);
  const japaneseFontDictionaries = createCookieState<boolean>('japanese-font-dictionaries', false);
  const furiganaSize = createCookieState<TextSizeStep>('furigana-size', 'md');
  const furiganaOnHover = createCookieState<boolean>('furigana-on-hover', false);
  const pitchAccentDisplay = createCookieState<PitchAccentDisplay>('pitch-accent-display', 'graph');
  const pitchAccentColours = createCookieState<boolean>('pitch-accent-colours', false);
  const colourWordsByState = createCookieState<boolean>('colour-words-by-state', false);
  // Null until chosen, so the one-time copy of the old watch-page colours can tell "never set" from "set to defaults".
  const stateColours = createCookieState<WordStateColours | null>('state-colours', null);
  const resolvedStateColours = computed<WordStateColours>(() => ({ ...DEFAULT_WORD_STATE_COLOURS, ...(stateColours.value ?? {}) }));
  const reducedMotion = createCookieState<ReducedMotionSetting>('reduced-motion', 'system');
  // Null keeps the built-in media-card layout.
  const mediaCardStatColumns = createCookieState<MediaCardStatColumns | null>('media-card-stat-columns', null);
  const defaultTheme = ThemeMode.Auto;

  const themeMode = createCookieState<ThemeMode>('theme-mode', defaultTheme);
  const displayAdminFunctions = createCookieState<boolean>('display-admin-functions', false);
  const readingSpeed = createCookieState<number>('reading-speed', 14000);
  const readingSpeedByDifficulty = createCookieState<boolean>('reading-speed-by-difficulty', false);
  const readingSpeeds = createCookieState<number[]>('reading-speeds', [...DEFAULT_READING_SPEEDS]);
  /** Characters per hour for a title of this difficulty; with per-difficulty speeds, a title with no difficulty reads as Average. */
  const readingSpeedFor = (difficulty: number | null | undefined): number => {
    if (!readingSpeedByDifficulty.value) return readingSpeed.value;
    const band = difficulty == null || difficulty < 0 ? AVERAGE_BAND : difficultyBand(difficulty);
    return readingSpeeds.value[band] ?? readingSpeed.value;
  };
  const displayAllNsfw = createCookieState<boolean>('display-all-nsfw', false);
  const hideVocabularyDefinitions = createCookieState<boolean>('hide-vocabulary-definitions', false);
  const hideCoverageBorders = createCookieState<boolean>('hide-coverage-borders', false);
  const hideGenres = createCookieState<boolean>('hide-genres', false);
  const hideTags = createCookieState<boolean>('hide-tags', false);
  const hideRelations = createCookieState<boolean>('hide-relations', false);
  const hideDescriptions = createCookieState<boolean>('hide-descriptions', false);
  const hideExternalRating = createCookieState<boolean>('hide-external-rating', false);
  const hideAlternativeTitles = createCookieState<boolean>('hide-alternative-titles', false);
  const mediaCardSectionLayout = createCookieState<MediaCardSectionLayout>('media-card-section-layout', copySectionLayout(DEFAULT_MEDIA_CARD_SECTION_LAYOUT));
  // Either the flag or the stat columns can hide the rating; showing it again must undo both.
  const externalRatingHidden = computed<boolean>({
    get: () => hideExternalRating.value || !(mediaCardStatColumns.value ?? DEFAULT_MEDIA_CARD_COLUMNS).some((c) => c.includes('externalRating')),
    set: (hidden) => {
      hideExternalRating.value = hidden;
      const columns = mediaCardStatColumns.value;
      if (!hidden && columns && !columns.some((c) => c.includes('externalRating'))) {
        mediaCardStatColumns.value = columns.map((c, i) => (i === columns.length - 1 ? [...c, 'externalRating'] : c));
      }
    },
  });
  const quickMasterVocabulary = createCookieState<boolean>('quick-master-vocabulary', false);
  const ttsVoice = createCookieState<TtsVoice>('tts-voice', 'female');
  const difficultyDisplayStyle = createCookieState<DifficultyDisplayStyle>('difficulty-display-style', 0);
  const kanjiScale = createCookieState<KanjiScalePref>('kanji-scale', 'jlpt');
  const kanjiStrokeStepsOpen = createCookieState<boolean>('kanji-stroke-steps-open', false);
  const kanjiStrokeSpeed = createCookieState<number>('kanji-stroke-speed', 1);
  const kanjiStrokeOrderShown = createCookieState<boolean>('kanji-stroke-order-shown', false);
  const kanjiStrokeStepsZoom = createCookieState<number>('kanji-stroke-steps-zoom', 0);
  const similarMediaPinnedType = createCookieState<number>('similar-media-pinned-type', 0);
  const preferredDictionaryId = createCookieState<string>('preferred-dictionary-id', '');
  // Media types left out of the All tab; the browse URL carries the same list once set.

  const difficultyPalette = createCookieState<DifficultyPalette>('difficulty-palette', 'default');
  const difficultyValueDisplayStyle = createCookieState<DifficultyValueDisplayStyle>(
    'difficulty-value-display-style',
    DifficultyValueDisplayStyle.ZeroToFive
  );

  // Migrate users from removed "1 to 6" option (value 0) to "0 to 5" (value 1)
  if ((difficultyValueDisplayStyle.value as number) === 0) {
    difficultyValueDisplayStyle.value = DifficultyValueDisplayStyle.ZeroToFive;
  }

  const getKnownWordIds = (): number[] => {
    if (import.meta.client) {
      try {
        const stored = localStorage.getItem('jiten-known-word-ids');
        return stored ? JSON.parse(stored) : [];
      } catch (error) {
        console.error('Error reading known word IDs from localStorage:', error);
        return [];
      }
    }
    return [];
  };

  const knownWordIds = ref<number[]>([]);
  let isInitialized = false;

  const ensureInitialized = () => {
    if (!isInitialized && import.meta.client) {
      knownWordIds.value = getKnownWordIds();
      isInitialized = true;
    }
  };

  onMounted(() => {
    ensureInitialized();
  });

  // Only consulted while the user lacks Jiten+; getting the tier brings the section back.
  const hideCoverageJourney = createLocalStorageState<boolean>('hide-coverage-journey', false);
  const hideSentenceStats = createLocalStorageState<boolean>('hide-sentence-stats', false);

  // Off means bulk-declared words are folded into the curve, spike and all.
  const separatePriorKnowledge = createLocalStorageState<boolean>('separate-prior-knowledge', true);

  const coverageJourneyScale = createLocalStorageState<CoverageScale>('coverage-journey-scale', 'fit');

  // Drives the unread dot on the home page's "what's new" strip.
  const lastSeenUpdateId = createLocalStorageState<number>('last-seen-update-id', 0);
  const customDictionaryFontSize = createLocalStorageState<number>('custom-dictionary-font-size', DEFAULT_DICTIONARY_FONT_SIZE);

  const ttsVolume = createLocalStorageState<number>('tts-volume', DEFAULT_TTS_VOLUME);
  const watchPrefs = createLocalStorageState<WatchPrefs>('watch-prefs', { ...DEFAULT_WATCH_PREFS });

  const coverageVersion = ref(0);

  function bumpCoverageVersion() {
    coverageVersion.value++;
  }

  // Per-media invalidation
  const deckCoverageVersions = ref<Record<number, number>>({});

  function bumpDeckCoverageVersion(deckId: number) {
    deckCoverageVersions.value[deckId] = (deckCoverageVersions.value[deckId] ?? 0) + 1;
  }

  return {
    getKnownWordIds,

    titleLanguage,
    headwordFurigana,
    sentenceFurigana,
    headwordSize,
    sentenceSize,
    japaneseFont,
    japaneseCustomFont,
    japaneseFontWordsOnly,
    japaneseFontDictionaries,
    furiganaSize,
    furiganaOnHover,
    pitchAccentDisplay,
    pitchAccentColours,
    colourWordsByState,
    stateColours,
    resolvedStateColours,
    reducedMotion,
    mediaCardStatColumns,
    themeMode,
    displayAdminFunctions,
    readingSpeed,
    readingSpeedByDifficulty,
    readingSpeeds,
    readingSpeedFor,
    knownWordIds,
    displayAllNsfw,
    hideVocabularyDefinitions,
    hideCoverageBorders,
    hideGenres,
    hideTags,
    hideRelations,
    hideDescriptions,
    hideExternalRating,
    hideAlternativeTitles,
    mediaCardSectionLayout,
    externalRatingHidden,
    quickMasterVocabulary,
    ttsVoice,
    ttsVolume,
    watchPrefs,
    difficultyDisplayStyle,
    difficultyValueDisplayStyle,
    difficultyPalette,
    kanjiScale,
    kanjiStrokeStepsOpen,
    kanjiStrokeSpeed,
    kanjiStrokeOrderShown,
    kanjiStrokeStepsZoom,
    similarMediaPinnedType,
    preferredDictionaryId,
    hideCoverageJourney,
    hideSentenceStats,
    separatePriorKnowledge,
    coverageJourneyScale,
    lastSeenUpdateId,
    customDictionaryFontSize,
    coverageVersion,
    bumpCoverageVersion,
    deckCoverageVersions,
    bumpDeckCoverageVersion,
  };
});
