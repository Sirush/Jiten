<script setup lang="ts">
  import { ref, computed, onMounted, nextTick, watch } from 'vue';
  import Button from 'primevue/button';
  import InputText from 'primevue/inputtext';
  import BuilderFloating from '~/components/dashboard/franchise/BuilderFloating.vue';
  import { getEdgeFlow, linkTypeInfo } from '~/utils/relationshipRoles';
  import type { FranchiseEdge } from '~/types/types';
  import { choiceToEdge, filterLinkChoices, linkChoiceByKey, linkSentence, linkToneClass, type BuilderEdge, type LinkCheck } from '~/utils/franchiseBuilder';

  const props = defineProps<{
    subject: number;
    object: number;
    editId: number | null;
    initialKey: string;
    anchor: { x: number; y: number };
    check: (edge: FranchiseEdge, ignoreId: number | null) => LinkCheck;
    name: (deckId: number) => string;
    year: (deckId: number) => number | null;
  }>();

  const emit = defineEmits<{ commit: [edge: FranchiseEdge, replaces: BuilderEdge | null]; cancel: [] }>();

  const subj = ref(props.subject);
  const obj = ref(props.object);
  const filter = ref('');
  const hiKey = ref<string | null>(props.initialKey);
  const shaking = ref(false);
  const floating = ref<InstanceType<typeof BuilderFloating> | null>(null);
  const listEl = ref<HTMLElement | null>(null);

  const visible = computed(() => filterLinkChoices(filter.value));
  const choice = computed(() => (hiKey.value ? linkChoiceByKey(hiKey.value) : undefined));
  const edge = computed(() => (choice.value ? choiceToEdge(choice.value, subj.value, obj.value) : null));
  const result = computed<LinkCheck>(() => (edge.value ? props.check(edge.value, props.editId) : {}));
  const info = computed(() => (edge.value ? linkTypeInfo(edge.value.relationshipType) : null));
  const flow = computed(() => (edge.value ? getEdgeFlow(edge.value) : null));
  const replaceText = computed(() => {
    const r = result.value.replaces;
    if (!r || r.id === props.editId) return '';
    const s = linkSentence(r);
    return `Replaces the current link: ${props.name(s.first)} ${s.verb} ${props.name(s.second)}.`;
  });
  const optionBad = (key: string) => !!props.check(choiceToEdge(linkChoiceByKey(key)!, subj.value, obj.value), props.editId).error;

  watch(filter, (f) => {
    if (f.trim()) hiKey.value = visible.value[0]?.key ?? null;
    else if (!visible.value.some((c) => c.key === hiKey.value)) hiKey.value = visible.value[0]?.key ?? null;
  });

  watch([hiKey, subj], () => {
    nextTick(() => {
      listEl.value?.querySelector('[aria-selected="true"]')?.scrollIntoView({ block: 'nearest' });
      floating.value?.place();
    });
  });

  const focusFilter = () => document.getElementById('fb-picker-filter')?.focus();

  onMounted(() => nextTick(focusFilter));

  function shake() {
    shaking.value = false;
    requestAnimationFrame(() => (shaking.value = true));
  }

  function commit() {
    if (!edge.value || result.value.error) return shake();
    emit('commit', edge.value, result.value.replaces ?? null);
  }

  function swap() {
    [subj.value, obj.value] = [obj.value, subj.value];
  }

  function pick(key: string) {
    hiKey.value = key;
    if (!optionBad(key)) commit();
    else focusFilter();
  }

  function onKey(ev: KeyboardEvent) {
    const list = visible.value;
    const i = list.findIndex((c) => c.key === hiKey.value);
    if (ev.key === 'ArrowDown' || ev.key === 'ArrowUp') {
      ev.preventDefault();
      if (!list.length) return;
      const n = ev.key === 'ArrowDown' ? (i + 1) % list.length : (i - 1 + list.length) % list.length;
      hiKey.value = list[n]!.key;
    } else if (ev.key === 'Enter') {
      ev.preventDefault();
      commit();
    } else if (ev.key === 'Tab' && !ev.shiftKey) {
      ev.preventDefault();
      swap();
    } else if (ev.key === 'Escape') {
      ev.preventDefault();
      ev.stopPropagation();
      emit('cancel');
    }
  }
</script>

