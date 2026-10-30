<script setup lang="ts">
  import type { MediaListDeck } from '~/types';
  import { formatScore } from '~/utils/formatScore';
  import { isMainEntry } from '~/utils/displayDeck';

  const props = defineProps<{
    deck: MediaListDeck;
    rowClass: string;
  }>();

  const emit = defineEmits<{ openScore: []; close: [] }>();

  const deckDetails = useDeckDetails();
  const localiseTitle = useLocaliseTitle();

  function toggleHidden() {
    emit('close');
    void deckDetails.toggleHidden(props.deck);
  }

  function togglePinned() {
    emit('close');
    void deckDetails.togglePinned(props.deck, localiseTitle(props.deck), !props.deck.isPinned);
  }
</script>

<template>
  <template v-if="isMainEntry(deck)">
    <button type="button" :class="[rowClass, 'justify-between']" @click="emit('openScore')">
      <span class="flex items-center gap-2">
        <i class="pi pi-sort-numeric-up text-xs" aria-hidden="true" />
        Score
      </span>
      <span class="flex items-center gap-2">
        <span class="tabular-nums" :class="deck.score != null ? 'font-semibold' : 'text-gray-500 dark:text-gray-400'">
          {{ deck.score != null ? formatScore(deck.score) : 'Add' }}
        </span>
        <i class="pi pi-chevron-right text-[10px]" aria-hidden="true" />
      </span>
    </button>
    <button type="button" role="menuitemcheckbox" :aria-checked="!!deck.isPrivate" :class="[rowClass, 'justify-between']" @click="toggleHidden">
      <span class="flex items-center gap-2">
        <i class="pi pi-eye-slash text-xs" aria-hidden="true" />
        Hide from profile
      </span>
      <i v-if="deck.isPrivate" class="pi pi-check text-xs" aria-hidden="true" />
    </button>
  </template>
  <button
    v-if="deck.isPinned || !deck.isPrivate"
    type="button"
    role="menuitemcheckbox"
    :aria-checked="!!deck.isPinned"
    :class="[rowClass, 'justify-between']"
    @click="togglePinned"
  >
    <span class="flex items-center gap-2">
      <i class="pi pi-thumbtack text-xs" aria-hidden="true" />
      Pin to profile
    </span>
    <i v-if="deck.isPinned" class="pi pi-check text-xs" aria-hidden="true" />
  </button>
</template>
