<script setup lang="ts">
  import type { MetadataFieldChange } from '~/types/metadataEdits';
  import { describeChange, diffExcerpt, fieldLabel, sortChanges, textDiff, type ChangeView, type DiffSegment } from '~/utils/metadataEdits';

  const props = defineProps<{
    changes: MetadataFieldChange[];
    /** One line per field with descriptions cut down to the changed parts, for timelines and feeds. */
    compact?: boolean;
  }>();

  const rows = computed(() =>
    sortChanges(props.changes).map((change) => {
      const view = describeChange(change);
      return { field: change.field, label: fieldLabel(change.field), view, tokens: listTokens(view), segments: textSegments(view) };
    })
  );

  const BEFORE = 'text-red-700 dark:text-red-400 line-through decoration-1';
  const AFTER = 'text-green-700 dark:text-green-400';

  type ListToken = { kind: 'add' | 'remove'; label: string } | { kind: 'change'; label: string; before: string; after: string };

  function listTokens(view: ChangeView): ListToken[] {
    if (view.kind !== 'list') return [];
    return [
      ...view.added.map((label) => ({ kind: 'add' as const, label })),
      ...view.removed.map((label) => ({ kind: 'remove' as const, label })),
      ...view.changed.map((c) => ({ kind: 'change' as const, ...c })),
    ];
  }

  function textSegments(view: ChangeView): DiffSegment[] {
    if (view.kind !== 'text' || !view.before || !view.after) return [];
    const segments = textDiff(view.before, view.after);
    return props.compact ? diffExcerpt(segments) : segments;
  }

  const SEGMENT_CLASS: Record<DiffSegment['kind'], string> = {
    same: '',
    add: 'text-green-700 dark:text-green-400 bg-green-50 dark:bg-green-900/30 rounded-sm',
    remove: 'text-red-700 dark:text-red-400 line-through decoration-1 bg-red-50 dark:bg-red-900/30 rounded-sm',
    gap: 'text-muted-color',
  };
</script>

<template>
  <div :class="compact ? 'flex flex-col gap-1 text-sm' : 'flex flex-col divide-y divide-surface-200 dark:divide-surface-700 text-sm'">
    <div v-for="row in rows" :key="row.field" :class="compact ? 'min-w-0 [overflow-wrap:anywhere]' : 'flex flex-col gap-1 py-2 first:pt-0 last:pb-0 min-w-0'">
      <span :class="compact ? 'font-medium mr-1' : 'font-medium'">{{ row.label }}<template v-if="compact">:</template></span>

      <template v-if="row.view.kind === 'scalar'">
        <span class="[overflow-wrap:anywhere]">
          <span :class="BEFORE" v-bind="japaneseTextAttrs(row.view.before)">{{ row.view.before ?? 'empty' }}</span>
          <span class="text-muted-color px-1" aria-label="changed to">→</span>
          <span :class="AFTER" v-bind="japaneseTextAttrs(row.view.after)">{{ row.view.after ?? 'empty' }}</span>
        </span>
      </template>

      <template v-else-if="row.view.kind === 'text'">
        <span v-if="!row.view.after" :class="BEFORE">removed</span>
        <component
          :is="compact ? 'span' : 'p'"
          v-else-if="!row.view.before"
          :class="[AFTER, compact ? 'line-clamp-2' : 'm-0 whitespace-pre-line']"
          v-bind="japaneseTextAttrs(row.view.after)"
          >{{ row.view.after }}</component
        >
        <component :is="compact ? 'span' : 'p'" v-else :class="compact ? '' : 'm-0 whitespace-pre-line'" v-bind="japaneseTextAttrs(row.view.after)"
          ><span v-for="(segment, i) in row.segments" :key="i" :class="SEGMENT_CLASS[segment.kind]">{{ segment.text }}</span></component
        >
      </template>

      <template v-else>
        <span :class="compact ? '' : 'flex flex-col gap-0.5'">
          <span v-for="(token, i) in row.tokens" :key="i" class="[overflow-wrap:anywhere]"
            ><template v-if="token.kind === 'change'"
              >{{ token.label }} <span :class="[BEFORE, 'tabular-nums']">{{ token.before }}</span
              ><span class="text-muted-color px-1" aria-label="changed to">→</span><span :class="[AFTER, 'tabular-nums']">{{ token.after }}</span></template
            ><span v-else :class="token.kind === 'add' ? AFTER : BEFORE" v-bind="japaneseTextAttrs(token.label)">{{
              token.kind === 'add' ? `+ ${token.label}` : token.label
            }}</span
            >{{ compact && i < row.tokens.length - 1 ? ', ' : '' }}</span
          >
          <span v-if="!row.tokens.length" class="text-muted-color">reordered</span>
        </span>
      </template>
    </div>
  </div>
</template>
