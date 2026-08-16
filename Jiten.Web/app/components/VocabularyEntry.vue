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
    selectable?: boolean;
    selected?: boolean;
  }>();

  const emit = defineEmits<{
    remove: [word: Word];
    select: [word: Word];
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
      <!-- Click-anywhere is a mouse convenience only; the chevron stays the real control so the
           nested word link and status actions aren't trapped inside an interactive ancestor. -->
      <div class="flex justify-between cursor-pointer" @click="toggleCompact">
        <div class="flex flex-row md:gap-4 flex-wrap items-center">
          <Checkbox v-if="selectable" :model-value="selected" :binary="true" class="mr-2" @change="emit('select', word)" @click.stop />
          <router-link class="text-2xl" :to="`/vocabulary/${word.wordId}/${word.mainReading.readingIndex}`" lang="ja" @click.stop v-html="convertToRuby(word.mainReading.text)" />
          <Button
            text
            rounded
            size="small"
            severity="secondary"
            class="!text-surface-600 dark:!text-surface-300"
            :icon="isCompact ? 'pi pi-chevron-down' : 'pi pi-chevron-up'"
            :aria-label="isCompact ? 'Expand definitions' : 'Collapse definitions'"
            :aria-expanded="!isCompact"
            @click.stop="toggleCompact"
          />
        </div>
        <div class="text-gray-500 dark:text-gray-300 text-sm text-right">
          <span @click.stop>
            <VocabularyStatus :word="word" />
          </span>
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
