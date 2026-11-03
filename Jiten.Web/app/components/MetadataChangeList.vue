<script setup lang="ts">
  import type { MetadataFieldChange } from '~/types/metadataEdits';
  import {
    describeChange,
    diffExcerpt,
    diffPieces,
    fieldLabel,
    isRewrite,
    isWhitespaceOnlyChange,
    sortChanges,
    textDiff,
    type ChangeView,
    type DiffPiece,
    type DiffSegment,
    type ListEntry,
  } from '~/utils/metadataEdits';

  const props = defineProps<{
    changes: MetadataFieldChange[];
    /** One line per field with descriptions cut down to the changed parts, for timelines and feeds. */
    compact?: boolean;
  }>();

  const rows = computed(() =>
    sortChanges(props.changes).map((change) => {
      const view = describeChange(change);
      return { field: change.field, label: fieldLabel(change.field), view, tokens: listTokens(view), text: textView(view) };
    })
  );

  const BEFORE = 'text-red-700 dark:text-red-400 line-through decoration-1';
  const AFTER = 'text-green-700 dark:text-green-400';

  type ListToken = ({ kind: 'add' | 'remove' } & ListEntry) | { kind: 'change'; label: string; before: string; after: string };

  function listTokens(view: ChangeView): ListToken[] {
    if (view.kind !== 'list') return [];
    return [
      ...view.added.map((entry) => ({ kind: 'add' as const, ...entry })),
      ...view.removed.map((entry) => ({ kind: 'remove' as const, ...entry })),
      ...view.changed.map((c) => ({ kind: 'change' as const, ...c })),
    ];
  }

  type TextView = { mode: 'diff'; pieces: DiffPiece[]; spacingOnly: boolean } | { mode: 'rewrite' } | { mode: 'spacing' };

  function textView(view: ChangeView): TextView | null {
    if (view.kind !== 'text' || !view.before || !view.after) return null;
    const spacingOnly = isWhitespaceOnlyChange(view.before, view.after);
    if (spacingOnly && props.compact) return { mode: 'spacing' };
    const segments = textDiff(view.before, view.after);
    if (isRewrite(view.before, view.after, segments)) return { mode: 'rewrite' };
    return { mode: 'diff', pieces: diffPieces(props.compact ? diffExcerpt(segments) : segments, !!props.compact), spacingOnly };
  }

  const SEGMENT_CLASS: Record<DiffSegment['kind'], string> = {
    same: '',
    add: 'text-green-700 dark:text-green-400 bg-green-50 dark:bg-green-900/30 rounded-sm',
    remove: 'text-red-700 dark:text-red-400 line-through decoration-1 bg-red-50 dark:bg-red-900/30 rounded-sm',
    gap: 'text-muted-color',
  };

  function pieceClass(piece: DiffPiece): string {
    if (!piece.lineBreak) return SEGMENT_CLASS[piece.kind];
    return piece.kind === 'add'
      ? 'text-green-700 dark:text-green-400 bg-green-50 dark:bg-green-900/30 rounded-sm px-0.5'
      : 'text-red-700 dark:text-red-400 bg-red-50 dark:bg-red-900/30 rounded-sm px-0.5';
  }
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
        <span v-else-if="row.text?.mode === 'spacing'" class="text-muted-color">line breaks and spacing only</span>
        <template v-else-if="row.text?.mode === 'rewrite'">
          <template v-if="compact">
            <span class="text-muted-color">rewritten</span>
            <span class="line-clamp-2" v-bind="japaneseTextAttrs(row.view.after)">{{ row.view.after }}</span>
          </template>
          <div v-else class="flex flex-col gap-2">
            <div>
              <span class="text-xs font-medium text-red-700 dark:text-red-400">Previous text</span>
              <p class="m-0 whitespace-pre-line text-muted-color" v-bind="japaneseTextAttrs(row.view.before)">{{ row.view.before }}</p>
            </div>
            <div>
              <span class="text-xs font-medium text-green-700 dark:text-green-400">New text</span>
              <p class="m-0 whitespace-pre-line" v-bind="japaneseTextAttrs(row.view.after)">{{ row.view.after }}</p>
            </div>
          </div>
        </template>
        <template v-else-if="row.text?.mode === 'diff'">
          <span v-if="row.text.spacingOnly" class="text-muted-color">line breaks and spacing only</span>
          <component :is="compact ? 'span' : 'p'" :class="compact ? '' : 'm-0 whitespace-pre-line'" v-bind="japaneseTextAttrs(row.view.after)"
            ><span
              v-for="(piece, i) in row.text.pieces"
              :key="i"
              :class="pieceClass(piece)"
              :title="piece.lineBreak ? (piece.kind === 'add' ? 'Line break added' : 'Line break removed') : undefined"
              >{{ piece.text }}</span
            ></component
          >
        </template>
      </template>

      <template v-else>
        <span :class="compact ? '' : 'flex flex-col gap-0.5'">
          <span v-for="(token, i) in row.tokens" :key="i" class="[overflow-wrap:anywhere]"
            ><template v-if="token.kind === 'change'"
              >{{ token.label }} <span :class="[BEFORE, 'tabular-nums']">{{ token.before }}</span
              ><span class="text-muted-color px-1" aria-label="changed to">→</span><span :class="[AFTER, 'tabular-nums']">{{ token.after }}</span></template
            ><template v-else
              ><span :class="token.kind === 'add' ? AFTER : BEFORE"
                >{{ token.kind === 'add' ? '+ ' : '' }}<template v-if="token.role">{{ token.role }}: </template
                ><NuxtLink
                  v-if="token.href"
                  :to="token.href"
                  target="_blank"
                  class="!text-inherit underline decoration-1 underline-offset-2 hover:decoration-2"
                  v-bind="japaneseTextAttrs(token.label)"
                  >{{ token.label }}</NuxtLink
                ><span v-else v-bind="japaneseTextAttrs(token.label)">{{ token.label }}</span></span
              ><span v-for="m in token.meta ?? []" :key="m" class="text-xs text-muted-color ml-2 whitespace-nowrap">{{ m }}</span></template
            >{{ compact && i < row.tokens.length - 1 ? ', ' : '' }}</span
          >
          <span v-if="!row.tokens.length" class="text-muted-color">reordered</span>
        </span>
      </template>
    </div>
  </div>
</template>
