<script setup lang="ts">
  import { useRoute, useRouter } from 'vue-router';
  import { stripRuby } from '~/utils/stripRuby';
  import type { Word } from '~/types/types';

  definePageMeta({
    key: (route) => `vocab-${route.params.wordId}`,
  });

  const route = useRoute();
  const router = useRouter();

  const wordId = computed(() => Number(route.params.wordId) || 0);
  const readingIndex = computed(() => Number(route.params.readingIndex) || 0);

  const initialUrl = `vocabulary/${wordId.value}/${readingIndex.value}/info`;
  const { data: wordData } = await useApiFetch<Word>(initialUrl, {
    key: `page-vocab-title-${wordId.value}-${readingIndex.value}`,
  });

  const mainReadingText = ref(wordData.value?.mainReading?.text ?? '');

  const onMainReadingTextChanged = (text: string) => {
    mainReadingText.value = text;
  };

  const title = computed(() => {
    if (mainReadingText.value) return stripRuby(mainReadingText.value);
    return 'Word';
  });

  const onReadingSelected = (newIndex: number) => {

    router.replace({ path: `/vocabulary/${wordId.value}/${newIndex}` });
  };

  useHead(() => {
    return {
      title: `${title.value}`,
      meta: [
        {
          name: 'description',
          content: `Detail for the word ${title.value}`
        }]
    };
  });

  if (import.meta.server) {
    defineOgImageComponent('VocabularyWordOgImage', {
      wordId: String(wordId.value),
      readingIndex: String(readingIndex.value),
    });
  }
</script>

<template>
  <VocabularyDetail
    :word-id="wordId"
    :reading-index="readingIndex"
    @reading-selected="onReadingSelected"
    @main-reading-text-changed="onMainReadingTextChanged"
  />
</template>
