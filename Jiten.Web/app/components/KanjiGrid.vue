<script lang="ts">
  import type { KanjiGridItem, KanjiGridResponse } from '~/types';
  import { displayTypeOptions, groupKanji } from '~/data/kanjiGroupings';
  import type { KanjiGroupStats } from '~/utils/kanjiGridStats';
  import { useJitenStore } from '~/stores/jitenStore';

  /** Last response per profile, painted at once on a revisit while a fresh copy loads. */
  const gridCache = new Map<string, KanjiGridResponse>();
</script>

<script setup lang="ts">
  const props = defineProps<{
    username: string;
  }>();

  const MIN_CELL_REM = 2;
  const CELL_GAP_PX = 2;
  const CELLS_PER_CHUNK = 256;
  const CHUNKS_PER_IDLE_SLICE = 6;
  const IDLE_FILL_TIMEOUT_MS = 500;
  const VISIBLE_MARGIN_PX = 200;
  const SKELETON_CELLS = 600;

  const { $api } = useNuxtApp();
  const jitenStore = useJitenStore();

  const displayType = toRef(jitenStore, 'kanjiGridGrouping');
  const onlySeen = toRef(jitenStore, 'kanjiGridOnlyTracked');
  const gridData = shallowRef<KanjiGridResponse | null>(gridCache.get(props.username) ?? null);
  const error = ref<string | null>(null);
  const gridRef = ref<HTMLElement | null>(null);

  const tooltip = ref({ visible: false, lines: [] as string[], x: 0, y: 0 });

  const fetchKanjiGrid = async () => {
    error.value = null;

    try {
      const data = await $api<KanjiGridResponse>(profileApiPath(props.username, 'kanji-grid'));
      gridCache.set(props.username, data);
      const shown = gridData.value;
      if (!shown || shown.lastComputedAt !== data.lastComputedAt || shown.totalKanjiCount !== data.totalKanjiCount) gridData.value = data;
    } catch (err: unknown) {
      if (gridData.value) return;
      const fetchError = err as { response?: { status?: number } };
      error.value = fetchError?.response?.status === 404 ? 'Kanji data not available' : 'Failed to load kanji grid';
    }
  };

  const getKanjiColour = (score: number): string => {
    if (score <= 0) return 'rgb(128,128,128)';

    const t = Math.min(score, 1);
    if (t <= 0.5) {
      const p = t / 0.5;
      const r = Math.round(128 + (220 - 128) * p);
      const g = Math.round(128 + (38 - 128) * p);
      const b = Math.round(128 + (38 - 128) * p);
      return `rgb(${r},${g},${b})`;
    }

    const p = (t - 0.5) / 0.5;
    const r = Math.round(220 + (34 - 220) * p);
    const g = Math.round(38 + (197 - 38) * p);
    const b = Math.round(38 + (94 - 38) * p);
    return `rgb(${r},${g},${b})`;
  };

  const tooltipLines = (k: KanjiGridItem): string[] => {
    const lines = [`${k.character}: ${(k.score * 100).toFixed(0)}% (${k.wordCount} words)`];
    for (const r of k.readings ?? []) {
      lines.push(`${r.reading}: ${Number(r.known.toFixed(2))}/${r.required} (${(r.weight * 100).toFixed(0)}% weight)`);
    }
    return lines;
  };

  const kanjiByCharacter = computed(() => new Map((gridData.value?.kanji ?? []).map((k) => [k.character, k])));

  /** Untracked cells carry no inline style; the stylesheet gives them the 0% grey. */
  const cellHtml = computed(() => {
    const html = new Map<KanjiGridItem, string>();
    for (const k of gridData.value?.kanji ?? []) {
      const ch = escapeHtml(k.character);
      html.set(
        k,
        k.score > 0 ? `<a href="/kanji/${ch}" style="background:${getKanjiColour(k.score)};color:white">${ch}</a>` : `<a href="/kanji/${ch}">${ch}</a>`
      );
    }
    return html;
  });

  const groups = computed(() => {
    if (!gridData.value?.kanji) return [];
    return groupKanji(gridData.value.kanji, displayType.value);
  });

  const groupStats = computed(() => groups.value.map((g) => kanjiGroupStats(g.kanji)));

  const groupHeaderHtml = (name: string, stats: KanjiGroupStats): string => {
    const seenPct = stats.total > 0 ? ((stats.seen / stats.total) * 100).toFixed(0) : '0';
    const masteredPct = stats.total > 0 ? ((stats.mastered / stats.total) * 100).toFixed(0) : '0';
    return (
      `<div class="kanji-group-header"><span class="kanji-group-title">${escapeHtml(name)}</span>` +
      `<span class="kanji-group-stats">${stats.seen}/${stats.total} seen (${seenPct}%) · ${stats.mastered} mastered (${masteredPct}%)</span></div>`
    );
  };

  const pendingChunks = new Map<HTMLElement, KanjiGridItem[]>();
  let chunkObserver: IntersectionObserver | null = null;
  let resizeObserver: ResizeObserver | null = null;
  let observedContainer: HTMLElement | null = null;
  let cancelIdleFill: (() => void) | null = null;
  let renderedCols = 0;

  const fillChunk = (el: HTMLElement) => {
    const cells = pendingChunks.get(el);
    if (!cells) return;
    pendingChunks.delete(el);
    chunkObserver?.unobserve(el);
    el.innerHTML = cells.map((k) => cellHtml.value.get(k)).join('');
  };

  const onIdle = (callback: () => void): (() => void) => {
    if (typeof window.requestIdleCallback === 'function') {
      const id = window.requestIdleCallback(callback, { timeout: IDLE_FILL_TIMEOUT_MS });
      return () => window.cancelIdleCallback(id);
    }
    const id = setTimeout(callback, 16);
    return () => clearTimeout(id);
  };

  /** Fills off-screen chunks a few at a time, so find-in-page reaches every kanji without one long frame. */
  const scheduleIdleFill = () => {
    cancelIdleFill = onIdle(() => {
      let filled = 0;
      for (const el of pendingChunks.keys()) {
        fillChunk(el);
        if (++filled === CHUNKS_PER_IDLE_SLICE) break;
      }
      if (pendingChunks.size) scheduleIdleFill();
      else cancelIdleFill = null;
    });
  };

  const resetChunks = () => {
    cancelIdleFill?.();
    cancelIdleFill = null;
    chunkObserver?.disconnect();
    pendingChunks.clear();
  };

  const columnCount = (width: number): number => {
    const minCell = MIN_CELL_REM * parseFloat(getComputedStyle(document.documentElement).fontSize);
    return Math.max(1, Math.floor((width + CELL_GAP_PX) / (minCell + CELL_GAP_PX)));
  };

  /** Chunks hold whole rows, so stacked chunks read as one grid; content-visibility then skips layout for every chunk off screen. */
  const renderGrid = () => {
    const container = gridRef.value;
    if (!container || !gridData.value?.kanji) return;
    resetChunks();

    const width = container.clientWidth;
    const cols = columnCount(width);
    const cellSize = (width - (cols - 1) * CELL_GAP_PX) / cols;
    const chunkSize = Math.max(1, Math.round(CELLS_PER_CHUNK / cols)) * cols;
    const chunkCells: KanjiGridItem[][] = [];
    let html = '';

    for (let gi = 0; gi < groups.value.length; gi++) {
      const group = groups.value[gi]!;
      if (group.name) html += groupHeaderHtml(group.name, groupStats.value[gi]!);

      const cells = onlySeen.value ? group.kanji.filter(isTrackedKanji) : group.kanji;
      if (cells.length === 0) continue;

      html += '<div class="kanji-chunks">';
      for (let i = 0; i < cells.length; i += chunkSize) {
        const slice = cells.slice(i, i + chunkSize);
        const rows = Math.ceil(slice.length / cols);
        const height = Math.round(rows * cellSize + (rows - 1) * CELL_GAP_PX);
        html += `<div class="kanji-chunk" style="contain-intrinsic-size:auto ${height}px"></div>`;
        chunkCells.push(slice);
      }
      html += '</div>';
    }

    renderedCols = cols;
    container.style.setProperty('--kanji-cols', String(cols));
    container.innerHTML = html;

    const chunks = [...container.querySelectorAll<HTMLElement>('.kanji-chunk')];
    chunks.forEach((el, i) => pendingChunks.set(el, chunkCells[i]!));

    const top = -VISIBLE_MARGIN_PX;
    const bottom = window.innerHeight + VISIBLE_MARGIN_PX;
    const visible = chunks.filter((el) => {
      const rect = el.getBoundingClientRect();
      return rect.bottom > top && rect.top < bottom;
    });
    visible.forEach(fillChunk);

    chunkObserver ??= new IntersectionObserver(
      (entries) => {
        for (const entry of entries) if (entry.isIntersecting) fillChunk(entry.target as HTMLElement);
      },
      { rootMargin: '50% 0px' }
    );
    for (const el of pendingChunks.keys()) chunkObserver.observe(el);
    if (pendingChunks.size) scheduleIdleFill();

    if (observedContainer !== container) {
      resizeObserver ??= new ResizeObserver(([entry]) => {
        if (entry && columnCount(entry.contentRect.width) !== renderedCols) requestAnimationFrame(renderGrid);
      });
      if (observedContainer) resizeObserver.unobserve(observedContainer);
      resizeObserver.observe(container);
      observedContainer = container;
    }
  };

  /** Keeps kanji clicks inside the app, so returning to the grid repaints from the cache instead of reloading the site. */
  const openKanji = (e: MouseEvent) => {
    if (e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
    const cell = (e.target as HTMLElement).closest('.kanji-chunk > a');
    const href = cell?.getAttribute('href');
    if (!href) return;
    e.preventDefault();
    navigateTo(href);
  };

  const seenPercentage = computed(() => {
    if (!gridData.value) return 0;
    return ((gridData.value.seenKanjiCount / gridData.value.totalKanjiCount) * 100).toFixed(1);
  });

  const masteredCount = computed(() => {
    if (!gridData.value?.kanji) return 0;
    return gridData.value.kanji.filter(isMasteredKanji).length;
  });

  const masteredPercentage = computed(() => {
    if (!gridData.value) return 0;
    return ((masteredCount.value / gridData.value.totalKanjiCount) * 100).toFixed(1);
  });

  const showTooltip = (e: MouseEvent) => {
    const cell = (e.target as HTMLElement).closest('.kanji-chunk > a');
    const kanji = cell && kanjiByCharacter.value.get(cell.textContent ?? '');
    if (!kanji) return;
    tooltip.value = { visible: true, lines: tooltipLines(kanji), x: e.clientX + 10, y: e.clientY + 10 };
  };

  const moveTooltip = (e: MouseEvent) => {
    if (tooltip.value.visible) {
      tooltip.value.x = e.clientX + 10;
      tooltip.value.y = e.clientY + 10;
    }
  };

  const hideTooltip = () => {
    tooltip.value.visible = false;
  };

  watch([gridData, displayType, onlySeen], () => nextTick(renderGrid));

  onMounted(() => {
    renderGrid();
    fetchKanjiGrid();
  });

  onBeforeUnmount(() => {
    resetChunks();
    resizeObserver?.disconnect();
  });
</script>

<template>
  <div class="kanji-grid-container">
    <div v-if="error" class="text-center py-4">
      <Message severity="warn">{{ error }}</Message>
    </div>

    <div v-else class="flex flex-col gap-4">
      <div class="flex justify-between items-center flex-wrap gap-4">
        <div v-if="gridData" class="flex gap-4 text-sm text-surface-500 dark:text-surface-400">
          <span>{{ gridData.seenKanjiCount }} / {{ gridData.totalKanjiCount }} kanji tracked ({{ seenPercentage }}%)</span>
          <span>{{ masteredCount }} mastered ({{ masteredPercentage }}%)</span>
        </div>
        <div v-else class="flex gap-4" aria-hidden="true">
          <Skeleton width="15rem" height="1.25rem" />
          <Skeleton width="8rem" height="1.25rem" />
        </div>

        <div class="flex items-center gap-3">
          <Select v-model="displayType" :options="displayTypeOptions" option-label="label" option-value="value" class="w-48" />
          <div class="flex items-center gap-2">
            <Checkbox v-model="onlySeen" input-id="only-seen" :binary="true" />
            <label for="only-seen" class="text-sm cursor-pointer">Only tracked</label>
          </div>
        </div>
      </div>

      <div class="flex items-center gap-2 text-sm text-surface-600 dark:text-surface-400">
        <span class="flex items-center gap-1">
          Reading coverage:
          <Icon
            v-tooltip="'Score reflects how many readings of each kanji you know, weighted by reading frequency. Young card only count for half a point. 90%+ = mastered.'"
            name="material-symbols:info-outline"
            class="text-primary-700 dark:text-primary-300 cursor-help"
            style="font-size: 1rem"
          />
        </span>
        <div class="flex items-center gap-1">
          <div class="w-4 h-4 rounded" style="background: rgb(128, 128, 128)" />
          <span>0%</span>
        </div>
        <span class="mx-1">→</span>
        <div class="flex items-center gap-1">
          <div class="w-4 h-4 rounded" style="background: rgb(220, 38, 38)" />
          <span>50%</span>
        </div>
        <span class="mx-1">→</span>
        <div class="flex items-center gap-1">
          <div class="w-4 h-4 rounded" style="background: rgb(34, 197, 94)" />
          <span>100%</span>
        </div>
      </div>

      <Teleport to="body">
        <div v-show="tooltip.visible" class="kanji-tooltip" :style="{ left: tooltip.x + 'px', top: tooltip.y + 'px' }">
          <div v-for="(line, i) in tooltip.lines" :key="i">{{ line }}</div>
        </div>
      </Teleport>

      <div
        v-if="gridData"
        ref="gridRef"
        class="kanji-groups-container"
        lang="ja"
        @mouseover="showTooltip"
        @mousemove="moveTooltip"
        @mouseleave="hideTooltip"
        @click="openKanji"
      />
      <div v-else class="kanji-grid-skeleton" aria-hidden="true">
        <div v-for="i in SKELETON_CELLS" :key="i" />
      </div>
    </div>
  </div>
</template>

<style scoped>
  .kanji-groups-container {
    display: flex;
    flex-direction: column;
    gap: 1rem;
  }

  .kanji-grid-skeleton {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(2rem, 1fr));
    gap: 2px;
    max-height: 100vh;
    overflow: hidden;
    animation: kanji-skeleton-pulse 1.5s ease-in-out infinite;
  }

  .kanji-grid-skeleton > div {
    aspect-ratio: 1;
    border-radius: var(--radius-sm);
    background: var(--p-skeleton-background);
  }

  @keyframes kanji-skeleton-pulse {
    50% {
      opacity: 0.5;
    }
  }

  @media (prefers-reduced-motion: reduce) {
    .kanji-grid-skeleton {
      animation: none;
    }
  }
