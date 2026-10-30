<script setup lang="ts">
  import type { ProfileColumn } from '~/types';
  import { formatCompact } from '~/utils/siteStats';

  const props = withDefaults(
    defineProps<{
      title: string;
      columns: ProfileColumn[];
      caption?: string;
      /** 'fit' shows a value only where it fits its column, for long series on narrow screens; the tooltip always carries it. */
      showValues?: boolean | 'fit';
      /** For time series, where one bulk-import month would otherwise flatten every other column. */
      clipOutliers?: boolean;
      /** Minimum column width in px; a longer series scrolls sideways and opens on its last column. */
      columnWidth?: number;
      /** Columns that fit the card at once; a longer series scrolls sideways and opens on its last column. */
      visibleColumns?: number;
    }>(),
    { caption: undefined, showValues: true, clipOutliers: false, columnWidth: undefined, visibleColumns: undefined }
  );

  /** Narrowest a scrolling column gets on small screens, so its label and value still fit, in px. */
  const MIN_SCROLL_COLUMN_WIDTH = 36;
  const scrolls = computed(() => !!props.columnWidth || (!!props.visibleColumns && props.columns.length > props.visibleColumns));
  /** Sized in CSS rather than measured, so the server render already has the final width and nothing jumps on hydration. */
  const trackMinWidth = computed(() => {
    const count = props.columns.length;
    if (props.columnWidth) return `${count * props.columnWidth}px`;
    if (!scrolls.value) return undefined;
    return `max(${count * MIN_SCROLL_COLUMN_WIDTH}px, ${(count / props.visibleColumns!) * 100}%)`;
  });

  /** Widest a column grows in a wide card before the series is centred instead, unless a label needs more, in px. */
  const MAX_COLUMN_WIDTH = 48;
  const columnCapPx = computed(() => Math.max(MAX_COLUMN_WIDTH, widestLabel.value + 8));
  /** Capped columns take their cap as width and shrink on narrow cards, so the track and its baseline hug the series. */
  const columnStyle = computed(() => (scrolls.value ? undefined : { flex: '0 1 auto', width: `${columnCapPx.value}px` }));
  const trackClass = computed(() => (scrolls.value ? 'w-full' : 'mx-auto w-fit max-w-full'));

  const peak = computed(() => Math.max(0, ...props.columns.map((c) => c.value)));
  function barClass(col: ProfileColumn) {
    if (col.colour) return 'group-hover:brightness-110 group-focus-visible:brightness-110';
    return col.value === peak.value
      ? 'bg-primary-500 group-hover:bg-primary-600 group-focus-visible:bg-primary-600 dark:bg-primary-400 dark:group-hover:bg-primary-300'
      : 'bg-primary-300 group-hover:bg-primary-400 group-focus-visible:bg-primary-400 dark:bg-primary-400/35 dark:group-hover:bg-primary-400/60 dark:group-focus-visible:bg-primary-400/60';
  }

  /** A column more than three times the next tallest is drawn 1.6 times its height, so it still clearly dominates. */
  const scale = computed(() => {
    const sorted = props.columns.map((c) => c.value).sort((a, b) => b - a);
    const [top = 0, second = 0] = sorted;
    if (props.clipOutliers && second > 0 && top > second * 3) return { max: second * 1.6, clipped: true };
    return { max: Math.max(1, top), clipped: false };
  });

  const isClipped = (value: number) => scale.value.clipped && value > scale.value.max;
  const heightOf = (value: number) => (value > 0 ? `max(2px, ${(Math.min(value, scale.value.max) / scale.value.max) * 80}%)` : '0');
  const headingId = useId();

  /** Approximate width of one 11px label character, in px, used to thin labels before they collide. */
  const LABEL_CHAR_WIDTH = 5.5;
  const axisEl = ref<HTMLElement | null>(null);
  const axisWidth = ref(0);
  const renderedColumnWidth = computed(() => {
    const share = axisWidth.value / Math.max(1, props.columns.length);
    return scrolls.value ? share : Math.min(share, columnCapPx.value);
  });
  const widestLabel = computed(() => Math.max(1, ...props.columns.map((c) => c.label.length)) * LABEL_CHAR_WIDTH + 4);
  /** Labels and fitted values wait for the first measurement, so they appear once instead of being thinned after hydration. */
  const measured = computed(() => axisWidth.value > 0);
  const labelStep = computed(() => (measured.value ? Math.max(1, Math.ceil(widestLabel.value / renderedColumnWidth.value)) : 1));
  let observer: ResizeObserver | undefined;
  onMounted(() => {
    observer = new ResizeObserver(([entry]) => {
      axisWidth.value = entry?.contentRect.width ?? 0;
    });
    if (axisEl.value) observer.observe(axisEl.value);
  });
  onBeforeUnmount(() => observer?.disconnect());

  const scroller = ref<HTMLElement | null>(null);
  function scrollToLatest() {
    if (scroller.value && scrolls.value) scroller.value.scrollLeft = scroller.value.scrollWidth;
  }
  onMounted(scrollToLatest);
  watch([() => props.columns.length, scrolls], () => nextTick(scrollToLatest));

  function showsValue(value: number) {
    if (value <= 0) return false;
    if (props.showValues !== 'fit') return props.showValues;
    return renderedColumnWidth.value >= formatCompact(value).length * LABEL_CHAR_WIDTH + 4;
  }
