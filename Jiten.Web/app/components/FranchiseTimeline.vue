<script setup lang="ts">
  import type { Franchise, FranchiseNode, FranchiseEdge, MediaType } from '~/types';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { useJitenStore } from '~/stores/jitenStore';
  import { useAuthStore } from '~/stores/authStore';
  import { getCoverageBorder } from '~/utils/coverageBorder';
  import { useFranchiseGraph } from '~/composables/useFranchiseGraph';

  const props = defineProps<{
    franchise: Franchise;
    currentDeckId: number;
  }>();

  const store = useJitenStore();
  const authStore = useAuthStore();

  const franchiseRef = computed(() => props.franchise);
  const { localiseTitle, edges, nodeById, releaseYear, coverSrc, captionsFor, useAdjacentNodes, edgeActive: edgeActiveOf } = useFranchiseGraph(franchiseRef);

  // Chronological RANK of release date (not linear time) — gives evenly spaced rows
  // regardless of multi-decade gaps. Nodes sharing a release date share a row.
  // Unknown dates sort last.
  const sortedNodes = computed<FranchiseNode[]>(() =>
    [...props.franchise.nodes].sort((a, b) => {
      const va = releaseYear(a) == null ? Number.POSITIVE_INFINITY : Date.parse(a.releaseDate);
      const vb = releaseYear(b) == null ? Number.POSITIVE_INFINITY : Date.parse(b.releaseDate);
      if (va !== vb) return va - vb;
      return a.deckId - b.deckId;
    })
  );

  // deckId -> row index (chronological rank, deduped by identical release date).
  const rowOf = computed<Map<number, number>>(() => {
    const m = new Map<number, number>();
    let row = -1;
    let prevKey: string | null = null;
    for (const n of sortedNodes.value) {
      const key = releaseYear(n) == null ? `unknown` : n.releaseDate.slice(0, 10);
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
        years[row] = releaseYear(n);
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

  // Media-type columns, ordered as the types first appear chronologically.
  const columns = computed<MediaType[]>(() => {
    const seen = new Set<MediaType>();
    const order: MediaType[] = [];
    for (const n of sortedNodes.value) {
      if (!seen.has(n.mediaType)) {
        seen.add(n.mediaType);
        order.push(n.mediaType);
      }
    }
    return order;
  });

  const columnOf = computed<Map<number, number>>(() => {
    const colIndex = new Map<MediaType, number>();
    columns.value.forEach((t, i) => colIndex.set(t, i));
    const m = new Map<number, number>();
    for (const n of sortedNodes.value) m.set(n.deckId, colIndex.get(n.mediaType)!);
    return m;
  });

  const isCurrent = (id: number) => id === props.currentDeckId;

  // Per-user coverage outline, matching MediaDeckCard's convention.
  function coverageBorder(node: FranchiseNode): string {
    if (!authStore.isAuthenticated || store.hideCoverageBorders || (node.coverage === 0 && node.uniqueCoverage === 0)) return 'none';
    return getCoverageBorder(node.coverage);
  }

  // ---- Hover / focus highlighting + relations popover ----
  // The popover lists the hovered node's relations by NAME, so long connectors whose other
  // end is scrolled off-screen stay interpretable. Clearing is delayed so the pointer can
  // travel from the card onto the popover without dismissing it.
  const activeNode = ref<number | null>(null);
  const popoverStyle = ref<Record<string, string>>({});
  let clearTimer: ReturnType<typeof setTimeout> | null = null;

  const activeNodeData = computed(() => (activeNode.value == null ? null : nodeById.value.get(activeNode.value) ?? null));
  const activeCaptions = computed(() => (activeNode.value == null ? [] : captionsFor(activeNode.value)));

  function cancelClear() {
    if (clearTimer != null) {
      clearTimeout(clearTimer);
      clearTimer = null;
    }
  }

  function scheduleClear() {
    cancelClear();
    clearTimer = setTimeout(() => {
      activeNode.value = null;
    }, 200);
  }

  function activate(id: number) {
    cancelClear();
    activeNode.value = id;
    positionPopover(id);
  }

  // Coarse-pointer (touch) gate: first tap activates, second tap on the active card navigates.
  // Tracked live (not just at mount) so DevTools device emulation toggles are picked up.
  const isCoarsePointer = ref(false);
  let coarseMq: MediaQueryList | null = null;
  const onCoarseChange = (e: MediaQueryListEvent) => {
    isCoarsePointer.value = e.matches;
  };

  // Bound in the CAPTURE phase: NuxtLink's own navigate handler is merged before fallthrough
  // listeners, so a bubble-phase preventDefault would come too late to stop the router.
  function onCardClick(e: MouseEvent, id: number) {
    if (!isCoarsePointer.value) return; // fine pointers keep click = navigate
    if (activeNode.value === id) return; // second tap on active card: let the link navigate
    e.preventDefault();
    activate(id);
  }

  const adjacentNodes = useAdjacentNodes(activeNode);

  function edgeActive(e: FranchiseEdge): boolean {
    return edgeActiveOf(e, activeNode.value);
  }

  function nodeDimmed(id: number): boolean {
    return activeNode.value != null && !adjacentNodes.value.has(id);
  }

  // Brief flash to locate a node after a popover row scrolls it into view.
  const flashNode = ref<number | null>(null);
  let flashTimer: ReturnType<typeof setTimeout> | null = null;

  function scrollToNode(id: number) {
    const el = nodeEls.value.get(id);
    if (!el) return;
    el.scrollIntoView({ behavior: 'smooth', block: 'center' });
    flashNode.value = id;
    if (flashTimer != null) clearTimeout(flashTimer);
    flashTimer = setTimeout(() => {
      flashNode.value = null;
    }, 1600);
  }

  // Hovering a popover row brings its target into view, debounced so skimming the list
  // doesn't thrash the scroll position.
  let hoverScrollTimer: ReturnType<typeof setTimeout> | null = null;

  function onRowHover(id: number) {
    if (hoverScrollTimer != null) clearTimeout(hoverScrollTimer);
    hoverScrollTimer = setTimeout(() => scrollToNode(id), 150);
  }

  function cancelRowHover() {
    if (hoverScrollTimer != null) {
      clearTimeout(hoverScrollTimer);
      hoverScrollTimer = null;
    }
  }

  // ---- SVG overlay geometry (client-only DOM measurement) ----
  const gridRef = ref<HTMLElement | null>(null);
  const nodeEls = ref<Map<number, HTMLElement>>(new Map());

  function setNodeEl(id: number, el: Element | null) {
    if (el) nodeEls.value.set(id, el as HTMLElement);
    else nodeEls.value.delete(id);
  }

  function nodeRect(id: number): { left: number; right: number; top: number; bottom: number; x: number; y: number } | null {
    const grid = gridRef.value;
    const el = nodeEls.value.get(id);
    if (!grid || !el) return null;
    const base = grid.getBoundingClientRect();
    const r = el.getBoundingClientRect();
    return {
      left: r.left - base.left,
      right: r.left - base.left + r.width,
      top: r.top - base.top,
      bottom: r.top - base.top + r.height,
      x: r.left - base.left + r.width / 2,
      y: r.top - base.top + r.height / 2,
    };
  }

  // Fixed (viewport-anchored) popover so scrolling the page underneath — e.g. while hover-
  // scrolling a relation into view — does not slide the popover out from under the pointer.
  function positionPopover(id: number) {
    if (!import.meta.client) return;
    const el = nodeEls.value.get(id);
    if (!el) return;
    const r = el.getBoundingClientRect();
    const popWidth = 260;
    const margin = 8;
    const vw = window.innerWidth;
    const vh = window.innerHeight;
    // Prefer the right side of the card; flip left when it would overflow the viewport.
    let left = r.right + margin;
    if (left + popWidth > vw) left = r.left - margin - popWidth;
    left = Math.max(margin, Math.min(left, vw - popWidth - margin));
    const top = Math.max(margin, Math.min(r.top, vh - 16));
    popoverStyle.value = {
      left: `${left}px`,
      top: `${top}px`,
      width: `${popWidth}px`,
    };
  }

  interface EdgeGeom {
    edge: FranchiseEdge;
    path: string;
  }

  const svgSize = ref({ w: 0, h: 0 });
  const edgeGeoms = ref<EdgeGeom[]>([]);

  function measure() {
    if (!import.meta.client) return;
    const grid = gridRef.value;
    if (!grid) return;
    svgSize.value = { w: grid.scrollWidth, h: grid.scrollHeight };

    const geoms: EdgeGeom[] = [];
    for (const e of edges.value) {
      const a = nodeRect(e.sourceDeckId);
      const b = nodeRect(e.targetDeckId);
      if (!a || !b) continue;

      // Anchor on the facing top/bottom edges so connectors flow mostly top->bottom.
      const topToBottom = a.y <= b.y;
      const y1 = topToBottom ? a.bottom : a.top;
      const y2 = topToBottom ? b.top : b.bottom;
      const x1 = a.x;
      const x2 = b.x;
      const dy = Math.max(Math.abs(y2 - y1) * 0.5, 40);
      const c1y = topToBottom ? y1 + dy : y1 - dy;
      const c2y = topToBottom ? y2 - dy : y2 + dy;
      const path = `M ${x1} ${y1} C ${x1} ${c1y}, ${x2} ${c2y}, ${x2} ${y2}`;
      geoms.push({ edge: e, path });
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

  // Closing the touch popover when tapping outside it / the active card.
  function onDocumentPointerDown(e: PointerEvent) {
    if (activeNode.value == null) return;
    const target = e.target as Node | null;
    if (!target) return;
    if (popoverRef.value?.contains(target)) return;
    const activeEl = activeNode.value != null ? nodeEls.value.get(activeNode.value) : null;
    if (activeEl?.contains(target)) return;
    activeNode.value = null;
  }

  const popoverRef = ref<HTMLElement | null>(null);

  onMounted(() => {
    if (!import.meta.client) return;
    coarseMq = window.matchMedia('(pointer: coarse)');
    isCoarsePointer.value = coarseMq.matches;
    coarseMq.addEventListener('change', onCoarseChange);
    nextTick(() => {
      measure();
      // Scroll the current deck into view using natural window scrolling.
      const el = nodeEls.value.get(props.currentDeckId);
      el?.scrollIntoView({ block: 'center' });
    });
    if (gridRef.value) {
      resizeObserver = new ResizeObserver(scheduleMeasure);
      resizeObserver.observe(gridRef.value);
    }
    document.addEventListener('pointerdown', onDocumentPointerDown);
  });

  onBeforeUnmount(() => {
    resizeObserver?.disconnect();
    if (rafId != null) cancelAnimationFrame(rafId);
    cancelClear();
    cancelRowHover();
    if (flashTimer != null) clearTimeout(flashTimer);
    if (import.meta.client) {
      document.removeEventListener('pointerdown', onDocumentPointerDown);
      coarseMq?.removeEventListener('change', onCoarseChange);
    }
  });

  // Re-measure when the data (and therefore the rendered grid) changes.
  watch([sortedNodes, columns], () => {
    nextTick(scheduleMeasure);
  });
</script>

<template>
  <div class="pt-4">
    <!-- Vertical chronological timeline: time flows downward, media types are columns.
         Natural window scrolling; horizontal overflow only when many media types are present. -->
    <div class="overflow-x-auto pb-2">
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
          <path
            v-for="(g, i) in edgeGeoms"
            :key="i"
            :d="g.path"
            fill="none"
            stroke="currentColor"
            :stroke-width="edgeActive(g.edge) ? 2.5 : 1.5"
            :class="[edgeActive(g.edge) ? 'text-primary' : activeNode != null ? 'text-gray-300 dark:text-gray-700' : 'text-gray-400 dark:text-gray-600']"
            :style="{ opacity: activeNode != null && !edgeActive(g.edge) ? 0.25 : 1 }"
          />
        </svg>

        <!-- Column headers (media types) — first grid row. -->
        <div
          class="z-10 flex items-center pl-1 text-[11px] font-semibold whitespace-nowrap text-gray-400 dark:text-gray-500"
          :style="{ gridColumn: 1, gridRow: 1 }"
        ></div>
        <div
          v-for="(type, colIdx) in columns"
          :key="`head-${type}`"
          class="z-10 flex items-center justify-center pb-1 text-xs font-semibold whitespace-nowrap text-gray-500 dark:text-gray-400"
          :style="{ gridColumn: colIdx + 2, gridRow: 1 }"
        >
          {{ getMediaTypeText(type) }}
        </div>

        <!-- Year axis labels (first column, offset by the header row). -->
        <div
          v-for="(label, row) in rowYearLabels"
          :key="`year-${row}`"
          class="z-10 flex items-center pr-1 text-xs whitespace-nowrap text-gray-400 dark:text-gray-500"
          :style="{ gridColumn: 1, gridRow: row + 2 }"
        >
          {{ label }}
        </div>

        <!-- Node cards at (chronological row, media-type column). -->
        <NuxtLink
          v-for="node in sortedNodes"
          :key="node.deckId"
          :ref="(el: any) => setNodeEl(node.deckId, el?.$el ?? el)"
          :to="`/decks/media/${node.deckId}/detail`"
          class="group relative z-10 flex w-24 flex-col rounded-md border bg-surface-0 transition sm:w-28 md:w-34 dark:bg-surface-900"
          :class="[
            isCurrent(node.deckId) ? 'border-primary ring-2 ring-primary' : 'border-surface-200 dark:border-surface-700 hover:border-primary',
            flashNode === node.deckId ? 'ring-2 ring-amber-400 dark:ring-amber-300' : '',
            nodeDimmed(node.deckId) ? 'opacity-30' : 'opacity-100',
          ]"
          :style="{
            gridColumn: (columnOf.get(node.deckId) ?? 0) + 2,
            gridRow: (rowOf.get(node.deckId) ?? 0) + 2,
            outline: flashNode === node.deckId || activeNode === node.deckId ? 'none' : coverageBorder(node),
            outlineOffset: '-2px',
          }"
          @mouseenter="!isCoarsePointer && activate(node.deckId)"
          @mouseleave="!isCoarsePointer && scheduleClear()"
          @focus="!isCoarsePointer && activate(node.deckId)"
          @blur="!isCoarsePointer && scheduleClear()"
          @click.capture="onCardClick($event, node.deckId)"
        >
          <img
            :src="coverSrc(node)"
            :alt="localiseTitle(node)"
            class="h-28 w-full rounded-t-md object-cover sm:h-32 md:h-40"
            loading="lazy"
            decoding="async"
            width="136"
            height="160"
          />
          <div class="flex flex-col gap-0.5 p-1.5">
            <span class="line-clamp-2 text-xs font-medium leading-tight" :title="localiseTitle(node)">
              {{ localiseTitle(node) }}
            </span>
            <div class="flex items-center justify-between gap-1 text-[11px]">
              <span class="text-gray-500 dark:text-gray-400">{{ releaseYear(node) ?? '?' }}</span>
              <DifficultyDisplay v-if="node.difficulty >= 0" :difficulty="node.difficulty" :difficulty-raw="node.difficultyRaw" class="text-[11px]" />
            </div>
          </div>
        </NuxtLink>
      </div>
    </div>

    <!-- Relations popover: names for every connection of the hovered/active node. FIXED to the
         viewport so the page can scroll underneath without dragging the popover off the pointer.
         Rows bring their target into view (hover, debounced) or navigate via the header link. -->
    <div
      v-if="activeNode != null && activeNodeData"
      ref="popoverRef"
      class="fixed z-50 flex max-h-72 flex-col overflow-y-auto rounded-md border border-surface-200 bg-surface-0 shadow-lg dark:border-surface-700 dark:bg-surface-900"
      :style="popoverStyle"
      @mouseenter="!isCoarsePointer && cancelClear()"
      @mouseleave="!isCoarsePointer && scheduleClear()"
    >
      <!-- Header: hovered deck title + explicit open link (touch navigation path). -->
      <div class="flex items-baseline justify-between gap-2 border-b border-surface-200 px-2 py-1.5 dark:border-surface-700">
        <span class="truncate text-xs font-semibold" :title="localiseTitle(activeNodeData)">{{ localiseTitle(activeNodeData) }}</span>
        <NuxtLink :to="`/decks/media/${activeNode}/detail`" class="shrink-0 text-xs font-semibold text-primary hover:underline">Open →</NuxtLink>
      </div>
      <div v-if="activeCaptions.length" class="flex flex-col gap-1 p-2">
        <button
          v-for="(c, i) in activeCaptions"
          :key="i"
          type="button"
          class="flex w-full items-baseline gap-1.5 rounded px-1 py-0.5 text-left text-xs hover:bg-surface-100 dark:hover:bg-surface-800"
          :title="`Go to ${c.otherTitle}`"
          @mouseenter="onRowHover(c.otherId)"
          @mouseleave="cancelRowHover"
          @click="scrollToNode(c.otherId)"
        >
          <span class="shrink-0 font-semibold text-gray-500 dark:text-gray-400">{{ c.label }}:</span>
          <span class="truncate text-surface-700 dark:text-surface-200">{{ c.otherTitle }}</span>
        </button>
      </div>
      <p v-else class="px-2 py-1.5 text-xs text-gray-500 dark:text-gray-400">No direct relations.</p>
    </div>
  </div>
</template>

<style scoped></style>
