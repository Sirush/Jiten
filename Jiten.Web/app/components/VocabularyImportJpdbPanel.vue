<script setup lang="ts">
  const emit = defineEmits<{ changed: [] }>();

  const { $api } = useNuxtApp();
  const toast = useToast();
  const { JpdbApiClient } = useJpdbApi();

  const isLoading = ref(false);
  const jpdbApiKey = ref('');
  const importAdditionalReadings = ref(true);
  const frequencyThreshold = ref(15000);
  const jpdbProgress = ref('');
  const overwriteCardStates = ref(true);
  const reviewsFile = ref<File | null>(null);

  function onReviewsFileSelect(event: { files: File[] }) {
    reviewsFile.value = event.files?.[0] ?? null;
  }

  function onReviewsFileClear() {
    reviewsFile.value = null;
  }

  async function importFromJpdb() {
    const hasApiKey = !!jpdbApiKey.value;
    const hasFile = !!reviewsFile.value;

    if (!hasApiKey && !hasFile) {
      toast.add({ severity: 'error', summary: 'Error', detail: 'Please enter your JPDB API key or upload a reviews.json file.', life: 5000 });
      return;
    }

    try {
      isLoading.value = true;

      if (hasApiKey) {
        await importViaApi();
      }

      if (hasFile) {
        await importReviewsFile();
      }
    } catch (error) {
      console.error('Error importing from JPDB:', error);
      const errorMessage = error instanceof Error ? error.message : 'Failed to import from JPDB. Please check your input and try again.';
      toast.add({ severity: 'error', summary: 'Error', detail: errorMessage, life: 5000 });
    } finally {
      isLoading.value = false;
      jpdbProgress.value = '';
      jpdbApiKey.value = '';
    }
  }

  async function importViaApi() {
    jpdbProgress.value = 'Initialising JPDB API client...';
    toast.add({ severity: 'info', summary: 'Processing', detail: 'Importing from JPDB API...', life: 5000 });

    const client = new JpdbApiClient(jpdbApiKey.value);

    jpdbProgress.value = 'Fetching user decks...';
    await new Promise((resolve) => setTimeout(resolve, 100));

    const response = await client.getFilteredVocabularyIds();
    const totalWords = response.knownIds.length + response.blacklistedIds.length + response.suspendedIds.length;

    if (totalWords > 0) {
      jpdbProgress.value = 'Sending vocabulary to your account...';
      await new Promise((resolve) => setTimeout(resolve, 100));

      const result = await $api<{ added: number; skipped: number }>('user/vocabulary/import-from-ids', {
        method: 'POST',
        body: JSON.stringify({
          wordIds: response.knownIds,
          blacklistedWordIds: response.blacklistedIds,
          suspendedWordIds: response.suspendedIds,
          frequencyThreshold: importAdditionalReadings.value ? frequencyThreshold.value : null,
        }),
        headers: { 'Content-Type': 'application/json' },
      });

      if (result) {
        emit('changed');
        toast.add({ severity: 'success', summary: 'API import complete', detail: `Added ${result.added}, skipped ${result.skipped}.`, life: 6000 });
      } else {
        toast.add({ severity: 'info', summary: 'No changes', detail: 'No words were added from the API.', life: 5000 });
      }
    } else {
      toast.add({ severity: 'info', summary: 'No words found', detail: 'No words were found from JPDB API.', life: 5000 });
    }
  }

  async function importReviewsFile() {
    jpdbProgress.value = 'Reading reviews file...';
    await new Promise((resolve) => setTimeout(resolve, 100));

    const text = await reviewsFile.value!.text();
    const data = JSON.parse(text);

    const vocabCards = data.cards_vocabulary_jp_en;
    if (!Array.isArray(vocabCards) || vocabCards.length === 0) {
      toast.add({ severity: 'info', summary: 'No reviews', detail: 'No vocabulary reviews found in the file.', life: 5000 });
      return;
    }

    const cards = vocabCards.map((card: { vid: number; spelling: string; reviews: { timestamp: number; grade: string }[] }) => ({
      wordId: card.vid,
      spelling: card.spelling,
      reviews: card.reviews.map((r) => ({ timestamp: r.timestamp, grade: r.grade })),
    }));

    jpdbProgress.value = `Importing ${cards.length} cards with review history...`;
    await new Promise((resolve) => setTimeout(resolve, 100));

    const result = await $api<{ cardsProcessed: number; reviewsImported: number; reviewsUpdated: number; skipped: number }>('user/vocabulary/import-jpdb-reviews', {
      method: 'POST',
      body: JSON.stringify({ cards, overwriteCardStates: overwriteCardStates.value }),
      headers: { 'Content-Type': 'application/json' },
    });

    if (result) {
      emit('changed');
      toast.add({
        severity: 'success',
        summary: 'Reviews imported',
        detail: `${result.cardsProcessed} cards processed, ${result.reviewsImported} reviews imported, ${result.reviewsUpdated} updated, ${result.skipped} skipped.`,
        life: 6000,
      });
    }
  }
