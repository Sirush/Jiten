<script setup lang="ts">
  import type { Deck, DeckDetail } from '~/types';
  import Card from 'primevue/card';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { useApiFetchPaginated } from '~/composables/useApiFetch';

  const props = defineProps<{
    deckId: string;
    isCompact?: boolean;
  }>();

  const {
    data: response,
    status,
    error,
  } = await useApiFetchPaginated<DeckDetail[]>(`media-deck/${props.deckId}/detail`, {
    query: {
      offset: 0,
    },
  });

  const deck = computed<Deck | undefined>(() => {
    if (response.value?.data) {
      return response.value.data.mainDeck;
    }
    return undefined;
  });

  const statsItems = computed(() => {
    const items = [
      { label: 'Character count:', value: deck.value?.characterCount?.toLocaleString() ?? 'N/A' },
      { label: 'Words:', value: deck.value?.wordCount?.toLocaleString() ?? 'N/A' },
      { label: 'Unique Words:', value: deck.value?.uniqueWordCount?.toLocaleString() ?? 'N/A' },
      { label: 'Unique Kanji:', value: deck.value?.uniqueKanjiCount?.toLocaleString() ?? 'N/A' },
      { label: 'Kanji (1-occurence):', value: deck.value?.uniqueKanjiUsedOnceCount?.toLocaleString() ?? 'N/A' },
    ];

    if (deck.value?.averageSentenceLength !== 0 && !deck.value?.hideAverageSentenceLength) {
      items.push({ label: 'Average sentence length:', value: deck.value?.averageSentenceLength.toFixed(1) ?? 'N/A' });
    }

    if (!deck.value?.hideDialoguePercentage && deck.value?.dialoguePercentage != 0 && deck.value?.dialoguePercentage != 100) {
      items.push({ label: 'Dialogue:', value: `${deck.value?.dialoguePercentage.toFixed(1)}%` });
    }

    return items;
  });
</script>

<template>
  <div
    v-if="status !== 'pending' && deck"
    class="og-card-container bg-white text-black border border-gray-300 flex flex-row overflow-hidden"
    style="width: 1200px; height: 630px; padding: 32px; font-family: 'Noto Sans JP', sans-serif; align-items: flex-start"
  >
    <div class="flex-shrink-0 h-full" style="width: 340px; margin-right: 32px">
      <img
        :src="deck.coverName == 'nocover.jpg' ? '/img/nocover.jpg' : deck.coverName"
        :alt="deck.originalTitle"
        class="object-cover rounded"
        style="height: 100%; width: 100%"
      />
    </div>

    <div class="flex flex-col flex-grow">
      <h1 class="font-bold truncate" style="font-size: 2rem; margin-bottom: 8px; line-height: 1.2; ">
        {{ deck.originalTitle.slice(0, 20) }}
      </h1>

      <span class="text-gray-600" style="font-size: 1.5rem; margin-bottom: 32px">
        {{ getMediaTypeText(deck.mediaType) }}
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
          v-if="deck.difficulty != -1"
          class="flex justify-between mb-8 px-4 py-2 rounded-md"
          :style="{
            padding: '2px 2px',
            borderRadius: '4px',
            backgroundColor: '#ad45fe11',
          }"
        >
          <span class="text-gray-700">Difficulty:</span>
          <span v-if="deck.difficulty == 0" class="font-mono text-green-700 dark:text-green-300"> ★☆☆☆☆ </span>
          <span v-else-if="deck.difficulty == 1" class="font-mono text-green-500"> ★★☆☆☆ </span>
          <span v-else-if="deck.difficulty == 2" class="font-mono text-cyan-500"> ★★★☆☆ </span>
          <span v-else-if="deck.difficulty == 3" class="font-mono text-amber-600"> ★★★★☆ </span>
          <span v-else-if="deck.difficulty == 4" class="font-mono text-orange-600"> ★★★★★ </span>
          <span v-else-if="deck.difficulty == 5" class="font-mono text-red-600"> ★★★★★ </span>
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
