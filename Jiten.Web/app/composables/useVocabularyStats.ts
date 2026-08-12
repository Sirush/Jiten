export function useVocabularyStats() {
  const { $api } = useNuxtApp();

  const vocabStatsLoading = ref(true);

  const youngWordsAmount = ref(0);
  const matureWordsAmount = ref(0);
  const masteredWordsAmount = ref(0);
  const blacklistedWordsAmount = ref(0);
  const youngFormsAmount = ref(0);
  const matureFormsAmount = ref(0);
  const masteredFormsAmount = ref(0);
  const blacklistedFormsAmount = ref(0);

  const totalWordsAmount = computed(() => youngWordsAmount.value + matureWordsAmount.value + masteredWordsAmount.value + blacklistedWordsAmount.value);
  const totalFormsAmount = computed(() => youngFormsAmount.value + matureFormsAmount.value + masteredFormsAmount.value + blacklistedFormsAmount.value);

  const wordSetMasteredWords = ref(0);
  const wordSetMasteredForms = ref(0);
  const wordSetBlacklistedWords = ref(0);
  const wordSetBlacklistedForms = ref(0);
  const redundantForms = ref(0);
  const derivationCoveredWords = ref(0);
  const derivationCoveredForms = ref(0);
  const archivedCards = ref(0);
  const hasWordSetContributions = computed(() => wordSetMasteredWords.value > 0 || wordSetBlacklistedWords.value > 0);

  async function fetchKnownWordsAmount() {
    try {
      const result = await $api<{
        young: number;
        mature: number;
        mastered: number;
        blacklisted: number;
        youngForm: number;
        matureForm: number;
        masteredForm: number;
        blacklistedForm: number;
        wordSetMastered: number;
        wordSetMasteredForm: number;
        wordSetBlacklisted: number;
        wordSetBlacklistedForm: number;
        redundantForms: number;
        derivationCovered: number;
        derivationCoveredForm: number;
        archivedCards: number;
      }>('user/vocabulary/known-ids/amount');
      youngWordsAmount.value = result.young;
      matureWordsAmount.value = result.mature;
      masteredWordsAmount.value = result.mastered;
      blacklistedWordsAmount.value = result.blacklisted;
      youngFormsAmount.value = result.youngForm;
      matureFormsAmount.value = result.matureForm;
      masteredFormsAmount.value = result.masteredForm;
      blacklistedFormsAmount.value = result.blacklistedForm;
      wordSetMasteredWords.value = result.wordSetMastered;
      wordSetMasteredForms.value = result.wordSetMasteredForm;
      wordSetBlacklistedWords.value = result.wordSetBlacklisted;
      wordSetBlacklistedForms.value = result.wordSetBlacklistedForm;
      redundantForms.value = result.redundantForms;
      derivationCoveredWords.value = result.derivationCovered ?? 0;
      derivationCoveredForms.value = result.derivationCoveredForm ?? 0;
      archivedCards.value = result.archivedCards ?? 0;
    } catch {} finally {
      vocabStatsLoading.value = false;
    }
  }

  function resetStats() {
    youngWordsAmount.value = 0;
    matureWordsAmount.value = 0;
    masteredWordsAmount.value = 0;
    blacklistedWordsAmount.value = 0;
    youngFormsAmount.value = 0;
    matureFormsAmount.value = 0;
    masteredFormsAmount.value = 0;
    blacklistedFormsAmount.value = 0;
    wordSetMasteredWords.value = 0;
    wordSetMasteredForms.value = 0;
    wordSetBlacklistedWords.value = 0;
    wordSetBlacklistedForms.value = 0;
    redundantForms.value = 0;
    derivationCoveredWords.value = 0;
    derivationCoveredForms.value = 0;
    archivedCards.value = 0;
  }

  return {
    vocabStatsLoading,
    youngWordsAmount,
    matureWordsAmount,
    masteredWordsAmount,
    blacklistedWordsAmount,
    youngFormsAmount,
    matureFormsAmount,
    masteredFormsAmount,
    blacklistedFormsAmount,
    totalWordsAmount,
    totalFormsAmount,
    wordSetMasteredWords,
    wordSetMasteredForms,
    wordSetBlacklistedWords,
    wordSetBlacklistedForms,
    hasWordSetContributions,
    redundantForms,
    derivationCoveredWords,
    derivationCoveredForms,
    archivedCards,
    fetchKnownWordsAmount,
    resetStats,
  };
}
