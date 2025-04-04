<script setup lang="ts">
  import type { Word } from '~/types';
  import Card from 'primevue/card';
  import Button from 'primevue/button';
  import VocabularyDefinitions from '~/components/VocabularyDefinitions.vue';
  import { convertToRuby } from '~/utils/convertToRuby';

  const props = defineProps<{
    word: Word;
    isCompact: boolean;
  }>();

  const isCompact = ref(props.isCompact);

  const toggleCompact = () => {
    isCompact.value = !isCompact.value;
  };
</script>

<template>
  <Card class="">
    <template #title>
      <div class="flex justify-between">
        <div class="flex flex-row gap-4">
        <router-link
          class="text-2xl"
          :to="`/vocabulary/${word.wordId}/${word.mainReading.readingIndex}`"
          v-html="convertToRuby(word.mainReading.text)"
        >
        </router-link>
        <Button text @click="toggleCompact">{{ isCompact ? 'Expand' : 'Compact' }}</Button>
        </div>
        <div class="text-gray-500 dark:text-gray-300 text-sm text-right">
          x{{ word.occurrences }} | Rank #{{ word.mainReading.frequencyRank.toLocaleString() }}
        </div>
      </div>
    </template>
    <template #subtitle />
    <template #content>
      <VocabularyDefinitions :definitions="word.definitions" :is-compact="isCompact" />
    </template>
  </Card>
</template>

<style scoped></style>
