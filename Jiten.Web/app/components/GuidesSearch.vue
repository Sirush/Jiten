<script setup lang="ts">
  import Dialog from 'primevue/dialog';

  type Section = { id: string; title: string; titles?: string[]; level?: number; content?: string };
  type IndexedSection = Section & { hay: string; titleLower: string; contentLower: string };
  type Segment = { text: string; hl: boolean };
  type Result = IndexedSection & { titleSegments: Segment[]; snippet: Segment[]; crumb: string };

  const { isOpen, close } = useGuidesSearch();

  // Section index is loaded lazily on first open and cached for the session.
  const sections = ref<IndexedSection[]>([]);
  const loaded = ref(false);
  async function ensureLoaded() {
    if (loaded.value) return;
    const data = await queryCollectionSearchSections('guides');
    // Precompute the lowercased haystack, title, and body once so per-keystroke filtering and
    // snippet extraction are plain substring scans rather than re-lowercasing every section each character.
    sections.value = ((data ?? []) as Section[]).map((s) => ({
      ...s,
      hay: `${s.title ?? ''} ${(s.titles ?? []).join(' ')} ${s.content ?? ''}`.toLowerCase(),
      titleLower: (s.title ?? '').toLowerCase(),
      contentLower: (s.content ?? '').toLowerCase(),
    }));
    loaded.value = true;
  }

  const query = ref('');
  const activeIndex = ref(0);
  const inputEl = ref<HTMLInputElement | null>(null);

  const tokens = computed(() =>
    query.value
      .toLowerCase()
      .split(/\s+/)
      .filter((t) => t.length > 0)
  );

  // Split `text` into highlighted / plain segments for every token occurrence (case-insensitive).
  function segment(text: string, toks: string[]): Segment[] {
    if (!text || toks.length === 0) return [{ text, hl: false }];
    const lower = text.toLowerCase();
    const hits: Array<[number, number]> = [];
    for (const t of toks) {
      let from = 0;
      let idx = lower.indexOf(t, from);
      while (idx !== -1) {
        hits.push([idx, idx + t.length]);
        from = idx + t.length;
        idx = lower.indexOf(t, from);
      }
    }
    if (hits.length === 0) return [{ text, hl: false }];
    hits.sort((a, b) => a[0] - b[0]);
    // Merge overlapping ranges.
    const merged: Array<[number, number]> = [];
    for (const [s, e] of hits) {
      const last = merged[merged.length - 1];
      if (last && s <= last[1]) last[1] = Math.max(last[1], e);
      else merged.push([s, e]);
    }
    const out: Segment[] = [];
    let cursor = 0;
    for (const [s, e] of merged) {
      if (s > cursor) out.push({ text: text.slice(cursor, s), hl: false });
      out.push({ text: text.slice(s, e), hl: true });
      cursor = e;
    }
    if (cursor < text.length) out.push({ text: text.slice(cursor), hl: false });
    return out;
  }

  // A windowed snippet of `content` centred on the first token hit, with ellipses.
  // `contentLower` is the precomputed lowercase of `content` (see ensureLoaded).
  function snippet(content: string, contentLower: string, toks: string[], len = 160): Segment[] {
    if (!content) return [];
    let first = -1;
    for (const t of toks) {
      const idx = contentLower.indexOf(t);
      if (idx !== -1 && (first === -1 || idx < first)) first = idx;
    }
    let start = 0;
    if (first > 60) start = first - 60;
    let text = content.slice(start, start + len);
    if (start > 0) text = '…' + text;
    if (start + len < content.length) text = text + '…';
    return segment(text, toks);
  }

  const results = computed<Result[]>(() => {
    if (tokens.value.length === 0) return [];
    const toks = tokens.value;
    // Single O(n) pass: keep sections matching every token, partitioned so title hits rank above
    // body-only hits (a cheaper stable split than an O(n log n) sort recomputing the title check).
    const titleHits: IndexedSection[] = [];
    const bodyHits: IndexedSection[] = [];
    for (const s of sections.value) {
      if (!toks.every((t) => s.hay.includes(t))) continue;
      (toks.some((t) => s.titleLower.includes(t)) ? titleHits : bodyHits).push(s);
    }
    return [...titleHits, ...bodyHits].slice(0, 10).map((s) => ({
      ...s,
      titleSegments: segment(s.title ?? '', toks),
      snippet: snippet(s.content ?? '', s.contentLower, toks),
      crumb: (s.titles ?? []).join(' › '),
    }));
  });

  watch(results, () => {
    activeIndex.value = 0;
  });

  watch(isOpen, async (open) => {
    if (open) {
      query.value = '';
      activeIndex.value = 0;
      await ensureLoaded();
      await nextTick();
      inputEl.value?.focus();
    }
  });

  async function go(r: Result | undefined) {
    if (!r) return;
    close();
    await navigateTo(r.id);
  }

  function onKeydown(e: KeyboardEvent) {
    if (!results.value.length) return;
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      activeIndex.value = (activeIndex.value + 1) % results.value.length;
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      activeIndex.value = (activeIndex.value - 1 + results.value.length) % results.value.length;
    } else if (e.key === 'Enter') {
      e.preventDefault();
      go(results.value[activeIndex.value]);
    }
  }

  // Global Ctrl/⌘+K shortcut.
  function onGlobalKey(e: KeyboardEvent) {
    if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === 'k') {
      e.preventDefault();
      isOpen.value = true;
    }
  }
  onMounted(() => window.addEventListener('keydown', onGlobalKey));
  onUnmounted(() => window.removeEventListener('keydown', onGlobalKey));
