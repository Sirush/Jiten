<script setup lang="ts">
  import { DeckStatus, type SharedShelf } from '~/types';
  import Skeleton from 'primevue/skeleton';

  const props = defineProps<{
    username: string;
  }>();

  type SectionKey = 'shared' | 'theirs';

  const SHOW_MORE_ROWS = 2;

  const { data, status, error } = await useApiFetch<SharedShelf>(() => `user/profile/${props.username}/shared-shelf`, {
    lazy: true,
    server: false,
    watch: [() => props.username],
  });

  const shelfEl = ref<HTMLElement | null>(null);
  const tilesPerRow = ref(7);
  let resizeObserver: ResizeObserver | undefined;

  function measureTilesPerRow(el: HTMLElement) {
    const row = el.querySelector<HTMLElement>('[data-shelf-tiles]');
    const tile = row?.firstElementChild as HTMLElement | null | undefined;
    if (!row || !tile) return;
    const gap = parseFloat(getComputedStyle(row).columnGap) || 0;
    tilesPerRow.value = Math.max(1, Math.floor((row.clientWidth + gap) / (tile.offsetWidth + gap)));
  }

  watch(shelfEl, (el) => {
    resizeObserver?.disconnect();
    if (!el) return;
    resizeObserver = new ResizeObserver(() => measureTilesPerRow(el));
    resizeObserver.observe(el);
  });
  onBeforeUnmount(() => resizeObserver?.disconnect());

  const rows = ref<Record<SectionKey, number>>({ shared: 1, theirs: 1 });
  watch(
    () => props.username,
    () => (rows.value = { shared: 1, theirs: 1 })
  );

  const isEmpty = computed(() => !!data.value && data.value.sharedCount === 0 && data.value.theirsOnlyCount === 0);

  const sections = computed(() => {
    const shelf = data.value;
    if (!shelf) return [];
    const configs = [
      {
        key: 'shared' as SectionKey,
        heading: 'You both share',
        list: shelf.shared,
        count: shelf.sharedCount,
        empty: shelf.viewerHasList ? 'Nothing in common yet.' : "Set a status on the titles you've started to see what you have in common.",
        showCoverage: false,
      },
      {
        key: 'theirs' as SectionKey,
        heading: "You don't have on your list",
        list: shelf.theirsOnly,
        count: shelf.theirsOnlyCount,
        empty: `You've already started everything on ${props.username}'s list.`,
        showCoverage: shelf.theirsOnly.some((i) => i.deck.coverage > 0),
      },
    ];
    return configs.map(({ list, ...config }) => {
      const items = list.slice(0, rows.value[config.key] * tilesPerRow.value);
      return { ...config, items, loaded: list.length, remaining: list.length - items.length };
    });
  });
</script>

<template>
  <Card v-if="!isEmpty" :pt="{ body: { class: 'max-sm:!px-3' } }">
    <template #title>
      <div class="flex items-center gap-2">
        <Icon name="material-symbols:library-books" />
        Shared shelf
      </div>
    </template>
    <template #content>
      <div v-if="status === 'pending' && !data" class="flex flex-wrap gap-x-3 gap-y-4 justify-center sm:gap-x-4 sm:justify-start">
        <Skeleton v-for="i in 6" :key="i" width="8.5rem" height="12rem" />
      </div>

      <Message v-else-if="error" severity="error" :closable="false">Couldn't load the shared shelf. Refresh the page to try again.</Message>

      <div v-else-if="data" ref="shelfEl" class="flex flex-col gap-6">
        <section v-for="section in sections" :key="section.key" class="flex flex-col gap-3" :aria-labelledby="`shelf-${section.key}`">
          <div class="flex flex-wrap items-baseline gap-x-2 gap-y-1">
            <h3 :id="`shelf-${section.key}`" class="font-semibold">{{ section.heading }}</h3>
            <span class="text-sm tabular-nums text-surface-500 dark:text-surface-400">{{ section.count }}</span>
            <span v-if="section.showCoverage" class="text-xs text-surface-500 dark:text-surface-400 sm:ml-auto">
              <span class="rounded bg-primary px-1 py-0.5 font-bold text-primary-contrast">xx%</span>
              = your coverage, highest first
            </span>
          </div>

          <p v-if="section.count === 0" class="text-sm text-surface-500 dark:text-surface-400">{{ section.empty }}</p>

          <template v-else>
            <div data-shelf-tiles class="flex flex-wrap gap-x-3 gap-y-4 justify-center sm:gap-x-4 sm:justify-start">
              <MediaDeckCoverTile
                v-for="item in section.items"
                :key="item.deck.deckId"
                :deck="item.deck"
                :badge="section.showCoverage && item.deck.coverage > 0 ? `${Math.round(item.deck.coverage)}%` : undefined"
              >
                <span v-if="item.yourStatus === DeckStatus.Planning" class="text-xs text-surface-500 dark:text-surface-400">On your Planning list</span>
              </MediaDeckCoverTile>
            </div>

            <div class="flex flex-wrap items-center justify-center gap-3">
              <button
                v-if="section.remaining > 0"
                type="button"
                class="text-sm text-primary-600 dark:text-primary-400 hover:underline cursor-pointer"
                @click="rows[section.key] += SHOW_MORE_ROWS"
              >
                Show more ({{ section.remaining }})
              </button>
              <NuxtLink
                v-else-if="section.count > section.loaded"
                :to="`/profile/${username}/media`"
                class="text-sm text-primary-600 dark:text-primary-400 hover:underline"
              >
                {{ section.count - section.loaded }} more on {{ username }}'s media list
              </NuxtLink>
            </div>
          </template>
        </section>
      </div>
    </template>
  </Card>
</template>
