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
    /** Names the requested ranking ("Anime"); each row swaps it for "global" when its own rank fell back. */
    rankSourceLabel?: string;
    hideOccurrences?: boolean;
  }>();

  const emit = defineEmits<{
    remove: [word: Word];
    select: [word: Word];
  }>();

  const convertToRuby = useConvertToRuby();
  const isCompact = ref(props.isCompact);

  const { resolvedGroups } = useDictionaryDefinitions(
    computed(() => props.word?.mainReading?.text),
    computed(() => props.word?.definitions)
  );

  const toggleCompact = () => {
    isCompact.value = !isCompact.value;
  };

  const rankLabel = computed(() => rowRankLabel(props.word.mainReading, props.rankSourceLabel));
</script>

<template>
  <Card>
    <template #title>
      <div class="flex flex-wrap justify-between gap-x-3 gap-y-1 cursor-pointer" @click="toggleCompact">
        <div class="flex flex-row md:gap-4 flex-wrap items-center min-w-0 grow">
          <Checkbox v-if="selectable" :model-value="selected" :binary="true" class="mr-2" @change="emit('select', word)" @click.stop />
          <router-link
            class="leading-relaxed"
            :class="headwordSizeClass(word.mainReading.text, true)"
            :to="`/vocabulary/${word.wordId}/${word.mainReading.readingIndex}`"
            lang="ja"
            @click.stop
            v-html="convertToRuby(word.mainReading.text)"
          />
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
        <div class="text-gray-500 dark:text-gray-300 text-sm text-right shrink-0 ml-auto">
          <span @click.stop>
            <VocabularyStatus :word="word" />
          </span>
          <template v-if="!hideOccurrences">x{{ word.occurrences }} | </template>Rank #{{ rankLabel.rank }}
          <Tooltip v-if="rankLabel.source && rankLabel.hint" :content="rankLabel.hint">
            <span class="text-xs whitespace-nowrap cursor-help">in {{ rankLabel.source }}</span>
          </Tooltip>
          <span v-else-if="rankLabel.source" class="text-xs whitespace-nowrap">in {{ rankLabel.source }}</span>
          <Button v-if="removable" icon="pi pi-trash" severity="danger" text size="small" :loading="removing" @click.stop="emit('remove', word)" />
        </div>
      </div>
    </template>
    <template #subtitle />
    <template #content>
      <VocabularyDictionaryDefinitions
        :resolved-groups="resolvedGroups"
        :is-compact="isCompact"
        :current-reading-index="word.mainReading.readingIndex"
        :readings="word.alternativeReadings"
      />
    </template>
  </Card>
</template>

<style scoped></style>
