<script setup lang="ts">
  import type { Word } from '~/types';
  import Card from 'primevue/card';
  import Button from 'primevue/button';
  import { useJitenStore } from '~/stores/jitenStore';
  import VocabularyStatus from '~/components/VocabularyStatus.vue';

  const props = defineProps<{
    word: Word;
    isCompact: boolean;
    removable?: boolean;
    removing?: boolean;
  }>();

  const emit = defineEmits<{
    remove: [word: Word];
  }>();

  const convertToRuby = useConvertToRuby();
  const isCompact = ref(props.isCompact);

  const { resolvedGroups } = useDictionaryDefinitions(
    computed(() => props.word?.mainReading?.text),
    computed(() => props.word?.definitions),
  );

  const toggleCompact = () => {
    isCompact.value = !isCompact.value;
  };
</script>

<template>
  <Card>
    <template #title>
      <div class="flex justify-between">
        <div class="flex flex-row md:gap-4 flex-wrap">
          <router-link class="text-2xl" :to="`/vocabulary/${word.wordId}/${word.mainReading.readingIndex}`" lang="ja" v-html="convertToRuby(word.mainReading.text)" />
          <Button text @click="toggleCompact">{{ isCompact ? 'Expand' : 'Compact' }}</Button>
        </div>
        <div class="text-gray-500 dark:text-gray-300 text-sm text-right">
          <VocabularyStatus :word="word" />
          x{{ word.occurrences }} | Rank #{{ word.mainReading.frequencyRank.toLocaleString() }}
          <Button
            v-if="removable"
            icon="pi pi-trash"
            severity="danger"
            text
            size="small"
            :loading="removing"
            @click.stop="emit('remove', word)"
          />
        </div>
      </div>
    </template>
    <template #subtitle />
    <template #content>
      <VocabularyDictionaryDefinitions :resolved-groups="resolvedGroups" :is-compact="isCompact" :current-reading-index="word.mainReading.readingIndex" :readings="word.alternativeReadings" />
    </template>
  </Card>
</template>

<style scoped></style>
