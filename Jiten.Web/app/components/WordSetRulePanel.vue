<script setup lang="ts">
  import { formatNumber } from '~/utils/formatNumber';
  import { formatDateTime } from '~/utils/dateFormat';

  const props = defineProps<{ setId: number }>();
  const emit = defineEmits<{ applied: [] }>();

  const { $api } = useNuxtApp();
  const toast = useToast();

  interface WordSetRule {
    includePos: string[];
    excludePos: string[];
    script: number;
    dictionary: number;
    origins: number[];
    sourceLanguages: string[];
    maxFrequencyRank: number | null;
    addKanaForm: boolean;
  }

  interface RuleState {
    rule: WordSetRule | null;
    regeneratedAt: string | null;
    manualCount: number;
    exclusionCount: number;
  }

  interface PreviewMember {
    wordId: number;
    readingIndex: number;
    text: string;
    meanings: string[];
    partsOfSpeech: string[];
    frequencyRank: number;
  }

  interface RulePreview {
    toAddCount: number;
    unmatchedCount: number;
    unchanged: number;
    manual: number;
    excluded: number;
    unknownPos: string[];
    toAdd: PreviewMember[];
    unmatched: PreviewMember[];
  }

  const dictionaryOptions = [
    { label: 'Any', value: 0 },
    { label: 'Vocabulary (JMdict)', value: 1 },
    { label: 'Names (JMnedict)', value: 2 },
  ];
  const scriptOptions = [
    { label: 'Any', value: 0 },
    { label: 'Words written only in katakana', value: 1 },
    { label: 'Katakana forms of any word', value: 2 },
  ];
  const originOptions = [
    { label: 'Native (wago)', value: 1 },
    { label: 'Sino-Japanese (kango)', value: 2 },
    { label: 'Loanword (gairaigo)', value: 3 },
  ];
  const unmatchedOptions = [
    { label: 'Keep', value: 0 },
    { label: 'Keep as manual', value: 1 },
    { label: 'Remove', value: 2 },
  ];

  const state = ref<RuleState | null>(null);
  const loadFailed = ref(false);

  const includePos = ref('');
  const excludePos = ref('');
  const sourceLanguages = ref('');
  const dictionary = ref(0);
  const script = ref(0);
  const origins = ref<number[]>([]);
  const maxFrequencyRank = ref<number | null>(null);
  const addKanaForm = ref(true);

  const preview = ref<RulePreview | null>(null);
  const previewing = ref(false);
  const applying = ref(false);
  const unmatchedAction = ref(0);

  const splitTags = (value: string) =>
    value
      .split(/[,\s]+/)
      .map((t) => t.trim())
      .filter(Boolean);

  const rule = computed<WordSetRule>(() => ({
    includePos: splitTags(includePos.value),
    excludePos: splitTags(excludePos.value),
    script: script.value,
    dictionary: dictionary.value,
    origins: origins.value,
    sourceLanguages: splitTags(sourceLanguages.value),
    maxFrequencyRank: maxFrequencyRank.value || null,
    addKanaForm: addKanaForm.value,
  }));

  const hasCriteria = computed(
    () =>
      rule.value.includePos.length > 0 ||
      rule.value.script !== 0 ||
      rule.value.origins.length > 0 ||
      rule.value.sourceLanguages.length > 0 ||
      rule.value.maxFrequencyRank !== null,
  );

  const hasChanges = computed(() => !!preview.value && (preview.value.toAddCount > 0 || preview.value.unmatchedCount > 0));

  const previewLists = computed(() =>
    preview.value
      ? [
          { title: 'To Add', total: preview.value.toAddCount, rows: preview.value.toAdd },
          { title: 'No Longer Matching', total: preview.value.unmatchedCount, rows: preview.value.unmatched },
        ]
      : [],
  );

  watch(rule, () => (preview.value = null), { deep: true });

  const formatCount = (n: number) => formatNumber(n);
  const forms = (n: number) => `${formatCount(n)} ${n === 1 ? 'form' : 'forms'}`;

  function fillForm(saved: WordSetRule | null) {
    includePos.value = saved?.includePos.join(', ') ?? '';
    excludePos.value = saved?.excludePos.join(', ') ?? '';
    sourceLanguages.value = saved?.sourceLanguages.join(', ') ?? '';
    dictionary.value = saved?.dictionary ?? 0;
    script.value = saved?.script ?? 0;
    origins.value = saved?.origins ?? [];
    maxFrequencyRank.value = saved?.maxFrequencyRank ?? null;
    addKanaForm.value = saved?.addKanaForm ?? true;
  }

  async function refresh(resetForm = false) {
    try {
      state.value = await $api<RuleState>(`/admin/word-sets/${props.setId}/rule`);
      loadFailed.value = false;
      if (resetForm) {
        fillForm(state.value.rule);
        await nextTick();
        preview.value = null;
      }
    } catch {
      loadFailed.value = true;
    }
  }

  async function previewChanges() {
    previewing.value = true;
    try {
      preview.value = await $api<RulePreview>(`/admin/word-sets/${props.setId}/rule/preview`, { method: 'POST', body: { rule: rule.value } });
      unmatchedAction.value = 0;
    } catch (error: unknown) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(error, 'Failed to preview the rule'), life: 5000 });
    } finally {
      previewing.value = false;
    }
  }

  async function apply() {
    applying.value = true;
    try {
      const result = await $api<{ added: number; removed: number; markedManual: number }>(`/admin/word-sets/${props.setId}/rule/apply`, {
        method: 'POST',
        body: { rule: rule.value, unmatched: unmatchedAction.value },
      });
      const parts = [`${forms(result.added)} added`];
      if (result.removed > 0) parts.push(`${formatCount(result.removed)} removed`);
      if (result.markedManual > 0) parts.push(`${formatCount(result.markedManual)} kept as manual`);
      toast.add({ severity: 'success', summary: 'Rule applied', detail: parts.join(', '), life: 4000 });
      preview.value = null;
      await refresh();
      emit('applied');
    } catch (error: unknown) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(error, 'Failed to apply the rule'), life: 5000 });
    } finally {
      applying.value = false;
    }
  }

  defineExpose({ refresh });

  onMounted(() => refresh(true));
