<script setup lang="ts">
  import { type Deck, MediaType, SortOrder } from '~/types';
  import type { PaginatedResponse } from '~/types/types';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { isMediaCardStatId, richestPreviewDeck, type MediaCardStatId } from '~/utils/mediaCardStats';

  /** A stat id or a card section to outline on the preview. */
  const props = defineProps<{ highlight: string | null }>();
  /** Emits the stats and sections the previewed card renders, or null while no card is shown. */
  const emit = defineEmits<{ rendered: [parts: { stats: MediaCardStatId[]; sections: string[] } | null] }>();

  const PREVIEW_TYPES = [MediaType.Anime, MediaType.Novel, MediaType.VisualNovel, MediaType.Manga, MediaType.VideoGame, MediaType.YouTube].map((value) => ({
    value,
    label: getMediaTypeText(value),
  }));

  const { $api } = useNuxtApp();
  const selectedType = ref(MediaType.Anime);
  const fromWordPage = ref(false);
  const decks = ref<Partial<Record<MediaType, Deck | null>>>({});
  const failed = ref(new Set<MediaType>());

  async function load(type: MediaType) {
    if (type in decks.value) return;
    failed.value.delete(type);
    try {
      const response = await $api<PaginatedResponse<Deck[]>>('media-deck/get-media-decks', {
        query: { mediaType: type, sortBy: 'popularity', sortOrder: SortOrder.Descending },
      });
      decks.value = { ...decks.value, [type]: richestPreviewDeck(response.data) };
    } catch {
      failed.value = new Set(failed.value).add(type);
    }
  }

  onMounted(() => load(selectedType.value));
  watch(selectedType, load);

  const deck = computed(() => decks.value[selectedType.value]);
  const typeLabel = computed(() => (selectedType.value === MediaType.YouTube ? 'YouTube channel' : getMediaTypeText(selectedType.value).toLowerCase()));

  // The list endpoint only fills word occurrences for a word filter, so the word-page view supplies a stand-in count.
  const previewDeck = computed(() => {
    const d = deck.value;
    if (!d) return null;
    return fromWordPage.value ? { ...d, selectedWordOccurrences: Math.max(3, Math.round(d.wordCount / 5000)) } : d;
  });

  const previewEl = ref<HTMLElement | null>(null);

  function sync() {
    const root = previewEl.value;
    if (!root) return;
    const parts = [...root.querySelectorAll<HTMLElement>('[data-stat], [data-section]')];
    for (const el of parts) el.classList.toggle('stat-preview-highlight', (el.dataset.stat ?? el.dataset.section) === props.highlight);
    emit(
      'rendered',
      previewDeck.value
        ? {
            stats: parts.map((el) => el.dataset.stat).filter(isMediaCardStatId),
            sections: parts.map((el) => el.dataset.section).filter((id): id is string => !!id),
          }
        : null
    );
  }

  let observer: MutationObserver | undefined;
  onMounted(() => {
    if (!previewEl.value) return;
    observer = new MutationObserver(sync);
    observer.observe(previewEl.value, { childList: true, subtree: true });
    sync();
  });
  onBeforeUnmount(() => observer?.disconnect());
  watch(() => props.highlight, sync);
</script>

<template>
  <div class="flex flex-col gap-3">
    <div class="flex flex-wrap items-center justify-between gap-x-6 gap-y-2">
      <div class="-mx-1 max-w-full overflow-x-auto px-1 py-1">
        <SelectButton v-model="selectedType" :options="PREVIEW_TYPES" option-label="label" option-value="value" :allow-empty="false" aria-label="Preview as" />
      </div>
      <div class="flex items-center gap-2">
        <Checkbox v-model="fromWordPage" input-id="previewFromWordPage" :binary="true" />
        <label for="previewFromWordPage" class="cursor-pointer text-sm">As listed on a word page</label>
      </div>
    </div>

    <div ref="previewEl" class="card-preview">
      <div v-if="previewDeck" inert>
        <MediaDeckCard :deck="previewDeck" lazy-cover />
      </div>
      <div
        v-else-if="failed.has(selectedType)"
        class="flex flex-col items-start gap-2 rounded-lg border border-dashed border-surface-300 p-4 text-sm dark:border-surface-600"
      >
        <span>Couldn’t load an example {{ typeLabel }}.</span>
        <Button label="Try again" icon="pi pi-refresh" size="small" severity="secondary" outlined @click="load(selectedType)" />
      </div>
      <p v-else-if="deck === null" class="rounded-lg border border-dashed border-surface-300 p-4 text-sm dark:border-surface-600">
        There’s no {{ typeLabel }} on Jiten to preview yet. Pick another type.
      </p>
      <div v-else role="status" class="rounded-lg border border-surface-200 p-4 dark:border-surface-700">
        <span class="sr-only">Loading an example {{ typeLabel }}</span>
        <div class="flex gap-4" aria-hidden="true">
          <Skeleton width="8.5rem" height="12rem" class="shrink-0" />
          <div class="flex flex-1 flex-col gap-2">
            <Skeleton width="60%" height="1.5rem" />
            <Skeleton width="30%" height="0.9rem" />
            <Skeleton v-for="n in 4" :key="n" height="1rem" />
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
  .card-preview :deep(.stat-preview-highlight) {
    outline: 2px solid var(--p-primary-color);
    outline-offset: 1px;
  }
</style>
