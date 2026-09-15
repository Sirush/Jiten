<script setup lang="ts">
  import { useToast } from 'primevue/usetoast';
  import { useConfirm } from 'primevue/useconfirm';
  import { useSrsStore } from '~/stores/srsStore';
  import { debounce } from 'perfect-debounce';
  import { MediaType, type MediaSuggestion, type SmartDeckTitleDto } from '~/types';
  import { coverUrl } from '~/utils/coverImage';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { apiErrorMessage, apiStatusCode } from '~/utils/apiErrorMessage';
  import { formatRelativeTime } from '~/utils/relativeTime';

  const props = defineProps<{ visible: boolean }>();
  const emit = defineEmits(['update:visible']);

  const { $api } = useNuxtApp();
  const toast = useToast();
  const localiseTitle = useLocaliseTitle();
  const smart = useSmartDeck();
  const srsStore = useSrsStore();
  const confirm = useConfirm();
  const { isPlus } = useJitenPlus();

  const localVisible = ref(props.visible);
  watch(
    () => props.visible,
    (v) => {
      localVisible.value = v;
      if (v) {
        showAllDriving.value = false;
        activeTab.value = 'titles';
        load();
      }
    }
  );
  watch(localVisible, (v) => {
    emit('update:visible', v);
    // Closing without saving drops the previewed values so the shared status matches what is stored.
    if (!v && previewDirty.value) smart.fetchStatus(true);
  });

  const activeTab = ref<'titles' | 'words'>('titles');
  const isNew = computed(() => !smart.status.value?.exists);

  const lookahead = ref(1);
  const lookaheadByType = ref<Record<number, number>>({});
  const showPerTypeLookahead = ref(false);
  const targetPercentage = ref(95);
  const restTargetPercentage = ref(90);
  const halfLife = ref(14);
  const weighPlanning = ref(false);
  const previewDirty = ref(false);
  const excludeKana = ref(false);
  const minRank = ref<number | null>(null);
  const maxRank = ref<number | null>(null);
  const posFilter = ref<string[]>([]);
  const pinned = ref<number[]>([]);
  const included = ref<number[]>([]);
  const excluded = ref<number[]>([]);
  const sequenceOverrides = ref<Record<number, boolean>>({});
  const knownTitles = ref<Record<number, { originalTitle: string; romajiTitle?: string | null; englishTitle?: string | null; coverName?: string | null }>>({});

  const lookaheadOptions = [
    { label: '1 unit', value: 1 },
    { label: '2 units', value: 2 },
    { label: '3 units', value: 3 },
  ];

  const lookaheadMediaTypes = [
    MediaType.Anime,
    MediaType.Audio,
    MediaType.Drama,
    MediaType.Manga,
    MediaType.Movie,
    MediaType.NonFiction,
    MediaType.Novel,
    MediaType.WebNovel,
  ];

  const perTypeOptions = [{ label: 'Default', value: 0 }, ...lookaheadOptions];
  const perTypeOverrideCount = computed(() => Object.keys(lookaheadByType.value).length);
  function perTypeValue(type: MediaType): number {
    return lookaheadByType.value[type] ?? 0;
  }
  function setPerType(type: MediaType, value: number) {
    const next = Object.fromEntries(Object.entries(lookaheadByType.value).filter(([key]) => Number(key) !== type));
    if (value > 0) next[type] = value;
    lookaheadByType.value = next;
    refreshPreview();
  }
  function parseLookaheadByType(raw: Record<string, number> | undefined): Record<number, number> {
    const out: Record<number, number> = {};
    for (const [key, units] of Object.entries(raw ?? {})) {
      const type = Number.isNaN(Number(key)) ? (MediaType as unknown as Record<string, number>)[key] : Number(key);
      if (type != null && lookaheadMediaTypes.includes(type)) out[type] = units;
    }
    return out;
  }
  const restTargetLabel = computed(() => (restTargetPercentage.value === 0 ? 'Units ahead only' : `${restTargetPercentage.value}%`));
  const halfLifeSteps = [
    { label: '1 week', value: 7 },
    { label: '2 weeks', value: 14 },
    { label: '1 month', value: 30 },
    { label: '2 months', value: 60 },
    { label: '3 months', value: 90 },
    { label: '6 months', value: 180 },
    { label: '1 year', value: 365 },
    { label: 'Never', value: 0 },
  ];
  const halfLifeIndex = computed({
    get: () =>
      Math.max(
        0,
        halfLifeSteps.findIndex((s) => s.value === halfLife.value)
      ),
    set: (i: number) => {
      halfLife.value = halfLifeSteps[i]?.value ?? 14;
    },
  });
  const halfLifeLabel = computed(() => halfLifeSteps[halfLifeIndex.value]?.label ?? '');

  let hydrating = false;
  async function load() {
    previewDirty.value = false;
    await smart.fetchStatus(true);
    const s = smart.status.value;
    if (!s) return;
    hydrating = true;
    await nextTick();
    lookahead.value = s.settings.lookaheadUnits;
    lookaheadByType.value = parseLookaheadByType(s.settings.lookaheadByMediaType);
    showPerTypeLookahead.value = perTypeOverrideCount.value > 0;
    targetPercentage.value = s.settings.targetPercentage;
    restTargetPercentage.value = s.settings.restTargetPercentage;
    halfLife.value = s.settings.recencyHalfLifeDays;
    weighPlanning.value = s.settings.weighPlanning;
    excludeKana.value = s.excludeKana;
    minRank.value = s.minGlobalFrequency ?? null;
    maxRank.value = s.maxGlobalFrequency ?? null;
    posFilter.value = s.posFilter ? JSON.parse(s.posFilter) : [];
    pinned.value = [...s.settings.pinnedDeckIds];
    included.value = [...s.settings.includedDeckIds];
    excluded.value = [...s.settings.excludedDeckIds];
    sequenceOverrides.value = { ...s.settings.sequenceOverrides };
    for (const t of s.titles) knownTitles.value[t.deckId] = t;
    for (const t of s.listedTitles ?? []) knownTitles.value[t.deckId] ??= t;
    await nextTick();
    hydrating = false;
  }

  /** Re-resolves titles, unit windows and the word count with the unsaved values, so the dialog shows what Save would build. */
  async function refreshPreview() {
    previewDirty.value = true;
    await smart.fetchStatus(true, {
      weighPlanning: weighPlanning.value,
      lookaheadUnits: lookahead.value,
      lookaheadByMediaType: lookaheadByType.value,
      targetPercentage: targetPercentage.value,
      restTargetPercentage: restTargetPercentage.value,
      sequenceOverrides: sequenceOverrides.value,
      excludeKana: excludeKana.value,
      minGlobalFrequency: minRank.value || null,
      maxGlobalFrequency: maxRank.value || null,
      posFilter: posFilter.value.length > 0 ? JSON.stringify(posFilter.value) : null,
    });
  }
  const refreshPreviewDebounced = debounce(refreshPreview, 400);
  watch([targetPercentage, restTargetPercentage, minRank, maxRank, posFilter, excludeKana], () => {
    if (!hydrating && localVisible.value && smart.status.value) refreshPreviewDebounced();
  });

  const previewSummary = computed(() => {
    const p = smart.status.value?.preview;
    if (!p) return null;
    const parts = [`${p.words.toLocaleString()} words`, `${p.newWords.toLocaleString()} new`];
    if (p.windowWords > 0) parts.push(`${p.windowWords.toLocaleString()} of them from the units ahead`);
    return parts.join(', ');
  });

  function nameOf(deckId: number) {
    const t = knownTitles.value[deckId];
    return t ? localiseTitle(t) : `Title #${deckId}`;
  }

  const pickerTarget = ref<'pin' | 'include' | 'exclude' | null>(null);
  const searchQuery = ref('');
  const searchResults = ref<MediaSuggestion[]>([]);
  const searching = ref(false);

  const runSearch = debounce(async (query: string) => {
    if (query.trim().length < 2) {
      searchResults.value = [];
      return;
    }
    searching.value = true;
    try {
      const response = await $api<{ suggestions: MediaSuggestion[] }>('media-deck/search-suggestions', { query: { query, limit: 8 } });
      searchResults.value = response.suggestions ?? [];
    } catch {
      searchResults.value = [];
    } finally {
      searching.value = false;
    }
  }, 300);
  watch(searchQuery, (q) => runSearch(q));

  function openPicker(target: 'pin' | 'include' | 'exclude') {
    pickerTarget.value = pickerTarget.value === target ? null : target;
    searchQuery.value = '';
    searchResults.value = [];
  }

  function pick(deck: MediaSuggestion) {
    if (pickerTarget.value === 'pin' && !isPinned(deck.deckId) && pinned.value.length >= maxPins.value) {
      toast.add({ severity: 'warn', summary: `You can pin up to ${maxPins.value} titles`, life: 2500 });
      return;
    }
    knownTitles.value[deck.deckId] = deck;
    removeEverywhere(deck.deckId);
    if (pickerTarget.value === 'pin') pinned.value.push(deck.deckId);
    else if (pickerTarget.value === 'include') included.value.push(deck.deckId);
    else if (pickerTarget.value === 'exclude') excluded.value.push(deck.deckId);
    pickerTarget.value = null;
    searchQuery.value = '';
  }

  function removeEverywhere(deckId: number) {
    pinned.value = pinned.value.filter((id) => id !== deckId);
    included.value = included.value.filter((id) => id !== deckId);
    excluded.value = excluded.value.filter((id) => id !== deckId);
  }

  function movePin(index: number, direction: -1 | 1) {
    const target = index + direction;
    if (target < 0 || target >= pinned.value.length) return;
    const next = [...pinned.value];
    const [moved] = next.splice(index, 1);
    next.splice(target, 0, moved!);
    pinned.value = next;
  }

  const maxPins = computed(() => smart.status.value?.maxPins ?? 3);

  const driving = computed(() => smart.status.value?.titles ?? []);
  const DRIVING_PREVIEW = 3;
  const showAllDriving = ref(false);
  const visibleDriving = computed(() => (showAllDriving.value ? driving.value : driving.value.slice(0, DRIVING_PREVIEW)));

  function isPinned(deckId: number) {
    return pinned.value.includes(deckId);
  }

  function togglePin(t: SmartDeckTitleDto) {
    if (isPinned(t.deckId)) {
      pinned.value = pinned.value.filter((id) => id !== t.deckId);
      return;
    }
    if (pinned.value.length >= maxPins.value) {
      toast.add({ severity: 'warn', summary: `You can pin up to ${maxPins.value} titles`, life: 2500 });
      return;
    }
    knownTitles.value[t.deckId] = t;
    removeEverywhere(t.deckId);
    pinned.value = [...pinned.value, t.deckId];
  }

  function isSequential(t: SmartDeckTitleDto) {
    return sequenceOverrides.value[t.deckId] ?? t.sequential;
  }

  function toggleSequence(t: SmartDeckTitleDto) {
    sequenceOverrides.value = { ...sequenceOverrides.value, [t.deckId]: !isSequential(t) };
    refreshPreview();
  }

  function progressText(t: SmartDeckTitleDto) {
    if (t.totalUnits === 0) return 'whole title';
    const done = `${t.completedUnits}/${t.totalUnits} done`;
    if (t.window.length) return `${done}, on ${t.window.map((u) => localiseTitle(u)).join(', ')}`;
    if (t.windowSource === 'sequence') return `${done}, all parts done`;
    return `${done}, nothing marked Ongoing yet`;
  }

  function lastActiveText(t: SmartDeckTitleDto) {
    const d = new Date(t.lastActivity);
    if (Number.isNaN(d.getTime()) || d.getFullYear() < 2000) return null;
    return formatRelativeTime(d);
  }

  const lastRebuiltText = computed(() => {
    const at = smart.status.value?.lastRebuiltAt;
    if (!at) return null;
    const d = new Date(at);
    return d.toLocaleString(undefined, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });
  });

  const deckSummary = computed(() => {
    const s = smart.status.value;
    if (!s) return '';
    if (s.building) return s.wordCount > 0 ? `${s.wordCount.toLocaleString()} words, rebuilding` : 'Building your deck, this could take up to a minute';
    if (s.wordCount === 0) return 'No words matched. Loosen the word filters or add a title.';
    const count = s.wordCount.toLocaleString();
    return lastRebuiltText.value ? `${count} words, rebuilt ${lastRebuiltText.value}` : `${count} words`;
  });

  const titlesSummary = computed(() => {
    const parts: string[] = [];
    if (pinned.value.length) parts.push(`${pinned.value.length} pinned`);
    if (included.value.length) parts.push(`${included.value.length} included`);
    if (excluded.value.length) parts.push(`${excluded.value.length} excluded`);
    return parts.length ? parts.join(', ') : `${driving.value.length} ${driving.value.length === 1 ? 'title' : 'titles'}`;
  });

  const wordsSummary = computed(() => {
    const parts: string[] = [];
    if (minRank.value && maxRank.value) parts.push(`rank ${minRank.value.toLocaleString()}-${maxRank.value.toLocaleString()}`);
    else if (minRank.value) parts.push(`rank ${minRank.value.toLocaleString()}+`);
    else if (maxRank.value) parts.push(`top ${maxRank.value.toLocaleString()}`);
    if (posFilter.value.length) parts.push(`${posFilter.value.length} tag${posFilter.value.length === 1 ? '' : 's'}`);
    if (excludeKana.value) parts.push('no kana');
    return parts.length ? parts.join(', ') : 'all words';
  });

  const rebuilding = ref(false);
  async function rebuildNow() {
    rebuilding.value = true;
    try {
      await smart.rebuild();
      toast.add({ severity: 'info', summary: 'Rebuild queued', detail: 'Your Smart Deck rebuild could take up to a minute.', life: 3000 });
    } catch (e) {
      const message =
        apiStatusCode(e) === 429 ? 'You can only queue three manual rebuilds an hour. Try again later.' : apiErrorMessage(e, 'Could not queue the rebuild.');
      toast.add({ severity: 'error', summary: message, life: 4000 });
    } finally {
      rebuilding.value = false;
    }
  }

  async function save() {
    const wasNew = isNew.value;
    if (maxRank.value && minRank.value && minRank.value > maxRank.value) {
      toast.add({ severity: 'warn', summary: 'Min rank cannot exceed max rank', life: 2500 });
      return;
    }
    try {
      await smart.saveSettings({
        weighPlanning: weighPlanning.value,
        lookaheadUnits: lookahead.value,
        lookaheadByMediaType: lookaheadByType.value,
        targetPercentage: targetPercentage.value,
        restTargetPercentage: restTargetPercentage.value,
        recencyHalfLifeDays: halfLife.value,
        pinnedDeckIds: pinned.value,
        includedDeckIds: included.value,
        excludedDeckIds: excluded.value,
        sequenceOverrides: sequenceOverrides.value,
        excludeKana: excludeKana.value,
        minGlobalFrequency: minRank.value || null,
        maxGlobalFrequency: maxRank.value || null,
        posFilter: posFilter.value.length > 0 ? JSON.stringify(posFilter.value) : null,
      });
      toast.add({ severity: 'success', summary: wasNew ? 'Smart Deck added to your study decks' : 'Smart Deck saved', life: 2000 });
      localVisible.value = false;
    } catch (e) {
      toast.add({ severity: 'error', summary: apiErrorMessage(e, 'Could not save the Smart Deck'), life: 4000 });
    }
  }

  const removing = ref(false);
  function confirmRemove() {
    const id = smart.status.value?.userStudyDeckId;
    if (!id) return;
    confirm.require({
      message: 'Remove the Smart Deck? Its pins, exclusions and filters are forgotten. Your existing cards and progress are kept.',
      header: 'Remove Smart Deck',
      acceptLabel: 'Remove',
      rejectLabel: 'Cancel',
      accept: async () => {
        removing.value = true;
        try {
          await srsStore.removeStudyDeck(id);
          await smart.fetchStatus(true);
          toast.add({ severity: 'info', summary: 'Smart Deck removed', life: 2000 });
          localVisible.value = false;
        } catch (e) {
          toast.add({ severity: 'error', summary: apiErrorMessage(e, 'Could not remove the Smart Deck'), life: 4000 });
        } finally {
          removing.value = false;
        }
      },
    });
  }

  onMounted(() => {
    if (props.visible) load();
  });
