<script setup lang="ts">
  import type {
    DerivationCategoryGroupDto,
    DerivationPairDto,
    DerivationPersonalPairsDto,
    DerivationPersonalSummaryDto,
  } from '~/types/types';

  const emit = defineEmits<{ changed: [] }>();

  const { $api } = useNuxtApp();
  const toast = useToast();
  const srsStore = useSrsStore();
  const convertToRuby = useConvertToRuby();

  const groups = ref<DerivationCategoryGroupDto[]>([]);
  const loading = ref(true);
  const saving = ref(false);
  const enabledGroups = ref<Record<string, boolean>>({});

  const personalTotal = ref(0);
  const personalByGroup = ref<Record<string, number>>({});
  const personalLoaded = ref(false);

  async function fetchPersonalSummary() {
    try {
      const summary = await $api<DerivationPersonalSummaryDto>('derivations/personal-summary');
      personalTotal.value = summary.totalCoveredWords;
      personalByGroup.value = Object.fromEntries(summary.groups.map((g) => [g.key, g.coveredWords]));
      personalLoaded.value = true;
    } catch {
      // The cards stay usable without personal counts.
    }
  }

  const showPreview = ref(false);
  const previewGroup = ref<DerivationCategoryGroupDto | null>(null);
  const previewPairs = ref<DerivationPairDto[]>([]);
  const previewLoading = ref(false);
  const pairsCache = new Map<string, DerivationPairDto[]>();

  const previewHasMultipleCategories = computed(() => (previewGroup.value?.categories.length ?? 0) > 1);

  type PreviewFilter = 'all' | 'redundant' | 'added' | 'known' | 'unknown';
  const previewFilter = ref<PreviewFilter>('all');
  const redundantKeys = ref<Set<number>>(new Set());
  const addedByGroupKeys = ref<Set<number>>(new Set());
  const studiedKeys = ref<Set<number>>(new Set());

  // Form keys, not word ids: 粗め and 粗目 are one entry, and only the reading actually derived is redundant.
  const wordKey = (wordId: number, readingIndex: number) => wordId * 256 + readingIndex;
  const derivedKey = (pair: DerivationPairDto) => wordKey(pair.derivedWordId, pair.derivedReadingIndex);

  const isKnown = (pair: DerivationPairDto) => studiedKeys.value.has(derivedKey(pair));
  const isRedundant = (pair: DerivationPairDto) => !isKnown(pair) && redundantKeys.value.has(derivedKey(pair));
  // Only ever shown for a group that is off: once it is on, its share is already redundant.
  const isAdded = (pair: DerivationPairDto) =>
    !isKnown(pair) && !isRedundant(pair) && addedByGroupKeys.value.has(derivedKey(pair));
  const isUnknown = (pair: DerivationPairDto) => !isKnown(pair) && !isRedundant(pair) && !isAdded(pair);

  const isBaseKnown = (pair: DerivationPairDto) =>
    studiedKeys.value.has(wordKey(pair.baseWordId, pair.baseReadingIndex));

  // Distinct forms, not rows: one derived form can appear under several bases.
  const countForms = (predicate: (pair: DerivationPairDto) => boolean) =>
    new Set(previewPairs.value.filter(predicate).map(derivedKey)).size;

  const redundantCount = computed(() => countForms(isRedundant));
  const addedCount = computed(() => countForms(isAdded));
  const knownCount = computed(() => countForms(isKnown));
  const unknownCount = computed(() => countForms(isUnknown));

  const previewGroupEnabled = computed(() => !!enabledGroups.value[previewGroup.value?.key ?? '']);
  const hasPersonalMarking = computed(
    () => redundantKeys.value.size > 0 || addedByGroupKeys.value.size > 0 || studiedKeys.value.size > 0,
  );

  // Form counts throughout, so the states add up to the total rather than to the row count.
  const previewChips = computed(() =>
    [
      { key: 'all' as const, label: 'All', count: countForms(() => true), color: '' },
      {
        key: 'redundant' as const,
        label: 'Already redundant',
        count: redundantCount.value,
        color: 'text-blue-500 dark:text-blue-300',
      },
      {
        key: 'added' as const,
        label: 'Would become redundant',
        count: addedCount.value,
        color: 'text-primary-600 dark:text-primary-300',
      },
      {
        key: 'known' as const,
        label: 'Words you know',
        count: knownCount.value,
        color: 'text-green-600 dark:text-green-300',
      },
      {
        key: 'unknown' as const,
        label: 'Not known yet',
        count: unknownCount.value,
        color: 'text-gray-500 dark:text-gray-400',
      },
    ].filter((chip) => chip.key === 'all' || chip.count > 0),
  );

  // The settings row credits this group with every word ticking it unlocks, including ones whose last derivation
  // step belongs to another group and therefore has no row here.
  const coveredElsewhere = computed(() => {
    const total = previewGroup.value ? (personalByGroup.value[previewGroup.value.key] ?? 0) : 0;
    // Against distinct words, matching what the settings row counts, not the form count the chips show.
    const attributable = previewPairs.value.filter((p) => addedByGroupKeys.value.has(derivedKey(p)));
    return Math.max(0, total - new Set(attributable.map((p) => p.derivedWordId)).size);
  });

  const filteredPairs = computed(() => {
    if (previewFilter.value === 'redundant') return previewPairs.value.filter(isRedundant);
    if (previewFilter.value === 'added') return previewPairs.value.filter(isAdded);
    if (previewFilter.value === 'known') return previewPairs.value.filter(isKnown);
    if (previewFilter.value === 'unknown') return previewPairs.value.filter(isUnknown);
    return previewPairs.value;
  });

  async function fetchPersonalPairs(groupKey: string) {
    try {
      const personal = await $api<DerivationPersonalPairsDto>(
        `derivations/pairs-personal?group=${encodeURIComponent(groupKey)}`,
      );
      if (previewGroup.value?.key !== groupKey) return;
      redundantKeys.value = new Set(personal.redundantKeys);
      addedByGroupKeys.value = new Set(personal.addedByGroupKeys);
      studiedKeys.value = new Set(personal.studiedKeys);
    } catch {
      // The list stays readable without the personal marking.
    }
  }

  async function openPreview(group: DerivationCategoryGroupDto) {
    previewGroup.value = group;
    showPreview.value = true;
    previewFilter.value = 'all';
    redundantKeys.value = new Set();
    addedByGroupKeys.value = new Set();
    studiedKeys.value = new Set();
    fetchPersonalPairs(group.key);

    const cached = pairsCache.get(group.key);
    if (cached) {
      previewPairs.value = cached;
      return;
    }
    previewPairs.value = [];
    previewLoading.value = true;
    try {
      const pairs = await $api<DerivationPairDto[]>(`derivations/pairs?group=${encodeURIComponent(group.key)}`);
      pairsCache.set(group.key, pairs);
      if (previewGroup.value?.key === group.key) previewPairs.value = pairs;
    } catch (e) {
      showPreview.value = false;
      toast.add({
        severity: 'error',
        summary: 'Could not load the list',
        detail: extractApiError(e, 'Please try again later.'),
        life: 5000,
      });
    } finally {
      previewLoading.value = false;
    }
  }

  function groupTooltip(group: DerivationCategoryGroupDto) {
    if (group.categories.length < 2) return group.explanation;
    const lines = group.categories.map(
      (c) => `**${c.exampleBase} → ${c.exampleDerived}** — ${c.explanation}`,
    );
    return `${group.explanation}\n\n${lines.join('\n')}`;
  }

  const totalEnabledPairs = computed(() =>
    groups.value.filter((g) => enabledGroups.value[g.key]).reduce((sum, g) => sum + g.pairCount, 0),
  );

  function syncFromSettings() {
    const enabled = new Set(srsStore.studySettings.derivationalRedundancyCategories ?? []);
    const next: Record<string, boolean> = {};
    for (const group of groups.value) {
      next[group.key] = group.categories.length > 0 && group.categories.every((c) => enabled.has(c.key));
    }
    enabledGroups.value = next;
  }

  onMounted(async () => {
    try {
      const [fetchedGroups] = await Promise.all([
        $api<DerivationCategoryGroupDto[]>('derivations/categories'),
        srsStore.fetchSettings(),
        fetchPersonalSummary(),
      ]);
      groups.value = fetchedGroups;
      syncFromSettings();
    } catch (e) {
      toast.add({
        severity: 'error',
        summary: 'Could not load derived forms',
        detail: extractApiError(e, 'Please try again later.'),
        life: 5000,
      });
    } finally {
      loading.value = false;
    }
  });

  async function toggleGroup(group: DerivationCategoryGroupDto, value: boolean) {
    const previous = enabledGroups.value[group.key] ?? false;
    enabledGroups.value = { ...enabledGroups.value, [group.key]: value };

    const selected = new Set(srsStore.studySettings.derivationalRedundancyCategories ?? []);
    for (const category of group.categories) {
      if (value) selected.add(category.key);
      else selected.delete(category.key);
    }

    saving.value = true;
    try {
      await srsStore.updateSettings({
        ...srsStore.studySettings,
        derivationalRedundancyCategories: [...selected],
      });
      emit('changed');
      fetchPersonalSummary();
    } catch (e) {
      enabledGroups.value = { ...enabledGroups.value, [group.key]: previous };
      toast.add({
        severity: 'error',
        summary: 'Could not save',
        detail: extractApiError(e, 'Your change was not saved.'),
        life: 5000,
      });
    } finally {
      saving.value = false;
    }
  }
