import { defineStore } from 'pinia';
import { type DifficultyDisplayStyle, DifficultyValueDisplayStyle, ThemeMode, TitleLanguage } from '~/types';
import type { KanjiScalePref } from '~/data/kanjiGroupings';

const YEAR = 60 * 60 * 24 * 365;

function createCookieState<T>(key: string, defaultValue: T): Ref<T> {
  const cookie = useCookie<T>(`jiten-${key}`, {
    default: () => defaultValue,
    watch: true,
    maxAge: YEAR,
    path: '/',
  });

  const state = ref<T>(cookie.value) as Ref<T>;

  watch(state, (newValue) => {
    cookie.value = newValue;
  });

  return state;
}

export const useJitenStore = defineStore('jiten', () => {
  const titleLanguage = createCookieState<TitleLanguage>('title-language', TitleLanguage.Romaji);
  const displayFurigana = createCookieState<boolean>('display-furigana', true);
  let defaultTheme = ThemeMode.Auto;

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
  const ttsVoice = createCookieState<'female' | 'female2' | 'male' | 'male2' | 'asmr' | 'system'>('tts-voice', 'female');
  const difficultyDisplayStyle = createCookieState<DifficultyDisplayStyle>('difficulty-display-style', 0);
  const kanjiScale = createCookieState<KanjiScalePref>('kanji-scale', 'jlpt');
  const similarMediaPinnedType = createCookieState<number>('similar-media-pinned-type', 0);

  const difficultyValueDisplayStyleCookie = useCookie<DifficultyValueDisplayStyle>('jiten-difficulty-value-display-style', {
    default: () => DifficultyValueDisplayStyle.ZeroToFive,
    watch: true,
    maxAge: YEAR,
    path: '/',
  });

  // Migrate users from removed "1 to 6" option (value 0) to "0 to 5" (value 1)
  if (difficultyValueDisplayStyleCookie.value === 0) {
    difficultyValueDisplayStyleCookie.value = DifficultyValueDisplayStyle.ZeroToFive;
  }

  const difficultyValueDisplayStyle = ref<DifficultyValueDisplayStyle>(difficultyValueDisplayStyleCookie.value);

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

  const coverageVersion = ref(0);

  function bumpCoverageVersion() {
    coverageVersion.value++;
  }

  return {
    getKnownWordIds,

    // state
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
    coverageVersion,
    bumpCoverageVersion,
  };
});
