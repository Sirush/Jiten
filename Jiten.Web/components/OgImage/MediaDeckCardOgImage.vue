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
</script>

<template>
  <div
      v-if="status !== 'pending' && deck"
      class="og-card-container bg-white text-black border border-gray-300 flex flex-row overflow-hidden"
      style="width: 1200px; height: 630px; padding: 32px; font-family: 'Noto Sans JP', sans-serif"
  >
    <div class="flex-shrink-0 h-full" style="width: 340px; margin-right: 32px;">
      <img
          :src="deck.coverName == 'nocover.jpg' ? '/img/nocover.jpg' : deck.coverName"
          :alt="deck.originalTitle"
          class="object-cover rounded"
          style="height: 100%; width: 100%"
      />
    </div>

    <div class="flex flex-col flex-grow">
      <h1
          class="font-bold truncate"
          style="font-size: 2.25rem; margin-bottom: 8px; max-height: 2.8em;"
      >
        {{ deck.originalTitle.slice(0, 20) }}
      </h1>

      <span class="text-gray-600" style="font-size: 1.5rem; margin-bottom: 32px;">
        {{ getMediaTypeText(deck.mediaType) }}
      </span>

      <div class="flex-grow"></div>

      <div class="flex flex-col" style="font-size: 1.75rem;">
        <div class="flex justify-between" style="margin-bottom: 8px">
          <span class="text-gray-700">Character count:</span>
          <span class="font-mono">{{ deck.characterCount?.toLocaleString() ?? 'N/A' }}</span>
        </div>
        <div class="flex justify-between" style="margin-bottom: 8px">
          <span class="text-gray-700">Words:</span>
          <span class="font-mono">{{ deck.wordCount?.toLocaleString() ?? 'N/A' }}</span>
        </div>
        <div class="flex justify-between" style="margin-bottom: 8px">
          <span class="text-gray-700">Unique Words:</span>
          <span class="font-mono">{{ deck.uniqueWordCount?.toLocaleString() ?? 'N/A' }}</span>
        </div>

        <div class="flex justify-between" style="margin-bottom: 8px">
          <span class="text-gray-700">Kanji:</span>
          <span class="font-mono">{{ deck.uniqueKanjiCount?.toLocaleString() ?? 'N/A' }}</span>
        </div>
        <div class="flex justify-between" style="margin-bottom: 8px">
          <span class="text-gray-700">Unique Kanji:</span>
          <span class="font-mono">{{ deck.uniqueKanjiUsedOnceCount?.toLocaleString() ?? 'N/A' }}</span>
        </div>

        <div v-if="deck.difficulty != 0" class="flex justify-between">
          <span class="text-gray-700">Difficulty:</span>
          <span class="font-mono">{{ deck.difficulty }}</span>
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
