<script setup lang="ts">
  import type { CrossReference, Definition, Reading } from '~/types';

  const props = defineProps<{
    definitions: Definition[];
    isCompact: boolean;
    currentReadingIndex?: number;
    readings?: Reading[];
    // When set (>0), the non-compact list shows only the first N senses behind a "Show N more" expander.
    maxDefinitions?: number | null;
    wordId?: number;
    hiddenBehaviour?: 'gray' | 'hide';
  }>();

  const store = useJitenStore();

  const { hiddenFor, ensureLoaded, toggle, isEditing } = useHiddenDefinitions();
  onMounted(() => ensureLoaded(props.wordId));
  watch(() => props.wordId, (id) => ensureLoaded(id));

  const hiddenIndices = computed(() => new Set(hiddenFor(props.wordId)));
  const editingVisibility = computed(() => isEditing(props.wordId));
  const isHidden = (definition: Definition) => hiddenIndices.value.has(definition.index);
  const hideDefinition = computed({
    get: () => store.hideVocabularyDefinitions,
    set: (value) => {
      store.hideVocabularyDefinitions = value;
    },
  });

  const readingTextByIndex = computed(() => {
    const map = new Map<number, string>();
    if (props.readings) {
      for (const r of props.readings) {
        map.set(r.readingIndex, r.text);
      }
    }
    return map;
  });

  function isRestricted(definition: Definition): boolean {
    if (props.currentReadingIndex == null || !definition.restrictedToReadingIndices) return false;
    return !definition.restrictedToReadingIndices.includes(props.currentReadingIndex);
  }

  function restrictedLabel(definition: Definition): string | null {
    if (!definition.restrictedToReadingIndices || definition.restrictedToReadingIndices.length === 0) return null;
    const names = definition.restrictedToReadingIndices
      .map((idx) => readingTextByIndex.value.get(idx) ?? `form ${idx}`)
      .join(', ');
    return `only applies to ${names}`;
  }

  // misc tags that warn the learner before they study/Ankify a word.
  const WARNING_MISC = new Set(['vulg', 'X', 'sens', 'derog', 'obs', 'dated', 'hist', 'rare']);
  const MISC_LABELS: Record<string, string> = {
    uk: 'usu. kana',
    abbr: 'abbreviation',
    'on-mim': 'onomatopoeia',
    yoji: 'yojijukugo',
    joc: 'jocular',
    'net-sl': 'net slang',
    'm-sl': 'manga slang',
    sl: 'slang',
    col: 'colloquial',
    hon: 'honorific',
    hum: 'humble',
    pol: 'polite',
    fam: 'familiar',
    derog: 'derogatory',
    vulg: 'vulgar',
    sens: 'sensitive',
    dated: 'dated',
    hist: 'historical',
    obs: 'obsolete',
    rare: 'rare',
    arch: 'archaic',
    poet: 'poetical',
    chn: "children's",
    fem: 'female term',
    male: 'male term',
    proverb: 'proverb',
    id: 'idiomatic',
    euph: 'euphemistic',
    X: 'X-rated',
  };
  const miscLabel = (m: string) => MISC_LABELS[m] ?? m;
  const isWarningMisc = (m: string) => WARNING_MISC.has(m);

  const GLOSS_PREFIX: Record<string, string> = {
    lit: 'literally: ',
    fig: 'figuratively: ',
    expl: '',
  };
  const BLOCK_TYPES = new Set(['lit', 'fig', 'expl']);

  // Plain glosses (and trademarks) render inline; lit/fig/expl glosses break onto their own indented line.
  function meaningSegments(definition: Definition): {
    inline: { text: string; tm: boolean }[];
    blocks: { text: string; prefix: string }[];
  } {
    const types = definition.glossTypes;
    const inline: { text: string; tm: boolean }[] = [];
    const blocks: { text: string; prefix: string }[] = [];
    definition.meanings.forEach((m, i) => {
      const t = types?.[i] ?? '';
      if (BLOCK_TYPES.has(t)) {
        blocks.push({ text: m, prefix: GLOSS_PREFIX[t] ?? '' });
      } else {
        inline.push({ text: m, tm: t === 'tm' });
      }
    });
    return { inline, blocks };
  }

  function xrefLabel(type: string): string {
    if (type === 'ant') return 'Antonym';
    if (type === 'syn') return 'Synonym';
    return 'See also';
  }

  // The raw display text carries a trailing "[N]" sense marker (ダウン[1]); strip it — the sense
  // number is shown separately as a small superscript. It also carries the reading as a furigana/ruby
  // in parentheses (良い(よい)); strip that too so only the headword shows.
  function xrefBaseText(x: CrossReference): string {
    return x.targetText
      .replace(/\s*\[\d+\]\s*$/, '')
      .replace(/[(（][^)）]*[)）]\s*$/, '')
      .trim();
  }

  function groupedXrefs(definition: Definition): { type: string; label: string; items: NonNullable<Definition['crossReferences']> }[] {
    if (!definition.crossReferences || definition.crossReferences.length === 0) return [];
    const order = ['see', 'ant', 'syn'];
    const groups = new Map<string, NonNullable<Definition['crossReferences']>>();
    for (const x of definition.crossReferences) {
      const key = order.includes(x.type) ? x.type : 'see';
      if (!groups.has(key)) groups.set(key, []);
      groups.get(key)!.push(x);
    }
    return order.filter((t) => groups.has(t)).map((t) => ({ type: t, label: xrefLabel(t), items: groups.get(t)! }));
  }

  const samePartsOfSpeech = (a: string[] | null | undefined, b: string[] | null | undefined) =>
    a === b || (a != null && b != null && a.length === b.length && a.every((v, i) => v === b[i]));

  const definitionsWithPartsOfSpeech = computed(() => {
    if (!Array.isArray(props.definitions)) {
      return [];
    }
    let previousPartOfSpeech: string[] | null = null;

    // Dropped senses must not take part in the POS-header dedupe, or the first surviving sense of a
    // group loses its header. Editing always shows everything so hidden senses can be brought back.
    const source = props.hiddenBehaviour === 'hide' && !editingVisibility.value
      ? props.definitions.filter((d) => !isHidden(d))
      : props.definitions;

    return source.map((definition) => {
      // JMdict's per-sense POS order is inconsistent; normalise to a canonical order so it reads
      // the same across senses and the header dedupes (same set in different order = one header).
      const sortedPos = sortPos(definition.partsOfSpeech);
      const isDifferentPartOfSpeech = !samePartsOfSpeech(previousPartOfSpeech, sortedPos);
      previousPartOfSpeech = sortedPos;
      return {
        ...definition,
        partsOfSpeech: sortedPos,
        isDifferentPartOfSpeech,
      };
    });
  });

  const definitionsExpanded = ref(false);
  watch(
    () => props.definitions,
    () => {
      definitionsExpanded.value = false;
    }
  );
  const definitionLimit = computed(() => (props.maxDefinitions && props.maxDefinitions > 0 ? props.maxDefinitions : null));
  const visibleDefinitions = computed(() =>
    definitionLimit.value && !definitionsExpanded.value
      ? definitionsWithPartsOfSpeech.value.slice(0, definitionLimit.value)
      : definitionsWithPartsOfSpeech.value
  );
  const hiddenDefinitionCount = computed(() => Math.max(0, definitionsWithPartsOfSpeech.value.length - visibleDefinitions.value.length));
