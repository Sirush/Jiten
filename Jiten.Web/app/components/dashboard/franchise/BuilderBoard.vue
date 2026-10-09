<script setup lang="ts">
  import { ref, shallowRef, computed, watch, onMounted, onBeforeUnmount } from 'vue';
  import { getEdgeFlow, linkTypeInfo } from '~/utils/relationshipRoles';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { prefersReducedMotion } from '~/utils/reducedMotion';
  import {
    BOARD_METRICS,
    cardRemoveAction,
    linkSentence,
    linkToneClass,
    sameDropTarget,
    type BoardBand,
    type BoardLayout,
    type BuilderDeck,
    type BuilderDropTarget,
    type BuilderState,
  } from '~/utils/franchiseBuilder';

  type Point = { x: number; y: number };

  const { cardW: CARD_W, cardH: CARD_H, pad: BOARD_PAD, railW: RAIL_W } = BOARD_METRICS;

  const props = defineProps<{
    layout: BoardLayout;
    state: BuilderState;
    seriesName: (seriesId: number) => string;
    deck: (deckId: number) => BuilderDeck | undefined;
    title: (deckId: number) => string;
    selected: number[];
    linkFrom: number | null;
    dropTarget: BuilderDropTarget | null;
    dragSource: number | null;
    ghost: Point | null;
    focusEdgeId: number | null;
  }>();

  const emit = defineEmits<{
    cardDown: [deckId: number, ev: PointerEvent];
    cardKey: [deckId: number, ev: KeyboardEvent];
    remove: [deckId: number];
    leave: [seriesId: number, deckId: number];
    edge: [edgeId: number, anchor: Point];
    railNode: [seriesId: number, index: number, anchor: Point];
    rename: [seriesId: number];
    background: [];
    select: [deckIds: number[]];
    resize: [width: number];
  }>();

  const scrollEl = ref<HTMLElement | null>(null);
  const contentEl = ref<HTMLElement | null>(null);
  const pos = shallowRef(new Map<number, Point>());
  const hoverCard = ref<number | null>(null);
  const hoverEdge = ref<number | null>(null);

  let frame: number | null = null;
  function animate() {
    const target = props.layout.cards;
    const next = new Map<number, Point>();
    for (const [id, p] of target) next.set(id, pos.value.get(id) ?? { ...p });
    pos.value = next;
    if (import.meta.server || prefersReducedMotion()) {
      pos.value = new Map([...target].map(([id, p]) => [id, { ...p }]));
      return;
    }
    if (frame != null) return;
    const step = () => {
      let moving = false;
      const m = new Map<number, Point>();
      for (const [id, p] of props.layout.cards) {
        const c = pos.value.get(id) ?? p;
        const nx = c.x + (p.x - c.x) * 0.2;
        const ny = c.y + (p.y - c.y) * 0.2;
        if (Math.abs(p.x - nx) < 0.5 && Math.abs(p.y - ny) < 0.5) m.set(id, { ...p });
        else {
          m.set(id, { x: nx, y: ny });
          moving = true;
        }
      }
      pos.value = m;
      frame = moving ? requestAnimationFrame(step) : null;
    };
    frame = requestAnimationFrame(step);
  }
  watch(() => props.layout, animate, { immediate: true });

  let observer: ResizeObserver | null = null;
  onMounted(() => {
    if (!scrollEl.value) return;
    emit('resize', scrollEl.value.clientWidth);
    observer = new ResizeObserver(() => scrollEl.value && emit('resize', scrollEl.value.clientWidth));
    observer.observe(scrollEl.value);
  });
  onBeforeUnmount(() => {
    observer?.disconnect();
    if (frame != null) cancelAnimationFrame(frame);
  });

  const edges = computed(() => props.state.edges);

  interface CardAction {
    label: string;
    tip: string;
    keyHint: string;
    run: () => void;
  }

  const cardActions = computed(() => {
    const out = new Map<number, CardAction>();
    for (const id of props.layout.cards.keys()) {
      const action = cardRemoveAction(props.state, props.layout, id);
      if (action?.kind === 'leave') {
        const { seriesId } = action;
        const name = props.seriesName(seriesId);
        out.set(id, {
          label: `Take ${props.title(id)} out of ${name}`,
          tip: `Take out of ${name}`,
          keyHint: `take out of ${name}`,
          run: () => emit('leave', seriesId, id),
        });
      } else if (action) {
        out.set(id, {
          label: `Take ${props.title(id)} off the board`,
          tip: 'Take off the board',
          keyHint: 'take off the board',
          run: () => emit('remove', id),
        });
      }
    }
    return out;
  });

  function cardLabel(id: number): string {
    const hint = cardActions.value.get(id)?.keyHint;
    return `${props.title(id)}. Enter to link, Space to select${hint ? `, Delete to ${hint}` : ''}.`;
  }

  const fullTitleOf = (id: number) => [props.title(id), subtitleOf(id)?.text].filter(Boolean).join(' · ');

  function subtitleOf(id: number): { text: string; lang?: string } | null {
    const d = props.deck(id);
    if (!d) return null;
    const shown = props.title(id);
    if (d.originalTitle !== shown) return { text: d.originalTitle, lang: 'ja' };
    if (d.romajiTitle && d.romajiTitle !== shown) return { text: d.romajiTitle };
    return null;
  }

  const focusIds = computed<Set<number>>(() => {
    if (props.dragSource != null) return new Set();
    const id = hoverEdge.value ?? props.focusEdgeId;
    if (id != null) return new Set([id]);
    if (hoverCard.value == null) return new Set();
    return new Set(edges.value.filter((e) => e.sourceDeckId === hoverCard.value || e.targetDeckId === hoverCard.value).map((e) => e.id));
  });

  const roleTags = computed(() => {
    const id = hoverEdge.value ?? props.focusEdgeId;
    const e = id != null ? edges.value.find((x) => x.id === id) : undefined;
    const tags = new Map<number, { text: string; tone: string }>();
    if (!e) return tags;
    const info = linkTypeInfo(e.relationshipType);
    const flow = getEdgeFlow(e);
    tags.set(flow.from, { text: info.fromRole, tone: linkToneClass[info.tone] });
    tags.set(flow.to, { text: info.toRole, tone: linkToneClass[info.tone] });
    return tags;
  });

  function cubic(p0: Point, p1: Point, p2: Point, p3: Point) {
    const dx = p3.x - p2.x;
    const dy = p3.y - p2.y;
    const len = Math.hypot(dx, dy) || 1;
    const ux = dx / len;
    const uy = dy / len;
    const tip = p3;
    const base = { x: tip.x - ux * 10, y: tip.y - uy * 10 };
    return {
      d: `M${p0.x},${p0.y} C${p1.x},${p1.y} ${p2.x},${p2.y} ${p3.x},${p3.y}`,
      mid: { x: (p0.x + 3 * p1.x + 3 * p2.x + p3.x) / 8, y: (p0.y + 3 * p1.y + 3 * p2.y + p3.y) / 8 },
      head: `M${tip.x},${tip.y} L${base.x - uy * 5},${base.y + ux * 5} L${base.x + uy * 5},${base.y - ux * 5} Z`,
    };
  }

  function geom(fromId: number, toId: number) {
    const a = pos.value.get(fromId);
    const b = pos.value.get(toId);
    if (!a || !b) return null;
    if (Math.abs(a.x - b.x) < CARD_W * 0.6) {
      const down = a.y < b.y;
      const s = { x: a.x + CARD_W / 2, y: down ? a.y + CARD_H : a.y };
      const t = { x: b.x + CARD_W / 2, y: down ? b.y : b.y + CARD_H };
      const dy = (t.y - s.y) / 2;
      return cubic(s, { x: s.x, y: s.y + dy }, { x: t.x, y: t.y - dy }, t);
    }
    const right = a.x < b.x;
    const s = { x: right ? a.x + CARD_W : a.x, y: a.y + CARD_H / 2 };
    const t = { x: right ? b.x : b.x + CARD_W, y: b.y + CARD_H / 2 };
    const dx = Math.max(40, Math.abs(t.x - s.x) / 2) * (right ? 1 : -1);
    return cubic(s, { x: s.x + dx, y: s.y }, { x: t.x - dx, y: t.y }, t);
  }

  const drawnEdges = computed(() =>
    edges.value.flatMap((e) => {
      const flow = getEdgeFlow(e);
      const g = geom(flow.from, flow.to);
      if (!g) return [];
      const info = linkTypeInfo(e.relationshipType);
      const s = linkSentence(e);
      const status = e.status === 'new' ? 'Not saved yet' : e.status === 'removed' ? 'Will be removed when you save' : 'Saved';
      return [
        {
          edge: e,
          g,
          info,
          directed: flow.directed,
          tone: e.status === 'removed' ? 'text-red-700 dark:text-red-400' : linkToneClass[info.tone],
          label: `${props.title(s.first)} ${s.verb} ${props.title(s.second)}. ${status}. Open link options.`,
          status,
        },
      ];
    })
  );

  const ghostPath = computed(() => {
    if (!props.ghost || props.dragSource == null || !contentEl.value) return null;
    const a = pos.value.get(props.dragSource);
    if (!a) return null;
    const rc = contentEl.value.getBoundingClientRect();
    const p = { x: props.ghost.x - rc.left, y: props.ghost.y - rc.top };
    const s = { x: p.x >= a.x + CARD_W / 2 ? a.x + CARD_W : a.x, y: a.y + CARD_H / 2 };
    const dx = (p.x - s.x) / 2;
    return `M${s.x},${s.y} C${s.x + dx},${s.y} ${p.x - dx},${p.y} ${p.x},${p.y}`;
  });

  const plural = (n: number) => `${n} ${n === 1 ? 'deck' : 'decks'}`;

  function bandLabel(b: BoardBand): { lead: string; rest: string } {
    const seriesName = b.seriesId != null ? props.seriesName(b.seriesId) : '';
    switch (b.kind) {
      case 'line':
        return { lead: `${b.ids.length} linked decks`, rest: ` · in ${seriesName} through ${b.via.map(props.title).join(', ')}` };
      case 'run': {
        const years = b.ids.map((id) => props.deck(id)?.year).filter((y): y is number => y != null);
        const span = years.length ? (Math.min(...years) === Math.max(...years) ? `${years[0]}` : `${Math.min(...years)}–${Math.max(...years)}`) : '';
        return { lead: 'Standalone entries', rest: span ? ` · ${span}` : '' };
      }
      case 'empty':
        return { lead: 'No decks yet', rest: ' · drop a deck on the rail to add it' };
      case 'outside':
        return { lead: b.ids.length === 1 ? 'Unlinked deck' : `${b.ids.length} linked decks`, rest: ' · not in a series' };
      case 'unlinked':
        return { lead: 'Not linked yet', rest: ` · ${plural(b.ids.length)} · drag one onto the deck it relates to, or onto a series rail` };
    }
    return { lead: '', rest: '' };
  }

  const pendingNode = (seriesId: number, via: number[]) =>
    via.some((id) => props.state.members.some((m) => m.seriesId === seriesId && m.deckId === id && m.status === 'new'));

  const isDrop = (t: BuilderDropTarget) => sameDropTarget(props.dropTarget, t);

  function nodeLabel(seriesId: number, node: { kind: string; ids: number[]; via: number[] }) {
    if (node.kind === 'empty') return `${props.seriesName(seriesId)} is empty`;
    if (node.kind === 'line') return `Decks linked to ${props.title(node.via[0]!)} in ${props.seriesName(seriesId)}: options`;
    return `${node.ids.map(props.title).join(', ')} in ${props.seriesName(seriesId)}: options`;
  }

  function anchorOf(ev: Event): Point {
    const r = (ev.currentTarget as HTMLElement).getBoundingClientRect();
    return { x: r.left, y: r.bottom };
  }

  function onBackgroundDown(ev: PointerEvent) {
    if ((ev.target as Element).closest('[data-card], button, [data-edge]')) return;
    emit('background');
  }

  function autoScroll(clientX: number) {
    const el = scrollEl.value;
    if (!el) return;
    const r = el.getBoundingClientRect();
    if (clientX > r.right - 40) el.scrollLeft += 14;
    else if (clientX < r.left + 40) el.scrollLeft -= 14;
  }

  function cardAnchor(deckId: number): Point {
    const el = contentEl.value?.querySelector<HTMLElement>(`[data-card="${deckId}"]`);
    const r = el?.getBoundingClientRect();
    return r ? { x: r.right - 20, y: r.bottom - 10 } : { x: window.innerWidth / 2 - 200, y: 120 };
  }

  function focusCard(deckId: number) {
    contentEl.value?.querySelector<HTMLElement>(`[data-card="${deckId}"]`)?.focus({ preventScroll: true });
  }

  defineExpose({ autoScroll, cardAnchor, focusCard });
