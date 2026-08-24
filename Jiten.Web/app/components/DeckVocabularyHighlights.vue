<script setup lang="ts">
  import type { Deck, DeckVocabularyPreviewWord } from '~/types';

  const props = defineProps<{
    deck: Deck;
  }>();

  const convertToRuby = useConvertToRuby();

  const { data: previewWords } = useApiFetch<DeckVocabularyPreviewWord[]>(
    `media-deck/${props.deck.deckId}/vocabulary-preview`,
  );

  const vocabularyLink = computed(() => `/decks/media/${props.deck.deckId}/vocabulary?sortBy=deckFreq`);
</script>

<template>
  <details v-if="previewWords?.length" class="pt-4 group">
    <summary class="flex items-center gap-1 cursor-pointer list-none [&::-webkit-details-marker]:hidden w-fit">
      <h2 class="font-bold">Vocabulary highlights</h2>
      <Icon name="material-symbols:expand-more" class="text-xl transition-transform group-open:rotate-180" />
    </summary>
    <p class="pt-1 text-sm text-surface-600 dark:text-surface-400">
      Here are some of the words that appear most often in this media, as well as a sample of its rarer vocabulary.
    </p>
    <ul class="pt-2 grid sm:grid-cols-2 gap-x-6">
      <li v-for="word in previewWords" :key="`${word.wordId}-${word.readingIndex}`">
        <NuxtLink
          :to="`/vocabulary/${word.wordId}/${word.readingIndex}`"
          class="grid grid-cols-[minmax(6rem,auto)_1fr_auto] items-baseline gap-x-3 py-1 px-2 rounded-lg hover:bg-surface-100 dark:hover:bg-surface-800 transition-colors"
        >
          <span class="text-lg font-medium" lang="ja" v-html="convertToRuby(word.readingFurigana)" />
          <span v-if="word.mainDefinition" class="text-surface-600 dark:text-surface-400 text-sm truncate">
            {{ word.mainDefinition }}
          </span>
          <span v-else />
          <span class="text-xs text-surface-500 dark:text-surface-400 shrink-0 tabular-nums">×{{ word.occurrences.toLocaleString('en-US') }}</span>
        </NuxtLink>
      </li>
    </ul>
    <NuxtLink :to="vocabularyLink" class="inline-block pt-2 text-primary text-sm">
      See the full vocabulary list<template v-if="deck.uniqueWordCount"> ({{ deck.uniqueWordCount.toLocaleString('en-US') }} words)</template> →
    </NuxtLink>
  </details>
</template>