</script>

<template>
  <section :aria-labelledby="headingId" class="flex flex-col gap-3">
    <div class="flex flex-wrap items-center justify-between gap-2">
      <h3 :id="headingId" class="text-sm font-semibold">{{ title }}</h3>
      <slot name="actions" />
    </div>
    <div ref="scroller" :class="{ 'scrollbar-styled overflow-x-auto pb-1': scrolls }">
      <div class="flex flex-col gap-1" :class="{ 'px-1': scrolls }" :style="trackMinWidth ? { minWidth: trackMinWidth } : undefined">
        <ul class="flex h-36 items-stretch gap-1 border-b border-surface-300 pt-4 dark:border-surface-600" :class="trackClass">
          <li v-for="col in columns" :key="col.key" class="relative min-w-0 flex-1" :style="columnStyle">
            <Tooltip :content="col.value > 0 ? col.tooltip : ''" block>
              <div
                :tabindex="col.value > 0 ? 0 : -1"
                :aria-label="`${col.label}: ${col.tooltip}`"
                class="group absolute inset-0 flex flex-col justify-end rounded-t focus-visible:outline-2 focus-visible:outline-primary-500"
              >
                <span
                  v-if="showsValue(col.value) || isClipped(col.value)"
                  class="mb-0.5 text-center text-[11px] tabular-nums text-muted-color"
                  :class="{ invisible: showValues === 'fit' && !measured }"
                >
                  {{ formatCompact(col.value) }}
                </span>
                <div
                  class="relative rounded-t transition-[background-color,filter]"
                  :class="barClass(col)"
                  :style="{ height: heightOf(col.value), backgroundColor: col.colour }"
                >
                  <span
                    v-if="isClipped(col.value)"
                    class="absolute -inset-x-1 top-[22%] h-2.5 -skew-y-12 border-y-2 border-surface-500 bg-surface-0 dark:border-surface-400 dark:bg-surface-900"
                    aria-hidden="true"
                  />
                </div>
              </div>
            </Tooltip>
          </li>
        </ul>
        <div ref="axisEl" class="flex h-4 gap-1" :class="trackClass" aria-hidden="true">
          <span v-for="(col, i) in columns" :key="col.key" class="relative min-w-0 flex-1" :style="columnStyle">
            <span
              v-if="i % labelStep === 0"
              class="absolute left-1/2 -translate-x-1/2 whitespace-nowrap text-[11px] text-muted-color"
              :class="{ invisible: !measured }"
              >{{ col.label }}</span
            >
          </span>
        </div>
      </div>
    </div>
    <p v-if="caption || scale.clipped" class="-mt-1 text-xs text-muted-color">
      {{ [caption, scale.clipped ? 'The tallest column is cut to keep the others readable.' : null].filter(Boolean).join(' ') }}
    </p>
  </section>
</template>
