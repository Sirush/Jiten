<script setup lang="ts">
  import type { Franchise, FranchiseNode, FranchiseEdge } from '~/types';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { coverUrl } from '~/utils/coverImage';
  import { useFranchiseGraph } from '~/composables/useFranchiseGraph';
  import { useFranchiseHover } from '~/composables/useFranchiseHover';
  import { useFranchiseDisplay } from '~/composables/useFranchiseDisplay';
  import {
    buildFranchiseSeriesLayout,
    compareFranchiseRelease,
    franchiseDifficultyRange,
    franchisePopoverMemberships,
    franchiseYearRange,
    horizontalConnector,
    layoutStoryLine,
    releaseYearOf,
    storyLineOrder,
    verticalConnector,
    type FranchisePopoverMembership,
    type SeriesGroup,
    type SeriesRow,
    type StoryLineLayout,
  } from '~/utils/franchiseLayout';
  import { getEdgeFlow, linkTypeInfo } from '~/utils/relationshipRoles';

  const props = defineProps<{
    franchise: Franchise;
    currentDeckId?: number | null;
    scopeDeckIds?: number[] | null;
  }>();

  const CARD_W = 224;
  const CARD_H = 88;
  const GAP_X = 56;
  const GAP_Y = 14;

  const SETTING_STYLES = [
    {
      chip: 'border-amber-600 bg-amber-50 text-amber-800 dark:border-amber-400 dark:bg-amber-950/40 dark:text-amber-300',
      ring: 'ring-2 ring-amber-500 dark:ring-amber-400',
      label: 'text-amber-700 dark:text-amber-300',
      border: 'border-l-amber-500 dark:border-l-amber-400',
    },
    {
      chip: 'border-teal-600 bg-teal-50 text-teal-800 dark:border-teal-400 dark:bg-teal-950/40 dark:text-teal-300',
      ring: 'ring-2 ring-teal-500 dark:ring-teal-400',
      label: 'text-teal-700 dark:text-teal-300',
      border: 'border-l-teal-500 dark:border-l-teal-400',
    },
    {
      chip: 'border-sky-600 bg-sky-50 text-sky-800 dark:border-sky-400 dark:bg-sky-950/40 dark:text-sky-300',
      ring: 'ring-2 ring-sky-500 dark:ring-sky-400',
      label: 'text-sky-700 dark:text-sky-300',
      border: 'border-l-sky-500 dark:border-l-sky-400',
    },
    {
      chip: 'border-rose-600 bg-rose-50 text-rose-800 dark:border-rose-400 dark:bg-rose-950/40 dark:text-rose-300',
      ring: 'ring-2 ring-rose-500 dark:ring-rose-400',
      label: 'text-rose-700 dark:text-rose-300',
      border: 'border-l-rose-500 dark:border-l-rose-400',
    },
    {
      chip: 'border-lime-700 bg-lime-50 text-lime-800 dark:border-lime-400 dark:bg-lime-950/40 dark:text-lime-300',
      ring: 'ring-2 ring-lime-600 dark:ring-lime-400',
      label: 'text-lime-700 dark:text-lime-300',
      border: 'border-l-lime-600 dark:border-l-lime-400',
    },
  ];

  const uid = useId();

  const rootRef = ref<HTMLElement | null>(null);
  const {
    activeNode,
    popoverStyle,
    popoverRef,
    flashNode,
    isCoarsePointer,
    cancelClear,
    scheduleClear,
    onCardEnter,
    onCardLeave,
    onCardClick,
    scrollToNode,
    onRowHover,
    cancelRowHover,
  } = useFranchiseHover(rootRef, { reveal });

  const franchiseRef = computed(() => props.franchise);
  const { localiseTitle, edges, nodeById, outOfScope, captionsFor, adjacentNodes, activeNodeData, edgeActive, showCoverage } = useFranchiseGraph(franchiseRef, {
    deckIds: () => props.scopeDeckIds,
    activeNode,
  });

  const { density } = useFranchiseDisplay();

  const openRows = ref(new Set<string>());
  watch(density, () => {
    openRows.value = new Set();
  });

  const layout = computed(() => buildFranchiseSeriesLayout(props.franchise));

  const lineLayouts = computed(() => {
    const m = new Map<string, StoryLineLayout>();
    const releaseOrder = (a: number, b: number) => compareFranchiseRelease(nodeById.value.get(a)!, nodeById.value.get(b)!);
    for (const row of layout.value.rows) if (row.kind === 'line') m.set(row.key, layoutStoryLine(row.deckIds, edges.value, releaseOrder));
    return m;
  });

  interface Section {
    key: string;
    group: SeriesGroup | null;
    rows: SeriesRow[];
  }

  const sections = computed<Section[]>(() => {
    const out: Section[] = layout.value.groups.map((g) => ({ key: `series-${g.seriesId}`, group: g, rows: g.rows }));
    if (layout.value.unassigned.length) out.push({ key: 'unassigned', group: null, rows: layout.value.unassigned });
    return out;
  });

  const activeSetting = ref<number | null>(null);
  const settingStyle = (index: number) => SETTING_STYLES[index % SETTING_STYLES.length]!;
  const settingIndex = computed(() => new Map((props.franchise.settings ?? []).map((s, i) => [s.seriesId, i])));
  const activeSettingMembers = computed(() => {
    const s = (props.franchise.settings ?? []).find((x) => x.seriesId === activeSetting.value);
    return new Set(s?.memberDeckIds ?? []);
  });

  function toggleSetting(seriesId: number, on?: boolean) {
    const enable = on ?? activeSetting.value !== seriesId;
    activeSetting.value = enable ? seriesId : null;
    if (!enable) return;
    for (const id of activeSettingMembers.value) reveal(id);
  }

  function settingRing(id: number): string {
    if (activeSetting.value == null || !activeSettingMembers.value.has(id)) return '';
    return settingStyle(settingIndex.value.get(activeSetting.value) ?? 0).ring;
  }

  type RowMode = 'graph' | 'mini' | 'main' | 'cards';

  function rowMode(row: SeriesRow): RowMode {
    const open = openRows.value.has(row.key);
    if (row.kind === 'standalone') return density.value !== 'compact' || open ? 'cards' : 'mini';
    if (open || density.value === 'detailed') return 'graph';
    return density.value === 'compact' ? 'mini' : 'main';
  }

  function rowExpandable(row: SeriesRow): boolean {
    return row.kind === 'line' ? density.value !== 'detailed' : density.value === 'compact';
  }

  function rowExpanded(row: SeriesRow): boolean {
    const mode = rowMode(row);
    return mode === 'graph' || mode === 'cards';
  }

  function toggleRow(row: SeriesRow) {
    const next = new Set(openRows.value);
    if (rowExpanded(row)) next.delete(row.key);
    else next.add(row.key);
    openRows.value = next;
  }

  function openRow(key: string) {
    if (openRows.value.has(key)) return;
    openRows.value = new Set(openRows.value).add(key);
  }

  function reveal(deckId: number) {
    const row = layout.value.rowOf.get(deckId);
    if (!row) return;
    const mode = rowMode(row);
    if (mode === 'mini' || (mode === 'main' && row.entryId !== deckId)) openRow(row.key);
  }

  function nodesOf(ids: number[]): FranchiseNode[] {
    return ids.map((id) => nodeById.value.get(id)).filter((n): n is FranchiseNode => !!n);
  }

  function rowName(row: SeriesRow): string {
    if (row.kind === 'standalone' && row.deckIds.length > 1) return 'Standalone entries';
    const entry = nodeById.value.get(row.entryId);
    return entry ? localiseTitle(entry) : '';
  }

  function metaOf(ids: number[]): string {
    const list = nodesOf(ids);
    return [`${list.length} ${list.length === 1 ? 'entry' : 'entries'}`, franchiseYearRange(list), franchiseDifficultyRange(list)].filter(Boolean).join(', ');
  }

  const rowMeta = computed(() => new Map(layout.value.rows.map((r) => [r.key, metaOf(r.deckIds)])));
  const groupMeta = computed(() => new Map(layout.value.groups.map((g) => [g.seriesId, metaOf(g.deckIds)])));

  function rowYear(row: SeriesRow): string {
    const n = nodeById.value.get(row.entryId);
    return n ? String(releaseYearOf(n.releaseDate) ?? '?') : '';
  }

  const rowHasCurrent = (ids: number[]) => props.currentDeckId != null && ids.includes(props.currentDeckId);

  const headerStats = computed(() => {
    const lines = layout.value.rows.filter((r) => r.kind === 'line' && layout.value.deckSeries.has(r.entryId)).length;
    return { entries: layout.value.entryCount, lines };
  });

  interface GraphEdge {
    edge: FranchiseEdge;
    path: string;
    directed: boolean;
    label: string;
    mid: { x: number; y: number };
  }

  function cardPos(row: SeriesRow, id: number): { x: number; y: number } {
    const p = lineLayouts.value.get(row.key)?.pos.get(id);
    return p ? { x: p.layer * (CARD_W + GAP_X), y: p.slot * (CARD_H + GAP_Y) } : { x: 0, y: 0 };
  }

  function cardBox(row: SeriesRow, id: number) {
    const p = cardPos(row, id);
    return { left: p.x, top: p.y, right: p.x + CARD_W, bottom: p.y + CARD_H };
  }

  function cardStyle(row: SeriesRow | null, id: number): Record<string, string> {
    const size = { width: `${CARD_W}px`, height: `${CARD_H}px` };
    if (!row) return size;
    const p = cardPos(row, id);
    return { ...size, left: `${p.x}px`, top: `${p.y}px` };
  }

  function graphSize(row: SeriesRow) {
    const l = lineLayouts.value.get(row.key);
    if (!l) return { w: 0, h: 0 };
    return { w: l.layers * (CARD_W + GAP_X) - GAP_X, h: l.slots * (CARD_H + GAP_Y) - GAP_Y };
  }

  const graphEdges = computed(() => {
    const m = new Map<string, GraphEdge[]>();
    for (const row of layout.value.rows) {
      const l = lineLayouts.value.get(row.key);
      if (!l) continue;
      m.set(
        row.key,
        l.edges.map((e) => {
          const flow = getEdgeFlow(e);
          const a = cardBox(row, flow.from);
          const b = cardBox(row, flow.to);
          const { path, mid } = Math.abs(a.left - b.left) < CARD_W / 2 ? verticalConnector(a, b) : horizontalConnector(a, b, 24);
          return { edge: e, path, directed: flow.directed, label: linkTypeInfo(e.relationshipType).label, mid };
        })
      );
    }
    return m;
  });

  const activeRowKey = computed(() => (activeNode.value == null ? null : (layout.value.rowOf.get(activeNode.value)?.key ?? null)));

  function graphFocusing(row: SeriesRow): boolean {
    return activeRowKey.value === row.key && rowMode(row) === 'graph';
  }

  function nodeDimmed(row: SeriesRow, id: number): boolean {
    return graphFocusing(row) && !adjacentNodes.value.has(id);
  }

  function edgeHot(row: SeriesRow, e: FranchiseEdge): boolean {
    return graphFocusing(row) && edgeActive(e);
  }

  const activeCaptions = computed(() => {
    if (activeNode.value == null) return [];
    return captionsFor(activeNode.value).sort((a, b) => compareFranchiseRelease(nodeById.value.get(a.otherId)!, nodeById.value.get(b.otherId)!));
  });

  const titleOf = (deckId: number) => {
    const n = nodeById.value.get(deckId);
    return n ? localiseTitle(n) : null;
  };

  const activeMemberships = computed(() =>
    activeNode.value == null ? [] : franchisePopoverMemberships(props.franchise, activeNode.value, titleOf, layout.value.deckSeries.get(activeNode.value) ?? [])
  );

  function onMembership(m: FranchisePopoverMembership) {
    if (m.kind === 'setting') {
      toggleSetting(m.seriesId, true);
      document.getElementById(`${uid}-setting-${m.seriesId}`)?.scrollIntoView({ block: 'nearest' });
      return;
    }
    document.getElementById(`${uid}-series-${m.seriesId}`)?.scrollIntoView({ block: 'start', behavior: 'smooth' });
  }

  function cardClasses(row: SeriesRow | null, id: number): string[] {
    return [
      id === props.currentDeckId ? 'border-primary ring-2 ring-primary' : 'border-surface-200 hover:border-primary dark:border-surface-700',
      flashNode.value === id ? 'ring-2 ring-amber-400 dark:ring-amber-300' : settingRing(id),
      row && nodeDimmed(row, id) ? 'opacity-25' : outOfScope(id) ? 'opacity-40' : 'opacity-100',
    ];
  }
