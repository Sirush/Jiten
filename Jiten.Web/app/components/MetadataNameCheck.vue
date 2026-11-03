<script setup lang="ts">
  import type { CharacterNameSurface, CharacterNameSurfaceStatus } from '~/types/metadataEdits';

  const props = defineProps<{
    surfaces: CharacterNameSurface[];
    /** Replaces the "Will add N entries" summary, e.g. on the review queue. */
    title?: string;
  }>();

  const FALLBACK: Record<CharacterNameSurfaceStatus, string> = {
    ok: 'Looks good',
    flagged: 'Needs a closer look',
    notInText: "Doesn't appear in the text, so it won't be added",
    risky: "Short hiragana names can't be told apart from ordinary words, so this part will be skipped",
    duplicate: 'Already on this deck',
  };

  const TONE: Record<CharacterNameSurfaceStatus, string> = {
    ok: 'text-green-700 dark:text-green-400',
    flagged: 'text-amber-700 dark:text-amber-400',
    notInText: 'text-muted-color line-through',
    risky: 'text-muted-color line-through',
    duplicate: 'text-muted-color line-through',
  };

  const kept = computed(() => props.surfaces.filter((s) => s.status === 'ok' || s.status === 'flagged'));
  const flagged = computed(() => props.surfaces.filter((s) => s.status === 'flagged').length);
  const skipped = computed(() => props.surfaces.length - kept.value.length);

  const summary = computed(() => {
    if (flagged.value) return { text: `${flagged.value} ${flagged.value === 1 ? 'needs' : 'need'} a closer look`, tone: TONE.flagged };
    if (skipped.value) return { text: `${skipped.value} ${skipped.value === 1 ? 'part' : 'parts'} skipped`, tone: 'text-muted-color' };
    return { text: 'All found in the text', tone: TONE.ok };
  });

  const presence = (s: CharacterNameSurface) => (s.occurrences != null ? `${s.occurrences.toLocaleString()} in text` : s.found ? 'In the text' : 'Not in the text');

  /** Splits an excerpt around each occurrence so the name can be highlighted without v-html. */
  function excerptParts(excerpt: string, surface: string): { text: string; match: boolean }[] {
    const parts: { text: string; match: boolean }[] = [];
    let rest = excerpt;
    let at = rest.indexOf(surface);
    while (at >= 0) {
      if (at > 0) parts.push({ text: rest.slice(0, at), match: false });
      parts.push({ text: surface, match: true });
      rest = rest.slice(at + surface.length);
      at = rest.indexOf(surface);
    }
    if (rest) parts.push({ text: rest, match: false });
    return parts;
  }
</script>

<template>
  <div class="rounded-md border border-surface-200 dark:border-surface-700 overflow-hidden text-sm">
    <div class="flex flex-wrap items-center justify-between gap-2 px-3 py-2 bg-surface-100 dark:bg-surface-800">
      <span class="font-medium">{{ title ?? `Will add ${kept.length} ${kept.length === 1 ? 'entry' : 'entries'}` }}</span>
      <span class="text-xs" :class="summary.tone">{{ summary.text }}</span>
    </div>
    <div v-for="s in surfaces" :key="s.surface" class="px-3 py-1.5 border-t border-surface-200 dark:border-surface-700 flex flex-col gap-1">
      <div class="grid grid-cols-2 sm:grid-cols-[minmax(5rem,auto)_6.5rem_minmax(0,1fr)] gap-x-3 gap-y-0.5 items-baseline">
        <span lang="ja" class="font-medium ja-general" :class="TONE[s.status]">{{ s.surface }}</span>
        <span class="text-xs text-muted-color tabular-nums">{{ presence(s) }}</span>
        <span class="col-span-2 sm:col-span-1 text-xs" :class="s.status === 'ok' || s.status === 'flagged' ? TONE[s.status] : 'text-muted-color'">
          {{ s.detail ?? FALLBACK[s.status] }}
        </span>
      </div>
      <ul v-if="s.examples?.length" class="m-0 pl-0 list-none flex flex-col gap-0.5" :aria-label="`Where ${s.surface} appears`">
        <li v-for="(example, i) in s.examples" :key="i" lang="ja" class="ja-general text-xs text-muted-color [overflow-wrap:anywhere]">
          <span v-for="(part, j) in excerptParts(example, s.surface)" :key="j" :class="part.match ? 'font-semibold text-color' : ''">{{ part.text }}</span>
        </li>
      </ul>
    </div>
  </div>
</template>
