<script setup lang="ts">
  import { useRoute, useRouter } from 'vue-router';
  import { stripRuby } from '~/utils/stripRuby';

  const route = useRoute();
  const router = useRouter();

  const wordId = ref(Number(route.params.wordId) || 0);
  const readingIndex = ref(Number(route.params.readingIndex) || 0);
  const title = ref('Word');

  const onReadingSelected = (newIndex: number) => {
    readingIndex.value = newIndex;
    router.replace(
      {
        path: `/vocabulary/${wordId.value}/${readingIndex.value}`,
      },
      false
    );
  };

  onMounted(() => {
    if (route.params.wordId) {
      wordId.value = Number(route.params.wordId);
    }
    if (route.params.readingIndex) {
      readingIndex.value = Number(route.params.readingIndex);
    }
  });

  useHead(() => {
    return {
      title: 'Word - ' + title.value,
      meta: [
        {
          name: 'description',
          content: `Detail for the word ${title.value}`
        }]
    };
  });

  const onMainReadingTextChanged = (newText: string) => {
    if (newText != undefined) {
      title.value = stripRuby(newText);
    }
  };
</script>

<template>
  <VocabularyDetail
    :word-id="wordId"
    :reading-index="readingIndex"
    @reading-selected="onReadingSelected"
    @main-reading-text-changed="onMainReadingTextChanged"
  />
</template>
