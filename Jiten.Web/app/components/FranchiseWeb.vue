<script setup lang="ts">
  import type { Franchise, FranchiseNode, FranchiseEdge } from '~/types';
  import { useJitenStore } from '~/stores/jitenStore';
  import { useAuthStore } from '~/stores/authStore';
  import { getCoverageBorder } from '~/utils/coverageBorder';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { useFranchiseGraph, forwardLabels, inverseLabels } from '~/composables/useFranchiseGraph';

  const props = defineProps<{
    franchise: Franchise;
    currentDeckId: number;
  }>();

  const store = useJitenStore();
  const authStore = useAuthStore();

  const franchiseRef = computed(() => props.franchise);
  const { localiseTitle, edges, nodeById, releaseYear, coverSrc, captionsFor, useAdjacentNodes, edgeActive: edgeActiveOf } = useFranchiseGraph(franchiseRef);

  // ---- Projection constants (single source of truth for cards AND the SVG edge overlay) ----
  const P = 1000; // perspective focal distance
  const ZRANGE = 600; // world-units a unit of normalised depth maps to
  const REST_LENGTH = 260; // spring rest length between linked nodes
  const COLLISION_DIST = 230; // min centre distance — cards are ~112x190, so anything below ~220 overlaps
  const REPULSION = 20000; // pairwise repulsion constant
  // Beyond this, pairs stop repelling. Without a cutoff, the summed long-range push from the
  // whole cluster stretches degree-1 chains (sequel runs) absurdly far from the centre.
  const REPULSION_CUTOFF = 520; // ~2x rest length
  const MIN_ZOOM = 0.4;
  const MAX_ZOOM = 2.5;
  const CARD_W = 112; // px at scale 1 (w-28)

  // ---- Simulation state (world coordinates, centred on origin) ----
  interface SimNode {
    id: number;
    x: number;
    y: number;
    vx: number;
    vy: number;
    z: number; // normalised depth [0,1]; current deck = 0
  }

  const simNodes = ref<SimNode[]>([]);
  const containerRef = ref<HTMLElement | null>(null);
  const center = reactive({ x: 0, y: 0 });
  const pan = reactive({ x: 0, y: 0 });
  const zoom = ref(1);

  // Deterministic small hash → stable jitter so layout is identical across visits (no Math.random).
  function hash(n: number): number {
    let h = (n ^ 0x9e3779b9) >>> 0;
    h = Math.imul(h ^ (h >>> 16), 0x45d9f3b) >>> 0;
    h = Math.imul(h ^ (h >>> 16), 0x45d9f3b) >>> 0;
    h = (h ^ (h >>> 16)) >>> 0;
    return h / 0xffffffff; // [0,1)
  }

  // BFS graph distance from a root deck over the (undirected) edges, normalised to [0,1].
  function computeDepths(rootId: number): Map<number, number> {
    const adj = new Map<number, number[]>();
    for (const n of props.franchise.nodes) adj.set(n.deckId, []);
    for (const e of edges.value) {
      adj.get(e.sourceDeckId)?.push(e.targetDeckId);
      adj.get(e.targetDeckId)?.push(e.sourceDeckId);
    }
    const dist = new Map<number, number>();
    const queue: number[] = [];
    if (adj.has(rootId)) {
      dist.set(rootId, 0);
      queue.push(rootId);
    }
    let head = 0;
    while (head < queue.length) {
      const cur = queue[head++]!;
      const d = dist.get(cur)!;
      for (const nb of adj.get(cur) ?? []) {
        if (!dist.has(nb)) {
          dist.set(nb, d + 1);
          queue.push(nb);
        }
      }
    }
    let maxD = 0;
    for (const d of dist.values()) maxD = Math.max(maxD, d);
    const out = new Map<number, number>();
    for (const n of props.franchise.nodes) {
      const d = dist.get(n.deckId);
      // Unreachable nodes (shouldn't happen in a connected component) sink to the back.
      out.set(n.deckId, d == null ? 1 : maxD === 0 ? 0 : d / maxD);
    }
    return out;
  }

  function zJitterOf(deckId: number): number {
    return (hash(deckId * 31 + 5) - 0.5) * 0.16; // ±0.08
  }

  // Build a fresh (plain, non-reactive) node array seeded deterministically around a circle.
  function buildSim(rootId: number): SimNode[] {
    const nodes = props.franchise.nodes;
    const depths = computeDepths(rootId);
    const n = nodes.length;
    const radius = Math.max(300, n * 30);
    return nodes.map((node, i) => {
      const angle = (i / Math.max(1, n)) * Math.PI * 2;
      const jx = (hash(node.deckId) - 0.5) * radius * 0.6;
      const jy = (hash(node.deckId * 7 + 13) - 0.5) * radius * 0.6;
      const z = node.deckId === rootId ? 0 : Math.min(1, Math.max(0, (depths.get(node.deckId) ?? 1) + zJitterOf(node.deckId)));
      return {
        id: node.deckId,
        x: Math.cos(angle) * radius + jx,
        y: Math.sin(angle) * radius + jy,
        vx: 0,
        vy: 0,
        z,
      };
    });
  }

  // ---- Force simulation: always run to convergence SYNCHRONOUSLY (a few ms for n <= 100),
  // never animated live — the user sees only settled layouts, morphed between via animateToLayout.
  function tickOnce(ns: SimNode[], alpha: number, pinnedId: number) {
    const count = ns.length;
    const byId = new Map<number, SimNode>();
    for (const s of ns) byId.set(s.id, s);

    // Spring force along edges toward the rest length.
    for (const e of edges.value) {
      const a = byId.get(e.sourceDeckId);
      const b = byId.get(e.targetDeckId);
      if (!a || !b) continue;
      const dx = b.x - a.x;
      const dy = b.y - a.y;
      const dist = Math.hypot(dx, dy) || 0.001;
      const force = (dist - REST_LENGTH) * 0.02 * alpha;
      const ux = dx / dist;
      const uy = dy / dist;
      a.vx += ux * force;
      a.vy += uy * force;
      b.vx -= ux * force;
      b.vy -= uy * force;
    }

    // O(n^2) repulsion + collision push-apart (n <= 100).
    for (let i = 0; i < count; i++) {
      const a = ns[i]!;
      for (let j = i + 1; j < count; j++) {
        const b = ns[j]!;
        const dx = b.x - a.x;
        const dy = b.y - a.y;
        const dist = Math.hypot(dx, dy) || 0.001;
        if (dist > REPULSION_CUTOFF) continue;
        const ux = dx / dist;
        const uy = dy / dist;
        const rep = (REPULSION / (dist * dist)) * alpha;
        a.vx -= ux * rep;
        a.vy -= uy * rep;
        b.vx += ux * rep;
        b.vy += uy * rep;
        if (dist < COLLISION_DIST) {
          const push = (COLLISION_DIST - dist) * 0.5;
          a.vx -= ux * push;
          a.vy -= uy * push;
          b.vx += ux * push;
          b.vy += uy * push;
        }
      }
    }

    // Centring gravity (linear pull, so stragglers far out feel it strongly) + damping integration.
    for (const s of ns) {
      s.vx += -s.x * 0.005 * alpha;
      s.vy += -s.y * 0.005 * alpha;
      s.vx *= 0.85;
      s.vy *= 0.85;
      s.x += s.vx;
      s.y += s.vy;
    }

    // The pinned (focused) node owns the origin: the rest of the graph arranges around it.
    const pinned = byId.get(pinnedId);
    if (pinned) {
      pinned.x = 0;
      pinned.y = 0;
      pinned.vx = 0;
      pinned.vy = 0;
    }
  }

  function settle(ns: SimNode[], pinnedId: number) {
    let alpha = 1;
    for (let t = 0; t < 600 && alpha > 0.003; t++) {
      tickOnce(ns, alpha, pinnedId);
      alpha *= 0.985;
    }
  }

  // Morph the rendered layout to target positions/depths (and pan back to origin) in one
  // ease-out pass — replaces watching the sim jiggle live.
  let layoutAnimId: number | null = null;

  function animateToLayout(targets: Map<number, { x: number; y: number; z: number }>, animate: boolean) {
    if (layoutAnimId != null) cancelAnimationFrame(layoutAnimId);
    const apply = () => {
      for (const s of simNodes.value) {
        const t = targets.get(s.id);
        if (!t) continue;
        s.x = t.x;
        s.y = t.y;
        s.z = t.z;
        s.vx = 0;
        s.vy = 0;
      }
      pan.x = 0;
      pan.y = 0;
      simNodes.value = [...simNodes.value];
    };
    if (!animate || prefersReducedMotion()) {
      apply();
      return;
    }
    const from = new Map(simNodes.value.map((s) => [s.id, { x: s.x, y: s.y, z: s.z }]));
    const fromPan = { x: pan.x, y: pan.y };
    const start = performance.now();
    const DURATION = 450;
    const step = (now: number) => {
      const t = Math.min(1, (now - start) / DURATION);
      const e = 1 - Math.pow(1 - t, 3); // ease-out cubic
      for (const s of simNodes.value) {
        const f = from.get(s.id);
        const to = targets.get(s.id);
        if (!f || !to) continue;
        s.x = f.x + (to.x - f.x) * e;
        s.y = f.y + (to.y - f.y) * e;
        s.z = f.z + (to.z - f.z) * e;
      }
      pan.x = fromPan.x * (1 - e);
      pan.y = fromPan.y * (1 - e);
      simNodes.value = [...simNodes.value];
      if (t < 1) layoutAnimId = requestAnimationFrame(step);
      else layoutAnimId = null;
    };
    layoutAnimId = requestAnimationFrame(step);
  }

  // Fit the initial settled layout into the container (zoom out only, never in).
  function fitZoom() {
    const ns = simNodes.value;
    if (!ns.length || !svgSize.w || !svgSize.h) return;
    let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity;
    for (const s of ns) {
      minX = Math.min(minX, s.x);
      maxX = Math.max(maxX, s.x);
      minY = Math.min(minY, s.y);
      maxY = Math.max(maxY, s.y);
    }
    const pad = 280; // card extents + breathing room
    const fit = Math.min(svgSize.w / (maxX - minX + pad), svgSize.h / (maxY - minY + pad));
    zoom.value = Math.min(1, Math.max(MIN_ZOOM, fit));
  }

  // ---- Projection: world → screen (same math feeds cards, edges and label pills) ----
  function depthFactor(z: number): number {
    return P / (P + z * ZRANGE);
  }

  function project(s: { x: number; y: number; z: number }): { sx: number; sy: number; f: number } {
    const f = depthFactor(s.z);
    const sx = center.x + (s.x + pan.x) * f * zoom.value;
    const sy = center.y + (s.y + pan.y) * f * zoom.value;
    return { sx, sy, f };
  }

  interface ProjectedNode {
    node: FranchiseNode;
    sim: SimNode;
    sx: number;
    sy: number;
    f: number;
    scale: number;
  }

  const projected = computed<ProjectedNode[]>(() => {
    const out: ProjectedNode[] = [];
    for (const s of simNodes.value) {
      const node = nodeById.value.get(s.id);
      if (!node) continue;
      const { sx, sy, f } = project(s);
      out.push({ node, sim: s, sx, sy, f, scale: f * zoom.value });
    }
    return out;
  });

  const projectedById = computed<Map<number, ProjectedNode>>(() => {
    const m = new Map<number, ProjectedNode>();
    for (const p of projected.value) m.set(p.node.deckId, p);
    return m;
  });

  // ---- Focus / highlight state (mirrors timeline) ----
  const activeNode = ref<number | null>(null);
  const adjacentNodes = useAdjacentNodes(activeNode);

  const activeNodeData = computed(() => (activeNode.value == null ? null : nodeById.value.get(activeNode.value) ?? null));
  const activeCaptions = computed(() => (activeNode.value == null ? [] : captionsFor(activeNode.value)));

  function edgeActive(e: FranchiseEdge): boolean {
    return edgeActiveOf(e, activeNode.value);
  }

  function nodeDimmed(id: number): boolean {
    return activeNode.value != null && !adjacentNodes.value.has(id);
  }

  const isCurrent = (id: number) => id === props.currentDeckId;

  // Per-user coverage outline, suppressed while a node is highlighted/active (matches timeline).
  function coverageBorder(node: FranchiseNode): string {
    if (!authStore.isAuthenticated || store.hideCoverageBorders || (node.coverage === 0 && node.uniqueCoverage === 0)) return 'none';
    return getCoverageBorder(node.coverage);
  }

  function nodeOutline(node: FranchiseNode): string {
    if (activeNode.value === node.deckId || flashNode.value === node.deckId) return 'none';
    return coverageBorder(node);
  }

  // ---- Projected edges + mid-edge label pills (same projection math, no DOM reads) ----
  interface EdgeGeom {
    edge: FranchiseEdge;
    x1: number;
    y1: number;
    x2: number;
    y2: number;
    mx: number;
    my: number;
    label: string;
  }

  // Label relative to the FOCUSED node's direction (direction is unambiguous when one end is focused).
  function edgeLabel(e: FranchiseEdge): string {
    if (e.sourceDeckId === activeNode.value) return forwardLabels[e.relationshipType] ?? '';
    return inverseLabels[e.relationshipType] ?? '';
  }

  const edgeGeoms = computed<EdgeGeom[]>(() => {
    const out: EdgeGeom[] = [];
    for (const e of edges.value) {
      const a = projectedById.value.get(e.sourceDeckId);
      const b = projectedById.value.get(e.targetDeckId);
      if (!a || !b) continue;
      // Label sits near the FAR end of the edge — next to the deck it describes, and clear of
      // the popover, which always hugs the focused card and would cover a midpoint label.
      const t = e.sourceDeckId === activeNode.value ? 0.72 : e.targetDeckId === activeNode.value ? 0.28 : 0.5;
      out.push({
        edge: e,
        x1: a.sx,
        y1: a.sy,
        x2: b.sx,
        y2: b.sy,
        mx: a.sx + (b.sx - a.sx) * t,
        my: a.sy + (b.sy - a.sy) * t,
        label: edgeLabel(e),
      });
    }
    return out;
  });

  const svgSize = reactive({ w: 0, h: 0 });

  // ---- Focus: centre the node (animated pan) + open popover ----
  const popoverStyle = ref<Record<string, string>>({});
  const popoverRef = ref<HTMLElement | null>(null);
  const flashNode = ref<number | null>(null);
  let flashTimer: ReturnType<typeof setTimeout> | null = null;

  function prefersReducedMotion(): boolean {
    return import.meta.client && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  }

  function positionPopover(id: number) {
    if (!import.meta.client) return;
    const p = projectedById.value.get(id);
    const container = containerRef.value;
    if (!p || !container) return;
    const base = container.getBoundingClientRect();
    const popWidth = 260;
    const margin = 8;
    const vw = window.innerWidth;
    const vh = window.innerHeight;
    const cardRight = base.left + p.sx + (CARD_W * p.scale) / 2;
    const cardLeft = base.left + p.sx - (CARD_W * p.scale) / 2;
    const cardTop = base.top + p.sy - 80 * p.scale;
    let left = cardRight + margin;
    if (left + popWidth > vw) left = cardLeft - margin - popWidth;
    left = Math.max(margin, Math.min(left, vw - popWidth - margin));
    const top = Math.max(margin, Math.min(cardTop, vh - 16));
    popoverStyle.value = { left: `${left}px`, top: `${top}px`, width: `${popWidth}px` };
  }

  // Focus = ego re-layout: depths recomputed from the focused node, which gets pinned at the
  // origin; a copy of the graph is settled synchronously and the visible layout morphs onto it.
  function focusNode(id: number, animate = true) {
    activeNode.value = id;
    const depths = computeDepths(id);
    const clone: SimNode[] = simNodes.value.map((s) => ({
      id: s.id,
      x: s.x,
      y: s.y,
      vx: 0,
      vy: 0,
      z: s.id === id ? 0 : Math.min(1, Math.max(0, (depths.get(s.id) ?? 1) + zJitterOf(s.id))),
    }));
    settle(clone, id);
    const targets = new Map(clone.map((c) => [c.id, { x: c.x, y: c.y, z: c.z }]));
    animateToLayout(targets, animate);
    nextTick(() => positionPopover(id));
  }

  function clearFocus() {
    activeNode.value = null;
  }

  // A drag that ends on the background also fires a click — don't treat panning as dismissal.
  function onBackgroundClick() {
    if (dragMoved) return;
    clearFocus();
  }

  // Amber flash when locating a node via a popover row.
  function flashAndFocus(id: number) {
    focusNode(id);
    flashNode.value = id;
    if (flashTimer != null) clearTimeout(flashTimer);
    flashTimer = setTimeout(() => {
      flashNode.value = null;
    }, 1600);
  }

  // ---- Pan (drag) + zoom (wheel / pinch) on the background ----
  let dragging = false;
  let dragMoved = false;
  let dragStart = { x: 0, y: 0, panX: 0, panY: 0 };
  const activePointers = new Map<number, { x: number; y: number }>();
  let pinchStartDist = 0;
  let pinchStartZoom = 1;

  function onBackgroundPointerDown(e: PointerEvent) {
    const el = e.currentTarget as HTMLElement;
    el.setPointerCapture(e.pointerId);
    activePointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
    if (activePointers.size === 1) {
      dragging = true;
      dragMoved = false;
      dragStart = { x: e.clientX, y: e.clientY, panX: pan.x, panY: pan.y };
    } else if (activePointers.size === 2) {
      dragging = false;
      const pts = [...activePointers.values()];
      pinchStartDist = Math.hypot(pts[0]!.x - pts[1]!.x, pts[0]!.y - pts[1]!.y) || 1;
      pinchStartZoom = zoom.value;
    }
  }

  function onBackgroundPointerMove(e: PointerEvent) {
    if (!activePointers.has(e.pointerId)) return;
    activePointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
    if (activePointers.size === 2) {
      const pts = [...activePointers.values()];
      const dist = Math.hypot(pts[0]!.x - pts[1]!.x, pts[0]!.y - pts[1]!.y) || 1;
      const midX = (pts[0]!.x + pts[1]!.x) / 2;
      const midY = (pts[0]!.y + pts[1]!.y) / 2;
      applyZoom((pinchStartZoom * dist) / pinchStartDist, midX, midY);
      return;
    }
    if (!dragging) return;
    if (Math.abs(e.clientX - dragStart.x) + Math.abs(e.clientY - dragStart.y) > 5) dragMoved = true;
    // Pan moves screen pixels; divide by zoom so drag tracks the cursor regardless of depth.
    pan.x = dragStart.panX + (e.clientX - dragStart.x) / zoom.value;
    pan.y = dragStart.panY + (e.clientY - dragStart.y) / zoom.value;
  }

  function onBackgroundPointerUp(e: PointerEvent) {
    activePointers.delete(e.pointerId);
    if (activePointers.size < 2) pinchStartDist = 0;
    if (activePointers.size === 0) dragging = false;
  }

  // Zoom toward a screen point (clientX/clientY) so content under the cursor stays put.
  function applyZoom(nextZoom: number, clientX: number, clientY: number) {
    const container = containerRef.value;
    if (!container) return;
    const base = container.getBoundingClientRect();
    const px = clientX - base.left;
    const py = clientY - base.top;
    const clamped = Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, nextZoom));
    // Keep the world point under the cursor fixed. Using f=1 reference keeps this simple and stable.
    const worldX = (px - center.x) / zoom.value - pan.x;
    const worldY = (py - center.y) / zoom.value - pan.y;
    zoom.value = clamped;
    pan.x = (px - center.x) / zoom.value - worldX;
    pan.y = (py - center.y) / zoom.value - worldY;
  }

  function onWheel(e: WheelEvent) {
    e.preventDefault();
    const factor = e.deltaY < 0 ? 1.1 : 1 / 1.1;
    applyZoom(zoom.value * factor, e.clientX, e.clientY);
  }

  // ---- Escape to clear, outside-tap handled by background pointerdown ----
  function onKeydown(e: KeyboardEvent) {
    if (e.key === 'Escape') clearFocus();
  }

  // ---- Sizing ----
  let resizeObserver: ResizeObserver | null = null;
  function measure() {
    const el = containerRef.value;
    if (!el) return;
    const r = el.getBoundingClientRect();
    center.x = r.width / 2;
    center.y = r.height / 2;
    svgSize.w = r.width;
    svgSize.h = r.height;
  }

  onMounted(() => {
    if (!import.meta.client) return;
    measure();
    // Settle synchronously before first paint — the user only ever sees a stable layout.
    const ns = buildSim(props.currentDeckId);
    settle(ns, props.currentDeckId);
    simNodes.value = ns;
    fitZoom();
    if (containerRef.value) {
      resizeObserver = new ResizeObserver(measure);
      resizeObserver.observe(containerRef.value);
    }
    window.addEventListener('keydown', onKeydown);
  });

  onBeforeUnmount(() => {
    resizeObserver?.disconnect();
    if (layoutAnimId != null) cancelAnimationFrame(layoutAnimId);
    if (flashTimer != null) clearTimeout(flashTimer);
    if (import.meta.client) window.removeEventListener('keydown', onKeydown);
  });

  // Reposition the popover as the node moves under it (sim ticks / pan / zoom).
  watch([projectedById, zoom], () => {
    if (activeNode.value != null) positionPopover(activeNode.value);
  });
