<script setup lang="ts">
  import type { CardLayoutBlock, DeckOccurrencesBlockOptions } from '~/types';
  import { deckOccurrencesDefaults, resolveOptions } from './cardBlockOptions';
  import { useCardContext } from './useCardContext';

  const props = defineProps<{ block: CardLayoutBlock; side: 'front' | 'back' }>();
  const opts = computed(() => resolveOptions<DeckOccurrencesBlockOptions>(deckOccurrencesDefaults, props.block.options));

  const { card, isPreview, sample } = useCardContext();
  const localiseTitle = useLocaliseTitle();

  const occExpanded = ref(false);
  const sectionOpen = ref(!opts.value.collapsed);
  watch(
    () => (card.value ? `${card.value.wordId}-${card.value.readingIndex}` : ''),
    () => {
      occExpanded.value = false;
      sectionOpen.value = !opts.value.collapsed;
    }
  );
  watch(
    () => opts.value.collapsed,
    (collapsed) => {
      sectionOpen.value = !collapsed;
    }
  );

  const occurrences = computed(() => (isPreview ? (sample?.deckOccurrences ?? []) : (card.value?.deckOccurrences ?? [])));
  const sourceDeckName = computed(() => (isPreview ? '' : (card.value?.sourceDeckName ?? '')));
  const visible = computed(() => occurrences.value.length > 0 || !!sourceDeckName.value);
</script>

<template>
  <div v-if="visible" class="mt-4 pt-3 border-t border-surface-200 dark:border-surface-700">
    <button
      v-if="opts.collapsed"
      type="button"
      class="flex items-center gap-1 text-xs text-surface-400 dark:text-surface-400 hover:text-surface-600 dark:hover:text-surface-300"
      @click.stop="sectionOpen = !sectionOpen"
    >
      <i class="pi text-[0.6rem]" :class="sectionOpen ? 'pi-chevron-down' : 'pi-chevron-right'" />
      Deck occurrences
    </button>
    <template v-if="sectionOpen">
      <div
        class="flex flex-wrap gap-x-3 gap-y-1 text-xs text-surface-400 dark:text-surface-400 overflow-hidden transition-[max-height] duration-200"
        :class="[occExpanded ? 'max-h-none' : 'max-h-[3.75rem]', { 'mt-1': opts.collapsed }]"
      >
        <template v-if="occurrences.length">
          <span v-for="occ in occurrences" :key="occ.deckId">
            ×{{ occ.occurrences }}
            <template v-if="occ.parentOriginalTitle"
              >{{
                localiseTitle({ originalTitle: occ.parentOriginalTitle, romajiTitle: occ.parentRomajiTitle, englishTitle: occ.parentEnglishTitle })
              }}
              - </template
            >{{ localiseTitle(occ) }}
          </span>
        </template>
        <span v-else-if="sourceDeckName">{{ sourceDeckName }}</span>
      </div>
      <button
        v-if="occurrences.length > 3"
        type="button"
        class="mt-1 text-xs text-primary-600 dark:text-primary-400 hover:underline"
        @click.stop="occExpanded = !occExpanded"
      >
        {{ occExpanded ? '- View less' : `+ View all ${occurrences.length}` }}
      </button>
    </template>
  </div>
</template>
