<script setup lang="ts">
  import { useJitenStore } from '~/stores/jitenStore';
  import { useAuthStore } from '~/stores/authStore';
  import { useTouchReorderMulti, type ReorderPoint } from '~/composables/useTouchReorderMulti';
  import {
    DEFAULT_MEDIA_CARD_COLUMNS,
    MEDIA_CARD_STAT_IDS,
    MEDIA_CARD_STATS,
    isDefaultMediaCardStatColumns,
    type MediaCardStatColumns,
    type MediaCardStatId,
  } from '~/utils/mediaCardStats';
  import {
    MEDIA_CARD_SECTION_IDS,
    MEDIA_CARD_SECTION_LABELS,
    copySectionLayout,
    type MediaCardSectionId,
    type MediaCardSectionLayout,
    type MediaCardSectionZone,
  } from '~/utils/mediaCardSections';

  const HIDDEN = 'hidden';
  type Reorder = ReturnType<typeof useTouchReorderMulti>;
  type Move = { zone: number; index: number } | 'hide' | null;

  const store = useJitenStore();
  const auth = useAuthStore();

  const rootEl = ref<HTMLElement | null>(null);
  const announcement = ref('');
  const hovered = ref<string | null>(null);
  const rendered = ref<{ stats: MediaCardStatId[]; sections: string[] } | null>(null);

  async function focusChip(id: string) {
    await nextTick();
    rootEl.value?.querySelector<HTMLElement>(`[data-chip-id="${id}"]`)?.focus();
  }

  const isSource = (r: Reorder, list: string, index: number) => {
    const from = r.fromPoint.value;
    return r.isDragging.value && from?.list === list && from.index === index;
  };
  const isDropTarget = (r: Reorder, list: string) => r.isDragging.value && r.dropList.value === list;

  function dropMarker(r: Reorder, list: string, length: number, index: number): 'before' | 'after' | null {
    if (!isDropTarget(r, list) || isSource(r, list, index)) return null;
    const others = Array.from({ length }, (_, i) => i).filter((i) => !isSource(r, list, i));
    const at = r.dropIndex.value!;
    if (others[at] === index) return 'before';
    if (at === others.length && others[others.length - 1] === index) return 'after';
    return null;
  }

  function keyMove(key: string, zone: number, index: number, lengths: number[]): Move {
    const last = lengths[zone]! - 1;
    switch (key) {
      case 'ArrowUp':
        return index > 0 ? { zone, index: index - 1 } : null;
      case 'ArrowDown':
        return index < last ? { zone, index: index + 1 } : null;
      case 'ArrowLeft':
        return zone > 0 ? { zone: zone - 1, index: Math.min(index, lengths[zone - 1]!) } : null;
      case 'ArrowRight':
        return zone < lengths.length - 1 ? { zone: zone + 1, index: Math.min(index, lengths[zone + 1]!) } : null;
      case 'Home':
        return { zone, index: 0 };
      case 'End':
        return { zone, index: last };
      case 'Delete':
      case 'Backspace':
        return 'hide';
      default:
        return null;
    }
  }

  const HANDLED_KEYS = new Set(['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'Home', 'End', 'Delete', 'Backspace']);

  const statLabel = (id: MediaCardStatId) => MEDIA_CARD_STATS[id].label;

  // The rating flag hides it on every card, so while it's set the board shows the rating as hidden.
  const columns = computed<MediaCardStatColumns>(() => {
    const raw = store.mediaCardStatColumns ?? DEFAULT_MEDIA_CARD_COLUMNS;
    return store.hideExternalRating ? raw.map((column) => column.filter((id) => id !== 'externalRating')) : raw;
  });
  const hiddenStats = computed(() => MEDIA_CARD_STAT_IDS.filter((id) => !columns.value.some((column) => column.includes(id))));

  // Storing null for the built-in layout keeps uncustomised profiles on the default if it ever changes.
  const setColumns = (next: MediaCardStatColumns) => {
    store.mediaCardStatColumns = isDefaultMediaCardStatColumns(next) ? null : next;
  };
  const withoutStat = (id: MediaCardStatId) => columns.value.map((column) => column.filter((s) => s !== id));

  function placeStat(id: MediaCardStatId, column: number, index: number) {
    const next = withoutStat(id);
    next[column]!.splice(Math.min(index, next[column]!.length), 0, id);
    if (id === 'externalRating') store.hideExternalRating = false;
    setColumns(next);
  }
  // The rating flag also reaches episode and volume cards, which ignore the columns, so hiding it here must set it too.
  function hideStat(id: MediaCardStatId) {
    if (id === 'externalRating') store.hideExternalRating = true;
    setColumns(withoutStat(id));
  }

  const statColumnEls: (HTMLElement | null)[] = [];
  const setStatColumnEl = (column: number, el: unknown) => {
    statColumnEls[column] = el as HTMLElement | null;
  };
  const hiddenStatsEl = ref<HTMLElement | null>(null);

  const statAt = (point: ReorderPoint) => (point.list === HIDDEN ? hiddenStats.value : columns.value[Number(point.list)])?.[point.index];

  const statReorder = useTouchReorderMulti({
    getLists: () => [
      ...columns.value.map((_, c) => ({ name: String(c), el: statColumnEls[c] ?? null })),
      { name: HIDDEN, el: hiddenStatsEl.value, layout: 'wrap' as const },
    ],
    onReorder: (from, to) => {
      const id = statAt(from);
      if (!id) return;
      if (to.list === HIDDEN) hideStat(id);
      else placeStat(id, Number(to.list), to.index);
    },
  });

  const statMissing = (id: MediaCardStatId) => rendered.value !== null && !rendered.value.stats.includes(id);

  function announceStat(id: MediaCardStatId) {
    const column = columns.value.findIndex((c) => c.includes(id));
    const list = columns.value[column]!;
    announcement.value = `${statLabel(id)}, column ${column + 1}, position ${list.indexOf(id) + 1} of ${list.length}`;
  }

  function onStatKeydown(event: KeyboardEvent, id: MediaCardStatId, column: number, index: number) {
    if (!HANDLED_KEYS.has(event.key)) return;
    event.preventDefault();
    const move = keyMove(
      event.key,
      column,
      index,
      columns.value.map((c) => c.length)
    );
    if (move === 'hide') {
      hideStat(id);
      announcement.value = `${statLabel(id)} hidden`;
      const rest = columns.value[column]!;
      focusChip(rest[Math.min(index, rest.length - 1)] ?? id);
    } else if (move && (move.zone !== column || move.index !== index)) {
      placeStat(id, move.zone, move.index);
      announceStat(id);
      focusChip(id);
    }
  }

  function showStat(id: MediaCardStatId) {
    if (statReorder.justDragged.value) return;
    const lengths = columns.value.map((column) => column.length);
    const shortest = lengths.indexOf(Math.min(...lengths));
    placeStat(id, shortest, lengths[shortest]!);
    announceStat(id);
    focusChip(id);
  }

  const ZONES: { key: MediaCardSectionZone; label: string }[] = [
    { key: 'top', label: 'Under the stats' },
    { key: 'bottom', label: 'At the bottom' },
  ];

  const SECTION_HIDE_KEYS = {
    description: 'hideDescriptions',
    genres: 'hideGenres',
    tags: 'hideTags',
    relations: 'hideRelations',
  } as const satisfies Record<MediaCardSectionId, keyof typeof store>;

  const isSectionHidden = (id: MediaCardSectionId) => store[SECTION_HIDE_KEYS[id]];
  const setSectionHidden = (id: MediaCardSectionId, hidden: boolean) => {
    store[SECTION_HIDE_KEYS[id]] = hidden;
  };

  const zones = computed(() => ZONES.map((zone) => store.mediaCardSectionLayout[zone.key].filter((id) => !isSectionHidden(id))));
  const hiddenSections = computed(() => MEDIA_CARD_SECTION_IDS.filter(isSectionHidden));

  /** Inserts relative to the visible sections; hidden ones keep their stored place so showing them again restores it. */
  function placeSection(id: MediaCardSectionId, zone: number, index: number) {
    const layout: MediaCardSectionLayout = copySectionLayout(store.mediaCardSectionLayout);
    layout.top = layout.top.filter((s) => s !== id);
    layout.bottom = layout.bottom.filter((s) => s !== id);
    const key = ZONES[zone]!.key;
    const before = zones.value[zone]!.filter((s) => s !== id)[index];
    const at = before ? layout[key].indexOf(before) : layout[key].length;
    layout[key].splice(at, 0, id);
    store.mediaCardSectionLayout = layout;
    setSectionHidden(id, false);
  }

  const zoneEls: (HTMLElement | null)[] = [];
  const setZoneEl = (zone: number, el: unknown) => {
    zoneEls[zone] = el as HTMLElement | null;
  };
  const hiddenSectionsEl = ref<HTMLElement | null>(null);

  const sectionAt = (point: ReorderPoint) => (point.list === HIDDEN ? hiddenSections.value : zones.value[Number(point.list)])?.[point.index];

  const sectionReorder = useTouchReorderMulti({
    getLists: () => [
      ...ZONES.map((_, z) => ({ name: String(z), el: zoneEls[z] ?? null })),
      { name: HIDDEN, el: hiddenSectionsEl.value, layout: 'wrap' as const },
    ],
    onReorder: (from, to) => {
      const id = sectionAt(from);
      if (!id) return;
      if (to.list === HIDDEN) setSectionHidden(id, true);
      else placeSection(id, Number(to.list), to.index);
    },
  });

  const sectionMissing = (id: MediaCardSectionId) => rendered.value !== null && !rendered.value.sections.includes(id);

  function announceSection(id: MediaCardSectionId) {
    const zone = zones.value.findIndex((z) => z.includes(id));
    const list = zones.value[zone]!;
    announcement.value = `${MEDIA_CARD_SECTION_LABELS[id]}, ${ZONES[zone]!.label.toLowerCase()}, position ${list.indexOf(id) + 1} of ${list.length}`;
  }

  function onSectionKeydown(event: KeyboardEvent, id: MediaCardSectionId, zone: number, index: number) {
    if (!HANDLED_KEYS.has(event.key)) return;
    event.preventDefault();
    const move = keyMove(
      event.key,
      zone,
      index,
      zones.value.map((z) => z.length)
    );
    if (move === 'hide') {
      setSectionHidden(id, true);
      announcement.value = `${MEDIA_CARD_SECTION_LABELS[id]} hidden`;
      const rest = zones.value[zone]!;
      focusChip(rest[Math.min(index, rest.length - 1)] ?? id);
    } else if (move && (move.zone !== zone || move.index !== index)) {
      placeSection(id, move.zone, move.index);
      announceSection(id);
      focusChip(id);
    }
  }

  function showSection(id: MediaCardSectionId) {
    if (sectionReorder.justDragged.value) return;
    setSectionHidden(id, false);
    announceSection(id);
    focusChip(id);
  }

  const highlight = computed<string | null>(() => {
    const statFrom = statReorder.fromPoint.value;
    if (statReorder.isDragging.value && statFrom) return statAt(statFrom) ?? null;
    const sectionFrom = sectionReorder.fromPoint.value;
    if (sectionReorder.isDragging.value && sectionFrom) return sectionAt(sectionFrom) ?? null;
    return hovered.value;
  });

  const showAlternativeTitles = computed({
    get: () => !store.hideAlternativeTitles,
    set: (shown: boolean) => (store.hideAlternativeTitles = !shown),
  });
  const showCoverage = computed({
    get: () => !store.hideCoverageBorders,
    set: (shown: boolean) => (store.hideCoverageBorders = !shown),
  });
</script>

<template>
  <div ref="rootEl" class="flex flex-col gap-5">
    <MediaCardStatsPreview :highlight="highlight" @rendered="rendered = $event" />

    <section class="flex flex-col gap-2" aria-labelledby="card-stats-heading">
      <h3 id="card-stats-heading" class="text-sm font-semibold text-surface-900 dark:text-surface-0">Stats</h3>
      <div v-if="auth.isAuthenticated" class="grid grid-cols-1 gap-4 lg:grid-cols-[minmax(0,1fr)_minmax(0,15rem)]">
        <div class="grid grid-cols-1 gap-2 sm:grid-cols-3">
          <div v-for="(column, c) in columns" :key="c" class="flex min-w-0 flex-col gap-1">
            <span :id="`stats-column-${c}`" class="text-xs text-surface-600 dark:text-surface-400">Column {{ c + 1 }}</span>
            <ol
              :ref="(el) => setStatColumnEl(c, el)"
              class="card-zone min-h-24"
              :class="{ 'card-zone--target': isDropTarget(statReorder, String(c)) }"
              :aria-labelledby="`stats-column-${c}`"
              aria-describedby="card-editor-help"
            >
              <li
                v-for="(id, index) in column"
                :key="id"
                data-reorder-item
                :data-chip-id="id"
                tabindex="0"
                class="card-chip"
                :class="{
                  'card-chip--missing': statMissing(id),
                  'card-chip--source': isSource(statReorder, String(c), index),
                  'card-chip--drop-before': dropMarker(statReorder, String(c), column.length, index) === 'before',
                  'card-chip--drop-after': dropMarker(statReorder, String(c), column.length, index) === 'after',
                }"
                :title="MEDIA_CARD_STATS[id].hint"
                :aria-label="`${statLabel(id)}, position ${index + 1} of ${column.length}${statMissing(id) ? ', not on this card' : ''}`"
                @pointerdown="statReorder.handlePointerDown($event, String(c), index)"
                @contextmenu.prevent
                @pointerenter="hovered = id"
                @pointerleave="hovered = null"
                @focus="hovered = id"
                @blur="hovered = null"
                @keydown="onStatKeydown($event, id, c, index)"
              >
                <span class="min-w-0 flex-1">{{ statLabel(id) }}</span>
                <button type="button" tabindex="-1" class="card-chip__hide" :aria-label="`Hide ${statLabel(id)}`" @pointerdown.stop @click="hideStat(id)">
                  <i class="pi pi-times text-[0.65rem]" aria-hidden="true" />
                </button>
              </li>
              <li v-if="column.length === 0" class="m-auto px-1 text-center text-xs text-surface-600 dark:text-surface-400">Drop a stat here</li>
            </ol>
          </div>
        </div>

        <div class="flex flex-col gap-1">
          <span id="stats-hidden-label" class="text-xs text-surface-600 dark:text-surface-400">Hidden</span>
          <ul
            ref="hiddenStatsEl"
            class="card-zone card-zone--hidden min-h-14 flex-1"
            :class="{ 'card-zone--target': isDropTarget(statReorder, HIDDEN) }"
            aria-labelledby="stats-hidden-label"
          >
            <li v-for="(id, index) in hiddenStats" :key="id" data-reorder-item>
              <button
                type="button"
                :data-chip-id="id"
                class="card-chip"
                :class="{ 'card-chip--source': isSource(statReorder, HIDDEN, index) }"
                :title="MEDIA_CARD_STATS[id].hint"
                :aria-label="`Show ${statLabel(id)}`"
                @pointerdown="statReorder.handlePointerDown($event, HIDDEN, index)"
                @contextmenu.prevent
                @pointerenter="hovered = id"
                @pointerleave="hovered = null"
                @click="showStat(id)"
              >
                <i class="pi pi-plus text-[0.65rem] text-surface-500 dark:text-surface-400" aria-hidden="true" />
                <span>{{ statLabel(id) }}</span>
              </button>
            </li>
            <li v-if="hiddenStats.length === 0" class="self-center px-1 text-sm text-surface-600 dark:text-surface-400">Drag a stat here to hide it.</li>
          </ul>
        </div>
      </div>
      <p v-else class="text-sm text-surface-600 dark:text-surface-400">
        <NuxtLink to="/login" class="font-medium">Sign in</NuxtLink> to choose and arrange the stats on media cards.
      </p>
    </section>

    <section class="flex flex-col gap-2" aria-labelledby="card-sections-heading">
      <h3 id="card-sections-heading" class="text-sm font-semibold text-surface-900 dark:text-surface-0">Sections</h3>
      <div class="grid grid-cols-1 gap-4 lg:grid-cols-[minmax(0,1fr)_minmax(0,15rem)]">
        <div class="grid grid-cols-1 gap-2 sm:grid-cols-2">
          <div v-for="(zone, z) in zones" :key="ZONES[z]!.key" class="flex min-w-0 flex-col gap-1">
            <span :id="`sections-zone-${z}`" class="text-xs text-surface-600 dark:text-surface-400">{{ ZONES[z]!.label }}</span>
            <ol
              :ref="(el) => setZoneEl(z, el)"
              class="card-zone min-h-14"
              :class="{ 'card-zone--target': isDropTarget(sectionReorder, String(z)) }"
              :aria-labelledby="`sections-zone-${z}`"
              aria-describedby="card-editor-help"
            >
              <li
                v-for="(id, index) in zone"
                :key="id"
                data-reorder-item
                :data-chip-id="id"
                tabindex="0"
                class="card-chip"
                :class="{
                  'card-chip--missing': sectionMissing(id),
                  'card-chip--source': isSource(sectionReorder, String(z), index),
                  'card-chip--drop-before': dropMarker(sectionReorder, String(z), zone.length, index) === 'before',
                  'card-chip--drop-after': dropMarker(sectionReorder, String(z), zone.length, index) === 'after',
                }"
                :aria-label="`${MEDIA_CARD_SECTION_LABELS[id]}, position ${index + 1} of ${zone.length}${sectionMissing(id) ? ', not on this card' : ''}`"
                @pointerdown="sectionReorder.handlePointerDown($event, String(z), index)"
                @contextmenu.prevent
                @pointerenter="hovered = id"
                @pointerleave="hovered = null"
                @focus="hovered = id"
                @blur="hovered = null"
                @keydown="onSectionKeydown($event, id, z, index)"
              >
                <span class="min-w-0 flex-1">{{ MEDIA_CARD_SECTION_LABELS[id] }}</span>
                <button
                  type="button"
                  tabindex="-1"
                  class="card-chip__hide"
                  :aria-label="`Hide ${MEDIA_CARD_SECTION_LABELS[id]}`"
                  @pointerdown.stop
                  @click="setSectionHidden(id, true)"
                >
                  <i class="pi pi-times text-[0.65rem]" aria-hidden="true" />
                </button>
              </li>
              <li v-if="zone.length === 0" class="m-auto px-1 text-center text-xs text-surface-600 dark:text-surface-400">Drop a section here</li>
            </ol>
          </div>
        </div>

        <div class="flex flex-col gap-1">
          <span id="sections-hidden-label" class="text-xs text-surface-600 dark:text-surface-400">Hidden</span>
          <ul
            ref="hiddenSectionsEl"
            class="card-zone card-zone--hidden min-h-14 flex-1"
            :class="{ 'card-zone--target': isDropTarget(sectionReorder, HIDDEN) }"
            aria-labelledby="sections-hidden-label"
          >
            <li v-for="(id, index) in hiddenSections" :key="id" data-reorder-item>
              <button
                type="button"
                :data-chip-id="id"
                class="card-chip"
                :class="{ 'card-chip--source': isSource(sectionReorder, HIDDEN, index) }"
                :aria-label="`Show ${MEDIA_CARD_SECTION_LABELS[id]}`"
                @pointerdown="sectionReorder.handlePointerDown($event, HIDDEN, index)"
                @contextmenu.prevent
                @pointerenter="hovered = id"
                @pointerleave="hovered = null"
                @click="showSection(id)"
              >
                <i class="pi pi-plus text-[0.65rem] text-surface-500 dark:text-surface-400" aria-hidden="true" />
                <span>{{ MEDIA_CARD_SECTION_LABELS[id] }}</span>
              </button>
            </li>
            <li v-if="hiddenSections.length === 0" class="self-center px-1 text-sm text-surface-600 dark:text-surface-400">Drag a section here to hide it.</li>
          </ul>
        </div>
      </div>

      <div class="flex flex-wrap gap-x-6 gap-y-2 pt-1">
        <div class="flex items-center gap-2" @pointerenter="hovered = 'alternativeTitles'" @pointerleave="hovered = null">
          <Checkbox v-model="showAlternativeTitles" input-id="showAlternativeTitles" :binary="true" />
          <label for="showAlternativeTitles" class="cursor-pointer text-sm">Alternative titles</label>
        </div>
        <div v-if="auth.isAuthenticated" class="flex items-center gap-2" @pointerenter="hovered = 'coverage'" @pointerleave="hovered = null">
          <Checkbox v-model="showCoverage" input-id="showCoverage" :binary="true" />
          <label for="showCoverage" class="cursor-pointer text-sm">Coverage indicators</label>
        </div>
      </div>
    </section>

    <p id="card-editor-help" class="max-w-2xl text-xs text-surface-600 dark:text-surface-400">
      You can drag the different box to rearrange them on the card or hide them with the cross. Different media types use different info. This doesn't affect the compact and table views.
    </p>

    <p class="sr-only" aria-live="polite">{{ announcement }}</p>
  </div>