</script>

<template>
  <div class="pt-4">
    <div
      ref="containerRef"
      class="franchise-web relative min-h-[420px] overflow-hidden rounded-md border border-surface-200 bg-surface-0 dark:border-surface-700 dark:bg-surface-950"
      :style="{ height: '70vh', touchAction: 'none' }"
    >
      <!-- Background drag/zoom surface + outside-tap-to-clear. -->
      <div
        class="absolute inset-0 cursor-grab active:cursor-grabbing"
        @pointerdown="onBackgroundPointerDown"
        @pointermove="onBackgroundPointerMove"
        @pointerup="onBackgroundPointerUp"
        @pointercancel="onBackgroundPointerUp"
        @wheel="onWheel"
        @click="onBackgroundClick"
      ></div>

      <!-- Edge overlay: endpoints come from the same projection as the cards. -->
      <svg
        class="pointer-events-none absolute inset-0"
        :width="svgSize.w"
        :height="svgSize.h"
        :viewBox="`0 0 ${svgSize.w} ${svgSize.h}`"
        aria-hidden="true"
      >
        <line
          v-for="(g, i) in edgeGeoms"
          :key="i"
          :x1="g.x1"
          :y1="g.y1"
          :x2="g.x2"
          :y2="g.y2"
          stroke="currentColor"
          :stroke-width="edgeActive(g.edge) ? 2.5 : 1.5"
          :class="[edgeActive(g.edge) ? 'text-primary' : activeNode != null ? 'text-gray-300 dark:text-gray-700' : 'text-gray-400 dark:text-gray-600']"
          :style="{ opacity: activeNode != null && !edgeActive(g.edge) ? 0.2 : 1 }"
        />
      </svg>

      <!-- Mid-edge relationship pills: only for the focused node's incident edges. -->
      <template v-if="activeNode != null">
        <div
          v-for="(g, i) in edgeGeoms.filter((e) => edgeActive(e.edge))"
          :key="`pill-${i}`"
          class="pointer-events-none absolute z-30 -translate-x-1/2 -translate-y-1/2 rounded-full border border-surface-200 bg-surface-0 px-1.5 py-0.5 text-[10px] font-semibold whitespace-nowrap text-surface-700 shadow-sm dark:border-surface-600 dark:bg-surface-800 dark:text-surface-200"
          :style="{ left: `${g.mx}px`, top: `${g.my}px` }"
        >
          {{ g.label }}
        </div>
      </template>

      <!-- Node cards (buttons, not links — navigation is popover-only). -->
      <button
        v-for="p in projected"
        :key="p.node.deckId"
        type="button"
        class="franchise-card absolute flex w-28 flex-col rounded-md border bg-surface-0 text-left transition-[border-color,opacity] dark:bg-surface-900"
        :class="[
          isCurrent(p.node.deckId) ? 'border-primary ring-2 ring-primary' : 'border-surface-200 dark:border-surface-700 hover:border-primary',
          flashNode === p.node.deckId ? 'ring-2 ring-amber-400 dark:ring-amber-300' : '',
          nodeDimmed(p.node.deckId) ? 'opacity-30' : '',
        ]"
        :style="{
          left: `${p.sx}px`,
          top: `${p.sy}px`,
          transform: `translate(-50%, -50%) scale(${p.scale})`,
          zIndex: activeNode === p.node.deckId ? 40 : Math.round((1 - p.sim.z) * 20) + 5,
          opacity: nodeDimmed(p.node.deckId) ? undefined : p.sim.z > 0.5 ? 0.6 : 1,
          outline: nodeOutline(p.node),
          outlineOffset: '-2px',
        }"
        @click.stop="flashNode = null; focusNode(p.node.deckId)"
      >
        <img
          :src="coverSrc(p.node)"
          :alt="localiseTitle(p.node)"
          class="h-32 w-full rounded-t-md object-cover"
          loading="lazy"
          decoding="async"
          width="112"
          height="128"
          draggable="false"
        />
        <!-- Media type chip: the timeline shows this via column headers; here it lives on the card. -->
        <span class="absolute left-1 top-1 rounded bg-black/55 px-1 py-px text-[9px] font-semibold whitespace-nowrap text-white">
          {{ getMediaTypeText(p.node.mediaType) }}
        </span>
        <div class="flex flex-col gap-0.5 p-1.5">
          <span class="line-clamp-2 text-xs font-medium leading-tight" :title="localiseTitle(p.node)">
            {{ localiseTitle(p.node) }}
          </span>
          <div class="flex items-center justify-between gap-1 text-[11px]">
            <span class="text-gray-500 dark:text-gray-400">{{ releaseYear(p.node) ?? '?' }}</span>
            <DifficultyDisplay v-if="p.node.difficulty >= 0" :difficulty="p.node.difficulty" :difficulty-raw="p.node.difficultyRaw" class="text-[11px]" />
          </div>
        </div>
      </button>
    </div>

    <!-- Relations popover: same fixed-position pattern as the timeline. Navigation is via "Open →". -->
    <div
      v-if="activeNode != null && activeNodeData"
      ref="popoverRef"
      class="fixed z-50 flex max-h-72 flex-col overflow-y-auto rounded-md border border-surface-200 bg-surface-0 shadow-lg dark:border-surface-700 dark:bg-surface-900"
      :style="popoverStyle"
    >
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
          :title="`Focus ${c.otherTitle}`"
          @click="flashAndFocus(c.otherId)"
        >
          <span class="shrink-0 font-semibold text-gray-500 dark:text-gray-400">{{ c.label }}:</span>
          <span class="truncate text-surface-700 dark:text-surface-200">{{ c.otherTitle }}</span>
        </button>
      </div>
      <p v-else class="px-2 py-1.5 text-xs text-gray-500 dark:text-gray-400">No direct relations.</p>
    </div>
  </div>
</template>

<style scoped>
  /* Dark mode: subtle constellation feel — faint radial starfield + soft card glow. Light mode stays clean. */
  .dark-mode .franchise-web {
    background-image: radial-gradient(circle at 50% 40%, rgba(99, 102, 241, 0.08), transparent 60%);
  }

  .dark-mode .franchise-card {
    box-shadow: 0 0 12px rgba(129, 140, 248, 0.12);
  }
</style>