</script>

<template>
  <div ref="rootRef" class="flex flex-col gap-4">
    <div class="flex flex-wrap items-center gap-x-5 gap-y-2 text-xs text-gray-500 dark:text-gray-400">
      <span v-if="headerStats.entries">
        {{ headerStats.entries }} series {{ headerStats.entries === 1 ? 'entry' : 'entries'
        }}<template v-if="headerStats.lines">, {{ headerStats.lines }} with sequels or side stories</template>
      </span>
      <div v-if="franchise.settings?.length" role="group" aria-label="Highlight a shared setting" class="flex flex-wrap items-center gap-1.5">
        <span>Shares settings:</span>
        <button
          v-for="(s, i) in franchise.settings"
          :key="s.seriesId"
          type="button"
          class="min-h-11 rounded border px-2 py-0.5 text-xs font-medium transition-opacity focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary-500 sm:pointer-fine:min-h-0"
          :class="[settingStyle(i).chip, activeSetting != null && activeSetting !== s.seriesId ? 'opacity-60' : '']"
          :aria-pressed="activeSetting === s.seriesId"
          @click="toggleSetting(s.seriesId)"
        >
          {{ s.name }}
        </button>
      </div>
    </div>

    <section v-for="section in sections" :key="section.key" class="flex flex-col gap-1">
      <h2 v-if="section.group" :id="`${uid}-series-${section.group.seriesId}`" class="flex scroll-mt-20 flex-wrap items-baseline gap-x-2 text-base font-bold">
        <span v-bind="japaneseTextAttrs(section.group.name)">{{ section.group.name }}</span>
        <span class="rounded bg-primary-50 px-1.5 py-0.5 text-[11px] font-bold text-primary-700 dark:bg-primary-950/40 dark:text-primary-300">Series</span>
        <span class="text-sm font-normal text-gray-500 dark:text-gray-400">{{ groupMeta.get(section.group.seriesId) }}</span>
      </h2>
      <h2 v-else-if="sections.length > 1" class="pt-2 text-base font-bold">Not in a series</h2>

      <div class="flex flex-col">
        <div
          v-for="row in section.rows"
          :key="row.key"
          class="relative grid grid-cols-[2.25rem_1.5rem_minmax(0,1fr)] border-t border-surface-200 py-2.5 first:border-t-0 sm:grid-cols-[3rem_1.75rem_minmax(0,1fr)] dark:border-surface-700"
        >
          <span class="pt-1.5 pr-1.5 text-right text-xs text-gray-500 tabular-nums dark:text-gray-400">{{ rowYear(row) }}</span>
          <span class="relative flex justify-center" aria-hidden="true">
            <span class="absolute -inset-y-2.5 w-[3px] bg-surface-300 dark:bg-surface-600" />
            <span
              class="relative mt-2 size-3.5 rounded-full border-[3px]"
              :class="rowHasCurrent(row.deckIds) ? 'border-primary bg-primary' : 'border-surface-400 bg-[var(--jiten-page-bg)] dark:border-surface-500'"
            />
          </span>

          <div class="flex min-w-0 flex-col gap-2 pl-2">
            <button
              type="button"
              class="flex flex-wrap items-baseline gap-x-2.5 gap-y-0.5 pt-1 text-left disabled:cursor-default"
              :disabled="!rowExpandable(row)"
              :aria-expanded="rowExpandable(row) ? rowExpanded(row) : undefined"
              :aria-controls="rowExpandable(row) ? `${uid}-row-${row.key}` : undefined"
              @click="toggleRow(row)"
            >
              <i
                v-if="rowExpandable(row)"
                class="pi pi-chevron-right self-center text-[10px] text-gray-500 transition-transform dark:text-gray-400"
                :class="rowExpanded(row) ? 'rotate-90' : ''"
                aria-hidden="true"
              />
              <span class="text-sm font-bold" v-bind="japaneseTextAttrs(rowName(row))">{{ rowName(row) }}</span>
              <span class="text-xs text-gray-500 dark:text-gray-400">{{ rowMeta.get(row.key) }}</span>
              <span v-if="rowHasCurrent(row.deckIds)" class="text-[11px] font-bold text-primary">You are here</span>
            </button>

            <div :id="`${uid}-row-${row.key}`">
              <div v-if="rowMode(row) === 'graph'" class="scrollbar-styled overflow-x-auto pb-1">
                <div class="relative" :style="{ width: `${graphSize(row).w}px`, height: `${graphSize(row).h}px` }">
                  <svg class="pointer-events-none absolute inset-0 overflow-visible" :width="graphSize(row).w" :height="graphSize(row).h" aria-hidden="true">
                    <defs>
                      <marker
                        :id="`${uid}-${row.key}-ah`"
                        viewBox="0 0 10 10"
                        refX="9"
                        refY="5"
                        markerWidth="8"
                        markerHeight="8"
                        markerUnits="userSpaceOnUse"
                        orient="auto"
                      >
                        <path d="M0 0L10 5L0 10z" class="fill-gray-400 dark:fill-gray-500" />
                      </marker>
                      <marker
                        :id="`${uid}-${row.key}-ah-hot`"
                        viewBox="0 0 10 10"
                        refX="9"
                        refY="5"
                        markerWidth="9"
                        markerHeight="9"
                        markerUnits="userSpaceOnUse"
                        orient="auto"
                      >
                        <path d="M0 0L10 5L0 10z" class="fill-primary" />
                      </marker>
                    </defs>
                    <path
                      v-for="(g, i) in graphEdges.get(row.key)"
                      :key="i"
                      :d="g.path"
                      fill="none"
                      stroke="currentColor"
                      :stroke-width="edgeHot(row, g.edge) ? 2.5 : 1.5"
                      :stroke-dasharray="g.directed ? undefined : '5 4'"
                      :marker-end="g.directed ? `url(#${uid}-${row.key}-${edgeHot(row, g.edge) ? 'ah-hot' : 'ah'})` : undefined"
                      :class="edgeHot(row, g.edge) ? 'text-primary' : 'text-gray-400 dark:text-gray-500'"
                      :style="{ opacity: graphFocusing(row) && !edgeHot(row, g.edge) ? 0.25 : 1 }"
                    />
                  </svg>
                  <template v-for="id in row.deckIds" :key="id">
                    <FranchiseSeriesCard
                      v-if="nodeById.get(id)"
                      :node="nodeById.get(id)!"
                      :current="id === currentDeckId"
                      :show-coverage="showCoverage(nodeById.get(id)!)"
                      class="absolute z-10"
                      :class="cardClasses(row, id)"
                      :style="cardStyle(row, id)"
                      @mouseenter="onCardEnter(id)"
                      @mouseleave="onCardLeave"
                      @focus="onCardEnter(id)"
                      @blur="onCardLeave"
                      @click.capture="onCardClick($event, id)"
                    />
                  </template>
                  <template v-for="(g, i) in graphEdges.get(row.key)" :key="`l-${i}`">
                    <span
                      v-if="g.label"
                      class="pointer-events-none absolute -translate-x-1/2 -translate-y-1/2 rounded-sm bg-[var(--jiten-page-bg)] px-1 text-[10.5px] whitespace-nowrap"
                      :class="edgeHot(row, g.edge) ? 'z-20 font-semibold text-primary' : 'z-[5] text-gray-500 dark:text-gray-400'"
                      :style="{ left: `${g.mid.x}px`, top: `${g.mid.y}px`, opacity: graphFocusing(row) && !edgeHot(row, g.edge) ? 0.25 : 1 }"
                    >
                      {{ g.label }}
                    </span>
                  </template>
                </div>
              </div>

              <div v-else-if="rowMode(row) === 'mini'" class="flex flex-wrap items-center gap-x-1.5 gap-y-1">
                <template v-for="(id, i) in row.kind === 'line' ? storyLineOrder(lineLayouts.get(row.key)!) : row.deckIds" :key="id">
                  <span v-if="i > 0 && row.kind === 'line'" class="text-xs text-gray-400 dark:text-gray-500" aria-hidden="true">›</span>
                  <button
                    v-if="nodeById.get(id)"
                    type="button"
                    :data-franchise-deck="id"
                    class="inline-flex max-w-[12rem] items-center gap-1.5 rounded text-xs text-gray-600 hover:text-surface-900 dark:text-gray-300 dark:hover:text-surface-0"
                    :class="[outOfScope(id) ? 'opacity-40' : '', flashNode === id ? 'ring-2 ring-amber-400 dark:ring-amber-300' : settingRing(id)]"
                    :title="localiseTitle(nodeById.get(id)!)"
                    @click="scrollToNode(id)"
                  >
                    <img
                      :src="coverUrl(nodeById.get(id)!.coverName)"
                      alt=""
                      class="h-8 w-6 shrink-0 rounded-sm object-cover"
                      :class="id === currentDeckId ? 'outline-2 outline-offset-1 outline-primary' : ''"
                      loading="lazy"
                      decoding="async"
                      width="24"
                      height="32"
                    />
                    <span class="truncate" v-bind="japaneseTextAttrs(localiseTitle(nodeById.get(id)!))">{{ localiseTitle(nodeById.get(id)!) }}</span>
                  </button>
                </template>
              </div>

              <div v-else class="flex flex-col items-start gap-1.5">
                <div class="flex flex-wrap gap-3">
                  <template v-for="id in rowMode(row) === 'main' ? [row.entryId] : row.deckIds" :key="id">
                    <FranchiseSeriesCard
                      v-if="nodeById.get(id)"
                      :node="nodeById.get(id)!"
                      :current="id === currentDeckId"
                      :show-coverage="showCoverage(nodeById.get(id)!)"
                      class="relative max-w-full"
                      :class="cardClasses(null, id)"
                      :style="cardStyle(null, id)"
                      @mouseenter="onCardEnter(id)"
                      @mouseleave="onCardLeave"
                      @focus="onCardEnter(id)"
                      @blur="onCardLeave"
                      @click.capture="onCardClick($event, id)"
                    />
                  </template>
                </div>
                <button v-if="rowMode(row) === 'main'" type="button" class="text-xs text-primary hover:underline" @click="openRow(row.key)">
                  Show the other {{ row.deckIds.length - 1 }} in this series
                </button>
              </div>
            </div>
          </div>
        </div>
      </div>
    </section>

    <section v-if="franchise.settings?.length" class="flex flex-col gap-2 pt-2">
      <h2 class="text-base font-bold">Shared settings</h2>
      <div class="grid grid-cols-[repeat(auto-fit,minmax(min(100%,20rem),1fr))] gap-3">
        <div
          v-for="(s, i) in franchise.settings"
          :id="`${uid}-setting-${s.seriesId}`"
          :key="s.seriesId"
          class="flex flex-col gap-2 rounded-md border border-l-4 border-surface-200 bg-surface-0 p-3 dark:border-surface-700 dark:bg-surface-900"
          :class="settingStyle(i).border"
        >
          <div class="flex flex-wrap items-baseline gap-2">
            <span class="text-sm font-bold" v-bind="japaneseTextAttrs(s.name)">{{ s.name }}</span>
            <span class="text-[11px] font-bold" :class="settingStyle(i).label">Setting</span>
            <button
              type="button"
              class="ml-auto text-xs text-primary hover:underline"
              :aria-pressed="activeSetting === s.seriesId"
              @click="toggleSetting(s.seriesId)"
            >
              {{ activeSetting === s.seriesId ? 'Clear highlight' : 'Highlight' }}
            </button>
          </div>
          <p class="text-xs text-gray-500 dark:text-gray-400">Shares a world with other entries.</p>
          <div class="flex flex-wrap gap-1.5">
            <template v-for="id in s.memberDeckIds" :key="id">
              <button
                v-if="nodeById.get(id)"
                type="button"
                class="max-w-full truncate rounded border border-surface-300 bg-surface-0 px-2 py-0.5 text-xs hover:border-primary dark:border-surface-600 dark:bg-surface-900"
                v-bind="japaneseTextAttrs(localiseTitle(nodeById.get(id)!))"
                @click="scrollToNode(id)"
              >
                {{ localiseTitle(nodeById.get(id)!) }}
              </button>
            </template>
            <NuxtLink
              v-for="o in s.outside"
              :key="`o-${o.deckId}`"
              :to="`/decks/media/${o.deckId}/detail`"
              class="inline-flex max-w-full gap-1 rounded border border-dashed border-surface-400 px-2 py-0.5 text-xs text-gray-600 hover:border-primary dark:border-surface-500 dark:text-gray-300"
            >
              <span class="truncate" v-bind="japaneseTextAttrs(localiseTitle(o))">{{ localiseTitle(o) }}</span>
              <span class="shrink-0 text-gray-500 dark:text-gray-400">
                {{ getMediaTypeText(o.mediaType) }}<template v-if="releaseYearOf(o.releaseDate)">, {{ releaseYearOf(o.releaseDate) }}</template></span
              >
            </NuxtLink>
            <span v-if="s.outsideCount > s.outside.length" class="px-1 py-0.5 text-xs text-gray-600 dark:text-gray-300">
              {{ s.outsideCount - s.outside.length }} more outside this franchise
            </span>
          </div>
        </div>
      </div>
    </section>

    <FranchisePopover
      v-if="activeNode != null && activeNodeData"
      ref="popoverRef"
      :style="popoverStyle"
      :deck-id="activeNode"
      :title="localiseTitle(activeNodeData)"
      :captions="activeCaptions"
      :memberships="activeMemberships"
      @mouseenter="!isCoarsePointer && cancelClear()"
      @mouseleave="!isCoarsePointer && scheduleClear()"
      @goto="scrollToNode"
      @row-hover="onRowHover"
      @row-leave="cancelRowHover"
      @membership="onMembership"
    />
  </div>
</template>
