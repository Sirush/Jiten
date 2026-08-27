<script setup lang="ts">
  import type { FsrsExportDto, FsrsImportResultDto } from '~/types/types';

  const props = withDefaults(
    defineProps<{
      mode?: 'export' | 'import' | 'both';
    }>(),
    { mode: 'both' }
  );

  const emit = defineEmits<{ changed: [] }>();

  const { $api } = useNuxtApp();
  const toast = useToast();

  const fsrsOverwriteExisting = ref(false);
  const fsrsIncludeWordText = ref(false);
  const fsrsImportResult = ref<FsrsImportResultDto | null>(null);
  const fsrsIsLoading = ref(false);

  async function downloadFsrsVocabulary() {
    try {
      fsrsIsLoading.value = true;
      toast.add({ severity: 'info', summary: 'Exporting...', detail: 'Fetching your vocabulary data...', life: 3000 });

      const data = await $api<FsrsExportDto>(`user/vocabulary/export?includeWordText=${fsrsIncludeWordText.value}`);

      const jsonContent = JSON.stringify(data, null, 2);
      const blob = new Blob([jsonContent], { type: 'application/json' });
      const url = URL.createObjectURL(blob);

      const exportDate = new Date(data.exportDate);
      const dateStr = exportDate.toISOString().split('T')[0];

      const a = document.createElement('a');
      a.href = url;
      a.download = `jiten-vocabulary-export-${dateStr}.json`;
      document.body.appendChild(a);
      a.click();
      document.body.removeChild(a);
      URL.revokeObjectURL(url);

      const extras = (data.customSentences?.length ?? 0) + (data.customMeanings?.length ?? 0);
      toast.add({
        severity: 'success',
        summary: 'Export Successful',
        detail:
          `Exported ${data.totalCards} cards with ${data.totalReviews} review logs` +
          (extras > 0 ? `, ${data.customSentences?.length ?? 0} custom sentences and ${data.customMeanings?.length ?? 0} notes.` : '.'),
        life: 5000,
      });
    } catch (error) {
      console.error('Error exporting FSRS vocabulary:', error);
      toast.add({ severity: 'error', summary: 'Error', detail: 'Failed to export vocabulary data.', life: 5000 });
    } finally {
      fsrsIsLoading.value = false;
    }
  }

  function handleFsrsFileSelect(event: any) {
    const file = event.files?.[0];
    if (!file) {
      toast.add({ severity: 'error', summary: 'Error', detail: 'No file selected.', life: 5000 });
      return;
    }

    if (file.type !== 'application/json') {
      toast.add({ severity: 'error', summary: 'Error', detail: 'Please upload a JSON file.', life: 5000 });
      return;
    }

    const reader = new FileReader();

    reader.onload = async (e) => {
      try {
        fsrsIsLoading.value = true;
        fsrsImportResult.value = null;
        toast.add({ severity: 'info', summary: 'Importing...', detail: 'Processing vocabulary data...', life: 3000 });

        const text = e.target?.result as string;
        const importData: FsrsExportDto = JSON.parse(text);

        const hasCards = Array.isArray(importData.cards);
        const hasExtras = Array.isArray(importData.customSentences) || Array.isArray(importData.customMeanings);
        if (!hasCards && !hasExtras) {
          throw new Error('Invalid file format: missing or invalid cards array');
        }

        const result = await $api<FsrsImportResultDto>(`user/vocabulary/import?overwrite=${fsrsOverwriteExisting.value}`, {
          method: 'POST',
          body: JSON.stringify(importData),
          headers: { 'Content-Type': 'application/json' },
        });

        fsrsImportResult.value = result;

        if (result.validationErrors && result.validationErrors.length > 0) {
          toast.add({
            severity: 'warn',
            summary: 'Import Completed with Errors',
            detail: `Imported ${result.cardsImported} cards, but ${result.validationErrors.length} errors occurred.`,
            life: 6000,
          });
        } else {
          const totalProcessed = result.cardsImported + result.cardsUpdated + result.cardsSkipped;
          const extras = result.customSentencesImported + result.customMeaningsImported;
          toast.add({
            severity: 'success',
            summary: 'Import Successful',
            detail:
              `Processed ${totalProcessed} cards: ${result.cardsImported} imported, ${result.cardsUpdated} updated, ${result.cardsSkipped} skipped.` +
              (extras > 0 ? ` Also restored ${result.customSentencesImported} custom sentences and ${result.customMeaningsImported} notes.` : ''),
            life: 6000,
          });
        }

        emit('changed');
      } catch (error) {
        console.error('Error importing FSRS vocabulary:', error);
        const errorMsg = error instanceof Error ? error.message : 'Failed to import vocabulary data.';
        toast.add({ severity: 'error', summary: 'Import Failed', detail: errorMsg, life: 5000 });
      } finally {
        fsrsIsLoading.value = false;
      }
    };

    reader.onerror = () => {
      toast.add({ severity: 'error', summary: 'Error', detail: 'Error reading file.', life: 5000 });
      fsrsIsLoading.value = false;
    };

    reader.readAsText(file);
  }
