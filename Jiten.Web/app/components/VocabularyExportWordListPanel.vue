<script setup lang="ts">
  const emit = defineEmits<{ changed: [] }>();

  const toast = useToast();
  const authStore = useAuthStore();

  const exportWordsLoading = ref(false);
  const exportKanaOnly = ref(true);
  const exportMastered = ref(true);
  const exportMature = ref(true);
  const exportYoung = ref(true);
  const exportBlacklisted = ref(true);

  async function exportWords() {
    try {
      exportWordsLoading.value = true;
      toast.add({ severity: 'info', summary: 'Exporting...', detail: 'Generating your vocabulary export...', life: 3000 });

      const config = useRuntimeConfig();

      const params = new URLSearchParams({
        exportKanaOnly: exportKanaOnly.value.toString(),
        exportMastered: exportMastered.value.toString(),
        exportMature: exportMature.value.toString(),
        exportYoung: exportYoung.value.toString(),
        exportBlacklisted: exportBlacklisted.value.toString(),
      });

      const response = await fetch(`${config.public.baseURL}user/vocabulary/export-words?${params}`, {
        method: 'GET',
        headers: { Authorization: `Bearer ${authStore.accessToken}` },
      });

      if (!response.ok) throw new Error('Failed to export vocabulary');
      trackEvent('vocabulary_export', { kind: 'word_list' });

      const blob = await response.blob();
      const url = URL.createObjectURL(blob);
      const dateStr = new Date().toISOString().split('T')[0];
      const a = document.createElement('a');
      a.href = url;
      a.download = `jiten-vocabulary-export-${dateStr}.txt`;
      document.body.appendChild(a);
      a.click();
      document.body.removeChild(a);
      URL.revokeObjectURL(url);

      toast.add({ severity: 'success', summary: 'Export Successful', detail: 'Your vocabulary has been exported.', life: 5000 });
      emit('changed');
    } catch (error) {
      console.error('Error exporting words:', error);
      toast.add({ severity: 'error', summary: 'Error', detail: 'Failed to export vocabulary.', life: 5000 });
    } finally {
      exportWordsLoading.value = false;
    }
  }
</script>

<template>
  <Card>
    <template #title>
      <h3 class="text-lg font-semibold">Export Word List</h3>
    </template>
    <template #content>
      <p class="mb-3">
        Export your vocabulary as a text file organised by learning state.
        <br />
        Each line contains a single word.
      </p>

      <div class="mb-3 flex flex-col gap-3">
        <h4 class="text-md font-semibold mb-2">Export Options</h4>

        <div class="mb-3 flex flex-col gap-2">
          <div class="flex items-center">
            <Checkbox id="exportKanaOnly" v-model="exportKanaOnly" :binary="true" />
            <label for="exportKanaOnly" class="ml-2">
              <span>
                Export
                <strong>kana only</strong>
                words
              </span>
              <span class="text-sm text-gray-600 dark:text-gray-400 block">Include words that are written entirely in hiragana or katakana</span>
            </label>
          </div>

          <div class="flex items-center">
            <Checkbox id="exportMastered" v-model="exportMastered" :binary="true" />
            <label for="exportMastered" class="ml-2">
              <span>
                Export
                <strong>mastered</strong>
                words
              </span>
            </label>
          </div>

          <div class="flex items-center">
            <Checkbox id="exportMature" v-model="exportMature" :binary="true" />
            <label for="exportMature" class="ml-2">
              <span>
                Export
                <strong>mature</strong>
                words
              </span>
              <span class="text-sm text-gray-600 dark:text-gray-400 block">Cards in review with interval ≥ 21 days</span>
            </label>
          </div>

          <div class="flex items-center">
            <Checkbox id="exportYoung" v-model="exportYoung" :binary="true" />
            <label for="exportYoung" class="ml-2">
              <span>
                Export
                <strong>young</strong>
                words
              </span>
              <span class="text-sm text-gray-600 dark:text-gray-400 block">New, learning, relearning, or review with interval &lt; 21 days</span>
            </label>
          </div>

          <div class="flex items-center">
            <Checkbox id="exportBlacklisted" v-model="exportBlacklisted" :binary="true" />
            <label for="exportBlacklisted" class="ml-2">
              <span>
                Export
                <strong>blacklisted</strong>
                words
              </span>
            </label>
          </div>
        </div>

        <Button icon="pi pi-download" label="Export Words" :loading="exportWordsLoading" class="w-full md:w-auto" @click="exportWords" />
      </div>
    </template>
  </Card>
</template>