</script>

<template>
  <Dialog v-model:visible="localVisible" header="Smart Deck" modal :style="{ width: '720px', maxWidth: '95vw' }" :pt="{ content: { class: 'p-4' } }">
    <div v-if="smart.loading.value && !smart.status.value" class="flex flex-col gap-3">
      <Skeleton width="100%" height="2.5rem" />
      <Skeleton width="80%" height="1rem" />
      <Skeleton width="100%" height="6rem" />
    </div>

    <div v-else-if="smart.statusError.value" class="text-center py-8">
      <p class="text-red-500 mb-3">{{ smart.statusError.value }}</p>
      <Button label="Try again" icon="pi pi-refresh" severity="secondary" @click="load" />
    </div>

    <div v-else-if="smart.status.value" class="flex flex-col gap-4">
      <p class="text-sm text-gray-600 dark:text-gray-300">
        The Smart Deck automatically picks new cards from the titles you mark as Ongoing, and automatically picks from the next episode/volume when you mark one
        as finished. The words are weighted towards those you're most likely to encounter next.
      </p>

      <div
        v-if="!isPlus"
        class="flex flex-col sm:flex-row sm:items-center gap-3 p-3 rounded-lg border border-primary-200 dark:border-primary-800 bg-primary-50 dark:bg-primary-950"
      >
        <div class="flex-1 min-w-0">
          <div class="flex items-center gap-2 font-semibold text-sm">
            <JitenPlusBadge :link="false" />
            <span v-if="smart.status.value.exists">Your Smart Deck is paused</span>
            <span v-else>Smart Deck is part of Jiten+</span>
          </div>
          <p class="text-sm text-gray-600 dark:text-gray-300 mt-1">
            <template v-if="smart.status.value.exists">Your settings are kept and you will be able to resume as soon as you resubscribe to Jiten+.</template>
            <template v-else>You can look through the settings below. Saving needs Jiten+.</template>
          </p>
        </div>
        <NuxtLink to="/jiten-plus" class="flex-shrink-0">
          <Button label="See Jiten+" icon="pi pi-sparkles" size="small" />
        </NuxtLink>
      </div>

      <template v-else>
        <p v-if="smart.status.value.exists" class="text-xs text-gray-500 dark:text-gray-400">
          {{ deckSummary }}<template v-if="!smart.status.value.isActive">, paused</template>
          <template v-if="previewSummary"> · with these new settings: {{ previewSummary }}</template>
        </p>
        <p v-else-if="previewSummary" class="text-xs text-gray-500 dark:text-gray-400">With these settings: {{ previewSummary }}</p>
        <p v-else class="text-xs text-gray-500 dark:text-gray-400">The smart deck works like any of your other study decks.</p>
      </template>

      <p v-if="smart.status.value.titlesBeyondCap > 0" class="text-xs text-amber-600 dark:text-amber-400">
        {{ smart.status.value.titlesBeyondCap }} older Ongoing titles are past the {{ smart.status.value.maxTitles }}-title limit and are left out. You can
        exclude some titles manually to make more room.
      </p>

      <Tabs v-model:value="activeTab" :show-navigators="false">
        <TabList>
          <Tab value="titles" class="!py-2">
            <span class="block text-sm font-semibold leading-tight">Titles</span>
            <span class="block text-xs font-normal text-gray-500 dark:text-gray-400 leading-tight">{{ titlesSummary }}</span>
          </Tab>
          <Tab value="words" class="!py-2">
            <span class="block text-sm font-semibold leading-tight">Words</span>
            <span class="block text-xs font-normal text-gray-500 dark:text-gray-400 leading-tight">{{ wordsSummary }}</span>
          </Tab>
        </TabList>
        <TabPanels class="!px-0 !pb-0">
          <TabPanel value="titles" class="flex flex-col gap-5">
            <section>
              <h3 class="text-sm font-semibold mb-2">Built from</h3>
              <div v-if="driving.length === 0" class="text-sm text-gray-500 dark:text-gray-400 p-3 rounded-lg bg-surface-50 dark:bg-surface-800">
                Nothing yet. Mark a title Ongoing from its deck page, or include one below.
              </div>
              <ul v-else class="flex flex-col divide-y divide-surface-200 dark:divide-surface-700 rounded-lg border border-surface-200 dark:border-surface-700">
                <li v-for="t in visibleDriving" :key="t.deckId" class="flex items-center gap-3 px-3 py-2">
                  <img :src="coverUrl(t.coverName)" :alt="''" class="w-8 h-11 rounded object-cover bg-surface-100 dark:bg-surface-700 shrink-0" />
                  <div class="min-w-0 flex-1">
                    <div class="text-sm font-medium truncate">
                      <NuxtLink :to="`/decks/media/${t.deckId}/detail`" class="hover:text-primary-500">{{ localiseTitle(t) }}</NuxtLink>
                    </div>
                    <div class="text-xs text-gray-500 dark:text-gray-400 truncate">{{ getMediaTypeText(t.mediaType) }} · {{ progressText(t) }}</div>
                  </div>
                  <div class="flex items-center gap-1 shrink-0 text-xs">
                    <Tooltip
                      v-if="t.totalUnits > 0"
                      :content="
                        isSequential(t)
                          ? 'Currently focus on your Ongoing subdeck / the number of subdecks within your pick ahead. Click to only focus on the Ongoing one.'
                          : 'Currently focus on your Ongoing subdeck, ignoring Pick ahead. Click to follow the pick ahead instead.'
                      "
                      placement="top"
                    >
                      <button
                        type="button"
                        class="px-1.5 py-0.5 rounded border border-surface-200 dark:border-surface-600 text-gray-600 dark:text-gray-300 hover:border-primary-400 dark:hover:border-primary-500 transition-colors"
                        @click="toggleSequence(t)"
                      >
                        {{ isSequential(t) ? 'Follows pick ahead' : 'Follows Ongoing' }}
                      </button>
                    </Tooltip>
                    <span v-if="isPinned(t.deckId)" class="px-1.5 py-0.5 rounded bg-primary-50 dark:bg-primary-900/40 text-primary-700 dark:text-primary-300"
                      >Pinned</span
                    >
                    <span v-else-if="lastActiveText(t)" class="text-gray-500 dark:text-gray-400 whitespace-nowrap">{{ lastActiveText(t) }}</span>
                    <span v-if="t.planning" class="px-1.5 py-0.5 rounded bg-surface-100 dark:bg-surface-700 text-gray-600 dark:text-gray-300">Planning</span>
                    <Tooltip :content="isPinned(t.deckId) ? 'Unpin' : 'Pin to the top'" placement="top">
                      <Button
                        :icon="isPinned(t.deckId) ? 'pi pi-bookmark-fill' : 'pi pi-bookmark'"
                        :severity="isPinned(t.deckId) ? 'primary' : 'secondary'"
                        text
                        size="small"
                        :aria-label="isPinned(t.deckId) ? 'Unpin' : 'Pin'"
                        @click="togglePin(t)"
                      />
                    </Tooltip>
                  </div>
                </li>
              </ul>
              <div v-if="driving.length > DRIVING_PREVIEW" class="flex items-center gap-2 mt-1 text-xs text-gray-500 dark:text-gray-400">
                <button
                  type="button"
                  class="text-primary-600 dark:text-primary-400 hover:underline whitespace-nowrap"
                  @click="showAllDriving = !showAllDriving"
                >
                  {{ showAllDriving ? 'Show fewer' : `Show all ${driving.length}` }}
                </button>
                <span>Weighted by how recently you touched them.</span>
              </div>
              <div class="flex items-start gap-2 mt-3">
                <Checkbox v-model="weighPlanning" input-id="smartPlanning" :binary="true" class="mt-0.5" @update:model-value="refreshPreview" />
                <label for="smartPlanning" class="text-sm cursor-pointer">
                  Also count titles on my Planning list
                  <span class="block text-xs text-gray-500 dark:text-gray-400">They get a lower priority than your Ongoing titles.</span>
                </label>
              </div>
              <div class="grid grid-cols-1 sm:grid-cols-2 gap-3 mt-4">
                <div>
                  <label class="block text-sm font-medium mb-1">Pick ahead</label>
                  <SelectButton
                    v-model="lookahead"
                    :options="lookaheadOptions"
                    option-label="label"
                    option-value="value"
                    :allow-empty="false"
                    class="w-full"
                    :pt="{ pcToggleButton: { root: { class: 'whitespace-nowrap' } } }"
                    @update:model-value="refreshPreview"
                  />
                  <p class="text-xs text-gray-500 dark:text-gray-400 mt-1">Determines the words of how many upcoming volumes/episodes that should be prioritized.</p>
                  <button
                    type="button"
                    class="text-xs text-primary-600 dark:text-primary-400 hover:underline mt-1"
                    :aria-expanded="showPerTypeLookahead"
                    aria-controls="smartPerTypeLookahead"
                    @click="showPerTypeLookahead = !showPerTypeLookahead"
                  >
                    {{ showPerTypeLookahead ? 'Hide per media type' : 'Set per media type' }}{{ perTypeOverrideCount > 0 ? ` (${perTypeOverrideCount} set)` : '' }}
                  </button>
                  <div v-if="showPerTypeLookahead" id="smartPerTypeLookahead" class="mt-2 flex flex-col gap-1.5">
                    <div v-for="type in lookaheadMediaTypes" :key="type" class="flex items-center justify-between gap-3">
                      <label :for="`smartLookahead-${type}`" class="text-sm">{{ getMediaTypeText(type) }}</label>
                      <Select
                        :model-value="perTypeValue(type)"
                        :options="perTypeOptions"
                        option-label="label"
                        option-value="value"
                        :input-id="`smartLookahead-${type}`"
                        class="w-28 shrink-0"
                        size="small"
                        @update:model-value="setPerType(type, $event)"
                      />
                    </div>
                    <p class="text-xs text-gray-500 dark:text-gray-400">Other media types don't support pick ahead.</p>
                  </div>
                </div>
                <div>
                  <div class="flex items-baseline justify-between mb-1">
                    <label for="smartDeckHalfLife" class="text-sm font-medium">Counts less after</label>
                    <span class="text-sm font-semibold text-primary tabular-nums">{{ halfLifeLabel }}</span>
                  </div>
                  <Slider
                    v-model="halfLifeIndex"
                    input-id="smartDeckHalfLife"
                    :min="0"
                    :max="halfLifeSteps.length - 1"
                    :step="1"
                    class="w-full my-3"
                    aria-label="Counts less after"
                  />
                  <p class="text-xs text-gray-500 dark:text-gray-400 mt-1">
                    How long a title sits in your list before its words start becoming less present. Your pinned deck always get top priority.
                  </p>
                </div>
              </div>
            </section>

            <section class="flex flex-col gap-3">
              <div>
                <div class="flex items-center justify-between mb-1">
                  <h3 class="text-sm font-semibold">
                    Pinned titles <span class="font-normal text-gray-400">(up to {{ maxPins }}, highest priority)</span>
                  </h3>
                  <Button label="Add" icon="pi pi-plus" text size="small" :disabled="pinned.length >= maxPins" @click="openPicker('pin')" />
                </div>
                <ul v-if="pinned.length" class="flex flex-col gap-1">
                  <li v-for="(id, index) in pinned" :key="id" class="flex items-center gap-2 text-sm px-2 py-1 rounded bg-surface-50 dark:bg-surface-800">
                    <span class="flex-1 truncate">{{ nameOf(id) }}</span>
                    <Button icon="pi pi-chevron-up" text size="small" :disabled="index === 0" aria-label="Move up" @click="movePin(index, -1)" />
                    <Button
                      icon="pi pi-chevron-down"
                      text
                      size="small"
                      :disabled="index === pinned.length - 1"
                      aria-label="Move down"
                      @click="movePin(index, 1)"
                    />
                    <Button icon="pi pi-times" text size="small" severity="secondary" aria-label="Unpin" @click="removeEverywhere(id)" />
                  </li>
                </ul>
                <p v-else class="text-xs text-gray-500 dark:text-gray-400">Pin the titles you're actively immersing in.</p>
              </div>

              <div>
                <div class="flex items-center justify-between mb-1">
                  <h3 class="text-sm font-semibold">Included <span class="font-normal text-gray-400">(add titles that don't have the Ongoing status)</span></h3>
                  <Button label="Add" icon="pi pi-plus" text size="small" @click="openPicker('include')" />
                </div>
                <ul v-if="included.length" class="flex flex-col gap-1">
                  <li v-for="id in included" :key="id" class="flex items-center gap-2 text-sm px-2 py-1 rounded bg-surface-50 dark:bg-surface-800">
                    <span class="flex-1 truncate">{{ nameOf(id) }}</span>
                    <Button icon="pi pi-times" text size="small" severity="secondary" aria-label="Remove" @click="removeEverywhere(id)" />
                  </li>
                </ul>
              </div>

              <div>
                <div class="flex items-center justify-between mb-1">
                  <h3 class="text-sm font-semibold">Excluded <span class="font-normal text-gray-400">(will never get picked)</span></h3>
                  <Button label="Add" icon="pi pi-plus" text size="small" @click="openPicker('exclude')" />
                </div>
                <ul v-if="excluded.length" class="flex flex-col gap-1">
                  <li v-for="id in excluded" :key="id" class="flex items-center gap-2 text-sm px-2 py-1 rounded bg-surface-50 dark:bg-surface-800">
                    <span class="flex-1 truncate">{{ nameOf(id) }}</span>
                    <Button icon="pi pi-times" text size="small" severity="secondary" aria-label="Remove" @click="removeEverywhere(id)" />
                  </li>
                </ul>
              </div>

              <div v-if="pickerTarget" class="rounded-lg border border-primary-300 dark:border-primary-700 p-3">
                <label for="smartPicker" class="block text-xs font-medium mb-1"> Search a title to {{ pickerTarget === 'pin' ? 'pin' : pickerTarget }} </label>
                <InputText id="smartPicker" v-model="searchQuery" placeholder="Title name" class="w-full" autofocus />
                <div v-if="searching" class="text-xs text-gray-500 dark:text-gray-400 mt-2">Searching...</div>
                <ul v-else-if="searchResults.length" class="mt-2 flex flex-col divide-y divide-surface-200 dark:divide-surface-700">
                  <li v-for="r in searchResults" :key="r.deckId">
                    <button
                      type="button"
                      class="w-full flex items-center gap-2 py-1.5 text-left hover:bg-surface-50 dark:hover:bg-surface-800 rounded px-1"
                      @click="pick(r)"
                    >
                      <img :src="coverUrl(r.coverName)" alt="" class="w-6 h-8 rounded object-cover shrink-0" />
                      <span class="text-sm truncate">{{ localiseTitle(r) }}</span>
                      <span class="text-xs text-gray-400 shrink-0">{{ getMediaTypeText(r.mediaType) }}</span>
                    </button>
                  </li>
                </ul>
                <div v-else-if="searchQuery.trim().length >= 2" class="text-xs text-gray-500 dark:text-gray-400 mt-2">No title matches that.</div>
              </div>
            </section>
          </TabPanel>
          <TabPanel value="words" class="flex flex-col gap-5">
            <p class="text-xs text-gray-500 dark:text-gray-400">Determine which words the deck will present to you.</p>
            <section>
              <div class="flex items-baseline justify-between mb-1">
                <label for="smartDeckTarget" class="text-sm font-medium">Coverage target</label>
                <span class="text-sm font-semibold text-primary tabular-nums">{{ targetPercentage }}%</span>
              </div>
              <Slider v-model="targetPercentage" input-id="smartDeckTarget" :min="50" :max="100" :step="1" class="w-full my-3" aria-label="Coverage target" />
              <p class="text-xs text-gray-500 dark:text-gray-400 mt-1">
                Stop adding new words once you would understand this share of each upcoming unit or titles without episodes or volumes.
              </p>
            </section>
            <section>
              <div class="flex items-baseline justify-between mb-1">
                <label for="smartDeckRestTarget" class="text-sm font-medium">Rest of each title</label>
                <span class="text-sm font-semibold text-primary tabular-nums">{{ restTargetLabel }}</span>
              </div>
              <Slider v-model="restTargetPercentage" input-id="smartDeckRestTarget" :min="0" :max="100" :step="1" class="w-full my-3" aria-label="Rest of each title" />
              <p class="text-xs text-gray-500 dark:text-gray-400 mt-1">
                The share for the whole title beyond your pick ahead. At 0, the deck will only add words from your units ahead.
              </p>
            </section>
            <section>
              <div class="grid grid-cols-1 sm:grid-cols-2 gap-3 mb-3">
                <div class="min-w-0">
                  <label class="block text-xs mb-1">Min global rank <span class="text-gray-400">(blank = no limit)</span></label>
                  <InputNumber v-model="minRank" :min="1" class="w-full [&_input]:w-full" />
                </div>
                <div class="min-w-0">
                  <label class="block text-xs mb-1">Max global rank <span class="text-gray-400">(blank = no limit)</span></label>
                  <InputNumber v-model="maxRank" :min="1" class="w-full [&_input]:w-full" />
                </div>
              </div>
              <div class="mb-3">
                <label class="block text-sm font-medium mb-1">Only include words tagged <span class="text-gray-400">(optional)</span></label>
                <PosFilterSelect v-model="posFilter" />
              </div>
              <div class="flex items-center gap-2">
                <Checkbox v-model="excludeKana" input-id="smartExcludeKana" :binary="true" />
                <label for="smartExcludeKana" class="text-sm cursor-pointer">Exclude kana-only words</label>
              </div>
            </section>
          </TabPanel>
        </TabPanels>
      </Tabs>

      <div class="flex flex-col sm:flex-row gap-2 sm:items-center sm:justify-between pt-2 border-t border-surface-200 dark:border-surface-700">
        <div class="flex items-center gap-1">
          <Button
            v-if="smart.status.value.exists && isPlus"
            label="Rebuild now"
            icon="pi pi-refresh"
            severity="secondary"
            text
            size="small"
            :loading="rebuilding"
            @click="rebuildNow"
          />
          <Button
            v-if="smart.status.value.exists"
            label="Remove"
            icon="pi pi-trash"
            severity="danger"
            text
            size="small"
            :loading="removing"
            @click="confirmRemove"
          />
        </div>
        <div class="flex items-center gap-2">
          <template v-if="!isPlus">
            <Button v-if="isNew && activeTab === 'words'" label="Back" severity="secondary" text @click="activeTab = 'titles'" />
            <Button
              v-if="isNew && activeTab === 'titles'"
              label="Next: word filters"
              icon="pi pi-arrow-right"
              icon-pos="right"
              severity="secondary"
              @click="activeTab = 'words'"
            />
            <Button label="Close" severity="secondary" text @click="localVisible = false" />
          </template>
          <template v-else-if="isNew">
            <Button v-if="activeTab === 'titles'" label="Next: word filters" icon="pi pi-arrow-right" icon-pos="right" @click="activeTab = 'words'" />
            <template v-else>
              <Button label="Back" severity="secondary" text @click="activeTab = 'titles'" />
              <Button label="Add Smart Deck" :loading="smart.saving.value" @click="save" />
            </template>
          </template>
          <Button v-else label="Save" :loading="smart.saving.value" @click="save" />
        </div>
      </div>
    </div>
  </Dialog>
</template>