</style>

<style>
  .kanji-chunks {
    display: flex;
    flex-direction: column;
    gap: 2px;
  }

  /* The padding is paint room for hover effects that content-visibility would clip; the margin and pointer-events undo its layout and hit-testing side effects. */
  .kanji-chunk {
    display: grid;
    grid-template-columns: repeat(var(--kanji-cols), minmax(0, 1fr));
    gap: 2px;
    padding: 8px;
    margin: -8px;
    content-visibility: auto;
    pointer-events: none;
  }

  .kanji-chunk:hover {
    z-index: 1;
  }

  .kanji-group-header {
    display: flex;
    align-items: baseline;
    gap: 0.75rem;
    padding: 0.5rem 0 0.25rem;
    border-bottom: 1px solid var(--p-surface-200);
  }

  :root.dark-mode .kanji-group-header {
    border-bottom-color: var(--p-surface-700);
  }

  .kanji-group-title {
    font-weight: 600;
    font-size: 0.95rem;
    color: var(--p-surface-800);
  }

  :root.dark-mode .kanji-group-title {
    color: var(--p-surface-100);
  }

  .kanji-group-stats {
    font-size: 0.8rem;
    color: var(--p-surface-500);
  }

  .kanji-chunk > a {
    display: flex;
    align-items: center;
    justify-content: center;
    aspect-ratio: 1;
    font-size: 1.25rem;
    font-weight: 500;
    border-radius: var(--radius-sm);
    background: rgb(128, 128, 128);
    color: inherit;
    pointer-events: auto;
    transition:
      transform 0.1s,
      box-shadow 0.1s;
    text-decoration: none;
  }

  .kanji-chunk > a:hover {
    transform: scale(1.1);
    box-shadow: 0 2px 8px rgba(0, 0, 0, 0.2);
    z-index: 1;
  }

  .kanji-tooltip {
    position: fixed;
    z-index: 9999;
    padding: 0.5rem 0.75rem;
    background: var(--p-surface-800);
    color: var(--p-surface-0);
    border-radius: var(--radius-md);
    font-size: 0.875rem;
    pointer-events: none;
    white-space: nowrap;
    box-shadow: 0 4px 12px rgba(0, 0, 0, 0.3);
  }
</style>
