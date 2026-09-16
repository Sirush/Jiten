<script setup lang="ts">
  import Drawer from 'primevue/drawer';
  import WatchWordPanelContent from '~/components/WatchWordPanelContent.vue';
  import type { FsrsRating, Word } from '~/types';

  const props = defineProps<{
    word: Word | null;
    /** Conjugation chain of the clicked surface form, innermost first */
    conjugation: string[];
    loading: boolean;
    grading: boolean;
    /** The grade this word received moments ago, while its cooldown lasts. */
    lastGrade: FsrsRating | null;
    /** Sentence to mine, with the surface form wrapped in ** markers; empty when the line is unavailable */
    sentence: string;
    sentenceContext: number;
    canExpandSentence: boolean;
    sentenceMined: boolean;
    mining: boolean;
  }>();

  const emit = defineEmits<{
    close: [];
    grade: [rating: FsrsRating];
    changed: [];
    mine: [];
    'update:sentenceContext': [value: number];
  }>();

  // Desktop renders in place beside the transcript; phones get a half-height bottom drawer over the pinned player
  const isDesktop = ref(false);
  onMounted(() => {
    const query = window.matchMedia('(min-width: 1024px)');
    const update = () => (isDesktop.value = query.matches);
    update();
    query.addEventListener('change', update);
    onBeforeUnmount(() => query.removeEventListener('change', update));
  });
  const drawerOpen = ref(true);
  watch(drawerOpen, (open) => {
    if (!open) emit('close');
  });
</script>

<template>
  <aside
    v-if="isDesktop"
    class="z-40 flex flex-col rounded-xl border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 shadow-xl text-sm absolute top-1/2 -translate-y-1/2 max-h-[min(36rem,calc(100vh-2rem))] w-[22rem] left-[min(calc(100%+1.5rem),calc(50vw+5rem))]"
    aria-label="Word"
  >
    <WatchWordPanelContent
      v-bind="props"
      @close="emit('close')"
      @grade="emit('grade', $event)"
      @changed="emit('changed')"
      @mine="emit('mine')"
      @update:sentence-context="emit('update:sentenceContext', $event)"
    />
  </aside>
  <Drawer
    v-else
    v-model:visible="drawerOpen"
    position="bottom"
    block-scroll
    :pt="{ root: { class: 'h-[50dvh]! rounded-t-xl overflow-hidden shadow-2xl' }, mask: { class: 'bg-transparent!' } }"
    aria-label="Word"
  >
    <template #container>
      <div class="flex h-full min-h-0 flex-col">
        <div class="flex shrink-0 justify-center pt-2">
          <div class="h-1 w-9 rounded-full bg-surface-300 dark:bg-surface-600" />
        </div>
        <WatchWordPanelContent
          v-bind="props"
          @close="drawerOpen = false"
          @grade="emit('grade', $event)"
          @changed="emit('changed')"
          @mine="emit('mine')"
          @update:sentence-context="emit('update:sentenceContext', $event)"
        />
      </div>
    </template>
  </Drawer>
</template>
