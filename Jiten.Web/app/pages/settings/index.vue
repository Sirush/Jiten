<script setup lang="ts">
  import SettingsCoverage from '~/components/SettingsCoverage.vue';
  import SettingsApiKey from '~/components/SettingsApiKey.vue';
  import SettingsWordSets from '~/components/SettingsWordSets.vue';
  import SettingsJitenPlus from '~/components/SettingsJitenPlus.vue';

  definePageMeta({
    middleware: ['auth'],
  });

  useHead({ title: 'Settings - Jiten' });

  const { vocabStatsLoading, totalWordsAmount, fetchKnownWordsAmount } = useVocabularyStats();

  const vocabularyStatus = computed(() => {
    if (vocabStatsLoading.value || totalWordsAmount.value === 0) return null;
    return `${totalWordsAmount.value.toLocaleString()} tracked word${totalWordsAmount.value === 1 ? '' : 's'}`;
  });

  onMounted(() => {
    fetchKnownWordsAmount();
  });
</script>

<template>
  <div class="container mx-auto p-2 md:p-4">
    <h1 class="mb-4 text-2xl font-bold">Settings</h1>

    <section class="mb-6" aria-labelledby="settings-account">
      <h2 id="settings-account" class="mb-2 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">Account</h2>
      <div class="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
        <SettingsTile icon="pi pi-user" title="Account" to="/settings/account" description="Account details, email, password and sign-in methods." />
        <SettingsJitenPlus />
      </div>
    </section>

    <section class="mb-6" aria-labelledby="settings-vocabulary">
      <h2 id="settings-vocabulary" class="mb-2 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">Vocabulary</h2>
      <div class="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
        <SettingsTile
          icon="pi pi-book"
          title="Vocabulary"
          to="/settings/vocabulary"
          description="Import and export your known words from Anki, JPDB, frequency ranges."
          :status="vocabularyStatus"
        />
        <SettingsWordSets />
        <SettingsCoverage />
      </div>
    </section>

    <section class="mb-6" aria-labelledby="settings-study">
      <h2 id="settings-study" class="mb-2 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">Study</h2>
      <div class="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
        <SettingsTile
          icon="pi pi-sliders-h"
          title="Study (SRS)"
          to="/settings/srs"
          description="Customise your study preferences, daily limits, card display and FSRS scheduling parameters."
        />
        <SettingsTile icon="pi pi-th-large" title="Cards" to="/settings/cards" description="Browse and bulk-edit your known words." />
        <SettingsTile icon="pi pi-image" title="Card Media" to="/settings/card-media" description="Manage images and audio on your cards." plus />
      </div>
    </section>

    <section aria-labelledby="settings-advanced">
      <h2 id="settings-advanced" class="mb-2 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">Advanced</h2>
      <div class="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
        <SettingsTile
          icon="pi pi-language"
          title="Dictionaries"
          to="/settings/dictionaries"
          description="Import Yomitan dictionaries to show custom definitions on the site and in downloaded decks. Data stays local and never leaves your browser."
        />
        <div class="lg:col-span-2"><SettingsApiKey /></div>
      </div>
    </section>
  </div>
</template>