</script>

<template>
  <section class="border rounded-lg p-4 mb-6 bg-white dark:bg-gray-900" aria-labelledby="word-set-rule-heading">
    <div class="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1 mb-1">
      <h2 id="word-set-rule-heading" class="text-lg font-semibold">Rule</h2>
      <div v-if="state" class="flex flex-wrap gap-x-4 gap-y-1 text-sm text-gray-600 dark:text-gray-400">
        <span v-if="state.regeneratedAt"><i class="pi pi-refresh text-xs mr-1" aria-hidden="true" />Applied {{ formatDateTime(state.regeneratedAt) }}</span>
        <span><i class="pi pi-user-edit text-xs mr-1" aria-hidden="true" />{{ formatCount(state.manualCount) }} added by hand</span>
        <span><i class="pi pi-ban text-xs mr-1" aria-hidden="true" />{{ formatCount(state.exclusionCount) }} excluded</span>
      </div>
    </div>
    <p class="text-sm text-gray-600 dark:text-gray-400 mb-4 max-w-prose">
      Members are generated from the dictionary by this rule. Forms added by hand are kept, and forms removed by hand stay excluded.
    </p>

    <Message v-if="loadFailed" severity="error" :closable="false" class="mb-4">
      The saved rule could not be loaded. Reload the page before applying a new one.
    </Message>

    <div class="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
      <div>
        <label for="rule-include-pos" class="block text-sm font-medium mb-1">Parts of speech</label>
        <InputText id="rule-include-pos" v-model="includePos" placeholder="place, station" class="w-full" />
      </div>
      <div>
        <label for="rule-exclude-pos" class="block text-sm font-medium mb-1">Excluded parts of speech</label>
        <InputText id="rule-exclude-pos" v-model="excludePos" placeholder="on-mim, int" class="w-full" />
      </div>
      <div>
        <label for="rule-dictionary" class="block text-sm font-medium mb-1">Dictionary</label>
        <Select v-model="dictionary" input-id="rule-dictionary" :options="dictionaryOptions" option-label="label" option-value="value" class="w-full" />
      </div>
      <div>
        <label for="rule-script" class="block text-sm font-medium mb-1">Script</label>
        <Select v-model="script" input-id="rule-script" :options="scriptOptions" option-label="label" option-value="value" class="w-full" />
      </div>
      <div>
        <label for="rule-origins" class="block text-sm font-medium mb-1">Origin</label>
        <MultiSelect
          v-model="origins"
          input-id="rule-origins"
          :options="originOptions"
          option-label="label"
          option-value="value"
          placeholder="Any"
          :show-toggle-all="false"
          class="w-full"
        />
      </div>
      <div>
        <label for="rule-languages" class="block text-sm font-medium mb-1">Source languages</label>
        <InputText id="rule-languages" v-model="sourceLanguages" placeholder="eng, fre" class="w-full" />
      </div>
      <div>
        <label for="rule-max-rank" class="block text-sm font-medium mb-1">Frequency rank up to</label>
        <InputNumber v-model="maxFrequencyRank" input-id="rule-max-rank" :min="1" placeholder="No limit" class="w-full" fluid />
      </div>
      <div class="flex items-center gap-2 md:col-span-2 lg:pt-6">
        <Checkbox v-model="addKanaForm" input-id="rule-add-kana" binary :disabled="script === 2" />
        <label for="rule-add-kana" class="text-sm">Add the kana form of words written in kanji</label>
      </div>
    </div>

    <div class="flex flex-wrap items-center gap-3 mt-4">
      <Button label="Preview Changes" icon="pi pi-search" :loading="previewing" :disabled="!hasCriteria || applying" @click="previewChanges" />
      <span v-if="!hasCriteria" class="text-sm text-gray-600 dark:text-gray-400">
        Set at least one of parts of speech, script, origin, source languages, or frequency rank.
      </span>
    </div>

    <div v-if="preview" class="mt-5 border-t pt-4" aria-live="polite">
      <Message v-if="preview.unknownPos.length > 0" severity="warn" :closable="false" class="mb-4">
        No dictionary word carries these tags: {{ preview.unknownPos.join(', ') }}
      </Message>

      <dl class="flex flex-wrap gap-x-6 gap-y-2 text-sm mb-4">
        <div>
          <dt class="text-gray-600 dark:text-gray-400">To add</dt>
          <dd class="text-lg font-semibold tabular-nums">{{ formatCount(preview.toAddCount) }}</dd>
        </div>
        <div>
          <dt class="text-gray-600 dark:text-gray-400">No longer matching</dt>
          <dd class="text-lg font-semibold tabular-nums">{{ formatCount(preview.unmatchedCount) }}</dd>
        </div>
        <div>
          <dt class="text-gray-600 dark:text-gray-400">Unchanged</dt>
          <dd class="text-lg font-semibold tabular-nums">{{ formatCount(preview.unchanged) }}</dd>
        </div>
        <div>
          <dt class="text-gray-600 dark:text-gray-400">Added by hand, kept</dt>
          <dd class="text-lg font-semibold tabular-nums">{{ formatCount(preview.manual) }}</dd>
        </div>
        <div>
          <dt class="text-gray-600 dark:text-gray-400">Excluded, left out</dt>
          <dd class="text-lg font-semibold tabular-nums">{{ formatCount(preview.excluded) }}</dd>
        </div>
      </dl>

      <p v-if="!hasChanges" class="text-sm text-gray-600 dark:text-gray-400 mb-4">The set already matches this rule. Applying only saves the rule.</p>

      <div v-else class="grid grid-cols-1 lg:grid-cols-2 gap-4 mb-4">
        <div v-for="list in previewLists" :key="list.title" class="min-w-0">
          <h3 class="font-semibold mb-1">{{ list.title }}</h3>
          <p v-if="list.total > list.rows.length" class="text-xs text-gray-600 dark:text-gray-400 mb-1">
            Showing the {{ formatCount(list.rows.length) }} most frequent of {{ formatCount(list.total) }}
          </p>
          <p v-if="list.rows.length === 0" class="text-sm text-gray-600 dark:text-gray-400">None</p>
          <ul v-else class="border rounded max-h-80 overflow-y-auto divide-y" tabindex="0" :aria-label="list.title">
            <li v-for="row in list.rows" :key="`${row.wordId}-${row.readingIndex}`" class="flex items-baseline gap-2 px-3 py-1.5 text-sm">
              <NuxtLink
                :to="`/vocabulary/${row.wordId}/${row.readingIndex}`"
                target="_blank"
                class="font-bold text-blue-600 dark:text-blue-400 hover:underline whitespace-nowrap"
              >
                {{ row.text }}
              </NuxtLink>
              <span class="truncate flex-1 min-w-0">{{ row.meanings.slice(0, 2).join('; ') }}</span>
              <span class="text-xs text-gray-600 dark:text-gray-400 whitespace-nowrap">{{ row.partsOfSpeech.slice(0, 3).join(', ') }}</span>
              <span v-if="row.frequencyRank > 0" class="text-xs tabular-nums text-gray-600 dark:text-gray-400">#{{ row.frequencyRank }}</span>
            </li>
          </ul>
        </div>
      </div>

      <div class="flex flex-wrap items-center gap-3">
        <template v-if="preview.unmatchedCount > 0">
          <label for="rule-unmatched" class="text-sm font-medium">Members that no longer match:</label>
          <Select v-model="unmatchedAction" input-id="rule-unmatched" :options="unmatchedOptions" option-label="label" option-value="value" class="w-48" />
        </template>
        <Button label="Apply" icon="pi pi-check" severity="success" :loading="applying" @click="apply" />
      </div>
    </div>
  </section>
</template>
