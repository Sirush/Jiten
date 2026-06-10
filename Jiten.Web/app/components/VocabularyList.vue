<script setup lang="ts">
  import type { Word } from '~/types/types';
  import type { AsyncDataRequestStatus } from '#app';
  import { LazyHydrateVocabularyEntry } from '~/utils/lazyHydratedComponents';

  const props = defineProps<{
    words: Word[];
    status: AsyncDataRequestStatus;
    error?: Error | string | null;
    emptyMessage?: string;
    skeletonCount?: number;
    removable?: boolean;
    removingKey?: string | null;
  }>();

  const emit = defineEmits<{
    remove: [word: Word];
  }>();
</script>

<template>
  <div v-if="status === 'pending'" class="flex flex-col gap-2">
    <Card v-for="i in (skeletonCount ?? 10)" :key="i" class="p-2">
      <template #content>
        <Skeleton width="100%" height="50px" />
      </template>
    </Card>
  </div>

  <div v-else-if="error">
    <slot name="error" :error="error">
      <Message severity="error">Failed to load vocabulary</Message>
    </slot>
  </div>

  <div v-else-if="words.length === 0" class="flex flex-col items-center justify-center py-16">
    <i class="pi pi-book text-4xl text-primary-500 mb-4" />
    <p class="text-lg font-medium text-primary-700 dark:text-primary-300">No vocabulary found</p>
    <p v-if="emptyMessage" class="text-sm text-surface-400">{{ emptyMessage }}</p>
  </div>

  <!-- LazyHydrate defers per-entry hydration until scrolled into view;
       content-visibility skips layout/paint for offscreen entries. The first
       viewport-worth of entries renders normally so the page paints at its real
       size immediately (no first-frame shift from the intrinsic-size estimate). -->
  <div v-else class="flex flex-col gap-2">
    <LazyHydrateVocabularyEntry
      v-for="(word, index) in words"
      :key="`${word.wordId}-${word.mainReading.readingIndex}`"
      :word="word"
      :is-compact="true"
      :removable="removable"
      :removing="removingKey === `${word.wordId}-${word.mainReading.readingIndex}`"
      :class="index >= 8 ? '[content-visibility:auto] [contain-intrinsic-size:auto_8rem]' : ''"
      @remove="emit('remove', word)"
    />
  </div>
</template>
