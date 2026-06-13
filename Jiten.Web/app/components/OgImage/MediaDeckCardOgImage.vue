<script setup lang="ts">
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { MediaType } from '~/types/enums';

  const props = defineProps<{
    title?: string;
    mediaType?: MediaType;
    coverName?: string;
    characterCount?: number;
    wordCount?: number;
    uniqueWordCount?: number;
    uniqueKanjiCount?: number;
    uniqueKanjiUsedOnceCount?: number;
    averageSentenceLength?: number;
    hideAverageSentenceLength?: boolean;
    dialoguePercentage?: number;
    hideDialoguePercentage?: boolean;
    difficulty?: number;
  }>();

  const statsItems = computed(() => {
    const items = [
      { label: 'Character count:', value: props.characterCount?.toLocaleString() ?? 'N/A' },
      { label: 'Words:', value: props.wordCount?.toLocaleString() ?? 'N/A' },
      { label: 'Unique Words:', value: props.uniqueWordCount?.toLocaleString() ?? 'N/A' },
      { label: 'Unique Kanji:', value: props.uniqueKanjiCount?.toLocaleString() ?? 'N/A' },
      { label: 'Kanji (1-occurence):', value: props.uniqueKanjiUsedOnceCount?.toLocaleString() ?? 'N/A' },
    ];

    if (props.averageSentenceLength !== 0 && !props.hideAverageSentenceLength) {
      items.push({ label: 'Average sentence length:', value: props.averageSentenceLength?.toFixed(1) ?? 'N/A' });
    }

    if (!props.hideDialoguePercentage && props.dialoguePercentage != 0 && props.dialoguePercentage != 100) {
      items.push({ label: 'Dialogue:', value: `${props.dialoguePercentage?.toFixed(1)}%` });
    }

    return items;
  });
</script>

<template>
  <div
    v-if="title"
    class="og-card-container bg-white text-black border border-gray-300 flex flex-row overflow-hidden"
    style="width: 1200px; height: 630px; padding: 32px; font-family: 'Noto Sans JP', sans-serif; align-items: flex-start"
  >
    <div class="flex-shrink-0 h-full" style="width: 340px; margin-right: 32px">
      <img
        :src="!coverName || coverName == 'nocover.jpg' ? '/img/nocover.jpg' : coverName"
        :alt="title"
        class="object-cover rounded"
        style="height: 100%; width: 100%"
      />
    </div>

    <div class="flex flex-col flex-grow">
      <h1 class="font-bold truncate" style="font-size: 2rem; margin-bottom: 8px; line-height: 1.2; ">
        {{ title.slice(0, 20) }}
      </h1>

      <span class="text-gray-600" style="font-size: 1.5rem; margin-bottom: 32px">
        {{ getMediaTypeText(mediaType ?? (0 as MediaType)) }}
      </span>

      <div class="flex-grow"></div>

      <div class="flex flex-col" style="font-size: 1.5rem">
        <div
          v-for="(item, index) in statsItems"
          :key="index"
          class="flex justify-between mb-8 px-4 py-2 rounded-md"
          :style="{
            marginBottom: '2px',
            padding: '2px 2px',
            borderRadius: '4px',
            backgroundColor: index % 2 === 0 ? '#f6f6f6' : '#e2e2e2',
          }"
        >
          <span class="text-gray-700">{{ item.label }}</span>
          <span class="font-mono">{{ item.value }}</span>
        </div>

        <div
          v-if="difficulty != null && difficulty != -1"
          class="flex justify-between mb-8 px-4 py-2 rounded-md"
          :style="{
            padding: '2px 2px',
            borderRadius: '4px',
            backgroundColor: '#ad45fe11',
          }"
        >
          <span class="text-gray-700">Difficulty:</span>
          <span v-if="difficulty == 0" class="font-mono text-green-700 dark:text-green-300"> ★☆☆☆☆ </span>
          <span v-else-if="difficulty == 1" class="font-mono text-green-500"> ★★☆☆☆ </span>
          <span v-else-if="difficulty == 2" class="font-mono text-cyan-500"> ★★★☆☆ </span>
          <span v-else-if="difficulty == 3" class="font-mono text-amber-600"> ★★★★☆ </span>
          <span v-else-if="difficulty == 4" class="font-mono text-orange-600"> ★★★★★ </span>
          <span v-else-if="difficulty == 5" class="font-mono text-red-600"> ★★★★★ </span>
        </div>
      </div>
    </div>
  </div>

  <div
    v-else
    class="og-card-container bg-white text-black border border-gray-300 flex items-center justify-center"
    style="width: 1200px; height: 630px; padding: 32px; font-family: 'Noto Sans JP', sans-serif"
  >
    <span class="text-gray-500">Loading OG Image...</span>
  </div>
</template>

<style scoped></style>
