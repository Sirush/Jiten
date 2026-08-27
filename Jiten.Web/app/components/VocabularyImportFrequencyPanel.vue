<script setup lang="ts">
  const emit = defineEmits<{ changed: [] }>();

  const { $api } = useNuxtApp();
  const toast = useToast();

  // The API rejects anything outside this range outright, so the inputs clamp to it rather than letting
  // a typed-in rank fail the request.
  const FREQUENCY_MIN = 0;
  const FREQUENCY_MAX = 10000;

  const frequencyRange = ref([0, 100]);
  const isLoading = ref(false);

  const clampRank = (value: number | null) => Math.min(FREQUENCY_MAX, Math.max(FREQUENCY_MIN, Math.round(value || 0)));

  const updateMinFrequency = (value: number) => {
    const min = clampRank(value);
    frequencyRange.value = [min, Math.max(min, frequencyRange.value[1])];
  };

  const updateMaxFrequency = (value: number) => {
    const max = clampRank(value);
    frequencyRange.value = [Math.min(frequencyRange.value[0], max), max];
  };

  async function getVocabularyByFrequency() {
    isLoading.value = true;
    try {
      const data = await $api<{ words: number; forms: number; skipped: number }>(
        `user/vocabulary/import-from-frequency/${frequencyRange.value[0]}/${frequencyRange.value[1]}`,
        { method: 'POST' }
      );
      toast.add({ severity: 'success', detail: `Added ${data.words} words, ${data.forms} forms by frequency range.`, life: 5000 });
      await nextTick();
      emit('changed');
    } catch (error) {
      toast.add({
        severity: 'error',
        summary: 'Error',
        detail: extractApiError(error, 'Failed to add words by frequency range.'),
        life: 5000,
      });
    } finally {
      isLoading.value = false;
    }
  }
</script>

<template>
  <Card>
    <template #title>
      <h3 class="text-lg font-semibold">Add Words by Frequency Range</h3>
    </template>
    <template #content>
      <div class="flex flex-col gap-4">
        <div class="flex flex-row flex-wrap gap-2 items-center">
          <InputNumber
            :model-value="frequencyRange[0]"
            :min="FREQUENCY_MIN"
            :max="FREQUENCY_MAX"
            show-buttons
            fluid
            size="small"
            class="max-w-30 flex-shrink-0"
            @update:model-value="updateMinFrequency"
          />
          <Slider v-model="frequencyRange" range :min="FREQUENCY_MIN" :max="FREQUENCY_MAX" class="flex-grow mx-2 flex-basis-auto" />
          <InputNumber
            :model-value="frequencyRange[1]"
            :min="FREQUENCY_MIN"
            :max="FREQUENCY_MAX"
            show-buttons
            fluid
            size="small"
            class="max-w-30 flex-shrink-0"
            @update:model-value="updateMaxFrequency"
          />
        </div>
        <Button icon="pi pi-plus" label="Add Words by Frequency" class="w-full md:w-auto" :loading="isLoading" @click="getVocabularyByFrequency" />
      </div>
    </template>
  </Card>
</template>