</script>

<template>
  <Dialog
    v-model:visible="isOpen"
    modal
    dismissable-mask
    :show-header="false"
    position="top"
    :style="{ width: '640px', maxWidth: '95vw' }"
    :pt="{ content: { class: '!p-0' }, root: { class: '!mt-16 sm:!mt-24' } }"
  >
    <div class="flex items-center gap-2 border-b border-surface-200 px-4 py-3 dark:border-surface-700">
      <Icon name="material-symbols-light:search" class="text-xl text-surface-400" />
      <input
        ref="inputEl"
        v-model="query"
        type="text"
        placeholder="Search guides…"
        class="w-full bg-transparent text-base outline-none placeholder:text-surface-400"
        aria-label="Search guides"
        @keydown="onKeydown"
      />
      <kbd class="hidden sm:inline rounded border border-surface-300 px-1.5 py-0.5 text-[10px] text-surface-400 dark:border-surface-600">esc</kbd>
    </div>

    <ul v-if="results.length" class="max-h-[60vh] overflow-y-auto py-2">
      <li v-for="(r, i) in results" :key="r.id">
        <button
          type="button"
          class="block w-full cursor-pointer px-4 py-2 text-left"
          :class="i === activeIndex ? 'bg-primary-50 dark:bg-primary-500/10' : 'hover:bg-surface-100 dark:hover:bg-surface-800'"
          @click="go(r)"
          @mousemove="activeIndex = i"
        >
          <div class="font-medium">
            <template v-for="(seg, si) in r.titleSegments" :key="si"
              ><mark v-if="seg.hl" class="search-hl">{{ seg.text }}</mark
              ><span v-else>{{ seg.text }}</span></template
            >
          </div>
          <div v-if="r.crumb" class="text-xs text-surface-400">{{ r.crumb }}</div>
          <p v-if="r.snippet.length" class="mt-0.5 text-sm text-surface-500">
            <template v-for="(seg, si) in r.snippet" :key="si"
              ><mark v-if="seg.hl" class="search-hl">{{ seg.text }}</mark
              ><span v-else>{{ seg.text }}</span></template
            >
          </p>
        </button>
      </li>
    </ul>

    <div v-else class="px-4 py-8 text-center text-sm text-surface-400">
      {{ tokens.length ? 'No matching guides.' : 'Type to search the guides.' }}
    </div>
  </Dialog>
</template>

<style scoped>
  .search-hl {
    background: var(--p-primary-200);
    color: var(--p-primary-950);
    border-radius: 2px;
    padding: 0 1px;
  }
  :global(.dark-mode .search-hl) {
    background: var(--p-primary-500);
    color: white;
  }
</style>
