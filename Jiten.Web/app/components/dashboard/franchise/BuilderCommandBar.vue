<script setup lang="ts">
  import { ref, computed, watch } from 'vue';
  import Button from 'primevue/button';
  import InputText from 'primevue/inputtext';
  import { debounce } from 'perfect-debounce';
  import type { Deck, FranchiseEdge, MediaSuggestion } from '~/types/types';
  import { choiceToEdge, toBuilderDeck, type BuilderDeck, type LinkCheck } from '~/utils/franchiseBuilder';
  import { parseBuilderCommand, type CommandContext, type CommandItem } from '~/utils/franchiseBuilderCommand';

  const props = defineProps<{
    decks: Record<number, BuilderDeck>;
    board: number[];
    title: (deckId: number) => string;
    check: (edge: FranchiseEdge, ignoreId: number | null) => LinkCheck;
  }>();

  const emit = defineEmits<{ found: [decks: BuilderDeck[]]; run: [edges: FranchiseEdge[]]; notice: [text: string] }>();

  const { $api } = useNuxtApp();
  const command = ref('');
  const ctx = computed<CommandContext>(() => ({ decks: Object.values(props.decks), board: new Set(props.board), name: props.title }));
  const items = computed(() => parseBuilderCommand(command.value, ctx.value));

  const lookedUp = new Set<string>();
  const lookupMissing = debounce(async (list: CommandItem[]) => {
    const pending = list.flatMap((it) => (it.kind === 'error' && it.lookup && !lookedUp.has(it.lookup) ? [it.lookup] : []));
    if (!pending.length) return;
    const found: BuilderDeck[] = [];
    await Promise.all(
      [...new Set(pending)].map(async (key) => {
        lookedUp.add(key);
        try {
          const id = key.match(/^#(\d+)$/)?.[1];
          if (id) {
            found.push(toBuilderDeck((await $api<{ mainDeck: Deck }>(`admin/deck/${id}`)).mainDeck));
          } else {
            const res = await $api<{ suggestions: MediaSuggestion[] }>('media-deck/search-suggestions', { query: { query: key, limit: 10 } });
            found.push(...(res.suggestions ?? []).map(toBuilderDeck));
          }
        } catch {
          // An unknown id or a failed search keeps the parser's own message.
        }
      })
    );
    if (found.length) emit('found', found);
  }, 300);
  watch(items, (list) => list && lookupMissing(list));

  type PreviewRow =
    | { kind: 'error'; message: string }
    | { kind: 'link'; edge: FranchiseEdge; subject: number; object: number; words: string; adds: number[]; replaces: boolean; error?: string };

  const preview = computed<PreviewRow[] | null>(
    () =>
      items.value?.map((it): PreviewRow => {
        if (it.kind === 'error')
          return {
            kind: 'error',
            message: it.lookup && lookedUp.has(it.lookup) && it.message.startsWith('Looking up') ? `Deck ${it.lookup} doesn't exist.` : it.message,
          };
        const edge = choiceToEdge(it.choice, it.subject, it.object);
        const v = props.check(edge, null);
        return {
          kind: 'link',
          edge,
          subject: it.subject,
          object: it.object,
          words: it.choice.words,
          adds: [it.subject, it.object].filter((id) => !props.board.includes(id)),
          replaces: !!v.replaces,
          error: v.error,
        };
      }) ?? null
  );

  function run() {
    const rows = preview.value;
    if (!rows?.length) return;
    if (rows.some((r) => r.kind === 'error' || r.error)) {
      emit('notice', 'Fix the problem shown under the box first.');
      return;
    }
    emit(
      'run',
      rows.flatMap((r) => (r.kind === 'link' ? [r.edge] : []))
    );
    command.value = '';
  }

  const markOk = 'bg-green-100 text-green-800 dark:bg-green-950 dark:text-green-300';
  const markProblem = 'bg-red-100 text-red-800 dark:bg-red-950 dark:text-red-300';
</script>

<template>
  <div class="flex flex-col gap-2 border-t border-gray-200 px-3.5 py-3 dark:border-gray-800">
    <label for="fb-cmd" class="text-[13px] font-medium">
      Type a link <span class="font-normal text-gray-500 dark:text-gray-400">(press / to jump here)</span>
    </label>
    <div class="flex gap-2">
      <InputText
        id="fb-cmd"
        v-model="command"
        autocomplete="off"
        spellcheck="false"
        placeholder="x-2 sequel to x"
        class="min-w-0 flex-1"
        @keydown.enter.prevent="run"
        @keydown.esc="command = ''"
      />
      <Button label="Link" severity="secondary" outlined @click="run" />
    </div>
    <div class="flex min-h-[22px] flex-col gap-1 text-[13px]">
      <p v-if="!preview" class="m-0 text-xs text-gray-500 dark:text-gray-400">
        Examples: <code>x-2 sequel to x</code> · <code>a &gt; b &gt; c</code> · <code>#123 adaptation of #456</code>. Names match decks on the board or in the
        search results.
      </p>
      <template v-else>
        <div v-for="(row, i) in preview" :key="i" class="flex flex-wrap items-center gap-1.5">
          <template v-if="row.kind === 'error'">
            <span class="fb-mark" :class="markProblem">Problem</span>
            <span class="text-red-700 dark:text-red-400">{{ row.message }}</span>
          </template>
          <template v-else>
            <span class="fb-mark" :class="row.error ? markProblem : markOk">
              {{ row.error ? 'Problem' : row.replaces ? 'Replaces' : 'Ready' }}
            </span>
            <span class="fb-chip">{{ title(row.subject) }}</span> {{ row.words }} <span class="fb-chip">{{ title(row.object) }}</span>
            <span v-if="row.adds.length" class="text-xs text-gray-500 dark:text-gray-400">adds {{ row.adds.map(title).join(' and ') }} to the board</span>
            <span v-if="row.error" class="text-red-700 dark:text-red-400">{{ row.error }}</span>
          </template>
        </div>
      </template>
    </div>
  </div>
</template>

<style scoped>
  .fb-chip {
    display: inline-block;
    padding: 1px 7px;
    border-radius: 3px;
    font-weight: 500;
    background: var(--p-surface-100);
    border: 1px solid var(--p-surface-200);
  }

  :global(.dark-mode .fb-chip) {
    background: var(--p-surface-800);
    border-color: var(--p-surface-700);
  }

  .fb-mark {
    font-size: 11px;
    font-weight: 700;
    padding: 2px 6px;
    border-radius: 3px;
  }
</style>
