<script setup lang="ts">
  import { useApiFetch } from '~/composables/useApiFetch';
  import type { Tag } from '~/types';
  import { getAllGenres } from '~/utils/genreMapper';
  import type { TagState } from '~/components/TriStateTag.vue';
  import type { TagCloudEntry } from '~/components/MediaListTagCloud.vue';

  const props = withDefaults(
    defineProps<{
      genreCounts?: Record<number, number>;
      tagCounts?: Record<number, number>;
      resultCount?: number | null;
    }>(),
    { genreCounts: () => ({}), tagCounts: () => ({}), resultCount: null }
  );

  const includeGenres = defineModel<number[]>('includeGenres', { required: true });
  const excludeGenres = defineModel<number[]>('excludeGenres', { required: true });
  const includeTags = defineModel<number[]>('includeTags', { required: true });
  const excludeTags = defineModel<number[]>('excludeTags', { required: true });

  const popover = ref();
  const drawerOpen = ref(false);
  const isMobile = ref(false);
  let breakpoint: MediaQueryList | null = null;
  const syncBreakpoint = (event: MediaQueryListEvent | MediaQueryList) => {
    isMobile.value = !event.matches;
  };

  onMounted(() => {
    breakpoint = window.matchMedia('(min-width: 768px)');
    syncBreakpoint(breakpoint);
    breakpoint.addEventListener('change', syncBreakpoint);
  });
  onBeforeUnmount(() => breakpoint?.removeEventListener('change', syncBreakpoint));

  const { data: availableTags } = useApiFetch<Tag[]>('media-deck/tags', { server: false, lazy: true });

  const genreSearch = ref('');
  const tagSearch = ref('');

  const matches = (text: string, query: string) => !query.trim() || text.toLowerCase().includes(query.trim().toLowerCase());

  const genreState = (id: number): TagState => (includeGenres.value.includes(id) ? 'include' : excludeGenres.value.includes(id) ? 'exclude' : 'neutral');
  const tagState = (id: number): TagState => (includeTags.value.includes(id) ? 'include' : excludeTags.value.includes(id) ? 'exclude' : 'neutral');

  const withCount = (label: string, count: number | undefined) => (count == null ? label : `${label} (${count.toLocaleString()})`);

  // Only values some matching request carries are offered; an active selection stays visible so it can be cleared.
  const genreEntries = computed<TagCloudEntry[]>(() =>
    getAllGenres()
      .filter((genre) => (props.genreCounts[genre.value] ?? 0) > 0 || genreState(genre.value) !== 'neutral')
      .filter((genre) => matches(genre.label, genreSearch.value))
      .map((genre) => ({ id: genre.value, label: withCount(genre.label, props.genreCounts[genre.value]), state: genreState(genre.value) }))
  );

  const tagEntries = computed<TagCloudEntry[]>(() =>
    (availableTags.value ?? [])
      .filter((tag) => (props.tagCounts[tag.tagId] ?? 0) > 0 || tagState(tag.tagId) !== 'neutral')
      .filter((tag) => matches(tag.name, tagSearch.value))
      .sort((a, b) => (props.tagCounts[b.tagId] ?? 0) - (props.tagCounts[a.tagId] ?? 0) || a.name.localeCompare(b.name))
      .map((tag) => ({ id: tag.tagId, label: withCount(tag.name, props.tagCounts[tag.tagId]), state: tagState(tag.tagId) }))
  );

  const selectedCount = computed(() => includeGenres.value.length + excludeGenres.value.length + includeTags.value.length + excludeTags.value.length);
  const hasAnyValues = computed(() => genreEntries.value.length > 0 || tagEntries.value.length > 0 || !!genreSearch.value || !!tagSearch.value);

  const setState = (include: Ref<number[]>, exclude: Ref<number[]>, id: number, state: TagState) => {
    include.value = state === 'include' ? [...include.value.filter((v) => v !== id), id] : include.value.filter((v) => v !== id);
    exclude.value = state === 'exclude' ? [...exclude.value.filter((v) => v !== id), id] : exclude.value.filter((v) => v !== id);
  };
  const setGenreState = (id: number, state: TagState) => setState(includeGenres, excludeGenres, id, state);
  const setTagState = (id: number, state: TagState) => setState(includeTags, excludeTags, id, state);

  const clearAll = () => {
    includeGenres.value = [];
    excludeGenres.value = [];
    includeTags.value = [];
    excludeTags.value = [];
    genreSearch.value = '';
    tagSearch.value = '';
  };

  const resultLabel = computed(() =>
    props.resultCount == null ? 'Show results' : `Show ${props.resultCount.toLocaleString()} ${props.resultCount === 1 ? 'request' : 'requests'}`
  );

  const toggle = (event: Event) => {
    if (isMobile.value) {
      drawerOpen.value = true;
      return;
    }
    popover.value?.toggle(event);
  };