</script>

<template>
  <Card>
    <template #title>
      <h3 class="text-lg font-semibold">Import from JPDB</h3>
    </template>
    <template #content>
      <div class="flex flex-col gap-4">
        <div>
          <h4 class="font-medium mb-2">API Import</h4>
          <p class="mb-2 text-sm">
            You can find your API key on the bottom of the settings page (<a
              href="https://jpdb.io/settings"
              target="_blank"
              rel="nofollow"
              class="text-primary-500 hover:underline"
              >https://jpdb.io/settings</a
            >)
          </p>
          <p class="mb-3 text-sm text-gray-600 dark:text-gray-400">
            Your API key will only be used for the import and won't be saved anywhere. Only the word list is sent to the server.
          </p>
          <span class="p-float-label">
            <InputText id="jpdbApiKey" v-model="jpdbApiKey" class="w-full" type="password" />
            <label for="jpdbApiKey">JPDB API Key</label>
          </span>
        </div>

        <div class="flex flex-col gap-2">
          <div class="flex items-center">
            <Checkbox id="importAdditionalReadings" v-model="importAdditionalReadings" :binary="true" />
            <label for="importAdditionalReadings" class="ml-2">Import additional readings within frequency range of the imported reading (only the most frequent reading by default)</label>
          </div>
          <div v-if="importAdditionalReadings" class="ml-6 flex items-center gap-2">
            <label for="frequencyThreshold" class="text-sm">Frequency range:</label>
            <InputNumber id="frequencyThreshold" v-model="frequencyThreshold" :min="1000" :max="100000" :step="1000" class="w-32" />
          </div>
        </div>

        <div>
          <h4 class="font-medium mb-2">Reviews Import</h4>
          <p class="mb-3 text-sm text-gray-600 dark:text-gray-400">
            Upload a reviews.json file exported from JPDB to import your review history. This can be used alongside the API import or on its own.
          </p>
          <FileUpload
            mode="basic"
            accept=".json"
            :auto="false"
            choose-label="Choose reviews.json"
            @select="onReviewsFileSelect"
            @clear="onReviewsFileClear"
          />
          <div class="flex items-center mt-2">
            <Checkbox id="overwriteCardStates" v-model="overwriteCardStates" :binary="true" />
            <label for="overwriteCardStates" class="ml-2">Overwrite existing card states (mastered, blacklisted, suspended) with review history</label>
          </div>
        </div>

        <Button label="Import from JPDB" icon="pi pi-download" :disabled="(!jpdbApiKey && !reviewsFile) || isLoading" class="w-full md:w-auto" @click="importFromJpdb" />
      </div>
    </template>
  </Card>

  <LoadingOverlay :visible="isLoading">
    <p v-if="jpdbProgress">{{ jpdbProgress }}</p>
    <p v-else>Processing your data...</p>
  </LoadingOverlay>
</template>