</script>

<template>
  <div ref="scrollEl" class="relative overflow-x-auto overflow-y-hidden bg-gray-50 dark:bg-gray-950">
    <div
      ref="contentEl"
      class="relative min-h-80"
      data-drop="board"
      :style="{ width: `${layout.width}px`, height: `${layout.height}px` }"
      @pointerdown="onBackgroundDown"
    >
      <template v-for="sec in layout.sections" :key="`sec-${sec.seriesId}`">
        <div
          class="absolute rounded-md transition-colors"
          :class="isDrop({ kind: 'series', id: sec.seriesId }) ? 'bg-primary-50 outline-2 outline-dashed outline-primary-500 dark:bg-primary-950/40' : ''"
          :style="{ left: `${sec.x - 6}px`, top: `${sec.dropTop}px`, width: `${RAIL_W}px`, height: `${sec.dropBottom - sec.dropTop}px` }"
          :data-drop="`series:${sec.seriesId}`"
        />
        <div
          class="absolute flex items-center gap-2 overflow-hidden whitespace-nowrap text-[13px] leading-6 text-gray-600 dark:text-gray-400"
          :style="{ left: `${sec.x}px`, top: `${sec.headY}px`, width: `${layout.width - BOARD_PAD - sec.x}px` }"
          :data-drop="`series:${sec.seriesId}`"
        >
          <span class="truncate">
            <b class="text-sm font-bold text-gray-900 dark:text-gray-100">{{ seriesName(sec.seriesId) }}</b>
            · {{ plural(sec.deckCount) }}
          </span>
          <Tooltip content="Rename">
            <button type="button" class="fb-icon-btn" :aria-label="`Rename ${seriesName(sec.seriesId)}`" @click="emit('rename', sec.seriesId)">
              <Icon name="material-symbols:edit-outline-rounded" />
            </button>
          </Tooltip>
        </div>
      </template>

      <div
        v-for="(b, i) in layout.bands"
        :key="`band-${i}`"
        class="absolute rounded-md border"
        :class="
          b.kind === 'empty' || b.kind === 'unlinked'
            ? 'border-dashed border-gray-300 bg-transparent dark:border-gray-600'
            : 'border-gray-200 bg-white dark:border-gray-800 dark:bg-gray-900'
        "
        :style="{ left: `${b.x}px`, top: `${b.y}px`, width: `${b.w}px`, height: `${b.h}px` }"
      >
        <span
          class="absolute left-3 top-1.5 truncate text-xs text-gray-500 dark:text-gray-400"
          :class="b.kind === 'unlinked' && b.ids.length > 1 ? 'max-w-[calc(100%-110px)]' : 'max-w-[calc(100%-24px)]'"
        >
          <b class="font-medium text-gray-900 dark:text-gray-100">{{ bandLabel(b).lead }}</b
          >{{ bandLabel(b).rest }}
        </span>
        <button
          v-if="b.kind === 'unlinked' && b.ids.length > 1"
          type="button"
          class="absolute right-2 top-0.5 rounded px-1.5 py-0.5 text-xs font-medium text-primary-700 hover:bg-primary-50 hover:underline focus-visible:outline-2 focus-visible:outline-primary-500 dark:text-primary-300 dark:hover:bg-primary-950/40"
          @click="emit('select', b.ids)"
        >
          Select all {{ b.ids.length }}
        </button>
      </div>

      <svg class="pointer-events-none absolute inset-0 overflow-visible" :width="layout.width" :height="layout.height" aria-hidden="true">
        <g v-for="sec in layout.sections" :key="`rail-${sec.seriesId}`" class="text-gray-700 dark:text-gray-300">
          <path
            :d="`M${sec.railX},${sec.headY + 30} V${sec.nodes[sec.nodes.length - 1]?.y ?? sec.headY + 30}`"
            stroke="currentColor"
            stroke-width="3"
            stroke-linecap="round"
            fill="none"
          />
          <path v-for="(n, i) in sec.nodes" :key="i" :d="`M${sec.railX},${n.y} H${sec.x + RAIL_W}`" stroke="currentColor" stroke-width="2" fill="none" />
        </g>
        <g
          v-for="d in drawnEdges"
          :key="`edge-${d.edge.id}`"
          :class="[d.tone, focusIds.size && !focusIds.has(d.edge.id) ? 'opacity-20' : '']"
          class="transition-opacity"
        >
          <path
            :d="d.g.d"
            fill="none"
            stroke="currentColor"
            :stroke-width="focusIds.has(d.edge.id) ? 3 : 2"
            :stroke-dasharray="d.edge.status === 'removed' ? '3 4' : d.directed ? undefined : '6 4'"
            :opacity="d.edge.status === 'removed' ? 0.6 : 1"
          />
          <path v-if="d.directed" :d="d.g.head" fill="currentColor" />
          <path
            :d="d.g.d"
            data-edge
            fill="none"
            stroke="transparent"
            stroke-width="14"
            class="cursor-pointer [pointer-events:stroke]"
            @click="emit('edge', d.edge.id, { x: $event.clientX, y: $event.clientY })"
            @mouseenter="hoverEdge = d.edge.id"
            @mouseleave="hoverEdge = null"
          />
        </g>
        <path v-if="ghostPath" :d="ghostPath" fill="none" stroke-width="2" stroke-dasharray="6 5" class="stroke-primary-600 dark:stroke-primary-400" />
      </svg>

      <div
        v-for="id in [...layout.cards.keys()]"
        :key="`card-${id}`"
        role="button"
        tabindex="0"
        :data-card="id"
        :data-drop="`deck:${id}`"
        :aria-label="cardLabel(id)"
        class="fb-card absolute z-[2] flex cursor-grab select-none flex-col gap-px rounded border py-[7px] pl-2.5 pr-[26px] focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary-500"
        :class="[
          isDrop({ kind: 'deck', id })
            ? 'border-primary-500 bg-primary-50 outline-2 outline-offset-2 outline-dashed outline-primary-500 dark:bg-primary-950/40'
            : 'border-gray-300 bg-white hover:border-primary-500 dark:border-gray-600 dark:bg-gray-900 dark:hover:border-primary-400',
          selected.includes(id) ? 'outline-2 outline-offset-1 outline-primary-500' : '',
          linkFrom === id ? 'outline-2 outline-offset-2 outline-dashed outline-primary-500' : '',
          dragSource === id ? 'opacity-55' : '',
          linkFrom != null ? 'cursor-crosshair' : '',
        ]"
        :style="{ left: `${pos.get(id)?.x ?? 0}px`, top: `${pos.get(id)?.y ?? 0}px`, width: `${CARD_W}px`, height: `${CARD_H}px` }"
        @pointerdown="emit('cardDown', id, $event)"
        @keydown="emit('cardKey', id, $event)"
        @mouseenter="hoverCard = id"
        @mouseleave="hoverCard = null"
      >
        <span
          v-if="roleTags.get(id)"
          class="absolute -top-2.5 left-2 whitespace-nowrap rounded-sm bg-white px-1.5 py-0.5 text-[10.5px] font-bold leading-none ring-1 ring-current dark:bg-gray-900"
          :class="roleTags.get(id)!.tone"
        >
          {{ roleTags.get(id)!.text }}
        </span>
        <span class="text-[11px] leading-4 tabular-nums text-gray-500 dark:text-gray-400">
          {{ deck(id) ? getMediaTypeText(deck(id)!.mediaType) : '' }}<template v-if="deck(id)?.year"> · {{ deck(id)!.year }}</template>
        </span>
        <Tooltip :content="fullTitleOf(id)">
          <span class="line-clamp-2 text-[13px] font-medium leading-[1.2] text-gray-900 dark:text-gray-100" v-bind="japaneseTextAttrs(title(id))">{{
            title(id)
          }}</span>
        </Tooltip>
        <span v-if="subtitleOf(id)" :lang="subtitleOf(id)!.lang" class="mt-auto truncate text-[11px] leading-4 text-gray-500 dark:text-gray-400">
          {{ subtitleOf(id)!.text }}
        </span>
        <button
          type="button"
          tabindex="-1"
          aria-hidden="true"
          data-link-handle
          class="absolute -right-3 top-1/2 -mt-3 grid h-6 w-6 cursor-crosshair place-items-center rounded-full border border-gray-300 bg-white p-0 text-gray-500 hover:border-primary-500 hover:text-primary-600 dark:border-gray-600 dark:bg-gray-900 dark:text-gray-400 dark:hover:text-primary-400"
        >
          <Icon name="material-symbols:arrow-forward-rounded" size="14" />
        </button>
        <Tooltip v-if="cardActions.get(id)" :content="cardActions.get(id)!.tip">
          <button
            type="button"
            class="fb-remove absolute right-1 top-1 hidden h-[18px] w-[18px] place-items-center rounded-sm p-0 text-gray-500 hover:bg-red-50 hover:text-red-700 dark:text-gray-400 dark:hover:bg-red-950 dark:hover:text-red-400"
            :aria-label="cardActions.get(id)!.label"
            @pointerdown.stop
            @click.stop="cardActions.get(id)!.run()"
          >
            <Icon name="material-symbols:close-rounded" size="12" />
          </button>
        </Tooltip>
      </div>

      <button
        v-for="d in drawnEdges"
        :key="`pill-${d.edge.id}`"
        type="button"
        class="absolute z-[3] inline-flex -translate-x-1/2 -translate-y-1/2 items-center gap-1 whitespace-nowrap rounded-sm border border-current bg-white px-[7px] py-1 text-[11px] font-medium leading-none transition-opacity hover:bg-gray-100 dark:bg-gray-900 dark:hover:bg-gray-800"
        :class="[d.tone, d.edge.status === 'removed' ? 'border-dashed line-through' : '', focusIds.size && !focusIds.has(d.edge.id) ? 'opacity-30' : '']"
        :style="{ left: `${d.g.mid.x}px`, top: `${d.g.mid.y}px` }"
        :aria-label="d.label"
        @click="emit('edge', d.edge.id, anchorOf($event))"
        @mouseenter="hoverEdge = d.edge.id"
        @mouseleave="hoverEdge = null"
        @focus="hoverEdge = d.edge.id"
        @blur="hoverEdge = null"
      >
        <span v-if="d.edge.status === 'new'" class="h-1.5 w-1.5 rounded-full bg-current" aria-hidden="true" />
        {{ d.info.label }}
      </button>

      <template v-for="sec in layout.sections" :key="`nodes-${sec.seriesId}`">
        <button
          v-for="(n, i) in sec.nodes"
          :key="i"
          type="button"
          class="absolute z-[3] h-[18px] w-[18px] -translate-x-1/2 -translate-y-1/2 rounded-full border-[3px] bg-white p-0 hover:bg-gray-700 focus-visible:bg-gray-700 dark:bg-gray-900 dark:hover:bg-gray-300 dark:focus-visible:bg-gray-300"
          :class="[
            pendingNode(sec.seriesId, n.via) ? 'border-primary-500 ring-[3px] ring-primary-100 dark:ring-primary-900' : 'border-gray-700 dark:border-gray-300',
            n.kind === 'empty' ? 'border-dashed' : '',
          ]"
          :style="{ left: `${sec.railX}px`, top: `${n.y}px` }"
          :aria-label="nodeLabel(sec.seriesId, n)"
          :data-drop="`series:${sec.seriesId}`"
          @click="emit('railNode', sec.seriesId, i, anchorOf($event))"
        />
      </template>
    </div>
  </div>
</template>

<style scoped>
  .fb-card {
    touch-action: none;
  }

  .fb-card:hover .fb-remove,
  .fb-card:focus-within .fb-remove {
    display: grid;
  }

  .fb-icon-btn {
    display: inline-grid;
    place-items: center;
    width: 22px;
    height: 22px;
    border-radius: 3px;
    color: inherit;
  }

  .fb-icon-btn:hover,
  .fb-icon-btn:focus-visible {
    background: var(--p-surface-200);
    color: var(--p-surface-900);
  }

  :global(.dark-mode .fb-icon-btn:hover),
  :global(.dark-mode .fb-icon-btn:focus-visible) {
    background: var(--p-surface-700);
    color: var(--p-surface-50);
  }
</style>