</template>

<style scoped>
  .card-zone {
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
    padding: 0.5rem;
    border: 1px solid var(--p-surface-200);
    border-radius: var(--radius-lg);
    transition:
      border-color 0.15s,
      background-color 0.15s;
  }

  .card-zone--hidden {
    flex-direction: row;
    flex-wrap: wrap;
    align-content: flex-start;
    border-style: dashed;
    border-color: var(--p-surface-300);
  }

  .card-zone--target {
    border-color: var(--p-primary-color);
    background: color-mix(in srgb, var(--p-primary-color) 8%, transparent);
  }

  .card-chip {
    display: inline-flex;
    align-items: center;
    gap: 0.4rem;
    min-height: 2.25rem;
    padding: 0.25rem 0.4rem 0.25rem 0.6rem;
    border: 1px solid var(--p-surface-300);
    border-radius: var(--radius-md);
    background: var(--p-surface-0);
    color: var(--p-surface-900);
    font-size: 0.875rem;
    line-height: 1.25;
    text-align: left;
    cursor: grab;
    user-select: none;
    -webkit-touch-callout: none;
    transition:
      border-color 0.15s,
      opacity 0.15s;
  }

  button.card-chip {
    padding-right: 0.6rem;
  }

  .card-chip:hover {
    border-color: var(--p-primary-color);
  }

  .card-chip:focus-visible {
    outline: 2px solid var(--p-primary-color);
    outline-offset: 2px;
  }

  .card-chip--missing {
    border-style: dashed;
    color: var(--p-surface-500);
  }

  .card-chip--source {
    opacity: 0.35;
  }

  .card-chip--drop-before {
    box-shadow: 0 -5px 0 -2px var(--p-primary-color);
  }

  .card-chip--drop-after {
    box-shadow: 0 5px 0 -2px var(--p-primary-color);
  }

  .card-chip__hide {
    display: flex;
    flex-shrink: 0;
    align-items: center;
    justify-content: center;
    width: 1.5rem;
    height: 1.5rem;
    border-radius: 9999px;
    color: var(--p-surface-500);
    cursor: pointer;
  }

  .card-chip__hide:hover {
    color: var(--p-surface-900);
    background: var(--p-surface-100);
  }

  :global(.dark-mode .card-zone) {
    border-color: var(--p-surface-700);
  }

  :global(.dark-mode .card-zone--hidden) {
    border-color: var(--p-surface-600);
  }

  :global(.dark-mode .card-zone--target) {
    border-color: var(--p-primary-color);
  }

  :global(.dark-mode .card-chip) {
    border-color: var(--p-surface-600);
    background: var(--p-surface-900);
    color: var(--p-surface-0);
  }

  :global(.dark-mode .card-chip--missing) {
    color: var(--p-surface-400);
  }

  :global(.dark-mode .card-chip:hover) {
    border-color: var(--p-primary-color);
  }

  :global(.dark-mode .card-chip__hide:hover) {
    color: var(--p-surface-0);
    background: var(--p-surface-700);
  }
</style>
