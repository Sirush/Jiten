<script setup lang="ts">
  import type { Deck } from '~/types';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { getDifficultyName } from '~/utils/difficultyColours';

  const props = defineProps<{
    deck: Deck;
  }>();

  const localiseTitle = useLocaliseTitle();

  // Fixed neutral phrasing (no display-preference formatting): this block is SSR copy for
  // search engines and must render the same for every request.
  const overview = computed(() => {
    const d = props.deck;
    const title = localiseTitle(d);
    const type = getMediaTypeText(d.mediaType).toLowerCase();
    const sentences: string[] = [];

    if (d.characterCount) {
      sentences.push(
        `${title} is a Japanese ${type} of ${d.characterCount.toLocaleString('en-US')} characters, with ${d.uniqueWordCount.toLocaleString('en-US')} unique words and ${d.uniqueKanjiCount.toLocaleString('en-US')} different kanji.`,
      );
    } else {
      sentences.push(`${title} is a Japanese ${type}.`);
    }

    const difficulty = d.difficultyRaw >= 0 ? d.difficultyRaw : d.difficulty;
    if (difficulty >= 0) {
      sentences.push(`Its difficulty rating on Jiten is ${getDifficultyName(difficulty).toLowerCase()}, ${Math.min(difficulty, 5).toFixed(1)} out of 5.`);
    }

    if (d.uniqueWordCount) {
      sentences.push(
        `The full vocabulary list ranks all ${d.uniqueWordCount.toLocaleString('en-US')} words by how often they appear, and you can study them on Jiten or download them as an Anki deck.`,
      );
    }

    return sentences.join(' ');
  });
</script>

<template>
  <section class="pt-4">
    <h2 class="font-bold">Japanese study overview</h2>
    <p class="pt-2 text-sm leading-relaxed text-surface-600 dark:text-surface-400">{{ overview }}</p>
  </section>
</template>
