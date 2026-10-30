<script setup lang="ts">
  import { DeckStatus, type SharedShelf } from '~/types';

  const { handle, apiPath } = useProfileContext();

  type SectionKey = 'shared' | 'theirs';

  const SHOW_MORE_ROWS = 2;

  const { data, status, error } = await useApiFetch<SharedShelf>(() => apiPath('shared-shelf'), {
    lazy: true,
    server: false,
  });

  const shelfEl = ref<HTMLElement | null>(null);
  const tilesPerRow = ref(7);
  let resizeObserver: ResizeObserver | undefined;

  function measureTilesPerRow(el: HTMLElement) {
    const row = el.querySelector<HTMLElement>('[data-shelf-tiles]');
    if (!row) return;
    tilesPerRow.value = Math.max(1, getComputedStyle(row).gridTemplateColumns.split(' ').length);
  }

  watch(shelfEl, (el) => {
    resizeObserver?.disconnect();
    if (!el) return;
    resizeObserver = new ResizeObserver(() => measureTilesPerRow(el));
    resizeObserver.observe(el);
  });
  onBeforeUnmount(() => resizeObserver?.disconnect());

  const rows = ref<Record<SectionKey, number>>({ shared: 1, theirs: 1 });

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
        showCoverage: shelf.shared.some((i) => i.deck.coverage > 0),
      },
      {
        key: 'theirs' as SectionKey,
        heading: "You don't have on your list",
        list: shelf.theirsOnly,
        count: shelf.theirsOnlyCount,
        empty: `You've already started everything on ${handle.value}'s list.`,
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
  <Card v-if="!isEmpty">
    <template #title>
      <h2 class="text-base font-semibold">Shared shelf</h2>
    </template>
    <template #content>
      <div v-if="status === 'pending' && !data" class="grid grid-cols-3 gap-3 sm:grid-cols-6 sm:gap-4">
        <Skeleton v-for="i in 6" :key="i" class="!aspect-[2/3] !h-auto" />
      </div>

      <Message v-else-if="error" severity="error" :closable="false">Couldn't load the shared shelf. Refresh the page to try again.</Message>

      <div v-else-if="data" ref="shelfEl" class="flex flex-col gap-6">
        <section v-for="section in sections" :key="section.key" class="flex flex-col gap-3" :aria-labelledby="`shelf-${section.key}`">
          <div class="flex items-baseline gap-2">
            <h3 :id="`shelf-${section.key}`" class="text-sm font-semibold">{{ section.heading }}</h3>
            <span class="text-sm tabular-nums text-muted-color">{{ section.count }}</span>
          </div>

          <p v-if="section.count === 0" class="text-sm text-muted-color">{{ section.empty }}</p>

          <template v-else>
            <div data-shelf-tiles class="grid grid-cols-3 gap-3 sm:grid-cols-6 sm:gap-4">
              <ProfileDeckTile v-for="item in section.items" :key="item.deck.deckId" :deck="item.deck" :show-coverage="section.showCoverage">
                <span v-if="item.yourStatus === DeckStatus.Planning" class="text-xs text-muted-color">On your Planning list</span>
              </ProfileDeckTile>
            </div>

            <div v-if="section.remaining > 0 || section.count > section.loaded" class="flex justify-center">
              <button
                v-if="section.remaining > 0"
                type="button"
                class="cursor-pointer text-sm !text-primary-600 hover:underline dark:!text-primary-400"
                @click="rows[section.key] += SHOW_MORE_ROWS"
              >
                Show more ({{ section.remaining }})
              </button>
              <NuxtLink v-else :to="profilePath(handle, 'library')" class="text-sm !text-primary-600 hover:underline dark:!text-primary-400">
                {{ section.count - section.loaded }} more in {{ handle }}'s library
              </NuxtLink>
            </div>
          </template>
        </section>
      </div>
    </template>
  </Card>
</template>
