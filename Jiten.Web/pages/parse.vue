<script setup lang="ts">
  import { useApiFetch } from '~/composables/useApiFetch';
  import type { DeckWord } from '~/types';
  import WordSearch from '~/components/WordSearch.vue';

  const route = useRoute();

  const url = computed(() => `vocabulary/parse`);

  const searchContent = ref(route.query.text || '');


  useHead(() => {
    return {
      title: 'Search ' + searchContent.value,
    };
  });

  const {
    data: response,
    status,
    error,
  } = await useApiFetch<DeckWord[]>(url.value, { query: { text: searchContent }, watch: [searchContent] });

  watch(
    () => route.query.text,
    (newText) => {
      if (newText) {
        response.value = [];
        searchContent.value = newText;
        selectedWord.value = response.value?.find((word) => word.wordId != 0);
      }
    }
  );

  const words = computed<DeckWord[]>(() => response.value || []);
  const selectedWord = ref<DeckWord | undefined>();

  watch(
    () => status.value,
    (newStatus) => {
      if (!selectedWord.value) {
        selectedWord.value = words.value.find((word) => word.wordId != 0);
      } else if (newStatus === 'error' || newStatus === 'idle') {
        selectedWord.value = undefined;
      }
    },
    { immediate: true }
  );

  const handleWordClick = (word: DeckWord) => {
    if (word.wordId !== 0) {
      selectedWord.value = word;
    }
  }

</script>

<template>
  <div>
    <WordSearch />
    <span v-for="word in words" :key="word.wordId" class="pr-1.5 font-noto-sans">
      <span
        v-if="word.wordId != 0"
        class="text-purple-600 text-lg underline underline-offset-4 cursor-pointer hover:font-bold"
        @click="handleWordClick(word)"
      >
        {{ word.originalText }}
      </span>
      <span v-else>{{ word.originalText }}</span>
    </span>

    <div v-if="selectedWord">
      <Transition name="fade" mode="out-in">
        <VocabularyDetail
          :key="`${selectedWord.wordId}-${selectedWord.readingIndex}`"
          :word-id="selectedWord.wordId"
          :reading-index="selectedWord.readingIndex"
        />
      </Transition>
    </div>
  </div>
</template>

<style scoped></style>