</script>

<template>
  <Card>
    <template #title>
      <h3 class="text-lg font-semibold">
        <template v-if="mode === 'export'">Export Complete Vocabulary (Backup)</template>
        <template v-else-if="mode === 'import'">Import Complete Vocabulary (Backup)</template>
        <template v-else>Export/Import Complete Vocabulary (Backup)</template>
      </h3>
    </template>
    <template #content>
      <p class="mb-3">
        <template v-if="mode === 'export'">
          Export your complete vocabulary with full data, including card states, review history, stability, difficulty, and due dates as well as your custom
          example sentences and custom notes.
        </template>
        <template v-else-if="mode === 'import'">
          Import a previously exported vocabulary backup. This includes card states, review history, stability, difficulty, due dates, custom example sentences
          and custom notes.
        </template>
        <template v-else>
          Export and import your complete vocabulary with full data. This includes card states, review history, stability, difficulty, due dates, custom example
          sentences and custom notes. Use this for complete backups or transferring data between accounts.
        </template>
      </p>

      <div v-if="mode === 'export' || mode === 'both'" class="mb-4">
        <h4 v-if="mode === 'both'" class="text-md font-semibold mb-2">Export</h4>
        <div class="mb-3 flex items-center">
          <Checkbox id="fsrsIncludeWordText" v-model="fsrsIncludeWordText" :binary="true" />
          <label for="fsrsIncludeWordText" class="ml-2">
            <span>Include word readings</span>
            <span class="text-sm text-gray-600 dark:text-gray-400 block">
              Add the word and its reading as full text instead of just the IDs at the cost of larger files.
            </span>
          </label>
        </div>
        <Button icon="pi pi-download" label="Export Complete Vocabulary" :loading="fsrsIsLoading" class="w-full md:w-auto" @click="downloadFsrsVocabulary" />
      </div>

      <div v-if="mode === 'import' || mode === 'both'" class="mb-3">
        <h4 v-if="mode === 'both'" class="text-md font-semibold mb-2">Import</h4>
        <div class="mb-3 flex items-center">
          <Checkbox id="fsrsOverwrite" v-model="fsrsOverwriteExisting" :binary="true" />
          <label for="fsrsOverwrite" class="ml-2">
            <span>Overwrite existing cards</span>
            <span class="text-sm text-gray-600 dark:text-gray-400 block">
              If unchecked, only new cards will be added. If checked, existing cards and custom notes will be replaced with imported data. Custom sentences are
              always appended and never replaced.
            </span>
          </label>
        </div>

        <FileUpload
          mode="basic"
          name="fsrsFile"
          accept=".json"
          :custom-upload="true"
          :auto="true"
          :choose-label="'Select JSON File'"
          :disabled="fsrsIsLoading"
          class="mb-3"
          @select="handleFsrsFileSelect"
        />

        <div v-if="fsrsImportResult" class="bg-gray-100 dark:bg-gray-800 p-3 rounded">
          <h5 class="font-semibold mb-2">Import Results</h5>
          <div class="text-sm space-y-1">
            <div>
              Cards imported:
              <strong class="text-green-600">{{ fsrsImportResult.cardsImported }}</strong>
            </div>
            <div>
              Cards updated:
              <strong class="text-blue-600">{{ fsrsImportResult.cardsUpdated }}</strong>
            </div>
            <div>
              Cards skipped:
              <strong class="text-amber-600">{{ fsrsImportResult.cardsSkipped }}</strong>
            </div>
            <div>
              Review logs imported:
              <strong>{{ fsrsImportResult.reviewLogsImported }}</strong>
            </div>
            <div>
              Custom sentences restored:
              <strong class="text-green-600">{{ fsrsImportResult.customSentencesImported }}</strong>
              <span v-if="fsrsImportResult.customSentencesSkipped" class="text-gray-600 dark:text-gray-400">
                ({{ fsrsImportResult.customSentencesSkipped }} skipped)
              </span>
            </div>
            <div>
              Word notes restored:
              <strong class="text-green-600">{{ fsrsImportResult.customMeaningsImported }}</strong>
              <span v-if="fsrsImportResult.customMeaningsSkipped" class="text-gray-600 dark:text-gray-400">
                ({{ fsrsImportResult.customMeaningsSkipped }} skipped)
              </span>
            </div>
            <div v-if="fsrsImportResult.validationErrors && fsrsImportResult.validationErrors.length > 0">
              <details class="mt-2">
                <summary class="cursor-pointer text-red-600 font-semibold">Validation Errors ({{ fsrsImportResult.validationErrors.length }})</summary>
                <ul class="list-disc list-inside mt-2 text-red-600 space-y-1">
                  <li v-for="(error, index) in fsrsImportResult.validationErrors" :key="index">{{ error }}</li>
                </ul>
              </details>
            </div>
          </div>
        </div>
      </div>
    </template>
  </Card>
</template>
