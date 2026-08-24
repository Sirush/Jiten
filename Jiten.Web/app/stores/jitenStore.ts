import { defineStore } from 'pinia';
import { type DifficultyDisplayStyle, DifficultyValueDisplayStyle, ThemeMode, TitleLanguage } from '~/types';
import type { KanjiScalePref } from '~/data/kanjiGroupings';

const YEAR = 60 * 60 * 24 * 365;

function createCookieState<T>(key: string, defaultValue: T): Ref<T> {
  const cookie = useCookie<T>(`jiten-${key}`, {
    watch: true,
    maxAge: YEAR,
    path: '/',
  });

  const state = ref<T>(cookie.value ?? defaultValue) as Ref<T>;

  watch(state, (newValue) => {
    cookie.value = newValue;
  });

  return state;
}

// For flags that only ever matter client-side, so they don't ride along on every request as a cookie.
function createLocalStorageState<T>(key: string, defaultValue: T): Ref<T> {
  const storageKey = `jiten-${key}`;
  const state = ref<T>(defaultValue) as Ref<T>;

  if (import.meta.client) {
    onMounted(() => {
      try {
        const stored = localStorage.getItem(storageKey);
        if (stored !== null) state.value = JSON.parse(stored) as T;
      } catch {
        // A corrupt entry just means the default stands.
      }
    });

    watch(state, (newValue) => {
      try {
        localStorage.setItem(storageKey, JSON.stringify(newValue));
      } catch {
        // Private-mode quota failures must not break the setting itself.
      }
    });
  }

  return state;
}

export const useJitenStore = defineStore('jiten', () => {
  const titleLanguage = createCookieState<TitleLanguage>('title-language', TitleLanguage.Romaji);
  const displayFurigana = createCookieState<boolean>('display-furigana', true);
  const defaultTheme = ThemeMode.Auto;

  const themeMode = createCookieState<ThemeMode>('theme-mode', defaultTheme);
  const displayAdminFunctions = createCookieState<boolean>('display-admin-functions', false);
  const readingSpeed = createCookieState<number>('reading-speed', 14000);
  const displayAllNsfw = createCookieState<boolean>('display-all-nsfw', false);
  const hideVocabularyDefinitions = createCookieState<boolean>('hide-vocabulary-definitions', false);
  const hideCoverageBorders = createCookieState<boolean>('hide-coverage-borders', false);
  const hideGenres = createCookieState<boolean>('hide-genres', false);
  const hideTags = createCookieState<boolean>('hide-tags', false);
  const hideRelations = createCookieState<boolean>('hide-relations', false);
  const hideDescriptions = createCookieState<boolean>('hide-descriptions', false);
  const hideExternalRating = createCookieState<boolean>('hide-external-rating', false);
  const hideAlternativeTitles = createCookieState<boolean>('hide-alternative-titles', false);
  const quickMasterVocabulary = createCookieState<boolean>('quick-master-vocabulary', false);
  const ttsVoice = createCookieState<'female' | 'female2' | 'male' | 'male2' | 'asmr' | 'system' | 'random'>('tts-voice', 'female');
  const difficultyDisplayStyle = createCookieState<DifficultyDisplayStyle>('difficulty-display-style', 0);
  const kanjiScale = createCookieState<KanjiScalePref>('kanji-scale', 'jlpt');
  const similarMediaPinnedType = createCookieState<number>('similar-media-pinned-type', 0);
  const preferredDictionaryId = createCookieState<string>('preferred-dictionary-id', '');

  const difficultyValueDisplayStyleCookie = useCookie<DifficultyValueDisplayStyle>('jiten-difficulty-value-display-style', {
    watch: true,
    maxAge: YEAR,
    path: '/',
  });

  // Migrate users from removed "1 to 6" option (value 0) to "0 to 5" (value 1)
  if (difficultyValueDisplayStyleCookie.value === 0) {
    difficultyValueDisplayStyleCookie.value = DifficultyValueDisplayStyle.ZeroToFive;
  }

  const difficultyValueDisplayStyle = ref<DifficultyValueDisplayStyle>(
    difficultyValueDisplayStyleCookie.value ?? DifficultyValueDisplayStyle.ZeroToFive
  );

  watch(difficultyValueDisplayStyle, (newValue) => {
    difficultyValueDisplayStyleCookie.value = newValue;
  });

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

  // Off means bulk-declared words are folded into the curve, spike and all.
  const separatePriorKnowledge = createLocalStorageState<boolean>('separate-prior-knowledge', true);

  // Drives the unread dot on the home page's "what's new" strip.
  const lastSeenUpdateId = createLocalStorageState<number>('last-seen-update-id', 0);
  const customDictionaryFontSize = createLocalStorageState<number>('custom-dictionary-font-size', 16);

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
    displayFurigana,
    themeMode,
    displayAdminFunctions,
    readingSpeed,
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
    quickMasterVocabulary,
    ttsVoice,
    difficultyDisplayStyle,
    difficultyValueDisplayStyle,
    kanjiScale,
    similarMediaPinnedType,
    preferredDictionaryId,
    hideCoverageJourney,
    separatePriorKnowledge,
    lastSeenUpdateId,
    customDictionaryFontSize,
    coverageVersion,
    bumpCoverageVersion,
    deckCoverageVersions,
    bumpDeckCoverageVersion,
  };
});
