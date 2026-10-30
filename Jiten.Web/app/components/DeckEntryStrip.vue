<script setup lang="ts">
  import { type Deck, type DeckDetails, DeckStatus } from '~/types';
  import type { EntryDialogField } from '~/composables/useMediaListSync';
  import { formatScore } from '~/utils/formatScore';

  const props = defineProps<{
    deck: Deck;
    /** From the deck page's payload; when absent the strip loads them itself. */
    details?: DeckDetails | null;
  }>();

  const draft = useDeckDetailsDraft(() => props.deck, props.details ?? null);
  const viewerDetails = draft.details;
  const openEntryDialog = useEntryDialog();

  const onList = computed(() => (props.deck.status ?? DeckStatus.None) !== DeckStatus.None || !!props.deck.listEntry);
  const noteLine = computed(() => viewerDetails.value?.note?.split('\n')[0] ?? '');

  function load() {
    if (onList.value && !viewerDetails.value && !draft.loading.value) void draft.load();
  }

  onMounted(load);
  watch(onList, load);

  const open = (field: EntryDialogField | null = null) => openEntryDialog({ ...props.deck, ...viewerDetails.value }, field);

  const itemClass =
    'inline-flex min-w-0 items-center gap-1.5 rounded px-2 py-1 text-sm hover:bg-gray-100 dark:hover:bg-gray-800 focus-visible:bg-gray-100 dark:focus-visible:bg-gray-800 transition-colors cursor-pointer';
</script>

<template>
  <div v-if="onList" class="mt-2 flex flex-wrap items-center gap-1 rounded-lg border border-gray-200 dark:border-gray-700 bg-surface-0 dark:bg-surface-900 px-2 py-1">
    <template v-if="viewerDetails">
      <button type="button" :class="itemClass" @click="open('score')">
        <template v-if="viewerDetails.score != null">
          <span class="text-gray-600 dark:text-gray-300">Your score</span>
          <span class="font-semibold tabular-nums">{{ formatScore(viewerDetails.score) }}</span>
        </template>
        <span v-else class="text-gray-600 dark:text-gray-300">Add a score</span>
      </button>
      <button
        type="button"
        :class="[itemClass, 'max-w-full sm:max-w-sm']"
        :aria-label="noteLine ? `Private note: ${viewerDetails.note}` : undefined"
        @click="open('note')"
      >
        <i class="pi pi-file-edit text-xs text-gray-500 dark:text-gray-400" aria-hidden="true" />
        <span v-if="noteLine" class="truncate">{{ noteLine }}</span>
        <span v-else class="text-gray-600 dark:text-gray-300">Add a private note</span>
      </button>
      <button v-if="viewerDetails.isPrivate" type="button" :class="[itemClass, 'text-gray-600 dark:text-gray-300']" @click="open()">
        <i class="pi pi-eye-slash text-xs" aria-hidden="true" />Hidden from profile
      </button>
      <button v-if="viewerDetails.isPinned" type="button" :class="[itemClass, 'text-gray-600 dark:text-gray-300']" @click="open()">
        <i class="pi pi-thumbtack text-xs" aria-hidden="true" />Pinned to profile
      </button>
    </template>
    <button type="button" :class="[itemClass, 'ml-auto font-medium text-primary-700 dark:text-primary-300']" @click="open()">
      <i class="pi pi-book text-xs" aria-hidden="true" />Your log
    </button>
  </div>
</template>
