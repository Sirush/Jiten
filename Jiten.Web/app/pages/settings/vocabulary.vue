<script setup lang="ts">
  import type { VocabularyOption } from '~/components/VocabularyOptionGrid.vue';

  definePageMeta({ middleware: ['auth'] });
  useHead({ title: 'Vocabulary - Settings - Jiten' });

  const route = useRoute();
  const router = useRouter();

  const {
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
    archivedCards,
    fetchKnownWordsAmount,
  } = useVocabularyStats();

  onMounted(async () => {
    await fetchKnownWordsAmount();
  });

  type Mode = 'import' | 'export';

  const mode = computed<Mode>({
    get: () => ((route.query.mode as string) === 'export' ? 'export' : 'import'),
    set: (v: Mode) => router.replace({ query: { mode: v } }),
  });

  const option = computed<string | null>({
    get: () => {
      const raw = route.query.option as string | undefined;
      if (!raw) return null;
      const allowed = mode.value === 'export' ? exportOptions : importOptions;
      return allowed.some((o) => o.key === raw) ? raw : null;
    },
    set: (v: string | null) => router.replace({ query: { ...route.query, option: v } }),
  });

  const importOptions: VocabularyOption[] = [
    { key: 'anki-connect', label: 'AnkiConnect', desc: 'Import directly from Anki', icon: 'pi pi-sync' },
    { key: 'anki-media', label: 'Anki Sentences & Media', desc: 'Import example sentences, images and audio', icon: 'pi pi-images' },
    { key: 'jpdb', label: 'JPDB', desc: 'Import from JPDB API', icon: 'pi pi-cloud-download' },
    { key: 'anki-file', label: 'Anki File', desc: 'Upload .txt or .csv', icon: 'pi pi-file' },
    { key: 'frequency', label: 'Frequency Range', desc: 'Add words by frequency rank', icon: 'pi pi-chart-bar' },
    { key: 'complete-vocabulary', label: 'Complete Backup', desc: 'Import full vocabulary backup', icon: 'pi pi-database' },
  ];

  const exportOptions: VocabularyOption[] = [
    { key: 'word-list', label: 'Word List', desc: 'Export as text file by state', icon: 'pi pi-file-export' },
    { key: 'complete-vocabulary', label: 'Complete Backup', desc: 'Export full vocabulary backup', icon: 'pi pi-database' },
  ];

  const modeOptions = [
    { label: 'Import', value: 'import' },
    { label: 'Export', value: 'export' },
  ];

  function onPanelChanged() {
    fetchKnownWordsAmount();
  }
</script>

<template>
  <div class="container mx-auto p-2 md:p-4 flex flex-col gap-4">
    <div class="flex items-center gap-2">
      <NuxtLink to="/settings">
        <Button icon="pi pi-arrow-left" severity="secondary" text rounded />
      </NuxtLink>
      <h1 class="text-2xl font-bold">Vocabulary</h1>
    </div>

    <SettingsVocabularyStatsCard
      :vocab-stats-loading="vocabStatsLoading"
      :young-words-amount="youngWordsAmount"
      :mature-words-amount="matureWordsAmount"
      :mastered-words-amount="masteredWordsAmount"
      :blacklisted-words-amount="blacklistedWordsAmount"
      :young-forms-amount="youngFormsAmount"
      :mature-forms-amount="matureFormsAmount"
      :mastered-forms-amount="masteredFormsAmount"
      :blacklisted-forms-amount="blacklistedFormsAmount"
      :total-words-amount="totalWordsAmount"
      :total-forms-amount="totalFormsAmount"
      :word-set-mastered-words="wordSetMasteredWords"
      :word-set-mastered-forms="wordSetMasteredForms"
      :word-set-blacklisted-words="wordSetBlacklistedWords"
      :word-set-blacklisted-forms="wordSetBlacklistedForms"
      :has-word-set-contributions="hasWordSetContributions"
    />

    <section>
      <div class="text-xs font-bold text-gray-500 dark:text-gray-400 uppercase tracking-widest mb-3">Mode</div>
      <SelectButton :model-value="mode" @update:model-value="mode = $event" :options="modeOptions" option-value="value" option-label="label" />
    </section>

    <section>
      <div class="text-xs font-bold text-gray-500 dark:text-gray-400 uppercase tracking-widest mb-3">
        {{ mode === 'export' ? 'Export Method' : 'Import Method' }}
      </div>
      <VocabularyOptionGrid :options="mode === 'export' ? exportOptions : importOptions" v-model="option" />
    </section>

    <div v-if="option" id="vocabulary-transfer-panel">
      <VocabularyImportAnkiConnectPanel v-if="mode === 'import' && option === 'anki-connect'" @changed="onPanelChanged" />
      <VocabularyImportAnkiExtrasPanel v-if="mode === 'import' && option === 'anki-media'" @changed="onPanelChanged" />
      <VocabularyImportJpdbPanel v-if="mode === 'import' && option === 'jpdb'" @changed="onPanelChanged" />
      <VocabularyImportAnkiFilePanel v-if="mode === 'import' && option === 'anki-file'" @changed="onPanelChanged" />
      <VocabularyImportFrequencyPanel v-if="mode === 'import' && option === 'frequency'" @changed="onPanelChanged" />
      <VocabularyCompleteVocabularyPanel v-if="mode === 'import' && option === 'complete-vocabulary'" mode="import" @changed="onPanelChanged" />
      <VocabularyExportWordListPanel v-if="mode === 'export' && option === 'word-list'" @changed="onPanelChanged" />
      <VocabularyCompleteVocabularyPanel v-if="mode === 'export' && option === 'complete-vocabulary'" mode="export" @changed="onPanelChanged" />
    </div>

    <SettingsVocabularyMassActionsCard @changed="onPanelChanged" />

    <SettingsCompositionInferenceCard @changed="onPanelChanged" />

    <SettingsRedundantFormsCard @changed="onPanelChanged" />

    <SettingsRecentlyRemovedCard :archived-count="archivedCards" :count-loading="vocabStatsLoading" @changed="onPanelChanged" />

    <SettingsVocabularyDangerZoneCard @changed="onPanelChanged" />
  </div>
</template>
