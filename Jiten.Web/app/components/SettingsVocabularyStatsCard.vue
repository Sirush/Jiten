<script setup lang="ts">
  const props = defineProps<{
    vocabStatsLoading: boolean;
    youngWordsAmount: number;
    matureWordsAmount: number;
    masteredWordsAmount: number;
    blacklistedWordsAmount: number;
    youngFormsAmount: number;
    matureFormsAmount: number;
    masteredFormsAmount: number;
    blacklistedFormsAmount: number;
    totalWordsAmount: number;
    totalFormsAmount: number;
    wordSetMasteredWords: number;
    wordSetMasteredForms: number;
    wordSetBlacklistedWords: number;
    wordSetBlacklistedForms: number;
    hasWordSetContributions: boolean;
    redundantForms: number;
    derivationCoveredWords: number;
    derivationCoveredForms: number;
  }>();

  // Kana redundancy only covers other forms of already-tracked words, so it adds forms but no words.
  const redundantWords = computed(() => props.derivationCoveredWords);
  const totalRedundantForms = computed(() => props.redundantForms + props.derivationCoveredForms);
</script>

<template>
  <Card>
    <template #title>
      <h2 class="text-xl font-bold">Vocabulary Management</h2>
    </template>
    <template #subtitle>
      <div v-if="vocabStatsLoading" class="flex flex-col items-center gap-2 py-4">
        <ProgressSpinner style="width: 2rem; height: 2rem" stroke-width="4" />
        <span class="text-gray-600 dark:text-gray-300">Loading vocabulary stats...</span>
      </div>
      <template v-else>
        <p class="text-gray-600 dark:text-gray-300">
          You're currently tracking
          <span class="font-extrabold text-primary-600 dark:text-primary-300">{{ totalWordsAmount }}</span>
          words under
          <b>{{ totalFormsAmount }}</b>
          forms. Of them,
        </p>
        <ul class="text-gray-600 dark:text-gray-300 space-y-1 ml-3">
          <li>
            <span class="font-extrabold text-yellow-600 dark:text-yellow-300">{{ youngWordsAmount }}</span>
            are young (
            <b>{{ youngFormsAmount }}</b>
            forms).
          </li>
          <li>
            <span class="font-extrabold text-green-600 dark:text-green-300">{{ matureWordsAmount }}</span>
            are mature (
            <b>{{ matureFormsAmount }}</b>
            forms).
          </li>
          <li>
            <span class="font-extrabold text-green-600 dark:text-green-300">{{ masteredWordsAmount }}</span>
            are mastered (
            <b>{{ masteredFormsAmount }}</b>
            forms).
          </li>
          <li>
            <span class="font-extrabold text-gray-600 dark:text-gray-300">{{ blacklistedWordsAmount }}</span>
            are blacklisted (
            <b>{{ blacklistedFormsAmount }}</b>
            forms).
          </li>
        </ul>
        <template v-if="hasWordSetContributions">
          <p class="text-gray-600 dark:text-gray-300 mt-2">
            Additionally, word sets contribute
            <span class="font-extrabold text-primary-600 dark:text-primary-300">{{ wordSetMasteredWords + wordSetBlacklistedWords }}</span>
            words to your coverage:
          </p>
          <ul class="text-gray-600 dark:text-gray-300 space-y-1 ml-3">
            <li v-if="wordSetMasteredWords > 0">
              <span class="font-extrabold text-green-600 dark:text-green-300">{{ wordSetMasteredWords }}</span>
              mastered (
              <b>{{ wordSetMasteredForms }}</b>
              forms)
            </li>
            <li v-if="wordSetBlacklistedWords > 0">
              <span class="font-extrabold text-gray-600 dark:text-gray-300">{{ wordSetBlacklistedWords }}</span>
              blacklisted (
              <b>{{ wordSetBlacklistedForms }}</b>
              forms)
            </li>
          </ul>
        </template>
        <p v-if="totalRedundantForms > 0" class="text-gray-600 dark:text-gray-300 mt-2">
          Redundancy covers another
          <span class="font-extrabold text-primary-600 dark:text-primary-300">{{ redundantWords }}</span>
          words (
          <b>{{ totalRedundantForms }}</b>
          forms).
        </p>
      </template>
    </template>
    <template #content>
      <p class="mb-3">You can upload a list of known words to calculate coverage and exclude them from downloads using one of the options below.</p>
      <div class="mt-3">
        <NuxtLink to="/settings/cards">
          <Button icon="pi pi-table" label="View All Words" severity="info" outlined class="w-full md:w-auto" />
        </NuxtLink>
      </div>
    </template>
  </Card>
</template>