<template>
  <BuilderFloating ref="floating" :anchor="anchor" label="Choose how the decks relate" wide @close="emit('cancel')">
    <div class="flex flex-col gap-2.5" :class="{ 'fb-shake': shaking }" @animationend="shaking = false">
      <div class="flex items-start justify-between gap-2.5">
        <h2 class="m-0 text-[15px] font-normal leading-relaxed">
          <template v-if="choice">
            <span class="fb-chip" v-bind="japaneseTextAttrs(name(subj))">{{ name(subj) }}</span>
            <span class="mx-1 font-bold text-primary-700 dark:text-primary-300">{{ choice.words }}</span>
            <span class="fb-chip" v-bind="japaneseTextAttrs(name(obj))">{{ name(obj) }}</span>
          </template>
          <template v-else>Nothing matches “{{ filter }}”</template>
        </h2>
        <Tooltip content="Swap the two decks (Tab)">
          <Button size="small" severity="secondary" outlined class="flex-none" aria-label="Swap the two decks" @click="swap">
            <Icon name="material-symbols:swap-horiz-rounded" />
            Swap
          </Button>
        </Tooltip>
      </div>

      <div v-if="edge && info && flow" class="flex items-center gap-2 rounded bg-gray-100 px-2.5 py-2 dark:bg-gray-800" :class="linkToneClass[info.tone]">
        <div class="flex min-w-0 flex-1 flex-col">
          <span class="text-[10.5px] font-bold">{{ info.fromRole }}</span>
          <span class="truncate text-[12.5px] font-medium text-gray-900 dark:text-gray-100">
            {{ name(flow.from) }} <small v-if="year(flow.from)" class="font-normal text-gray-500 dark:text-gray-400">{{ year(flow.from) }}</small>
          </span>
        </div>
        <svg class="h-3.5 w-16 flex-none" viewBox="0 0 64 14" aria-hidden="true">
          <template v-if="flow.directed">
            <path d="M2 7h52" stroke="currentColor" stroke-width="2" fill="none" />
            <path d="M52 1l10 6-10 6z" fill="currentColor" />
          </template>
          <path v-else d="M2 7h60" stroke="currentColor" stroke-width="2" stroke-dasharray="6 4" fill="none" />
        </svg>
        <div class="flex min-w-0 flex-1 flex-col text-right">
          <span class="text-[10.5px] font-bold">{{ flow.directed ? info.toRole : 'no direction' }}</span>
          <span class="truncate text-[12.5px] font-medium text-gray-900 dark:text-gray-100">
            {{ name(flow.to) }} <small v-if="year(flow.to)" class="font-normal text-gray-500 dark:text-gray-400">{{ year(flow.to) }}</small>
          </span>
        </div>
      </div>
      <p v-else class="m-0 text-xs text-gray-500 dark:text-gray-400">Try sequel, prequel, side story, spin-off, fandisc, adaptation or alternative.</p>

      <label for="fb-picker-filter" class="sr-only">Relationship type</label>
      <InputText
        id="fb-picker-filter"
        v-model="filter"
        autocomplete="off"
        spellcheck="false"
        placeholder="Type to pick: sequel, prequel, side story…"
        class="w-full"
        @keydown="onKey"
      />

      <div ref="listEl" role="listbox" aria-label="Relationship types" class="grid max-h-60 grid-cols-1 gap-1 overflow-y-auto sm:grid-cols-2">
        <button
          v-for="c in visible"
          :key="c.key"
          type="button"
          role="option"
          tabindex="-1"
          :aria-selected="c.key === hiKey"
          class="relative flex min-w-0 flex-col items-start rounded border py-1 pl-5 pr-2 text-left"
          :class="[
            linkToneClass[linkTypeInfo(choiceToEdge(c, subj, obj).relationshipType).tone],
            c.key === hiKey
              ? 'border-current bg-gray-100 ring-1 ring-current dark:bg-gray-800'
              : 'border-gray-200 bg-white hover:border-gray-400 dark:border-gray-700 dark:bg-gray-900 dark:hover:border-gray-500',
            optionBad(c.key) ? 'opacity-55' : '',
          ]"
          @click="pick(c.key)"
        >
          <span class="absolute left-2 top-[11px] h-1.5 w-1.5 rounded-full bg-current" aria-hidden="true" />
          <span class="text-[13px] font-medium text-gray-900 dark:text-gray-100">{{ c.label }}</span>
          <span class="max-w-full truncate text-[11px] text-gray-500 dark:text-gray-400">{{ name(subj) }} {{ c.words }} {{ name(obj) }}</span>
        </button>
      </div>

      <p v-if="result.error" class="m-0 text-[12.5px] text-red-700 dark:text-red-400" role="alert">{{ result.error }}</p>
      <p v-else-if="replaceText" class="m-0 text-[12.5px] text-amber-800 dark:text-amber-300">{{ replaceText }}</p>

      <div class="flex flex-wrap items-center gap-2">
        <span class="mr-auto text-[11.5px] text-gray-500 dark:text-gray-400">
          <kbd class="fb-kbd">Enter</kbd> link · <kbd class="fb-kbd">Tab</kbd> swap · <kbd class="fb-kbd">Esc</kbd> cancel
        </span>
        <Button label="Cancel" size="small" severity="secondary" text @click="emit('cancel')" />
        <Button :label="editId != null ? 'Update' : 'Link'" size="small" :disabled="!edge || !!result.error" @click="commit" />
      </div>
    </div>
  </BuilderFloating>
</template>

<style scoped>
  .fb-chip {
    display: inline-block;
    padding: 1px 7px;
    border-radius: 3px;
    font-size: 14px;
    font-weight: 500;
    background: var(--p-surface-100);
    border: 1px solid var(--p-surface-200);
  }

  :global(.dark-mode .fb-chip) {
    background: var(--p-surface-800);
    border-color: var(--p-surface-700);
  }

  .fb-kbd {
    font-family: inherit;
    font-size: 10.5px;
    border: 1px solid currentColor;
    border-bottom-width: 2px;
    border-radius: 3px;
    padding: 0 4px;
  }

  .fb-shake {
    animation: fb-shake 0.3s;
  }

  @keyframes fb-shake {
    25% {
      translate: -4px 0;
    }
    75% {
      translate: 4px 0;
    }
  }

  @media (prefers-reduced-motion: reduce) {
    .fb-shake {
      animation: none;
    }
  }
</style>