</script>

<template>
  <Card>
    <template #title>
      <h2 class="text-xl font-bold">Derived & Related Words</h2>
    </template>
    <template #content>
      <div class="text-sm text-muted-color mb-4">
        Some dictionary entries are inflection of words you know, polite forms, or other forms that you can easily guess if you know the base form. You can tick any category here and all the words in it will be marked as redundant to give you more accurate coverage and avoid showing up in your reviews.
        <Tooltip
          content="The words will stop appearing as new cards and count towards your coverage. You can simply untick to remove their redundancy at any moment."
        >
          <i class="pi pi-info-circle text-xs text-surface-400 dark:text-surface-500 cursor-help" />
        </Tooltip>
      </div>

      <div v-if="loading" class="flex flex-col gap-2">
        <Skeleton v-for="i in 8" :key="i" height="2.2rem" />
      </div>

      <div v-else-if="groups.length === 0" class="text-sm text-muted-color italic py-4 text-center">
        No derived words are available yet.
      </div>

      <div
        v-else
        class="rounded-md border border-surface-200 dark:border-surface-700 divide-y divide-surface-200 dark:divide-surface-700"
      >
        <div v-for="group in groups" :key="group.key" class="flex items-start gap-2 px-3 py-2">
          <Checkbox
            :model-value="enabledGroups[group.key]"
            :input-id="`deriv-${group.key}`"
            :disabled="saving"
            binary
            class="shrink-0 mt-0.5"
            @update:model-value="toggleGroup(group, $event)"
          />

          <div class="min-w-0 flex-1 flex flex-wrap items-center gap-x-3 gap-y-1">
            <div class="min-w-0 flex flex-wrap items-center gap-x-2 gap-y-1">
              <label :for="`deriv-${group.key}`" class="font-medium cursor-pointer">{{ group.label }}</label>
              <Tooltip :content="groupTooltip(group)">
                <i class="pi pi-info-circle text-xs text-surface-400 dark:text-surface-500 cursor-help" />
              </Tooltip>
              <span v-if="group.categories[0]" class="text-xs text-muted-color font-noto-sans" lang="ja">
                {{ group.categories[0].exampleBase }} → {{ group.categories[0].exampleDerived }}
              </span>
            </div>

            <div class="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs sm:ml-auto">
              <span class="text-muted-color">{{ group.pairCount.toLocaleString() }} entries</span>
              <span
                v-if="personalLoaded && (personalByGroup[group.key] ?? 0) > 0"
                :class="enabledGroups[group.key] ? 'text-green-600 dark:text-green-300' : 'text-primary-600 dark:text-primary-300'"
              >
                {{
                  enabledGroups[group.key]
                    ? `includes ${personalByGroup[group.key]!.toLocaleString()} words you know`
                    : `would include ${personalByGroup[group.key]!.toLocaleString()} words you know`
                }}
              </span>
              <button type="button" class="text-blue-500 hover:underline" @click="openPreview(group)">
                view list
              </button>
            </div>
          </div>
        </div>
      </div>

      <p v-if="!loading && personalTotal > 0" class="text-xs text-muted-color mt-4">
        Your selected choices will make
        <span class="font-bold text-green-600 dark:text-green-300">{{ personalTotal.toLocaleString() }}</span>
        words  redundant.
      </p>

      <Dialog
        v-model:visible="showPreview"
        modal
        :header="previewGroup ? `${previewGroup.label} — ${previewGroup.pairCount.toLocaleString()} entries` : ''"
        :style="{ width: '860px', maxWidth: '95vw' }"
      >
        <p class="text-sm text-muted-color mb-3 italic">{{ previewGroup?.explanation }}</p>
        <p class="text-xs text-muted-color mb-3">
          Sorted by frequency (most common first). ↔ means knowing either word covers the other; → means the base
          word on the left covers the entry on the right, but not the reverse — the left word has meanings of its
          own that the derived entry doesn't share.
        </p>

        <div v-if="previewLoading" class="flex flex-col gap-2">
          <Skeleton v-for="i in 8" :key="i" height="2.2rem" />
        </div>

        <div v-else-if="previewPairs.length === 0" class="text-sm text-muted-color italic py-4 text-center">
          No entries found.
        </div>

        <template v-else>
          <div v-if="hasPersonalMarking" class="flex gap-2 overflow-x-auto pb-2 mb-3 no-scrollbar">
            <button
              v-for="chip in previewChips"
              :key="chip.key"
              type="button"
              class="flex items-center gap-1.5 px-3 py-1.5 rounded-full text-sm font-medium whitespace-nowrap transition-all border shrink-0"
              :class="
                previewFilter === chip.key
                  ? 'bg-primary-600 dark:bg-primary-500 text-white border-primary-600 dark:border-primary-500'
                  : 'bg-surface-0 dark:bg-surface-800 border-surface-200 dark:border-surface-700 hover:border-surface-400 dark:hover:border-surface-500'
              "
              @click="previewFilter = chip.key"
            >
              <span :class="previewFilter !== chip.key ? chip.color : ''">{{ chip.label }}</span>
              <span class="text-xs tabular-nums" :class="previewFilter === chip.key ? 'opacity-80' : 'opacity-60'">
                {{ chip.count.toLocaleString() }}
              </span>
            </button>
          </div>

          <p v-if="hasPersonalMarking && coveredElsewhere > 0" class="text-xs text-muted-color mb-3">
            {{ coveredElsewhere.toLocaleString() }} further
            {{ coveredElsewhere === 1 ? 'entry counts' : 'entries count' }} towards this grammar point's total but
            {{ coveredElsewhere === 1 ? 'is' : 'are' }} reached through a chain ending in another one, so
            {{ coveredElsewhere === 1 ? 'it is' : 'they are' }} listed there instead.
          </p>

          <div v-if="filteredPairs.length === 0" class="text-sm text-muted-color italic py-4 text-center">
            Nothing here yet.
          </div>

          <DataTable
            v-else
            :value="filteredPairs"
            scrollable
            scroll-height="60vh"
            :virtual-scroller-options="{ itemSize: 46 }"
            striped-rows
            class="p-datatable-sm"
          >
            <Column header="Base word" style="min-width: 120px">
              <template #body="{ data: pair }">
                <span class="inline-flex items-center gap-1.5">
                  <Tooltip v-if="isBaseKnown(pair)" content="You already know this word">
                    <i class="pi pi-check text-[0.65rem] text-green-600 dark:text-green-300" />
                  </Tooltip>
                  <NuxtLink
                    :to="`/vocabulary/${pair.baseWordId}/${pair.baseReadingIndex}`"
                    target="_blank"
                    class="text-blue-500 hover:underline font-noto-sans"
                  >
                    <span lang="ja" v-html="convertToRuby(pair.baseText)" />
                  </NuxtLink>
                  <Tooltip v-if="pair.baseDefinition" :content="pair.baseDefinition">
                    <i class="pi pi-info-circle text-[0.65rem] text-surface-400 dark:text-surface-500 cursor-help" />
                  </Tooltip>
                </span>
              </template>
            </Column>
            <Column header="" style="width: 44px">
              <template #body="{ data: pair }">
                <Tooltip
                  :content="
                    pair.bidirectional
                      ? 'Knowing either word covers the other'
                      : 'Only the base word covers the derived entry, not the reverse'
                  "
                >
                  <span class="text-muted-color cursor-help">{{ pair.bidirectional ? '↔' : '→' }}</span>
                </Tooltip>
              </template>
            </Column>
            <Column header="Derived entry" style="min-width: 120px">
              <template #body="{ data: pair }">
                <NuxtLink
                  :to="`/vocabulary/${pair.derivedWordId}/${pair.derivedReadingIndex}`"
                  target="_blank"
                  class="text-blue-500 hover:underline font-noto-sans"
                >
                  <span lang="ja" v-html="convertToRuby(pair.derivedText)" />
                </NuxtLink>
              </template>
            </Column>
            <Column header="For you" style="width: 150px; min-width: 150px">
              <template #body="{ data: pair }">
                <Tooltip v-if="isKnown(pair)" content="This entry already counts as known, so nothing is gained here">
                  <span class="text-xs text-green-600 dark:text-green-300 cursor-default">Known</span>
                </Tooltip>
                <Tooltip
                  v-else-if="isRedundant(pair)"
                  :content="
                    previewGroupEnabled
                      ? 'Covered by a word you know, so it is not shown as a new card'
                      : 'Already covered through a category you have ticked'
                  "
                >
                  <span class="text-xs text-blue-500 dark:text-blue-300 cursor-default">Redundant</span>
                </Tooltip>
                <Tooltip
                  v-else-if="isAdded(pair)"
                  content="Ticking this category would make this form redundant for you"
                >
                  <span class="text-xs text-primary-600 dark:text-primary-300 cursor-default">
                    Would be redundant
                  </span>
                </Tooltip>
              </template>
            </Column>
            <Column header="Meaning" style="min-width: 160px">
              <template #body="{ data: pair }">
                <span class="text-muted-color">{{ pair.derivedDefinition || '—' }}</span>
              </template>
            </Column>
            <Column v-if="previewHasMultipleCategories" header="Grammar point" style="min-width: 140px">
              <template #body="{ data: pair }">
                <span class="text-xs text-muted-color">{{ pair.categoryLabel }}</span>
              </template>
            </Column>
            <Column header="Freq" style="width: 90px">
              <template #body="{ data: pair }">
                <span v-if="pair.frequencyRank > 0">#{{ pair.frequencyRank.toLocaleString() }}</span>
                <span v-else class="text-muted-color">—</span>
              </template>
            </Column>
          </DataTable>
        </template>

        <template #footer>
          <Button label="Close" severity="secondary" @click="showPreview = false" />
        </template>
      </Dialog>
    </template>
  </Card>
</template>
