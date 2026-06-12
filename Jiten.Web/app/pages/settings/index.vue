<script setup lang="ts">
  import SettingsCoverage from '~/components/SettingsCoverage.vue';
  import SettingsApiKey from '~/components/SettingsApiKey.vue';
  import SettingsWordSets from '~/components/SettingsWordSets.vue';
  import { useSrsStore } from '~/stores/srsStore';

  definePageMeta({
    middleware: ['auth'],
  });

  const srs = useSrsStore();
  const srsAcknowledged = ref(false);

  const enrolling = ref(false);

  async function enrollInSrs() {
    enrolling.value = true;
    try {
      await srs.enroll();
      await navigateTo('/settings/srs');
    } finally {
      enrolling.value = false;
    }
  }

  const { vocabStatsLoading, totalWordsAmount, fetchKnownWordsAmount } = useVocabularyStats();

  onMounted(() => {
    fetchKnownWordsAmount();
  });
</script>

<template>
  <div class="container mx-auto p-2 md:p-4">
    <Card class="mb-4">
      <template #title>
        <h3 class="text-lg font-semibold">Account</h3>
      </template>
      <template #content>
        <p class="text-gray-600 dark:text-gray-300 mb-3">
          Manage your email and password, update your newsletter preference, and review your sign-in methods and account details.
        </p>
        <NuxtLink to="/settings/account">
          <Button icon="pi pi-user" label="Account Settings" class="w-full md:w-auto" />
        </NuxtLink>
      </template>
    </Card>

    <SettingsCoverage class="mb-4" />

    <Card class="mb-4">
      <template #title>
        <h3 class="text-lg font-semibold">Vocabulary</h3>
      </template>
      <template #content>
        <p class="text-gray-600 dark:text-gray-300 mb-3">
          View your current known vocabulary. Import known words from AnkiConnect, JPDB, Anki text exports, or by frequency range. Export your word list, or
          back up your complete vocabulary including review history.
        </p>
        <p v-if="!vocabStatsLoading && totalWordsAmount > 0" class="mb-3 text-muted-color">
          You have <span class="font-extrabold text-primary-600 dark:text-primary-300">{{ totalWordsAmount }}</span> tracked word{{
            totalWordsAmount === 1 ? '' : 's'
          }}.
        </p>
        <NuxtLink to="/settings/vocabulary">
          <Button icon="pi pi-cog" label="Manage Vocabulary" class="w-full md:w-auto" />
        </NuxtLink>
      </template>
    </Card>

    <Card class="mb-4">
      <template #title>
        <h3 class="text-lg font-semibold">Dictionaries</h3>
      </template>
      <template #content>
        <p class="text-gray-600 dark:text-gray-300 mb-3">
          Import Yomitan dictionaries to show custom definitions on the website and in downloaded decks. Dictionary data is stored locally and never leaves your
          browser.
        </p>
        <NuxtLink to="/settings/dictionaries">
          <Button icon="pi pi-book" label="Manage Dictionaries" class="w-full md:w-auto" />
        </NuxtLink>
      </template>
    </Card>

    <SettingsWordSets class="mb-4" />

    <Card v-if="srs.srsEnrolled === true" class="mb-4">
      <template #title>
        <h3 class="text-lg font-semibold">SRS</h3>
      </template>
      <template #content>
        <p class="text-gray-600 dark:text-gray-300 mb-3">
          Configure your SRS study preferences, daily limits, card display options, and FSRS scheduling parameters.
        </p>
        <NuxtLink to="/settings/srs">
          <Button icon="pi pi-cog" label="SRS Settings" class="w-full md:w-auto" />
        </NuxtLink>
      </template>
    </Card>

    <Card v-else-if="srs.srsEnrolled === false" class="mb-4">
      <template #title>
        <h3 class="text-lg font-semibold">SRS <span class="text-sm font-normal text-orange-500">preview</span></h3>
      </template>
      <template #content>
        <p class="text-gray-600 dark:text-gray-300 mb-3">
          Jiten's built-in SRS is currently in preview. It is actively developed but may contain bugs. Please report any issues you encounter to help improve it
          and give all your feedback.
        </p>
        <div class="flex items-start gap-2 mb-4">
          <Checkbox v-model="srsAcknowledged" input-id="srsAcknowledge" :binary="true" />
          <label for="srsAcknowledge" class="text-sm cursor-pointer">
            I understand that the SRS is in preview and that it may contain bugs. I will share feedback and bug reports to help improve it.
          </label>
        </div>
        <Button icon="pi pi-arrow-right" label="Enable SRS" :disabled="!srsAcknowledged" :loading="enrolling" class="w-full md:w-auto" @click="enrollInSrs" />
      </template>
    </Card>

    <SettingsApiKey />
  </div>
</template>

<style scoped></style>