</script>

<template>
  <div v-if="!isCompact">
    <ul>
      <li
        v-for="definition in visibleDefinitions"
        :key="definition.index"
        :class="{
          'opacity-40': isRestricted(definition) || (isHidden(definition) && !editingVisibility),
          'opacity-60': isHidden(definition) && editingVisibility,
        }"
      >
        <div v-if="definition.isDifferentPartOfSpeech" class="flex flex-wrap gap-1 mt-1 mb-0.5">
          <Tooltip v-for="pos in definition.partsOfSpeech" :key="pos" :content="pos" placement="top">
            <span
              class="pos-badge"
              :class="`pos-${posColorClass(abbreviatePos(pos))}`"
            >{{ abbreviatePos(pos) }}</span>
          </Tooltip>
        </div>
        <Checkbox
          v-if="editingVisibility"
          :model-value="!isHidden(definition)"
          binary
          size="small"
          class="mr-1.5 align-middle"
          :aria-label="`Show meaning ${definition.index}`"
          @click.stop
          @pointerdown.stop
          @update:model-value="toggle(wordId!, definition.index)"
        />
        <span class="text-gray-400 mr-1">{{ definition.index }}.</span>
        <!-- plain meanings inline (trademarks get a ™) -->
        <template v-for="(seg, i) in meaningSegments(definition).inline" :key="'inl' + i">
          <span v-if="i > 0" class="text-gray-400">; </span><span>{{ seg.text }}<span v-if="seg.tm" class="text-gray-400">™</span></span>
        </template>
        <!-- trailing tag badges (misc / field / dial / restriction), grouped at the end like genre tags -->
        <Tooltip v-for="m in definition.misc" :key="'m' + m" :content="miscLabel(m)" placement="top">
          <span
            class="ml-1 inline-block rounded-full px-2 py-0.5 text-xs"
            :class="isWarningMisc(m)
              ? 'bg-red-100 text-red-700 dark:bg-red-900/30 dark:text-red-300'
              : 'bg-gray-100 text-gray-500 dark:bg-gray-800 dark:text-gray-400'"
          >{{ m }}</span>
        </Tooltip>
        <span v-for="f in definition.field" :key="f" class="ml-1 inline-block rounded-full px-2 py-0.5 text-xs bg-blue-100 text-blue-700 dark:bg-blue-900/30 dark:text-blue-300">{{ f }}</span>
        <span v-for="d in definition.dial" :key="d" class="ml-1 inline-block rounded-full px-2 py-0.5 text-xs bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-300">{{ d }}</span>
        <span v-if="restrictedLabel(definition)" class="ml-1 inline-block rounded-full px-2 py-0.5 text-xs bg-gray-100 text-gray-500 dark:bg-gray-800 dark:text-gray-400">{{ restrictedLabel(definition) }}</span>
        <!-- typed glosses (literally / figuratively / explanation) on their own indented line -->
        <div v-for="(blk, i) in meaningSegments(definition).blocks" :key="'blk' + i" class="ml-4 text-sm italic text-gray-500 dark:text-gray-400">
          <span v-if="blk.prefix" class="not-italic text-gray-400">{{ blk.prefix }}</span>{{ blk.text }}
        </div>
        <!-- s_inf usage notes -->
        <div v-for="(note, i) in definition.senseInfo" :key="'si' + i" class="ml-4 text-sm italic text-gray-500 dark:text-gray-400">
          {{ note }}
        </div>
        <!-- cross-reference chips -->
        <div v-for="grp in groupedXrefs(definition)" :key="grp.type" class="ml-4 mt-0.5 flex flex-wrap items-center gap-1 text-sm">
          <span class="text-gray-400">{{ grp.label }}:</span>
          <template v-for="(x, i) in grp.items" :key="grp.type + i">
            <NuxtLink
              v-if="x.targetWordId"
              :to="`/vocabulary/${x.targetWordId}/0`"
              :title="x.targetSenseIndex ? `sense ${x.targetSenseIndex}` : undefined"
              class="inline-block rounded-full px-2 py-0.5 text-xs bg-indigo-100 text-indigo-700 hover:bg-indigo-200 dark:bg-indigo-900/30 dark:text-indigo-300 dark:hover:bg-indigo-900/50"
            >{{ xrefBaseText(x) }}<sup v-if="x.targetSenseIndex" class="text-[0.65em] opacity-60">{{ x.targetSenseIndex }}</sup></NuxtLink>
            <span
              v-else
              :title="x.targetSenseIndex ? `sense ${x.targetSenseIndex}` : undefined"
              class="inline-block rounded-full px-2 py-0.5 text-xs bg-gray-100 text-gray-500 dark:bg-gray-800 dark:text-gray-400"
            >{{ xrefBaseText(x) }}<sup v-if="x.targetSenseIndex" class="text-[0.65em] opacity-60">{{ x.targetSenseIndex }}</sup></span>
          </template>
        </div>
      </li>
    </ul>
    <button
      v-if="hiddenDefinitionCount > 0"
      type="button"
      class="mt-1 text-xs text-primary-600 dark:text-primary-400 hover:underline"
      @click.stop="definitionsExpanded = true"
    >
      Show {{ hiddenDefinitionCount }} more
    </button>
  </div>

  <div v-if="isCompact && !hideDefinition">
    <template v-for="(definition, di) in definitionsWithPartsOfSpeech.slice(0, 10)" :key="definition.index">
      <span v-if="di > 0" class="text-gray-400">; </span>
      <span :class="{ 'opacity-40': isRestricted(definition) || isHidden(definition) }">{{ definition.meanings.join('; ') }}</span>
      <!-- glanceable tag badges (misc / field / dial); verbose s_inf/g_type/xref stay on the detail + SRS views -->
      <Tooltip v-for="m in definition.misc" :key="'cm' + m" :content="miscLabel(m)" placement="top">
        <span
          class="ml-1 inline-block rounded-full px-1.5 py-0 text-[0.65rem] align-middle"
          :class="isWarningMisc(m)
            ? 'bg-red-100 text-red-700 dark:bg-red-900/30 dark:text-red-300'
            : 'bg-gray-100 text-gray-500 dark:bg-gray-800 dark:text-gray-400'"
        >{{ m }}</span>
      </Tooltip>
      <span v-for="f in definition.field" :key="'cf' + f" class="ml-1 inline-block rounded-full px-1.5 py-0 text-[0.65rem] align-middle bg-blue-100 text-blue-700 dark:bg-blue-900/30 dark:text-blue-300">{{ f }}</span>
      <span v-for="d in definition.dial" :key="'cd' + d" class="ml-1 inline-block rounded-full px-1.5 py-0 text-[0.65rem] align-middle bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-300">{{ d }}</span>
    </template>
  </div>
</template>

<style scoped></style>