</script>

<template>
  <Button
    severity="secondary"
    outlined
    :class="[
      'w-full justify-between! font-normal! bg-(--p-select-background)! border-(--p-select-border-color)! hover:border-(--p-select-hover-border-color)!',
      selectedCount > 0 ? 'text-purple-700! dark:text-purple-300!' : 'text-(--p-select-color)!',
    ]"
    aria-haspopup="dialog"
    aria-label="Genres and tags"
    @click="toggle($event)"
  >
    <span class="truncate">{{ selectedCount > 0 ? `Genres & tags (${selectedCount})` : 'All genres & tags' }}</span>
    <Icon name="material-symbols:keyboard-arrow-down-rounded" size="1.25em" class="shrink-0 text-surface-400" />
  </Button>

  <Popover v-if="!isMobile" ref="popover" class="w-[min(40rem,calc(100vw_-_2rem))]">
    <div class="flex max-h-[min(36rem,calc(100dvh-13rem))] flex-col gap-2">
      <template v-if="hasAnyValues">
        <div class="shrink-0 px-1 text-[11px] leading-none font-semibold tracking-wider text-surface-500 uppercase">Genres</div>
        <MediaListTagCloud
          v-model:search="genreSearch"
          class="shrink-0 [&_.p-tag]:px-1.5 [&_.p-tag]:py-0.5 [&_.p-tag]:text-xs"
          :entries="genreEntries"
          :show-search="false"
          empty-label="No request has a genre yet."
          @set="setGenreState"
        />

        <div class="mt-1 shrink-0 px-1 text-[11px] leading-none font-semibold tracking-wider text-surface-500 uppercase">Tags</div>
        <MediaListTagCloud
          v-model:search="tagSearch"
          class="min-h-0 flex-1 [&_.p-tag]:px-1.5 [&_.p-tag]:py-0.5 [&_.p-tag]:text-xs"
          :entries="tagEntries"
          :placeholder="`Search ${tagEntries.length} tags...`"
          empty-label="No tag matches that search."
          list-class="min-h-24 max-h-72 overflow-y-auto rounded border border-surface-200 dark:border-surface-700"
          @set="setTagState"
        />
      </template>
      <p v-else class="px-1 py-2 text-sm text-muted-color">None of these requests have genres or tags yet.</p>

      <div class="flex shrink-0 items-center border-t border-surface-200 pt-2 dark:border-surface-700">
        <span class="text-sm text-surface-500">Click once to include, twice to exclude.</span>
        <Button v-if="selectedCount > 0" severity="danger" text size="small" class="ml-auto" @click="clearAll">Clear</Button>
      </div>
    </div>
  </Popover>

  <Drawer
    v-else
    v-model:visible="drawerOpen"
    position="bottom"
    block-scroll
    :pt="{ root: { class: 'h-[85dvh]! rounded-t-xl overflow-hidden' } }"
    aria-label="Genres and tags"
  >
    <template #container="{ closeCallback }">
      <div class="flex h-full min-h-0 flex-col">
        <div class="flex shrink-0 justify-center pt-2 pb-1">
          <div class="h-1 w-9 rounded-full bg-surface-300 dark:bg-surface-600" />
        </div>
        <div class="flex shrink-0 items-baseline gap-2 px-4 pb-2">
          <span class="text-lg font-bold text-surface-900 dark:text-surface-0">Genres & Tags</span>
          <span class="text-sm text-surface-500">{{ selectedCount }} selected</span>
          <Button v-if="selectedCount > 0" severity="danger" text size="small" class="ml-auto" @click="clearAll">Clear</Button>
        </div>

        <div class="flex min-h-0 flex-1 flex-col gap-3 overflow-y-auto px-4 pb-3 [scrollbar-width:thin]">
          <template v-if="hasAnyValues">
            <div>
              <div class="mb-1.5 text-[11px] font-semibold tracking-wider text-surface-500 uppercase">Genres</div>
              <MediaListTagCloud v-model:search="genreSearch" :entries="genreEntries" :show-search="false" empty-label="No request has a genre yet." @set="setGenreState" />
            </div>
            <div>
              <div class="mb-1.5 text-[11px] font-semibold tracking-wider text-surface-500 uppercase">Tags</div>
              <MediaListTagCloud
                v-model:search="tagSearch"
                :entries="tagEntries"
                :placeholder="`Search ${tagEntries.length} tags...`"
                empty-label="No tag matches that search."
                @set="setTagState"
              />
            </div>
          </template>
          <p v-else class="py-2 text-sm text-muted-color">None of these requests have genres or tags yet.</p>
        </div>

        <div class="shrink-0 border-t border-surface-200 px-4 pt-3 pb-7 dark:border-surface-700">
          <Button class="w-full justify-center" @click="closeCallback">{{ resultLabel }}</Button>
        </div>
      </div>
    </template>
  </Drawer>
</template>
