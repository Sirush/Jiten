<script setup lang="ts">
  const emit = defineEmits<{ changed: [] }>();

  const { $api } = useNuxtApp();
  const toast = useToast();

  const isLoading = ref(false);
  const uploadedCount = ref<number | null>(null);
  const addedCount = ref<number | null>(null);
  const updatedCount = ref<number | null>(null);
  const parseWordsAnkiTxt = ref(false);
  const overwriteExisting = ref(false);

  async function handleAnkiFileSelect(event: any) {
    const file = event.files?.[0];
    if (!file) {
      toast.add({ severity: 'error', summary: 'Error', detail: 'No file selected.', life: 5000 });
      return;
    }

    const fileName = file.name.toLowerCase();
    if (!fileName.endsWith('.txt') && !fileName.endsWith('.csv')) {
      toast.add({ severity: 'error', summary: 'Error', detail: 'Please upload a .txt or .csv file.', life: 5000 });
      return;
    }

    try {
      isLoading.value = true;
      toast.add({ severity: 'info', summary: 'Processing Anki file...', detail: 'Please wait...', life: 5000 });

      const formData = new FormData();
      formData.append('file', file);

      const result = await $api<{ parsed: number; added: number; updated: number }>(
        `user/vocabulary/import-from-anki-txt?parseWords=${parseWordsAnkiTxt.value}&overwriteExisting=${overwriteExisting.value}`,
        { method: 'POST', body: formData },
      );

      if (result) {
        uploadedCount.value = result.parsed;
        addedCount.value = result.added;
        updatedCount.value = result.updated;
        emit('changed');
        const detail = overwriteExisting.value
          ? `Parsed ${result.parsed} words, added ${result.added} forms, overwrote ${result.updated}.`
          : `Parsed ${result.parsed} words, added ${result.added} forms. Words you already track were left untouched.`;
        toast.add({ severity: 'success', summary: 'Known words updated', detail, life: 6000 });
      }
    } catch (error) {
      console.error('Error processing Anki file:', error);
      const data = (error as { data?: unknown })?.data;
      const detail = (typeof data === 'object' ? (data as { message?: string })?.message : data) || 'Failed to process Anki file.';
      toast.add({ severity: 'error', summary: 'Error', detail, life: 5000 });
    } finally {
      isLoading.value = false;
    }
  }
</script>

<template>
  <Card>
    <template #title>
      <h3 class="text-lg font-semibold">Import from Anki Deck or List of Words</h3>
    </template>
    <template #content>
      <p class="mb-2">Anki: export your deck as Export format: Notes in Plain Text (.txt) and untick all the boxes.</p>
      <p class="mb-2">This can also import a list of words, one per line. The word can be ended by a comma or a tab as long as there's only one per line.</p>
      <p class="mb-3 text-sm text-amber-600 dark:text-amber-400">
        Warning: This will mark ALL words contained in the deck as known. You will have to remove the lines you don't want manually before uploading your file.
        The words to add need to be the first word on each line. Limited to 50,000 lines and 50 MB.
      </p>

      <div class="mb-3 flex items-center">
        <Checkbox id="parseWordsAnkiTxt" v-model="parseWordsAnkiTxt" :binary="true" />
        <label for="parseWordsAnkiTxt" class="ml-2">
          <span>Parse words instead of importing directly</span>
          <span class="text-sm text-gray-600 dark:text-gray-400 block"> Only use if you have conjugated verbs instead of the dictionary form (less accurate) </span>
        </label>
      </div>

      <div class="mb-3 flex items-center">
        <Checkbox id="overwriteExistingAnkiTxt" v-model="overwriteExisting" :binary="true" />
        <label for="overwriteExistingAnkiTxt" class="ml-2">
          <span>Overwrite existing cards</span>
          <span class="text-sm text-gray-600 dark:text-gray-400 block">
            Marks words you are already studying as mastered, discarding their current state. Leave off to only add words you do not have yet.
          </span>
        </label>
      </div>

      <FileUpload mode="basic" name="ankiFile" accept=".txt, .csv" :custom-upload="true" :auto="true" :choose-label="'Select .txt or .csv File'" :disabled="isLoading" class="mb-3" @select="handleAnkiFileSelect" />

      <div v-if="addedCount !== null || uploadedCount !== null" class="text-sm text-gray-700 dark:text-gray-300">
        <div v-if="uploadedCount !== null">
          Parsed from file: <strong>{{ uploadedCount }}</strong>
        </div>
        <div v-if="addedCount !== null">
          Added: <strong class="text-green-600 dark:text-green-400">{{ addedCount }}</strong>
        </div>
        <div v-if="updatedCount">
          Overwritten: <strong class="text-amber-600 dark:text-amber-400">{{ updatedCount }}</strong>
        </div>
      </div>
    </template>
  </Card>
</template>
