<script setup lang="ts">
  import type { Franchise, FranchiseNode, FranchiseEdge, MediaType } from '~/types';
  import { MediaGroupKind } from '~/types';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { coverUrl } from '~/utils/coverImage';
  import { useFranchiseGraph } from '~/composables/useFranchiseGraph';
  import { useFranchiseHover } from '~/composables/useFranchiseHover';
  import { useFranchiseDisplay } from '~/composables/useFranchiseDisplay';
  import {
    compareFranchiseRelease,
    franchiseDerivedChips,
    franchiseDerivedLabel,
    franchiseHasSideLinks,
    franchisePopoverMemberships,
    isStoryRelation,
    releaseYearOf,
    verticalConnector,
    type ConnectorBox,
    type FranchiseLinkMode,
  } from '~/utils/franchiseLayout';
  import { countByMediaType, formatScope } from '~/utils/mediaGroup';
  import { getEdgeFlow, linkTypeInfo } from '~/utils/relationshipRoles';

  const props = defineProps<{
    franchise: Franchise;
    currentDeckId?: number | null;
    scopeDeckIds?: number[] | null;
  }>();

  const route = useRoute();

  const rootRef = ref<HTMLElement | null>(null);
  const {
    activeNode,
    popoverStyle,
    popoverRef,
    flashNode,
    isCoarsePointer,
    nodeEl,
    cancelClear,
    scheduleClear,
    onCardEnter,
    onCardLeave,
    onCardClick,
    scrollToNode,
    onRowHover,
    cancelRowHover,
  } = useFranchiseHover(rootRef);

  const franchiseRef = computed(() => props.franchise);
  const { localiseTitle, edges, nodeById, outOfScope, captionsFor, activeNodeData, edgeActive, nodeDimmed, showCoverage } = useFranchiseGraph(franchiseRef, {
    deckIds: () => props.scopeDeckIds,
    activeNode,
  });

  // Chronological RANK of release date (not linear time) — gives evenly spaced rows
  // regardless of multi-decade gaps. Nodes sharing a release date share a row.
  // Unknown dates sort last.
  const sortedNodes = computed<FranchiseNode[]>(() => [...props.franchise.nodes].sort(compareFranchiseRelease));

  // deckId -> row index (chronological rank, deduped by identical release date).
  const rowOf = computed<Map<number, number>>(() => {
    const m = new Map<number, number>();
    let row = -1;
    let prevKey: string | null = null;
    for (const n of sortedNodes.value) {
      const key = releaseYearOf(n.releaseDate) == null ? `unknown` : n.releaseDate.slice(0, 10);
      if (key !== prevKey) {
        row++;
        prevKey = key;
      }
      m.set(n.deckId, row);
    }
    return m;
  });

  const rowCount = computed(() => {
    let max = -1;
    for (const r of rowOf.value.values()) max = Math.max(max, r);
    return max + 1;
  });

  // Year label per row: '?' for the unknown-date row, '' when repeating the previous row's year.
  const rowYearLabels = computed<string[]>(() => {
    const years: (number | null)[] = new Array(rowCount.value).fill(null);
    const assigned: boolean[] = new Array(rowCount.value).fill(false);
    for (const n of sortedNodes.value) {
      const row = rowOf.value.get(n.deckId)!;
      if (!assigned[row]) {
        years[row] = releaseYearOf(n.releaseDate);
        assigned[row] = true;
      }
    }
    let prev: number | null | undefined;
    return years.map((y) => {
      if (y === prev) return '';
      prev = y;
      return y == null ? '?' : String(y);
    });
  });

  // Per-type deck counts; Map keeps the order in which the types first appear chronologically, which is the column order.
  const columnCounts = computed(() => countByMediaType(sortedNodes.value));
  const columns = computed<MediaType[]>(() => [...columnCounts.value.keys()]);

  const columnOf = computed<Map<number, number>>(() => {
    const colIndex = new Map<MediaType, number>();
    columns.value.forEach((t, i) => colIndex.set(t, i));
    const m = new Map<number, number>();
    for (const n of sortedNodes.value) m.set(n.deckId, colIndex.get(n.mediaType)!);
    return m;
  });

  // Nodes grouped per grid cell: same-date same-type entries (e.g. a double-feature
  // release) would otherwise stack in one cell and overlap.
  const cellGroups = computed<{ row: number; col: number; nodes: FranchiseNode[] }[]>(() => {
    const groups = new Map<string, { row: number; col: number; nodes: FranchiseNode[] }>();
    for (const n of sortedNodes.value) {
      const row = rowOf.value.get(n.deckId) ?? 0;
      const col = columnOf.value.get(n.deckId) ?? 0;
      const key = `${row}:${col}`;
      let g = groups.get(key);
      if (!g) {
        g = { row, col, nodes: [] };
        groups.set(key, g);
      }
      g.nodes.push(n);
    }
    return [...groups.values()];
  });

  const display = useFranchiseDisplay();
  const cardSize = display.cardSize;
  // Without side links the Display popover offers no link setting, so a stored None must not hide every link.
  const linkMode = computed<FranchiseLinkMode>(() => (franchiseHasSideLinks(edges.value) ? display.linkMode.value : 'all'));
  const compact = computed(() => cardSize.value === 'compact');
  const showColumnHeads = computed(() => columns.value.length > 1);
  const headRows = computed(() => (showColumnHeads.value ? 1 : 0));

  // Outside All, side works are named on their card instead of drawn, which keeps a hub with dozens of side stories readable.
  const derivedCaptions = computed(() => {
    const m = new Map<number, { label: string; origin: string; title: string }>();
    if (linkMode.value === 'all') return m;
    for (const [deckId, chip] of franchiseDerivedChips(edges.value, nodeById.value)) {
      const label = franchiseDerivedLabel(chip.type) ?? '';
      const originTitle = localiseTitle(nodeById.value.get(chip.originId)!);
      m.set(deckId, { label, origin: chip.extra ? `${originTitle} +${chip.extra}` : originTitle, title: `${label} ${originTitle}` });
    }
    return m;
  });

  const isCurrent = (id: number) => id === props.currentDeckId;

  // The popover lists the hovered node's relations by NAME, so long connectors whose other
  // end is scrolled off-screen stay interpretable.
  const activeCaptions = computed(() => (activeNode.value == null ? [] : captionsFor(activeNode.value)));
  const activeMemberships = computed(() => {
    if (activeNode.value == null) return [];
    return franchisePopoverMemberships(props.franchise, activeNode.value).map((m) =>
      m.kind === 'series'
        ? { ...m, to: { query: { ...route.query, scope: formatScope({ kind: MediaGroupKind.Series, id: m.seriesId }), offset: undefined } } }
        : { ...m, inert: true }
    );
  });

  function edgeVisible(e: FranchiseEdge): boolean {
    if (edgeActive(e) || linkMode.value === 'all') return true;
    return linkMode.value === 'story' && isStoryRelation(e.relationshipType);
  }

  // ---- SVG overlay geometry (client-only DOM measurement) ----
  const gridRef = ref<HTMLElement | null>(null);

  function nodeRect(id: number): ConnectorBox | null {
    const grid = gridRef.value;
    const el = nodeEl(id);
    if (!grid || !el) return null;
    const base = grid.getBoundingClientRect();
    const r = el.getBoundingClientRect();
    return { left: r.left - base.left, right: r.right - base.left, top: r.top - base.top, bottom: r.bottom - base.top };
  }

  interface EdgeGeom {
    edge: FranchiseEdge;
    path: string;
    mid: { x: number; y: number };
    directed: boolean;
  }

  const svgSize = ref({ w: 0, h: 0 });
  const edgeGeoms = ref<EdgeGeom[]>([]);
  const visibleEdgeGeoms = computed(() => edgeGeoms.value.filter((g) => edgeVisible(g.edge)));
  const activeEdgeGeoms = computed(() => edgeGeoms.value.filter((g) => edgeActive(g.edge)));

  function measure() {
    if (!import.meta.client) return;
    const grid = gridRef.value;
    if (!grid) return;
    // The overlay itself overflows the grid, so scroll sizes would keep a stale height after the grid shrinks.
    svgSize.value = { w: grid.offsetWidth, h: grid.offsetHeight };

    const geoms: EdgeGeom[] = [];
    for (const e of edges.value) {
      const flow = getEdgeFlow(e);
      const a = nodeRect(flow.from);
      const b = nodeRect(flow.to);
      if (!a || !b) continue;
      geoms.push({ edge: e, ...verticalConnector(a, b, 40), directed: flow.directed });
    }
    edgeGeoms.value = geoms;
  }

  let resizeObserver: ResizeObserver | null = null;
  let rafId: number | null = null;

  function scheduleMeasure() {
    if (!import.meta.client) return;
    if (rafId != null) cancelAnimationFrame(rafId);
    rafId = requestAnimationFrame(() => {
      rafId = null;
      measure();
    });
  }

  onMounted(() => {
    nextTick(() => {
      measure();
      if (props.currentDeckId != null) nodeEl(props.currentDeckId)?.scrollIntoView({ block: 'center' });
    });
    if (gridRef.value) {
      resizeObserver = new ResizeObserver(scheduleMeasure);
      resizeObserver.observe(gridRef.value);
    }
  });

  onBeforeUnmount(() => {
    resizeObserver?.disconnect();
    if (rafId != null) cancelAnimationFrame(rafId);
  });

  // Re-measure when the data (and therefore the rendered grid) changes.
  watch([sortedNodes, columns, cardSize], () => {
    nextTick(scheduleMeasure);
  });
</script>

<template>
  <div ref="rootRef" class="flex flex-col gap-2">
    <!-- Vertical chronological timeline: time flows downward, media types are columns.
         Natural window scrolling; horizontal overflow only when many media types are present. -->
    <div class="scrollbar-styled overflow-x-auto pb-2">
      <div
        ref="gridRef"
        class="relative grid w-max gap-x-4 gap-y-6 sm:gap-x-6"
        :style="{ gridTemplateColumns: `auto repeat(${columns.length}, minmax(6rem, max-content))` }"
      >
        <!-- SVG connector overlay -->
        <svg
          class="pointer-events-none absolute inset-0 z-0 overflow-visible"
          :width="svgSize.w"
          :height="svgSize.h"
          :viewBox="`0 0 ${svgSize.w} ${svgSize.h}`"
          aria-hidden="true"
        >
          <defs>
            <marker id="franchise-arrow" class="text-gray-400" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto">
              <path d="M0 0L10 5L0 10z" fill="currentColor" />
            </marker>
            <marker id="franchise-arrow-active" class="text-primary" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto">
              <path d="M0 0L10 5L0 10z" fill="currentColor" />
            </marker>
          </defs>
          <path
            v-for="(g, i) in visibleEdgeGeoms"
            :key="i"
            :d="g.path"
            fill="none"
            stroke="currentColor"
            :stroke-width="edgeActive(g.edge) ? 2.5 : 1.5"
            :stroke-dasharray="g.directed ? undefined : '5 4'"
            :marker-end="g.directed ? (edgeActive(g.edge) ? 'url(#franchise-arrow-active)' : 'url(#franchise-arrow)') : undefined"
            :class="[edgeActive(g.edge) ? 'text-primary' : activeNode != null ? 'text-gray-300 dark:text-gray-700' : 'text-gray-400 dark:text-gray-400']"
            :style="{ opacity: activeNode != null && !edgeActive(g.edge) ? 0.25 : 1 }"
          />
        </svg>
        <span
          v-for="(g, i) in activeEdgeGeoms"
          :key="`label-${i}`"
          class="pointer-events-none absolute z-20 -translate-x-1/2 -translate-y-1/2 rounded bg-surface-0 px-1 text-[10px] font-semibold whitespace-nowrap text-primary dark:bg-surface-900"
          :style="{ left: `${g.mid.x}px`, top: `${g.mid.y}px` }"
        >
          {{ linkTypeInfo(g.edge.relationshipType).label }}
        </span>

        <!-- Column headers (media types) — first grid row. -->
        <template v-if="showColumnHeads">
          <div
            class="z-10 flex items-center pl-1 text-[11px] font-semibold whitespace-nowrap text-gray-400 dark:text-gray-400"
            :style="{ gridColumn: 1, gridRow: 1 }"
          />
          <div
            v-for="(type, colIdx) in columns"
            :key="`head-${type}`"
            class="z-10 flex items-center justify-center pb-1 text-xs font-semibold whitespace-nowrap text-gray-500 dark:text-gray-400"
            :style="{ gridColumn: colIdx + 2, gridRow: 1 }"
          >
            {{ getMediaTypeText(type) }}
            <span class="ml-1 font-normal">{{ columnCounts.get(type) }}</span>
          </div>
        </template>

        <!-- Year axis labels (first column, offset by the header row). -->
        <div
          v-for="(label, row) in rowYearLabels"
          :key="`year-${row}`"
          class="z-10 flex items-center pr-1 text-xs whitespace-nowrap text-gray-400 dark:text-gray-400"
          :style="{ gridColumn: 1, gridRow: row + 1 + headRows }"
        >
          {{ label }}
        </div>

        <!-- Node cards at (chronological row, media-type column); one flex wrapper per cell
             so same-date same-type entries sit side by side instead of stacking. -->
        <div
          v-for="cell in cellGroups"
          :key="`cell-${cell.row}-${cell.col}`"
          class="z-10 flex items-start gap-2"
          :style="{ gridColumn: cell.col + 2, gridRow: cell.row + 1 + headRows }"
        >
          <NuxtLink
            v-for="node in cell.nodes"
            :key="node.deckId"
            :data-franchise-deck="node.deckId"
            :to="`/decks/media/${node.deckId}/detail`"
            class="group relative flex rounded-md border bg-surface-0 transition dark:bg-surface-900"
            :class="[
              compact ? 'w-44 flex-row sm:w-52' : 'w-24 flex-col sm:w-28 md:w-34',
              isCurrent(node.deckId) ? 'border-primary ring-2 ring-primary' : 'border-surface-200 dark:border-surface-700 hover:border-primary',
              flashNode === node.deckId ? 'ring-2 ring-amber-400 dark:ring-amber-300' : '',
              nodeDimmed(node.deckId) ? 'opacity-30' : outOfScope(node.deckId) ? 'opacity-40' : 'opacity-100',
            ]"
            @mouseenter="onCardEnter(node.deckId)"
            @mouseleave="onCardLeave"
            @focus="onCardEnter(node.deckId)"
            @blur="onCardLeave"
            @click.capture="onCardClick($event, node.deckId)"
          >
            <img
              :src="coverUrl(node.coverName)"
              :alt="localiseTitle(node)"
              :class="compact ? 'h-16 w-12 shrink-0 rounded-l-md object-cover' : 'h-28 w-full rounded-t-md object-cover sm:h-32 md:h-40'"
              loading="lazy"
              decoding="async"
              width="136"
              height="160"
            />
            <div class="flex min-w-0 flex-col gap-0.5 p-1.5">
              <span class="line-clamp-2 text-xs font-medium leading-tight" :title="localiseTitle(node)" v-bind="japaneseTextAttrs(localiseTitle(node))">
                {{ localiseTitle(node) }}
              </span>
              <div class="flex items-center justify-between gap-1 text-[11px]">
                <span class="text-gray-500 dark:text-gray-400">{{ releaseYearOf(node.releaseDate) ?? '?' }}</span>
                <DifficultyDisplay v-if="node.difficulty >= 0" :difficulty="node.difficulty" :difficulty-raw="node.difficultyRaw" class="text-[11px]" />
              </div>
              <span
                v-if="!compact && derivedCaptions.has(node.deckId)"
                class="truncate text-[10px] text-gray-500 dark:text-gray-400"
                :title="derivedCaptions.get(node.deckId)!.title"
              >
                <span class="font-semibold">{{ derivedCaptions.get(node.deckId)!.label }}</span>
                {{ derivedCaptions.get(node.deckId)!.origin }}
              </span>
            </div>
            <CoverageStrip v-if="showCoverage(node)" :coverage="node.coverage" class="absolute inset-x-0 bottom-0 rounded-b-md" />
          </NuxtLink>
        </div>
      </div>
    </div>

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
    />
  </div>
</template>
