<script setup lang="ts">
  import { ref, computed, reactive, watch, onBeforeUnmount } from 'vue';
  import { debounce } from 'perfect-debounce';
  import Card from 'primevue/card';
  import Button from 'primevue/button';
  import InputText from 'primevue/inputtext';
  import InputNumber from 'primevue/inputnumber';
  import Slider from 'primevue/slider';
  import MultiSelect from 'primevue/multiselect';
  import AutoComplete from 'primevue/autocomplete';
  import type { AutoCompleteCompleteEvent } from 'primevue/autocomplete';
  import Tag from 'primevue/tag';
  import SelectButton from 'primevue/selectbutton';
  import Checkbox from 'primevue/checkbox';
  import Dialog from 'primevue/dialog';
  import Message from 'primevue/message';
  import ProgressSpinner from 'primevue/progressspinner';
  import { useToast } from 'primevue/usetoast';
  import { useConfirm } from 'primevue/useconfirm';
  import { useApiFetch } from '~/composables/useApiFetch';
  import { useAuthStore } from '~/stores/authStore';
  import { useSrsStore } from '~/stores/srsStore';
  import { buildPlanStudyBatch, isDeckStudied, planStepsToAdd } from '~/utils/planStudyBatch';
  import { useJitenStore } from '~/stores/jitenStore';
  import { useLocaliseTitle } from '~/composables/useLocaliseTitle';
  import { MediaType, type Tag as MediaTag, type MediaSuggestion } from '~/types';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { getAllGenres } from '~/utils/genreMapper';
  import { extractApiError } from '~/utils/toast';
  import { createBitmapLoader, drawCoverImage, fitCanvasText, saveCanvasPng } from '~/utils/imageExport';
  import { coverUrl } from '~/utils/coverImage';
  import type { TagState } from '~/components/TriStateTag.vue';
  import DifficultyDisplay from '~/components/DifficultyDisplay.vue';

  definePageMeta({ middleware: ['auth'] });
  useHead({ title: 'Immersion Plans - Jiten' });

  const { $api } = useNuxtApp();
  const toast = useToast();
  const confirm = useConfirm();
  const { isPlus, limits } = useJitenPlus();
  const auth = useAuthStore();
  const store = useJitenStore();
  const srsStore = useSrsStore();

  // ---- Types --------------------------------------------------------------

  interface RoadmapWord {
    wordId: number;
    readingIndex: number;
    text: string;
    reading: string;
    occurrences: number;
    frequencyRank: number;
  }


  interface FrequencyBands {
    band0To3k: number;
    band3kTo10k: number;
    band10kTo25k: number;
    band25kTo50k: number;
    band50kTo80k: number;
    band80kPlus: number;
    unranked: number;
  }

  interface RoadmapStep {
    index: number;
    deckId: number;
    title: string;
    romajiTitle: string | null;
    englishTitle: string | null;
    coverName: string | null;
    mediaType: number;
    genres: number[];
    difficulty: number;
    coverage: number;
    newWords: number;
    goalNewWords: number | null;
    wordCount: number;
    characterCount: number;
    speechDuration: number;
    frequencyBands: FrequencyBands;
    // Packed (wordId, readingIndex) keys; text/reading/rank fetched on demand when the step is expanded.
    words: number[];
    goalCoverageAfter: number | null;
  }

  interface RoadmapDrill {
    deckId: number;
    title: string;
    coverage: number;
    wordsNeeded: number;
    words: RoadmapWord[];
  }

  interface RoadmapGoal {
    deckId: number;
    title: string;
    romajiTitle: string | null;
    englishTitle: string | null;
    coverName: string | null;
    mediaType: number;
    difficulty: number;
    wordCount: number;
    coverage: number;
    reached: boolean;
    wordsRemaining: number;
  }

  interface RoadmapPayload {
    steps: RoadmapStep[];
    drill: RoadmapDrill | null;
    goalReached: boolean;
    goalCeilingReached: boolean;
    goalUnreachableWords: number;
    goalCoverageFinal: number | null;
    goalWordsRemaining: number | null;
    goalWordsAtStart: number | null;
    totalNewWords: number;
    totalGoalNewWords: number | null;
    goal: RoadmapGoal | null;
  }

  interface RoadmapDefinition {
    mediaTypes: number[];
    genresInclude: number[];
    genresExclude: number[];
    tagsInclude: number[];
    tagsExclude: number[];
    showsDifficultyMin: number | null;
    showsDifficultyMax: number | null;
    novelsDifficultyMin: number | null;
    novelsDifficultyMax: number | null;
    comprehensionFloor: number;
    comfortTarget: number;
    goalComprehensionTarget: number;
    includeLearningWords: boolean;
    acquisitionThreshold: number;
    steps: number;
    goalSteps: number;
    preference: 'efficiency' | 'volume';
    candidateMode: 'seeded' | 'catalogwide';
    contentSimilarity: number;
    includeAdultOnly: boolean;
    adultOnlyExclusive: boolean;
  }

  interface Roadmap {
    id: number;
    name: string;
    mode: 'discovery' | 'goal';
    goalDeckId: number | null;
    status: 'pending' | 'generating' | 'ready' | 'failed';
    failureReason: string | null;
    stepCount: number;
    candidateCount: number;
    createdAt: string;
    generatedAt: string | null;
    swappedCount: number;
    payload: RoadmapPayload | null;
    definition: RoadmapDefinition;
  }

  // ---- Builder state ------------------------------------------------------

  type Mode = 'discovery' | 'goal';
  const modeOptions = [
    { label: 'Next picks', value: 'discovery' as Mode },
    { label: 'Target media', value: 'goal' as Mode },
  ];
  const mode = ref<Mode>('discovery');
  const name = ref('');

  const mediaTypes = ref<number[]>([]);
  const showsDifficulty = ref<[number, number]>([0, 5]);
  const novelsDifficulty = ref<[number, number]>([0, 5]);
  // [hard floor, comfort target] — never suggest below the first, prefer at or above the second.
  const comprehension = ref<[number, number]>([80, 90]);
  const countLearningWords = ref(true);
  const acquisitionThreshold = ref(12);
  const steps = ref(5);
  const goalSteps = ref(30);
  const preference = ref<'efficiency' | 'volume'>('efficiency');
  const candidateMode = ref<'seeded' | 'catalogwide'>('seeded');
  const contentSimilarity = ref(0);
  const includeAdultOnly = ref(false);
  const adultOnlyExclusive = ref(false);

  const genreStates = reactive<Record<number, TagState>>({});
  const tagStates = reactive<Record<number, TagState>>({});

  const goalDeck = ref<MediaSuggestion | string | null>(null);
  const deckSuggestions = ref<MediaSuggestion[]>([]);
  // Goal mode only: how much of the goal title counts as "understanding it". Distinct from the comprehension
  // range below, which in goal mode only decides which stepping-stone titles are readable enough to suggest.
  const goalTarget = ref(95);

  const preferenceOptions = [
    { label: 'By time spent', value: 'efficiency' },
    { label: 'Per title', value: 'volume' },
  ];
  const candidateModeOptions = [
    { label: 'My list & similar', value: 'seeded' },
    { label: 'Everything', value: 'catalogwide' },
  ];

  const mediaTypeOptions = Object.values(MediaType)
    .filter((v) => typeof v === 'number')
    .map((v) => ({ name: getMediaTypeText(v as MediaType), id: v as MediaType }))
    .sort((a, b) => a.name.localeCompare(b.name));

  const genres = getAllGenres();
  const { data: availableTags } = useApiFetch<MediaTag[]>('media-deck/tags', { server: true, lazy: false });

  const AUDIO_VISUAL_TYPES = [MediaType.Anime, MediaType.Movie, MediaType.Drama, MediaType.Audio];

  // The two difficulty models are adapted to different training data, so a band is only meaningful
  // within its own family. Each slider is shown only when its family is actually in scope.
  const showsInScope = computed(
    () => mediaTypes.value.length === 0 || mediaTypes.value.some((t) => AUDIO_VISUAL_TYPES.includes(t)),
  );
  const novelsInScope = computed(
    () => mediaTypes.value.length === 0 || mediaTypes.value.some((t) => !AUDIO_VISUAL_TYPES.includes(t)),
  );

  const similarityLabel = computed(() => {
    const v = contentSimilarity.value;
    if (v <= -1.5) return 'Something totally different';
    if (v < -0.25) return 'A bit of a change';
    if (v <= 0.25) return 'No preference';
    if (v < 1.5) return 'A bit like what I know';
    return 'More of what I already like';
  });

  const coverageBasisOptions = [
    { label: 'Mature + Young words', value: true },
    { label: 'Only mature words', value: false },
  ];

  // ---- Defaults -----------------------------------------------------------

  const loadingDefaults = ref(false);
  const hasSuggestedBands = ref(false);
  const maxRoadmaps = ref(50);

  async function loadDefaults() {
    if (!isPlus.value) return;
    loadingDefaults.value = true;
    try {
      const res = await $api<{
        showsDifficultyMin: number | null;
        showsDifficultyMax: number | null;
        novelsDifficultyMin: number | null;
        novelsDifficultyMax: number | null;
        hasBands: boolean;
        maxRoadmaps: number;
      }>('roadmaps/defaults');

      if (res.showsDifficultyMin != null && res.showsDifficultyMax != null)
        showsDifficulty.value = [res.showsDifficultyMin, res.showsDifficultyMax];
      if (res.novelsDifficultyMin != null && res.novelsDifficultyMax != null)
        novelsDifficulty.value = [res.novelsDifficultyMin, res.novelsDifficultyMax];

      hasSuggestedBands.value = res.hasBands;
      if (res.maxRoadmaps) maxRoadmaps.value = res.maxRoadmaps;
    } catch {
      hasSuggestedBands.value = false;
    } finally {
      loadingDefaults.value = false;
    }
  }

  const atCap = computed(() => roadmaps.value.length >= maxRoadmaps.value);

  // ---- Deck search (goal mode) -------------------------------------------

  async function searchDecks(event: AutoCompleteCompleteEvent) {
    if (!event.query || event.query.length < 2) {
      deckSuggestions.value = [];
      return;
    }
    try {
      const res = await $api<{ suggestions: MediaSuggestion[] }>('media-deck/search-suggestions', {
        query: { query: event.query, limit: 8 },
      });
      deckSuggestions.value = res.suggestions ?? [];
    } catch {
      deckSuggestions.value = [];
    }
  }

  const goalDeckId = computed(() =>
    goalDeck.value && typeof goalDeck.value !== 'string' ? goalDeck.value.deckId : null,
  );

  // ---- Build & submit -----------------------------------------------------

  function toIdList(states: Record<number, TagState>, wanted: TagState): number[] {
    return Object.entries(states)
      .filter(([, s]) => s === wanted)
      .map(([id]) => Number(id));
  }

  function buildDefinition() {
    const [showsMin, showsMax] = showsDifficulty.value;
    const [novelsMin, novelsMax] = novelsDifficulty.value;

    return {
      mediaTypes: mediaTypes.value,
      genresInclude: toIdList(genreStates, 'include'),
      genresExclude: toIdList(genreStates, 'exclude'),
      tagsInclude: toIdList(tagStates, 'include'),
      tagsExclude: toIdList(tagStates, 'exclude'),
      yearFrom: null,
      yearTo: null,
      showsDifficultyMin: showsMin > 0 ? showsMin : null,
      showsDifficultyMax: showsMax < 5 ? showsMax : null,
      novelsDifficultyMin: novelsMin > 0 ? novelsMin : null,
      novelsDifficultyMax: novelsMax < 5 ? novelsMax : null,
      comprehensionFloor: comprehension.value[0] / 100,
      comfortTarget: comprehension.value[1] / 100,
      goalComprehensionTarget: goalTarget.value / 100,
      includeLearningWords: countLearningWords.value,
      acquisitionThreshold: acquisitionThreshold.value,
      steps: steps.value,
      goalSteps: goalSteps.value,
      preference: preference.value,
      candidateMode: candidateMode.value,
      contentSimilarity: contentSimilarity.value,
      includeAdultOnly: includeAdultOnly.value,
      adultOnlyExclusive: includeAdultOnly.value && adultOnlyExclusive.value,
    };
  }

  // ---- Live preview -------------------------------------------------------

  interface PlanPreview {
    matchingFilters: number;
    candidates: number;
    aboveFloor: number;
    aboveComfort: number;
    hasCoverageData: boolean;
    goalCoverage: number | null;
  }

  const preview = ref<PlanPreview | null>(null);
  const previewLoading = ref(false);

  const runPreview = debounce(async () => {
    previewLoading.value = true;
    try {
      preview.value = await $api<PlanPreview>('roadmaps/preview', {
        method: 'POST',
        body: buildDefinition(),
        query: mode.value === 'goal' && goalDeckId.value != null ? { goalDeckId: goalDeckId.value } : undefined,
      });
    } catch {
      preview.value = null;
    } finally {
      previewLoading.value = false;
    }
  }, 400);

  watch(
    [
      mediaTypes,
      showsDifficulty,
      novelsDifficulty,
      comprehension,
      countLearningWords,
      candidateMode,
      includeAdultOnly,
      adultOnlyExclusive,
      genreStates,
      tagStates,
      mode,
      goalDeckId,
    ],
    () => {
      if (isPlus.value) runPreview();
    },
    { deep: true },
  );

  // Below this the search is picking whatever fits rather than the best of several, and swapping a step
  // (which bars that title for good) starts running out of replacements.
  const HEALTHY_POOL = 50;

  // Counts come from precomputed coverage, which is all zeroes until a user's first computation run — that
  // would read as "nothing is readable" when the truth is simply not known yet.
  const previewWarning = computed(() => {
    const p = preview.value;
    if (!p || !p.hasCoverageData || p.aboveFloor >= HEALTHY_POOL) return null;

    return `Only ${p.aboveFloor} titles are at ${comprehension.value[0]}% or above for you, which might be too little. Lower the minimum or widen the filters for an optimal plan.`;
  });

  const previewSummary = computed(() => {
    const p = preview.value;
    if (!p || !p.hasCoverageData || previewWarning.value) return null;
    return `${p.aboveFloor.toLocaleString()} of ${p.candidates.toLocaleString()} matching titles are at ${comprehension.value[0]}% or above for you, ${p.aboveComfort.toLocaleString()} at ${comprehension.value[1]}% or above.`;
  });

  const creating = ref(false);

  // Null while composing a new plan; set to a plan's id when its settings are loaded in for editing.
  const editingId = ref<number | null>(null);

  const canSubmit = computed(() => {
    if (!name.value.trim()) return false;
    if (mode.value === 'goal' && goalDeckId.value == null) return false;
    return true;
  });

  async function create() {
    if (!canSubmit.value) return;
    creating.value = true;
    try {
      const created = await $api<Roadmap>('roadmaps', {
        method: 'POST',
        body: {
          name: name.value.trim(),
          mode: mode.value,
          goalDeckId: goalDeckId.value,
          definition: buildDefinition(),
        },
      });
      roadmaps.value = [created, ...roadmaps.value];
      activeId.value = created.id;

      // Stay on the plan that was just made, with the settings it was made from: the next thing a user does
      // is tweak and rebuild. The builder state is kept as typed rather than reloaded from the created DTO,
      // so the goal title stays resolved while the plan is still generating and has no payload to read it from.
      editingId.value = created.id;

      ensurePolling();
    } catch (e) {
      toast.add({ severity: 'error', summary: "Couldn't make the plan", detail: extractApiError(e, 'Something went wrong.'), life: 6000 });
    } finally {
      creating.value = false;
    }
  }

  // ---- Edit an existing plan ---------------------------------------------

  function applyTagStates(states: Record<number, TagState>, include: number[], exclude: number[]) {
    for (const key of Object.keys(states)) states[Number(key)] = 'neutral';
    for (const id of include) states[id] = 'include';
    for (const id of exclude) states[id] = 'exclude';
  }

  function resetBuilder() {
    editingId.value = null;
    mode.value = 'discovery';
    name.value = '';
    mediaTypes.value = [];
    showsDifficulty.value = [0, 5];
    novelsDifficulty.value = [0, 5];
    comprehension.value = [80, 90];
    goalTarget.value = 95;
    countLearningWords.value = true;
    acquisitionThreshold.value = 12;
    steps.value = 5;
    goalSteps.value = 30;
    preference.value = 'efficiency';
    candidateMode.value = 'seeded';
    contentSimilarity.value = 0;
    includeAdultOnly.value = false;
    adultOnlyExclusive.value = false;
    goalDeck.value = null;
    applyTagStates(genreStates, [], []);
    applyTagStates(tagStates, [], []);
  }

  // The goal title isn't in the definition; it's resolved from the loaded plan's payload.
  function goalSuggestionFromPayload(): MediaSuggestion | null {
    const g = activeDetail.value?.payload?.goal;
    if (!g) return null;
    return {
      deckId: g.deckId,
      originalTitle: g.title,
      romajiTitle: g.romajiTitle ?? undefined,
      englishTitle: g.englishTitle ?? undefined,
      mediaType: g.mediaType,
      coverName: g.coverName ?? '',
    };
  }

  function loadDefinitionIntoBuilder(roadmap: Roadmap) {
    editingId.value = roadmap.id;
    const d = roadmap.definition;
    mode.value = roadmap.mode;
    name.value = roadmap.name;
    mediaTypes.value = [...d.mediaTypes];
    applyTagStates(genreStates, d.genresInclude, d.genresExclude);
    applyTagStates(tagStates, d.tagsInclude, d.tagsExclude);
    showsDifficulty.value = [d.showsDifficultyMin ?? 0, d.showsDifficultyMax ?? 5];
    novelsDifficulty.value = [d.novelsDifficultyMin ?? 0, d.novelsDifficultyMax ?? 5];
    comprehension.value = [Math.round(d.comprehensionFloor * 100), Math.round(d.comfortTarget * 100)];
    goalTarget.value = Math.round(d.goalComprehensionTarget * 100);
    countLearningWords.value = d.includeLearningWords;
    acquisitionThreshold.value = d.acquisitionThreshold;
    steps.value = d.steps;
    goalSteps.value = d.goalSteps ?? 30;
    preference.value = d.preference;
    candidateMode.value = d.candidateMode;
    contentSimilarity.value = d.contentSimilarity;
    includeAdultOnly.value = d.includeAdultOnly;
    adultOnlyExclusive.value = d.adultOnlyExclusive;
    goalDeck.value =
      roadmap.mode === 'goal'
        ? (goalSuggestionFromPayload() ?? (roadmap.goalDeckId != null ? { deckId: roadmap.goalDeckId, originalTitle: '', mediaType: 0, coverName: '' } : null))
        : null;
  }

  function selectPlan(roadmap: Roadmap) {
    activeId.value = roadmap.id;
    loadDefinitionIntoBuilder(roadmap);
  }

  async function saveAndRegenerate() {
    if (editingId.value == null || !canSubmit.value) return;
    creating.value = true;
    try {
      const updated = await $api<Roadmap>(`roadmaps/${editingId.value}`, {
        method: 'PUT',
        body: {
          name: name.value.trim(),
          mode: mode.value,
          goalDeckId: goalDeckId.value,
          definition: buildDefinition(),
        },
      });
      roadmaps.value = roadmaps.value.map((r) => (r.id === updated.id ? updated : r));
      activeDetail.value = updated;
      ensurePolling();
      toast.add({ severity: 'success', summary: 'Plan updated', detail: 'Rebuilding with your changes…', life: 4000 });
    } catch (e) {
      toast.add({ severity: 'error', summary: "Couldn't update the plan", detail: extractApiError(e, 'Something went wrong.'), life: 6000 });
    } finally {
      creating.value = false;
    }
  }

  // ---- Roadmap list -------------------------------------------------------

  const roadmaps = ref<Roadmap[]>([]);
  const activeId = ref<number | null>(null);
  const activeDetail = ref<Roadmap | null>(null);
  const loadingList = ref(false);
  const loadingDetail = ref(false);

  async function loadList() {
    if (!auth.isAuthenticated) return;
    loadingList.value = true;
    try {
      roadmaps.value = await $api<Roadmap[]>('roadmaps');
      if (activeId.value == null && roadmaps.value.length > 0) activeId.value = roadmaps.value[0]!.id;
      ensurePolling();
    } finally {
      loadingList.value = false;
    }
  }

  async function loadDetail(id: number) {
    loadingDetail.value = true;
    stepWordsCache.value = {};
    try {
      activeDetail.value = await $api<Roadmap>(`roadmaps/${id}`);
    } catch {
      activeDetail.value = null;
    } finally {
      loadingDetail.value = false;
    }
  }

  watch(activeId, (id) => {
    activeDetail.value = null;
    if (id != null) loadDetail(id);
  });

  // When editing a goal plan, the goal title isn't in the definition — fill it from the payload once it loads,
  // unless the user has already picked a different title in the builder.
  watch(activeDetail, (detail) => {
    if (editingId.value == null || detail?.id !== editingId.value || detail.mode !== 'goal') return;
    const suggestion = goalSuggestionFromPayload();
    if (suggestion && (goalDeck.value == null || typeof goalDeck.value === 'string' || goalDeck.value.deckId === suggestion.deckId)) {
      goalDeck.value = suggestion;
    }
  });

  let pollTimer: ReturnType<typeof setInterval> | null = null;

  function ensurePolling() {
    const anyPending = roadmaps.value.some((r) => r.status === 'pending' || r.status === 'generating');
    if (anyPending && !pollTimer) {
      pollTimer = setInterval(refreshPending, 3000);
    } else if (!anyPending && pollTimer) {
      clearInterval(pollTimer);
      pollTimer = null;
    }
  }

  async function refreshPending() {
    const previous = new Map(roadmaps.value.map((r) => [r.id, r.status]));
    try {
      roadmaps.value = await $api<Roadmap[]>('roadmaps');
    } catch {
      return;
    }

    // Reload the open roadmap only when it actually finished, so polling never fights the detail view.
    const active = roadmaps.value.find((r) => r.id === activeId.value);
    if (active && previous.get(active.id) !== active.status && active.status === 'ready') {
      await loadDetail(active.id);
    }

    ensurePolling();
  }

  // ---- Step actions -------------------------------------------------------

  const swapping = ref<number | null>(null);

  async function swapStep(step: RoadmapStep) {
    if (activeId.value == null) return;
    swapping.value = step.index;
    try {
      await $api(`roadmaps/${activeId.value}/steps/${step.index}/swap`, { method: 'POST' });
      toast.add({
        severity: 'info',
        summary: 'Looking for something else',
        detail: `"${step.title}" won't come up again on this plan.`,
        life: 4000,
      });
      await refreshPending();
      ensurePolling();
    } catch (e) {
      toast.add({ severity: 'error', summary: "Couldn't swap that one", detail: extractApiError(e, 'Something went wrong.'), life: 6000 });
    } finally {
      swapping.value = null;
    }
  }

  async function resetSwaps() {
    if (activeId.value == null) return;
    try {
      await $api(`roadmaps/${activeId.value}/reset-swaps`, { method: 'POST' });
      await refreshPending();
      ensurePolling();
    } catch (e) {
      toast.add({ severity: 'error', summary: "Couldn't undo the skips", detail: extractApiError(e, 'Something went wrong.'), life: 6000 });
    }
  }

  async function regenerate(id: number) {
    try {
      await $api(`roadmaps/${id}/regenerate`, { method: 'POST' });
      await refreshPending();
      ensurePolling();
    } catch (e) {
      toast.add({ severity: 'error', summary: "Couldn't start over", detail: extractApiError(e, 'Something went wrong.'), life: 6000 });
    }
  }

  // Array order is not meaningful in a definition (a multi-select emits selection order), so a comparison
  // that respects it would report edits the user never made.
  function comparableDefinition(definition: Record<string, unknown>): string {
    const normalised: Record<string, unknown> = {};
    for (const key of Object.keys(definition).sort()) {
      const value = definition[key];
      normalised[key] = Array.isArray(value) ? [...value].sort() : (value ?? null);
    }
    return JSON.stringify(normalised);
  }

  // Selecting a plan loads its settings into the builder, so the builder can hold edits the plan has not been
  // saved with. Regenerating from the stored definition would silently drop them.
  const builderDirty = computed(() => {
    const roadmap = roadmaps.value.find((r) => r.id === editingId.value);
    if (!roadmap) return false;

    if (name.value.trim() !== roadmap.name) return true;
    if (mode.value !== roadmap.mode) return true;
    if ((goalDeckId.value ?? null) !== (roadmap.goalDeckId ?? null)) return true;

    const current = buildDefinition() as Record<string, unknown>;
    const saved = Object.fromEntries(
      Object.keys(current).map((key) => [key, (roadmap.definition as unknown as Record<string, unknown>)[key] ?? null]),
    );

    return comparableDefinition(current) !== comparableDefinition(saved);
  });

  function discardBuilderChanges() {
    const roadmap = roadmaps.value.find((r) => r.id === editingId.value);
    if (roadmap) loadDefinitionIntoBuilder(roadmap);
  }

  async function startOver(id: number) {
    if (editingId.value === id && builderDirty.value) {
      if (!canSubmit.value) {
        toast.add({
          severity: 'warn',
          summary: 'Finish your changes first',
          detail: mode.value === 'goal' && goalDeckId.value == null ? 'Pick a target title.' : 'Give the plan a name.',
          life: 5000,
        });
        return;
      }
      await saveAndRegenerate();
      return;
    }
    await regenerate(id);
  }

  function confirmDelete(roadmap: Roadmap) {
    confirm.require({
      message: `Delete "${roadmap.name}"? This can't be undone.`,
      header: 'Delete plan',
      icon: 'pi pi-exclamation-triangle',
      acceptClass: 'p-button-danger',
      accept: async () => {
        try {
          await $api(`roadmaps/${roadmap.id}`, { method: 'DELETE' });
          roadmaps.value = roadmaps.value.filter((r) => r.id !== roadmap.id);
          if (activeId.value === roadmap.id) {
            activeId.value = roadmaps.value[0]?.id ?? null;
          }
        } catch (e) {
          toast.add({ severity: 'error', summary: "Couldn't delete", detail: extractApiError(e, 'Something went wrong.'), life: 6000 });
        }
      },
    });
  }

  // ---- Display helpers ----------------------------------------------------

  const localiseTitle = useLocaliseTitle();

  function deckOptionLabel(option: MediaSuggestion): string {
    return localiseTitle(option);
  }

  const goalTitleText = computed(() =>
    goalDeck.value && typeof goalDeck.value !== 'string' ? localiseTitle(goalDeck.value) : 'the title',
  );

  // Comes from the precomputed coverage chunks the preview reads, so it can differ by a fraction of a percent
  // from the figure the generated plan reports off the live known-word walk.
  const goalCurrentCoverage = computed(() =>
    mode.value === 'goal' && goalDeckId.value != null ? preview.value?.goalCoverage ?? null : null,
  );

  function stepTitle(step: RoadmapStep): string {
    return localiseTitle({
      originalTitle: step.title,
      romajiTitle: step.romajiTitle,
      englishTitle: step.englishTitle,
    });
  }

  function goalCardTitle(goal: RoadmapGoal): string {
    return localiseTitle({
      originalTitle: goal.title,
      romajiTitle: goal.romajiTitle,
      englishTitle: goal.englishTitle,
    });
  }

  function pct(value: number): string {
    return `${(value * 100).toFixed(1)}%`;
  }

  const bandDefs: { key: keyof FrequencyBands; label: string; cls: string }[] = [
    { key: 'band0To3k', label: 'Top 3k', cls: 'bg-green-500' },
    { key: 'band3kTo10k', label: '3k–10k', cls: 'bg-emerald-400' },
    { key: 'band10kTo25k', label: '10k–25k', cls: 'bg-cyan-400' },
    { key: 'band25kTo50k', label: '25k–50k', cls: 'bg-amber-400' },
    { key: 'band50kTo80k', label: '50k–80k', cls: 'bg-orange-400' },
    { key: 'band80kPlus', label: '80k+', cls: 'bg-red-400' },
    { key: 'unranked', label: 'No data', cls: 'bg-surface-400' },
  ];

  // "Common, everyday" runs to roughly the top 10k ranks in this corpus.
  function usefulShare(bands: FrequencyBands): number {
    const total = bandTotal(bands);
    if (total === 0) return 0;
    return (bands.band0To3k + bands.band3kTo10k) / total;
  }

  function bandTotal(bands: FrequencyBands): number {
    return bandDefs.reduce((sum, b) => sum + bands[b.key], 0);
  }

  // Which step's word list is expanded. Only one open at a time keeps the page from becoming a wall of words.
  const expandedStep = ref<number | null>(null);

  // Step word lists are stored as bare keys; their text/reading/rank load on demand the first time a step is
  // expanded, cached by step index. Cleared in loadDetail whenever the payload is (re)loaded.
  type StepWordsState = { status: 'loading' | 'error' } | { status: 'ready'; words: RoadmapWord[] };
  const stepWordsCache = ref<Record<number, StepWordsState>>({});

  async function loadStepWords(index: number) {
    if (activeId.value == null) return;
    const existing = stepWordsCache.value[index]?.status;
    if (existing === 'loading' || existing === 'ready') return;
    stepWordsCache.value = { ...stepWordsCache.value, [index]: { status: 'loading' } };
    try {
      const res = await $api<{ words: RoadmapWord[] }>(`roadmaps/${activeId.value}/steps/${index}/words`);
      stepWordsCache.value = { ...stepWordsCache.value, [index]: { status: 'ready', words: res.words ?? [] } };
    } catch {
      stepWordsCache.value = { ...stepWordsCache.value, [index]: { status: 'error' } };
    }
  }

  function toggleWords(index: number) {
    if (expandedStep.value === index) {
      expandedStep.value = null;
      return;
    }
    expandedStep.value = index;
    loadStepWords(index);
  }

  function stepWordsStatus(index: number): 'loading' | 'error' | 'ready' | null {
    return stepWordsCache.value[index]?.status ?? null;
  }

  function stepWords(index: number): RoadmapWord[] {
    const state = stepWordsCache.value[index];
    return state?.status === 'ready' ? state.words : [];
  }

  const activeRoadmap = computed(() => roadmaps.value.find((r) => r.id === activeId.value) ?? null);
  const activePayload = computed(() => activeDetail.value?.payload ?? null);

  // How much media the plan actually is. Audio-visual steps are timed by their speech duration and everything
  // else by the user's reading speed, matching how a deck card states its own duration.
  const planTotals = computed(() => {
    let characters = 0;
    let hours = 0;
    for (const step of activePayload.value?.steps ?? []) {
      characters += step.characterCount ?? 0;
      if (step.speechDuration > 0) hours += step.speechDuration / 3_600_000;
      else if (step.characterCount > 0) hours += step.characterCount / store.readingSpeed;
    }
    return { characters, hours };
  });

  function stepHours(step: RoadmapStep): number {
    if (step.speechDuration > 0) return step.speechDuration / 3_600_000;
    return step.characterCount > 0 ? step.characterCount / store.readingSpeed : 0;
  }

  function formatHours(hours: number): string {
    if (hours <= 0) return '—';
    if (hours < 1) return `${Math.round(hours * 60)} min`;
    if (hours < 10) {
      const whole = Math.floor(hours);
      const minutes = Math.round((hours - whole) * 60);
      return minutes === 0 ? `${whole} h` : `${whole} h ${minutes} min`;
    }
    return `${Math.round(hours).toLocaleString()} h`;
  }

  // ---- Export as image ----------------------------------------------------

  const isExporting = ref(false);
  const loadExportBitmap = createBitmapLoader();

  const EXPORT_LIGHT = {
    bg: '#ffffff', card: '#f4f5f7', text: '#1f2937', sub: '#6b7280', foot: '#9ca3af',
    brand: '#9333ea', words: '#16a34a', goal: '#d97706', goalBg: '#fffbeb', onAccent: '#ffffff',
  };
  const EXPORT_DARK = {
    bg: '#18181b', card: '#27272a', text: '#e5e7eb', sub: '#a1a1aa', foot: '#71717a',
    brand: '#c084fc', words: '#4ade80', goal: '#fbbf24', goalBg: '#2c2410', onAccent: '#18181b',
  };

  // Layout constants (CSS px; the canvas is scaled by EXPORT_SCALE for crispness).
  const EXPORT_SCALE = 2;
  const EXPORT_W = 640;
  const EXPORT_PAD = 28;
  const EXPORT_INNER = EXPORT_W - EXPORT_PAD * 2;
  const EXPORT_ROW_H = 58;
  const EXPORT_ROW_GAP = 10;
  const EXPORT_COVER_W = 34;
  const EXPORT_COVER_H = 46;

  function drawExportFlag(ctx: CanvasRenderingContext2D, cx: number, cy: number, size: number) {
    ctx.fillRect(cx - 1, cy - size, 2, size * 2);
    ctx.beginPath();
    ctx.moveTo(cx + 1, cy - size);
    ctx.lineTo(cx + 1 + size * 1.3, cy - size * 0.45);
    ctx.lineTo(cx + 1, cy + size * 0.1);
    ctx.closePath();
    ctx.fill();
  }

  async function exportPng() {
    const roadmap = activeRoadmap.value;
    const payload = activePayload.value;
    if (!roadmap || !payload || payload.steps.length === 0) return;

    const pal = document.documentElement.classList.contains('dark-mode') ? EXPORT_DARK : EXPORT_LIGHT;
    isExporting.value = true;
    try {
      const exportSteps = payload.steps;
      const goal = payload.goal;

      const [stepBitmaps, goalBitmap, logoBitmap] = await Promise.all([
        Promise.all(exportSteps.map((s) => loadExportBitmap(coverUrl(s.coverName)))),
        goal ? loadExportBitmap(coverUrl(goal.coverName)) : Promise.resolve<ImageBitmap | null>(null),
        loadExportBitmap('/favicon-96x96.png'),
        document.fonts.ready,
      ]);

      const headerH = 88;
      const footH = 30;
      const rowsH = exportSteps.length * EXPORT_ROW_H + (exportSteps.length - 1) * EXPORT_ROW_GAP;
      const goalH = goal ? EXPORT_ROW_GAP + EXPORT_ROW_H : 0;
      const H = EXPORT_PAD + headerH + rowsH + goalH + footH + EXPORT_PAD;

      const canvas = document.createElement('canvas');
      canvas.width = EXPORT_W * EXPORT_SCALE;
      canvas.height = Math.ceil(H) * EXPORT_SCALE;
      const ctx = canvas.getContext('2d')!;
      ctx.scale(EXPORT_SCALE, EXPORT_SCALE);
      ctx.textBaseline = 'alphabetic';
      ctx.imageSmoothingQuality = 'high';
      const FONT = '"Noto Sans JP", sans-serif';
      const ls = (v: number) => {
        (ctx as CanvasRenderingContext2D & { letterSpacing: string }).letterSpacing = `${v}px`;
      };

      ctx.fillStyle = pal.bg;
      ctx.fillRect(0, 0, EXPORT_W, H);

      let y = EXPORT_PAD;

      // Header: kicker + plan name + totals (left), logo + brand (right)
      ctx.font = `800 16px ${FONT}`;
      const brand = 'jiten.moe';
      const brandW = ctx.measureText(brand).width;
      const logoSize = 22;
      if (logoBitmap) {
        drawCoverImage(ctx, logoBitmap, EXPORT_W - EXPORT_PAD - brandW - 8 - logoSize, y - 4, logoSize, logoSize, pal.brand);
      }
      ctx.fillStyle = pal.brand;
      ctx.textAlign = 'right';
      ctx.fillText(brand, EXPORT_W - EXPORT_PAD, y + 12);
      ctx.textAlign = 'left';

      ctx.font = `700 12px ${FONT}`;
      ctx.fillStyle = pal.brand;
      ls(1.5);
      ctx.fillText('IMMERSION PLAN', EXPORT_PAD, y + 12);
      ls(0);

      ctx.font = `800 26px ${FONT}`;
      ctx.fillStyle = pal.text;
      ctx.fillText(fitCanvasText(ctx, roadmap.name, EXPORT_INNER), EXPORT_PAD, y + 44);

      const totals = [
        `${exportSteps.length} ${exportSteps.length === 1 ? 'title' : 'titles'}`,
        `${payload.totalNewWords.toLocaleString()} new words`,
      ];
      if (payload.totalGoalNewWords != null) {
        totals.push(`${payload.totalGoalNewWords.toLocaleString()} your goal uses`);
      }
      if (planTotals.value.hours > 0) totals.push(formatHours(planTotals.value.hours));
      ctx.font = `500 14px ${FONT}`;
      ctx.fillStyle = pal.sub;
      ctx.fillText(totals.join('  ·  '), EXPORT_PAD, y + 68);
      y += headerH;

      // Step cards
      exportSteps.forEach((step, i) => {
        ctx.beginPath();
        ctx.roundRect(EXPORT_PAD, y, EXPORT_INNER, EXPORT_ROW_H, 10);
        ctx.fillStyle = pal.card;
        ctx.fill();

        const cx = EXPORT_PAD + 27;
        const cy = y + EXPORT_ROW_H / 2;
        ctx.beginPath();
        ctx.arc(cx, cy, 13, 0, Math.PI * 2);
        ctx.fillStyle = pal.brand;
        ctx.fill();
        ctx.fillStyle = pal.onAccent;
        ctx.font = `800 13px ${FONT}`;
        ctx.textAlign = 'center';
        ctx.fillText(String(step.index), cx, cy + 4.5);
        ctx.textAlign = 'left';

        drawCoverImage(
          ctx, stepBitmaps[i] ?? null,
          EXPORT_PAD + 50, y + (EXPORT_ROW_H - EXPORT_COVER_H) / 2, EXPORT_COVER_W, EXPORT_COVER_H, pal.sub,
        );

        const wordsLabel = `+${step.newWords.toLocaleString()} words`;
        // Named explicitly: a bare "% known" reads as a running total of the user's own knowledge, which it
        // isn't — it is comprehension of this one title, and so moves around as the titles change.
        const knownLabel = `${(step.coverage * 100).toFixed(1)}% of this title`;
        // The one figure that does accumulate. Without it the column above looks like progress going backwards.
        const goalLabel = step.goalCoverageAfter != null
          ? `→ ${(step.goalCoverageAfter * 100).toFixed(1)}% of goal`
          : null;

        ctx.font = `700 15px ${FONT}`;
        let rightW = ctx.measureText(wordsLabel).width;
        ctx.font = `400 11px ${FONT}`;
        rightW = Math.max(rightW, ctx.measureText(knownLabel).width);
        if (goalLabel) {
          ctx.font = `700 11px ${FONT}`;
          rightW = Math.max(rightW, ctx.measureText(goalLabel).width);
        }

        const rightX = EXPORT_W - EXPORT_PAD - 14;
        ctx.textAlign = 'right';
        ctx.font = `700 15px ${FONT}`;
        ctx.fillStyle = pal.words;
        ctx.fillText(wordsLabel, rightX, goalLabel ? y + 21 : y + 26);
        ctx.font = `400 11px ${FONT}`;
        ctx.fillStyle = pal.sub;
        ctx.fillText(knownLabel, rightX, goalLabel ? y + 36 : y + 42);
        if (goalLabel) {
          ctx.font = `700 11px ${FONT}`;
          ctx.fillStyle = pal.goal;
          ctx.fillText(goalLabel, rightX, y + 50);
        }
        ctx.textAlign = 'left';

        const textX = EXPORT_PAD + 96;
        const maxTextW = rightX - rightW - 12 - textX;
        ctx.font = `600 15px ${FONT}`;
        ctx.fillStyle = pal.text;
        ctx.fillText(fitCanvasText(ctx, stepTitle(step), maxTextW), textX, y + 25);

        const subParts = [getMediaTypeText(step.mediaType), `Difficulty ${step.difficulty.toFixed(1)}`];
        const hours = stepHours(step);
        if (hours > 0) subParts.push(formatHours(hours));
        ctx.font = `400 12px ${FONT}`;
        ctx.fillStyle = pal.sub;
        ctx.fillText(fitCanvasText(ctx, subParts.join(' · '), maxTextW), textX, y + 43);

        y += EXPORT_ROW_H + EXPORT_ROW_GAP;
      });

      // Goal card closes the sequence with the amber treatment the page uses.
      if (goal) {
        ctx.beginPath();
        ctx.roundRect(EXPORT_PAD, y, EXPORT_INNER, EXPORT_ROW_H, 10);
        ctx.fillStyle = pal.goalBg;
        ctx.fill();
        ctx.lineWidth = 1.5;
        ctx.strokeStyle = pal.goal;
        ctx.stroke();

        const cx = EXPORT_PAD + 27;
        const cy = y + EXPORT_ROW_H / 2;
        ctx.beginPath();
        ctx.arc(cx, cy, 13, 0, Math.PI * 2);
        ctx.fillStyle = pal.goal;
        ctx.fill();
        ctx.fillStyle = pal.onAccent;
        drawExportFlag(ctx, cx - 3, cy, 7);

        drawCoverImage(
          ctx, goalBitmap,
          EXPORT_PAD + 50, y + (EXPORT_ROW_H - EXPORT_COVER_H) / 2, EXPORT_COVER_W, EXPORT_COVER_H, pal.sub,
        );

        const pctLabel = `${(goal.coverage * 100).toFixed(1)}%`;
        const endLabel = 'by the end';
        ctx.font = `800 17px ${FONT}`;
        let rightW = ctx.measureText(pctLabel).width;
        ctx.font = `400 11px ${FONT}`;
        rightW = Math.max(rightW, ctx.measureText(endLabel).width);

        const rightX = EXPORT_W - EXPORT_PAD - 14;
        ctx.textAlign = 'right';
        ctx.font = `800 17px ${FONT}`;
        ctx.fillStyle = pal.goal;
        ctx.fillText(pctLabel, rightX, y + 27);
        ctx.font = `400 11px ${FONT}`;
        ctx.fillStyle = pal.sub;
        ctx.fillText(endLabel, rightX, y + 42);
        ctx.textAlign = 'left';

        const textX = EXPORT_PAD + 96;
        const maxTextW = rightX - rightW - 12 - textX;
        ctx.font = `600 15px ${FONT}`;
        ctx.fillStyle = pal.text;
        ctx.fillText(fitCanvasText(ctx, goalCardTitle(goal), maxTextW), textX, y + 25);

        ctx.font = `700 11px ${FONT}`;
        ctx.fillStyle = pal.goal;
        ls(1);
        const goalKicker = 'YOUR GOAL';
        ctx.fillText(goalKicker, textX, y + 43);
        const kickerW = ctx.measureText(goalKicker).width;
        ls(0);
        ctx.font = `400 12px ${FONT}`;
        ctx.fillStyle = pal.sub;
        ctx.fillText(fitCanvasText(ctx, ` · Difficulty ${goal.difficulty.toFixed(1)}`, maxTextW - kickerW), textX + kickerW, y + 43);

        y += EXPORT_ROW_H;
      } else {
        y -= EXPORT_ROW_GAP;
      }

      ctx.fillStyle = pal.foot;
      ctx.font = `400 12px ${FONT}`;
      ctx.textAlign = 'center';
      ctx.fillText('Generated on jiten.moe', EXPORT_W / 2, y + 22);
      ctx.textAlign = 'left';

      const slug = roadmap.name.trim().toLowerCase().replace(/\s+/g, '-') || 'plan';
      await saveCanvasPng(canvas, `jiten-plan-${slug}.png`, 'Immersion Plan');
    } catch (e) {
      console.error('Failed to export plan image', e);
      toast.add({ severity: 'error', summary: "Couldn't export the image", detail: 'Something went wrong.', life: 5000 });
    } finally {
      isExporting.value = false;
    }
  }

  // ---- Study decks --------------------------------------------------------

  const planThreshold = computed(() => activeRoadmap.value?.definition.acquisitionThreshold ?? 10);
  const planSteps = computed(() => activePayload.value?.steps ?? []);

  const studyStep = ref<RoadmapStep | null>(null);

  function stepIsStudied(step: RoadmapStep): boolean {
    return isDeckStudied(step.deckId, srsStore.studyDecks);
  }

  function closeStepStudy(open: boolean) {
    if (!open) studyStep.value = null;
  }

  const bulkOpen = ref(false);
  const bulkThreshold = ref(10);
  const bulkDeactivateOthers = ref(false);
  const bulkAddToTop = ref(false);
  const bulkSubmitting = ref(false);
  const bulkSettingsLoading = ref(false);
  const switchingGathering = ref(false);

  const bulkStepsToAdd = computed(() => {
    const ids = new Set(planStepsToAdd(planSteps.value, srsStore.studyDecks));
    return planSteps.value.filter((step) => ids.has(step.deckId));
  });
  const bulkAlreadyCount = computed(() => planSteps.value.length - bulkStepsToAdd.value.length);
  const studyDeckCap = computed(() => limits.value.studyDecks);
  const bulkFitCount = computed(() =>
    Math.max(0, Math.min(bulkStepsToAdd.value.length, studyDeckCap.value - srsStore.studyDecks.length)),
  );
  const bulkOverCap = computed(() => bulkFitCount.value < bulkStepsToAdd.value.length);
  // Anything but TopDeck draws new cards across every deck at once, which throws away the plan's ordering.
  const bulkGatheringWarning = computed(() => srsStore.studySettings.newCardGathering !== 'TopDeck');

  async function openBulkStudy() {
    bulkThreshold.value = planThreshold.value;
    bulkDeactivateOthers.value = false;
    bulkAddToTop.value = false;
    bulkOpen.value = true;
    // Study settings are only read by this dialog, so they load on first open rather than on every page view.
    // fetchSettings is a no-op once loaded, so reopening costs nothing.
    bulkSettingsLoading.value = true;
    try {
      await srsStore.fetchSettings();
    } finally {
      bulkSettingsLoading.value = false;
    }
  }

  async function addAllToStudy() {
    if (bulkStepsToAdd.value.length === 0) return;
    bulkSubmitting.value = true;
    try {
      const result = await srsStore.addStudyDecksBatch(
        buildPlanStudyBatch(planSteps.value, bulkThreshold.value, bulkDeactivateOthers.value, bulkAddToTop.value),
      );
      bulkOpen.value = false;
      toast.add({
        severity: 'success',
        summary: `Added ${result.added.length} ${result.added.length === 1 ? 'deck' : 'decks'}`,
        detail: result.stoppedAtCap
          ? `You're at your limit of ${result.limit} study decks, so the rest were left out.`
          : bulkAddToTop.value
            ? 'They are at the top of your study list, in plan order.'
            : 'They are at the bottom of your study list, in plan order.',
        life: 6000,
      });
    } catch (e) {
      toast.add({ severity: 'error', summary: "Couldn't add them", detail: extractApiError(e, 'Something went wrong.'), life: 6000 });
    } finally {
      bulkSubmitting.value = false;
    }
  }

  async function useTopDeckGathering() {
    switchingGathering.value = true;
    try {
      await srsStore.updateSettings({ ...srsStore.studySettings, newCardGathering: 'TopDeck' });
      toast.add({ severity: 'success', summary: 'New cards now come from the top deck first', life: 4000 });
    } catch (e) {
      toast.add({ severity: 'error', summary: "Couldn't change the setting", detail: extractApiError(e, 'Something went wrong.'), life: 6000 });
    } finally {
      switchingGathering.value = false;
    }
  }

  // The API rejects regenerate/reset-swaps while a run is in flight; disabling matches that.
  const activeBusy = computed(
    () => activeRoadmap.value?.status === 'pending' || activeRoadmap.value?.status === 'generating',
  );

  watch(activeId, () => {
    expandedStep.value = null;
  });

  // Existing lists stay readable after Jiten+ lapses, so the list loads for any authenticated viewer,
  // keyed off auth rather than tier. On a cold hard refresh isAuthenticated is still false at mount, so
  // the watch (immediate) reloads once the store hydrates.
  let loadedList = false;
  watch(
    () => auth.isAuthenticated,
    async (authed) => {
      if (!authed || loadedList) return;
      loadedList = true;
      // The plan list marks steps already in the study list, so study decks are needed to render, unlike
      // the study settings. The client prefetch plugin usually has them already.
      const loads: Promise<unknown>[] = [loadList()];
      if (srsStore.studyDecks.length === 0) loads.push(srsStore.fetchStudyDecks());
      await Promise.all(loads);
    },
    { immediate: true },
  );

  // The builder (difficulty bands) is Plus-only; fetch its defaults once the tier resolves.
  let loadedDefaults = false;
  watch(
    isPlus,
    async (plus) => {
      if (!plus || loadedDefaults) return;
      loadedDefaults = true;
      await loadDefaults();
      runPreview();
    },
    { immediate: true },
  );

  onBeforeUnmount(() => {
    if (pollTimer) clearInterval(pollTimer);
  });
</script>

<template>
  <div class="mx-auto max-w-7xl px-3 py-6 sm:px-4">
    <header class="mb-6">
      <h1 class="flex items-center gap-2 text-2xl font-bold sm:text-3xl">
        <Icon name="material-symbols:explore-rounded" class="shrink-0 text-primary-500" />
        Immersion plans
      </h1>
      <p class="mt-1 max-w-3xl text-sm opacity-80">
        Find what to immerse in next or forge a path towards a title you really care about.
        The algorithm will try to find the ideal picks according to your preferences.
      </p>
    </header>

    <div class="grid grid-cols-1 gap-6 lg:grid-cols-[380px_1fr]">
      <!-- Builder — Jiten+ only. Lapsed viewers see it locked, but keep read/delete on their lists. -->
      <div class="self-start">
        <JitenPlusGate feature="immersion-plan-generate" feature-label="Immersion plans">
        <Card>
          <template #title>
            <div class="flex flex-wrap items-center justify-between gap-2">
              <span class="text-lg">{{ editingId !== null ? 'Edit plan' : 'Make a plan' }}</span>
              <div class="flex flex-wrap gap-2">
                <Button
                  v-if="editingId !== null && builderDirty"
                  label="Discard changes"
                  icon="pi pi-undo"
                  size="small"
                  severity="secondary"
                  outlined
                  :disabled="creating"
                  @click="discardBuilderChanges"
                />
                <Button
                  v-if="editingId !== null"
                  label="New plan"
                  icon="pi pi-plus"
                  size="small"
                  severity="secondary"
                  outlined
                  :disabled="creating"
                  @click="resetBuilder"
                />
              </div>
            </div>
          </template>
          <template #content>
            <div class="flex flex-col gap-5">
              <div>
                <label class="mb-1 block text-sm font-medium" for="roadmap-name">Name</label>
                <InputText id="roadmap-name" v-model="name" class="w-full" placeholder="e.g. Anime fluency plan" />
              </div>

              <div>
                <label class="mb-2 block text-sm font-medium">
                  Goal
                  <Tooltip
                    content="'Next picks' suggests what to read or watch next. 'Target media' builds a path towards a specific title you pick."
                    placement="top"
                  >
                    <i class="pi pi-info-circle ml-1 cursor-help text-xs text-surface-400" />
                  </Tooltip>
                </label>
                <SelectButton
                  v-model="mode"
                  :options="modeOptions"
                  option-label="label"
                  option-value="value"
                  :allow-empty="false"
                  class="w-full"
                />
              </div>

              <div v-if="mode === 'goal'">
                <label class="mb-1 block text-sm font-medium" for="goal-deck">Target media</label>
                <AutoComplete
                  id="goal-deck"
                  v-model="goalDeck"
                  :suggestions="deckSuggestions"
                  :option-label="deckOptionLabel"
                  class="w-full"
                  input-class="w-full"
                  placeholder="Search for a media title"
                  @complete="searchDecks"
                >
                  <template #option="{ option }">
                    <div class="flex items-center gap-3">
                      <img
                        :src="coverUrl(option.coverName)"
                        :alt="localiseTitle(option)"
                        class="h-14 w-10 shrink-0 rounded object-cover"
                      >
                      <div class="min-w-0">
                        <div class="truncate text-sm font-medium">{{ localiseTitle(option) }}</div>
                        <div class="text-xs opacity-70">{{ getMediaTypeText(option.mediaType) }}</div>
                      </div>
                    </div>
                  </template>
                </AutoComplete>
                <p class="mt-1 text-xs opacity-70">
                  You will be suggested titles that build up to the vocabulary of the target media.
                </p>

                <div class="mt-4">
                  <div class="mb-1 flex items-baseline justify-between gap-2">
                    <label class="text-sm font-medium">
                      Comprehension goal
                      <Tooltip
                        content="Set the target coverage you want to achieve by reading all the suggested titles up to the target media."
                        placement="top"
                      >
                        <i class="pi pi-info-circle ml-1 cursor-help text-xs text-surface-400" />
                      </Tooltip>
                    </label>
                    <span class="shrink-0 text-sm font-semibold">{{ goalTarget }}%</span>
                  </div>
                  <div class="px-2 py-2">
                    <Slider v-model="goalTarget" :min="60" :max="99" :step="1" class="w-full" />
                  </div>
                  <p class="text-xs opacity-70">
                    The plan lines up as many titles as it takes to get you to {{ goalTarget }}% coverage of
                    {{ goalTitleText }}.
                    <template v-if="goalCurrentCoverage != null">
                      You're at {{ pct(goalCurrentCoverage) }} of it right now.
                    </template>
                  </p>
                </div>
              </div>

              <div>
                <label class="mb-1 block text-sm font-medium">Media types</label>
                <MultiSelect
                  v-model="mediaTypes"
                  :options="mediaTypeOptions"
                  option-label="name"
                  option-value="id"
                  placeholder="All types"
                  display="chip"
                  class="w-full"
                  filter
                />
              </div>

              <!-- px-2: the slider thumb is centred on the track ends, so a value at either extreme would
                   otherwise be clipped by the card edge. py-2 keeps the thumb clear of the label above. -->
              <div>
                <div class="mb-1 flex items-baseline justify-between gap-2">
                  <label class="text-sm font-medium">
                    Count as known
                    <Tooltip
                      content="'Mature + Young' counts every word you've learned, including ones you're still reviewing, the same as Total coverage on deck pages. 'Only mature' counts just the words you know well, so percentages come out lower."
                      placement="top"
                    >
                      <i class="pi pi-info-circle ml-1 cursor-help text-xs text-surface-400" />
                    </Tooltip>
                  </label>
                </div>
                <SelectButton
                  v-model="countLearningWords"
                  :options="coverageBasisOptions"
                  option-label="label"
                  option-value="value"
                  :allow-empty="false"
                  class="w-full"
                />
              </div>

              <div>
                <div class="mb-1 flex items-baseline justify-between gap-2">
                  <label class="text-sm font-medium">
                    {{ mode === 'goal' ? 'Steps comprehension goal' : 'Comprehension goal' }}
                    <Tooltip
                      :content="mode === 'goal'
                        ? 'Applies to the titles suggested on the way to your goal. The left handle is absolute, nothing below it will be suggested. The right handle is your minimum comfort zone, titles above it will be preferred.'
                        : 'The left handle is absolute, nothing below it will be suggested. The right handle is your minimum comfort zone, titles above it will be preferred.'"
                      placement="top"
                    >
                      <i class="pi pi-info-circle ml-1 cursor-help text-xs text-surface-400" />
                    </Tooltip>
                  </label>
                  <span class="shrink-0 text-sm font-semibold">
                    {{ comprehension[0] }}–{{ comprehension[1] }}%
                  </span>
                </div>
                <div class="px-2 py-2">
                  <Slider v-model="comprehension" range :min="60" :max="98" :step="1" class="w-full" />
                </div>
                <p class="text-xs opacity-70">
                  Nothing below {{ comprehension[0] }}% is suggested. Titles at {{ comprehension[1] }}% or
                  above are preferred, and lower ones are only selected when they will teach you a lot more words.
                </p>

                <!-- Counts come from stored coverage, not the live known-word set generation walks, so they
                     can lag by a recompute. -->
                <Message v-if="previewWarning" severity="warn" size="small" :closable="false" class="mt-2">
                  <span class="text-xs">{{ previewWarning }}</span>
                </Message>
                <p v-else-if="previewSummary" class="mt-2 text-xs opacity-70">
                  <i v-if="previewLoading" class="pi pi-spin pi-spinner mr-1 text-[10px]" />{{ previewSummary }}
                </p>
              </div>

              <div>
                <div class="mb-1 flex items-baseline justify-between gap-2">
                  <label class="text-sm font-medium">
                    Variety
                    <Tooltip
                      content="More to the right to have similar content to your list, more to the left to have completely different content."
                      placement="top"
                    >
                      <i class="pi pi-info-circle ml-1 cursor-help text-xs text-surface-400" />
                    </Tooltip>
                  </label>
                  <span class="shrink-0 text-xs opacity-80">{{ similarityLabel }}</span>
                </div>
                <div class="px-2 py-2">
                  <Slider v-model="contentSimilarity" :min="-3" :max="3" :step="0.5" class="w-full" />
                </div>
              </div>

              <div v-if="showsInScope">
                <div class="mb-1 flex items-baseline justify-between gap-2">
                  <label class="text-sm font-medium">
                    Difficulty — watching
                    <Tooltip
                      content="Only suggest anime, movies, drama and audio within this difficulty range (0–5). Automatically set fit your current level."
                      placement="top"
                    >
                      <i class="pi pi-info-circle ml-1 cursor-help text-xs text-surface-400" />
                    </Tooltip>
                  </label>
                  <span class="shrink-0 text-xs opacity-80">{{ showsDifficulty[0] }} – {{ showsDifficulty[1] }}</span>
                </div>
                <div class="px-2 py-2">
                  <Slider v-model="showsDifficulty" range :min="0" :max="5" :step="0.1" class="w-full" />
                </div>
              </div>

              <div v-if="novelsInScope">
                <div class="mb-1 flex items-baseline justify-between gap-2">
                  <label class="text-sm font-medium">
                    Difficulty — reading
                    <Tooltip
                      content="Only suggest novels, visual novels, manga and games within this difficulty range (0–5). Automatically set to fit your current level."
                      placement="top"
                    >
                      <i class="pi pi-info-circle ml-1 cursor-help text-xs text-surface-400" />
                    </Tooltip>
                  </label>
                  <span class="shrink-0 text-xs opacity-80">{{ novelsDifficulty[0] }} – {{ novelsDifficulty[1] }}</span>
                </div>
                <div class="px-2 py-2">
                  <Slider v-model="novelsDifficulty" range :min="0" :max="5" :step="0.1" class="w-full" />
                </div>
              </div>

              <div class="grid grid-cols-2 gap-3">
                <div v-if="mode === 'goal'" class="min-w-0">
                  <label class="mb-1 block text-sm font-medium" for="goalSteps">
                    Max titles
                    <Tooltip
                      content="How many titles the route may use at most. It stops early once you reach your target; if the budget runs out first, you'll be told how close it got."
                      placement="top"
                    >
                      <i class="pi pi-info-circle ml-1 cursor-help text-xs text-surface-400" />
                    </Tooltip>
                  </label>
                  <InputNumber id="goalSteps" v-model="goalSteps" :min="1" :max="30" show-buttons fluid class="w-full" />
                </div>
                <div v-else class="min-w-0">
                  <label class="mb-1 block text-sm font-medium" for="steps">Suggestion count</label>
                  <InputNumber id="steps" v-model="steps" :min="1" :max="15" show-buttons fluid class="w-full" />
                </div>
                <div class="min-w-0">
                  <label class="mb-1 block text-sm font-medium" for="threshold">
                    Min. occurrences
                    <Tooltip
                      content="How many times a word must appear in a title to consider it as known. Raise it for a more cautious estimate."
                      placement="top"
                    >
                      <i class="pi pi-info-circle ml-1 cursor-help text-xs text-surface-400" />
                    </Tooltip>
                  </label>
                  <InputNumber
                    id="threshold"
                    v-model="acquisitionThreshold"
                    :min="1"
                    :max="50"
                    show-buttons
                    fluid
                    class="w-full"
                  />
                </div>
              </div>

              <div>
                <label class="mb-2 block text-sm font-medium">
                  Learn the most words
                  <Tooltip
                    content="'By time spent' favours shorter titles with the most new words per hour. 'Per title' favours longer titles that teach the most in total."
                    placement="top"
                  >
                    <i class="pi pi-info-circle ml-1 cursor-help text-xs text-surface-400" />
                  </Tooltip>
                </label>
                <SelectButton
                  v-model="preference"
                  :options="preferenceOptions"
                  option-label="label"
                  option-value="value"
                  :allow-empty="false"
                  class="w-full"
                />
              </div>

              <div>
                <label class="mb-2 block text-sm font-medium">
                  Pick titles from
                  <Tooltip
                    content="'My list & similar' picks from titles you marked Planning, plus titles similar to the ones you marked Ongoing or Completed. 'Everything' searches the whole catalogue. Either way, Ongoing, Completed, Dropped and ignored titles are never suggested."
                    placement="top"
                  >
                    <i class="pi pi-info-circle ml-1 cursor-help text-xs text-surface-400" />
                  </Tooltip>
                </label>
                <SelectButton
                  v-model="candidateMode"
                  :options="candidateModeOptions"
                  option-label="label"
                  option-value="value"
                  :allow-empty="false"
                  class="w-full"
                />
              </div>

              <div class="flex flex-col gap-2">
                <div class="flex items-center gap-2">
                  <Checkbox v-model="includeAdultOnly" input-id="adult" binary />
                  <label class="text-sm" for="adult">Include adult-only titles</label>
                </div>
                <div v-if="includeAdultOnly" class="ml-6 flex items-center gap-2">
                  <Checkbox v-model="adultOnlyExclusive" input-id="adult-only" binary />
                  <label class="text-sm" for="adult-only">Adult-only titles only</label>
                </div>
              </div>

              <Message v-if="atCap && editingId === null" severity="warn" size="small" :closable="false">
                <span class="text-xs">
                  You've reached {{ maxRoadmaps }} plans. Delete one to make room for a new one.
                </span>
              </Message>

              <Button
                v-if="editingId !== null"
                :label="creating ? 'Saving…' : 'Save & regenerate'"
                icon="pi pi-save"
                :loading="creating"
                :disabled="!canSubmit || creating"
                class="w-full"
                @click="saveAndRegenerate"
              />
              <Button
                v-else
                :label="creating ? 'Working…' : 'Make my plan'"
                icon="pi pi-compass"
                :loading="creating"
                :disabled="!canSubmit || creating || atCap"
                class="w-full"
                @click="create"
              />
            </div>
          </template>
        </Card>
        </JitenPlusGate>
      </div>

      <!-- Results — always visible to the owner, even after Jiten+ lapses -->
      <div class="flex flex-col gap-4">
          <Card v-if="roadmaps.length > 0">
            <template #content>
              <div class="mb-3 flex items-center justify-between gap-2">
                <span class="text-sm font-medium">Your plans</span>
                <span class="text-xs opacity-70">{{ roadmaps.length }} / {{ maxRoadmaps }}</span>
              </div>
              <div class="flex flex-wrap items-center gap-2">
                <Button
v-for="r in roadmaps" :key="r.id" :label="r.name"
                        :severity="r.id === activeId ? 'primary' : 'secondary'"
                        :outlined="r.id !== activeId" size="small" @click="selectPlan(r)">
                  <template #icon>
                    <i v-if="r.status === 'generating' || r.status === 'pending'" class="pi pi-spin pi-spinner mr-2" />
                    <i v-else-if="r.status === 'failed'" class="pi pi-exclamation-circle mr-2" />
                    <i v-else class="pi pi-compass mr-2" />
                  </template>
                </Button>
              </div>
            </template>
          </Card>

          <Card v-if="activeRoadmap">
            <template #content>
              <div class="mb-4 flex flex-wrap items-center justify-between gap-3">
                <div>
                  <h2 class="text-xl font-semibold">{{ activeRoadmap.name }}</h2>
                  <p class="text-sm opacity-70">
                    <span v-if="activeRoadmap.mode === 'goal'">Path towards your target media</span>
                    <span v-else>Your next picks</span>
                    <span v-if="activeRoadmap.candidateCount > 0">
                      · picked from {{ activeRoadmap.candidateCount.toLocaleString() }} titles</span>
                    <span v-if="activeRoadmap.swappedCount > 0"> · {{ activeRoadmap.swappedCount }} skipped</span>
                  </p>
                </div>
                <div class="flex flex-wrap gap-2">
                  <Button
                    v-if="activeRoadmap.status === 'ready' && planSteps.length > 0"
                    label="Add all to study"
                    icon="pi pi-plus"
                    size="small"
                    severity="secondary"
                    outlined
                    @click="openBulkStudy"
                  />
                  <Button
                    v-if="activeRoadmap.status === 'ready'"
                    label="Export as image"
                    icon="pi pi-download"
                    size="small"
                    severity="secondary"
                    outlined
                    :loading="isExporting"
                    :disabled="!activePayload || activePayload.steps.length === 0"
                    @click="exportPng"
                  />
                  <Button
                    v-if="isPlus && activeRoadmap.swappedCount > 0"
                    label="Undo skips"
                    icon="pi pi-undo"
                    size="small"
                    severity="secondary"
                    outlined
                    :disabled="activeBusy"
                    @click="resetSwaps"
                  />
                  <Button
                    v-if="isPlus"
                    :label="editingId === activeRoadmap.id && builderDirty ? 'Save & start over' : 'Start over'"
                    icon="pi pi-refresh"
                    size="small"
                    severity="secondary"
                    outlined
                    :disabled="activeBusy || creating"
                    @click="startOver(activeRoadmap.id)"
                  />
                  <Button
                    icon="pi pi-trash"
                    size="small"
                    severity="danger"
                    outlined
                    @click="confirmDelete(activeRoadmap)"
                  />
                </div>
              </div>

              <Message v-if="!isPlus" severity="secondary" size="small" :closable="false" class="mb-4">
                <span class="text-xs">
                  Your Jiten+ has lapsed. You can still read and delete your plans, but making new ones or
                  changing them needs an active Jiten+.
                </span>
              </Message>

              <div
                v-if="activeRoadmap.status === 'pending' || activeRoadmap.status === 'generating'"
                class="flex flex-col items-center gap-3 py-12"
              >
                <ProgressSpinner style="width: 48px; height: 48px" />
                <p class="text-sm opacity-80">Picking your titles…</p>
              </div>

              <Message v-else-if="activeRoadmap.status === 'failed'" severity="error" :closable="false">
                {{ activeRoadmap.failureReason ?? "We couldn't put this plan together." }}
              </Message>

              <div v-else-if="loadingDetail" class="flex justify-center py-12">
                <ProgressSpinner style="width: 40px; height: 40px" />
              </div>

              <div v-else-if="activePayload" class="flex flex-col gap-4">
                <Message
                  v-if="activeRoadmap.mode === 'goal' && activePayload.goalReached"
                  severity="success"
                  :closable="false"
                >
                  <span v-if="activePayload.steps.length === 0">
                    You can already follow this — you know {{ pct(activePayload.goalCoverageFinal ?? 0) }} of
                    the words. Go for it.
                  </span>
                  <span v-else>
                    Go through these {{ activePayload.steps.length }} first and you'll know
                    {{ pct(activePayload.goalCoverageFinal ?? 0) }} of the words in the title you picked.
                  </span>
                </Message>

                <Message
                  v-else-if="activeRoadmap.mode === 'goal' && activePayload.goalCeilingReached"
                  severity="success"
                  :closable="false"
                >
                  As far as other titles can take you — you'll know
                  {{ pct(activePayload.goalCoverageFinal ?? 0) }} of
                  {{ activePayload.goal ? goalCardTitle(activePayload.goal) : 'the title' }}. The last
                  {{ activePayload.goalUnreachableWords.toLocaleString() }} words appear only in it, so no other
                  title can teach them — you'll pick them up by reading it.
                </Message>

                <Message
                  v-else-if="activeRoadmap.mode === 'goal' && !activePayload.goalReached"
                  severity="warn"
                  :closable="false"
                >
                  These get you to {{ pct(activePayload.goalCoverageFinal ?? 0) }} of the words in the title you
                  picked, about {{ (activePayload.goalWordsRemaining ?? 0).toLocaleString() }} short of your
                  target, using all {{ activePayload.steps.length }} of the titles it was allowed. Raise the title
                  limit to let it go further. This can also happen because the frequent words of this title are
                  very specific, in which case lowering the target percentage helps more.
                </Message>

                <!-- Plan summary -->
                <div
                  v-if="activePayload.steps.length > 0"
                  class="rounded-lg bg-surface-50 px-4 py-3 dark:bg-surface-800/50"
                >
                  <div class="grid grid-cols-2 gap-x-4 gap-y-3 sm:grid-cols-4">
                    <div>
                      <div class="text-xs uppercase tracking-wide text-gray-600 dark:text-gray-300">Titles</div>
                      <div class="text-lg font-semibold tabular-nums">{{ activePayload.steps.length }}</div>
                    </div>
                    <div>
                      <div class="text-xs uppercase tracking-wide text-gray-600 dark:text-gray-300">
                        New words
                        <Tooltip
                          v-if="activePayload.totalGoalNewWords != null"
                          content="Everything these titles teach, whether or not your goal uses it. The amber figure is the part that counts toward the goal."
                          placement="top"
                        >
                          <i class="pi pi-info-circle ml-0.5 cursor-help text-xs text-primary-400" />
                        </Tooltip>
                      </div>
                      <div class="text-lg font-semibold tabular-nums text-green-600 dark:text-green-400">
                        {{ activePayload.totalNewWords.toLocaleString() }}
                      </div>
                      <div
                        v-if="activePayload.totalGoalNewWords != null"
                        class="text-sm font-semibold tabular-nums text-amber-600 dark:text-amber-400"
                      >
                        {{ activePayload.totalGoalNewWords.toLocaleString() }} your goal uses
                      </div>
                    </div>
                    <div v-if="planTotals.characters > 0">
                      <div class="text-xs uppercase tracking-wide text-gray-600 dark:text-gray-300">Characters</div>
                      <div class="text-lg font-semibold tabular-nums">
                        {{ planTotals.characters.toLocaleString() }}
                      </div>
                    </div>
                    <div v-if="planTotals.hours > 0">
                      <div class="text-xs uppercase tracking-wide text-gray-600 dark:text-gray-300">
                        Time
                        <Tooltip
                          :content="`Anime, film, drama and audio are timed by their speech duration; everything else by your reading speed of ${store.readingSpeed.toLocaleString()} characters per hour, which you can change in the quick settings cog at the top right.`"
                          placement="top"
                        >
                          <i class="pi pi-info-circle ml-0.5 cursor-help text-xs text-primary-400" />
                        </Tooltip>
                      </div>
                      <div class="text-lg font-semibold tabular-nums">{{ formatHours(planTotals.hours) }}</div>
                    </div>
                  </div>
                  <p class="mt-2 text-sm opacity-80">Get through all of these and that's what you'll have covered.</p>
                  <p v-if="activePayload.goalWordsAtStart" class="mt-1 text-sm opacity-80">
                    Studied on their own, the
                    <strong class="tabular-nums">{{ activePayload.goalWordsAtStart.toLocaleString() }}</strong>
                    most-used words you're still missing from {{ goalTitleText }} would get you there too. No real
                    title teaches only those, so a reading route always costs more words than the bare minimum.
                  </p>
                </div>

                <!-- Steps -->
                <div
                  v-for="step in activePayload.steps"
                  :key="step.index"
                  class="flex flex-col gap-3 rounded-lg border border-surface-200 p-3 sm:flex-row dark:border-surface-700"
                >
                  <div class="flex shrink-0 items-start gap-3">
                    <span
                      class="flex h-8 w-8 items-center justify-center rounded-full bg-primary text-sm font-bold text-primary-contrast"
                    >
                      {{ step.index }}
                    </span>
                    <div class="flex shrink-0 flex-col items-center gap-1">
                      <Tag
                        :value="getMediaTypeText(step.mediaType)"
                        severity="secondary"
                        rounded
                        class="!px-2 !py-0.5 !text-[0.65rem] !font-semibold !uppercase !tracking-wider"
                      />
                      <a :href="`/decks/media/${step.deckId}/detail`" target="_blank" rel="noopener">
                        <img
                          :src="coverUrl(step.coverName)"
                          :alt="stepTitle(step)"
                          class="h-28 w-20 rounded object-cover"
                          loading="lazy"
                        >
                      </a>
                    </div>
                  </div>

                  <div class="min-w-0 flex-1">
                    <div class="flex flex-wrap items-start justify-between gap-2">
                      <a
                        :href="`/decks/media/${step.deckId}/detail`"
                        target="_blank"
                        rel="noopener"
                        :title="stepTitle(step)"
                        class="line-clamp-1 min-w-0 flex-1 font-semibold break-words hover:underline"
                      >
                        {{ stepTitle(step) }}
                      </a>
                      <div class="flex shrink-0 items-center gap-1">
                        <span
                          v-if="stepIsStudied(step)"
                          class="inline-flex items-center gap-1 text-xs font-medium text-green-700 dark:text-green-400"
                        >
                          <i class="pi pi-check-circle text-xs" />
                          In your study list
                        </span>
                        <Tooltip v-else content="Make a study deck from this title">
                          <Button
                            label="Study"
                            icon="pi pi-plus"
                            size="small"
                            severity="secondary"
                            outlined
                            @click="studyStep = step"
                          />
                        </Tooltip>
                        <Tooltip v-if="isPlus" content="Not this one — show me something else" placement="left">
                          <Button
                            icon="pi pi-refresh"
                            size="small"
                            severity="secondary"
                            text
                            :loading="swapping === step.index"
                            :disabled="swapping !== null"
                            @click="swapStep(step)"
                          />
                        </Tooltip>
                      </div>
                    </div>

                    <!-- Genres (reuses the deck card's GenreTagDisplay) -->
                    <GenreTagDisplay v-if="step.genres.length > 0" :genres="step.genres" label="Genres" class="mt-1.5" />

                    <!-- Difficulty — matches the deck card's stat row -->
                    <div class="stat-row mt-1 flex items-center justify-between">
                      <span class="pr-2 font-normal text-gray-600 dark:text-gray-300">Difficulty</span>
                      <DifficultyDisplay :difficulty="step.difficulty" />
                    </div>

                    <div v-if="stepHours(step) > 0" class="stat-row flex items-center justify-between">
                      <span class="pr-2 font-normal text-gray-600 dark:text-gray-300">
                        Length
                        <Tooltip
                          :content="step.speechDuration > 0
                            ? 'Total duration of speech, excluding silence.'
                            : `At your reading speed of ${store.readingSpeed.toLocaleString()} characters per hour.`"
                          placement="top"
                        >
                          <i class="pi pi-info-circle ml-0.5 cursor-help text-xs text-primary-400" />
                        </Tooltip>
                      </span>
                      <span class="tabular-nums font-semibold">
                        {{ formatHours(stepHours(step)) }}
                        <span v-if="step.characterCount > 0" class="ml-1 text-xs font-normal opacity-60">
                          {{ step.characterCount.toLocaleString() }} chars
                        </span>
                      </span>
                    </div>

                    <div class="stat-row flex items-center justify-between">
                      <span class="pr-2 font-normal text-gray-600 dark:text-gray-300">
                        You know of this title
                        <Tooltip
                          content="How much of this title's text you will be able to read when you reach it if you follow the exact steps."
                          placement="top"
                        >
                          <i class="pi pi-info-circle ml-0.5 cursor-help text-xs text-primary-400" />
                        </Tooltip>
                      </span>
                      <span class="tabular-nums font-semibold">{{ pct(step.coverage) }} of the words</span>
                    </div>

                    <div class="stat-row flex items-center justify-between">
                      <span class="pr-2 font-normal text-gray-600 dark:text-gray-300">
                        Teaches
                        <Tooltip
                          content="New words that appear often enough here to actually stick, assuming you've read the steps above first."
                          placement="top"
                        >
                          <i class="pi pi-info-circle ml-0.5 cursor-help text-xs text-primary-400" />
                        </Tooltip>
                      </span>
                      <span class="tabular-nums font-semibold text-green-600 dark:text-green-400">
                        {{ step.newWords.toLocaleString() }} new words
                        <span v-if="step.goalNewWords != null" class="ml-1 text-xs font-normal opacity-70">
                          ({{ step.goalNewWords.toLocaleString() }} your goal uses)
                        </span>
                      </span>
                    </div>

                    <div v-if="step.goalCoverageAfter != null" class="stat-row flex items-center justify-between">
                      <span class="pr-2 font-normal text-gray-600 dark:text-gray-300">
                        Then, of your goal
                        <Tooltip
                          content="Your coverage of the title you're aiming for, after this step. This one only ever goes up."
                          placement="top"
                        >
                          <i class="pi pi-info-circle ml-0.5 cursor-help text-xs text-primary-400" />
                        </Tooltip>
                      </span>
                      <span class="tabular-nums font-semibold text-amber-600 dark:text-amber-400">
                        {{ pct(step.goalCoverageAfter) }}
                      </span>
                    </div>

                    <!-- Frequency mix: how useful the new words are -->
                    <div v-if="bandTotal(step.frequencyBands) > 0" class="mt-2">
                      <div class="mb-1 flex items-center justify-between text-xs">
                        <span class="text-gray-600 dark:text-gray-300">
                          {{ Math.round(usefulShare(step.frequencyBands) * 100) }}% are common, everyday words
                        </span>
                        <button
                          type="button"
                          class="font-semibold text-primary-500 hover:text-primary-700 hover:underline"
                          @click="toggleWords(step.index)"
                        >
                          {{ expandedStep === step.index ? 'Hide words' : 'See all words' }}
                        </button>
                      </div>
                      <div class="flex h-2 overflow-hidden rounded-full">
                        <Tooltip
                          v-for="b in bandDefs"
                          :key="b.key"
                          :content="`${b.label}: ${step.frequencyBands[b.key].toLocaleString()} words`"
                          placement="top"
                        >
                          <div
                            v-if="step.frequencyBands[b.key] > 0"
                            :class="b.cls"
                            :style="{ width: `${(step.frequencyBands[b.key] / bandTotal(step.frequencyBands)) * 100}%` }"
                            class="h-full"
                          />
                        </Tooltip>
                      </div>
                    </div>

                    <!-- Expandable word list — resolved lazily on first expand -->
                    <div v-if="expandedStep === step.index" class="mt-3">
                      <div
                        v-if="stepWordsStatus(step.index) === 'loading'"
                        class="flex items-center gap-2 py-3 text-sm opacity-70"
                      >
                        <i class="pi pi-spin pi-spinner" /> Loading words…
                      </div>
                      <div
                        v-else-if="stepWordsStatus(step.index) === 'error'"
                        class="py-3 text-sm text-red-500 dark:text-red-400"
                      >
                        Couldn't load the words.
                        <button
                          type="button"
                          class="font-semibold underline"
                          @click="loadStepWords(step.index)"
                        >
                          Try again
                        </button>
                      </div>
                      <template v-else-if="stepWordsStatus(step.index) === 'ready'">
                        <div class="max-h-64 overflow-y-auto rounded border border-surface-200 dark:border-surface-700">
                          <a
                            v-for="w in stepWords(step.index)"
                            :key="`${w.wordId}-${w.readingIndex}`"
                            :href="`/vocabulary/${w.wordId}/${w.readingIndex}`"
                            target="_blank"
                            rel="noopener"
                            class="flex items-baseline justify-between gap-3 border-b border-surface-100 px-3 py-1.5 text-sm last:border-0 hover:bg-surface-50 dark:border-surface-800 dark:hover:bg-surface-800/50"
                          >
                            <span>
                              {{ w.text }}
                              <span v-if="w.reading && w.reading !== w.text" class="ml-1 text-xs opacity-60">
                                {{ w.reading }}
                              </span>
                            </span>
                            <span class="shrink-0 text-xs opacity-50">
                              {{ w.frequencyRank > 0 ? `#${w.frequencyRank.toLocaleString()}` : 'rare' }}
                            </span>
                          </a>
                        </div>
                        <p v-if="step.newWords > stepWords(step.index).length" class="mt-1 text-xs opacity-60">
                          Showing the {{ stepWords(step.index).length.toLocaleString() }} most common of
                          {{ step.newWords.toLocaleString() }}.
                          <a
                            :href="`/decks/media/${step.deckId}/vocabulary?display=unknown&sortBy=deckFreq`"
                            target="_blank"
                            rel="noopener"
                            class="font-medium hover:underline"
                          >See them all</a>.
                        </p>
                      </template>
                    </div>
                  </div>
                </div>

                <!-- Drill step -->
                <div
v-if="activePayload.drill"
                     class="rounded-lg border border-dashed border-surface-300 p-4 dark:border-surface-600">
                  <h3 class="font-semibold">
                    <span v-if="activePayload.steps.length === 0">Start here</span>
                    <span v-else>After that</span>
                  </h3>
                  <p class="mt-1 text-sm opacity-80">
                    Nothing else is easy enough for you yet. Learn
                    <strong>{{ activePayload.drill.wordsNeeded.toLocaleString() }}</strong> more words and
                    <a
                      :href="`/decks/media/${activePayload.drill.deckId}/detail`"
                      target="_blank"
                      rel="noopener"
                      class="font-medium hover:underline"
                    >
                      {{ activePayload.drill.title }}
                    </a>
                    opens up — you know {{ pct(activePayload.drill.coverage) }} of it right now.
                  </p>
                  <div class="mt-2 flex flex-wrap gap-1">
                    <a
                      v-for="w in activePayload.drill.words"
                      :key="`${w.wordId}-${w.readingIndex}`"
                      :href="`/vocabulary/${w.wordId}/${w.readingIndex}`"
                      target="_blank"
                      rel="noopener"
                      class="rounded bg-surface-100 px-2 py-0.5 text-sm hover:underline dark:bg-surface-800"
                    >
                      {{ w.text }}
                    </a>
                  </div>
                </div>

                <!-- Goal destination — the target title itself, closing the sequence -->
                <div
                  v-if="activePayload.goal"
                  class="flex flex-col gap-3 rounded-lg border-2 border-amber-400 bg-amber-50/60 p-3 sm:flex-row dark:border-amber-500/60 dark:bg-amber-500/10"
                >
                  <div class="flex shrink-0 items-start gap-3">
                    <span
                      class="flex h-8 w-8 items-center justify-center rounded-full bg-amber-400 text-white dark:bg-amber-500"
                    >
                      <i class="pi pi-flag-fill text-sm" />
                    </span>
                    <div class="flex shrink-0 flex-col items-center gap-1">
                      <Tag
                        value="Goal"
                        rounded
                        class="!bg-amber-400 !px-2 !py-0.5 !text-[0.65rem] !font-semibold !uppercase !tracking-wider !text-white dark:!bg-amber-500"
                      />
                      <a :href="`/decks/media/${activePayload.goal.deckId}/detail`" target="_blank" rel="noopener">
                        <img
                          :src="coverUrl(activePayload.goal.coverName)"
                          :alt="goalCardTitle(activePayload.goal)"
                          class="h-28 w-20 rounded object-cover"
                          loading="lazy"
                        >
                      </a>
                    </div>
                  </div>

                  <div class="min-w-0 flex-1">
                    <div class="flex flex-wrap items-start justify-between gap-2">
                      <a
                        :href="`/decks/media/${activePayload.goal.deckId}/detail`"
                        target="_blank"
                        rel="noopener"
                        :title="goalCardTitle(activePayload.goal)"
                        class="line-clamp-1 min-w-0 flex-1 font-semibold break-words hover:underline"
                      >
                        {{ goalCardTitle(activePayload.goal) }}
                      </a>
                      <span class="text-xs font-semibold uppercase tracking-wider text-amber-600 dark:text-amber-400">
                        Your goal
                      </span>
                    </div>

                    <div class="stat-row mt-1 flex items-center justify-between">
                      <span class="pr-2 font-normal text-gray-600 dark:text-gray-300">Difficulty</span>
                      <DifficultyDisplay :difficulty="activePayload.goal.difficulty" />
                    </div>

                    <div class="stat-row flex items-center justify-between">
                      <span class="pr-2 font-normal text-gray-600 dark:text-gray-300">
                        {{ activePayload.goal.reached ? "By the end you'll understand" : "By the end you'll reach" }}
                      </span>
                      <span
                        class="tabular-nums font-semibold"
                        :class="activePayload.goal.reached || activePayload.goalCeilingReached
                          ? 'text-green-600 dark:text-green-400'
                          : 'text-amber-600 dark:text-amber-400'"
                      >
                        {{ pct(activePayload.goal.coverage) }} of the words
                      </span>
                    </div>

                    <p
                      v-if="activePayload.goal.reached"
                      class="mt-2 inline-flex items-center gap-1.5 text-sm font-medium text-green-600 dark:text-green-400"
                    >
                      <i class="pi pi-check-circle" />
                      Target reached — this is what the plan builds you up to.
                    </p>
                    <p
                      v-else-if="activePayload.goalCeilingReached"
                      class="mt-2 inline-flex items-start gap-1.5 text-sm font-medium text-green-600 dark:text-green-400"
                    >
                      <i class="pi pi-check-circle mt-0.5" />
                      <span>
                        As far as other titles reach — the last
                        {{ activePayload.goalUnreachableWords.toLocaleString() }} words appear only here, so you'll
                        pick them up by reading it.
                      </span>
                    </p>
                    <p v-else class="mt-2 text-sm text-gray-600 dark:text-gray-300">
                      Still about
                      <strong>{{ activePayload.goal.wordsRemaining.toLocaleString() }}</strong> words short of your
                      target.
                    </p>
                  </div>
                </div>

                <Message
                  v-if="activeRoadmap.mode !== 'goal' && activePayload.steps.length === 0 && !activePayload.drill"
                  severity="info"
                  :closable="false"
                >
                  Nothing matched. Try adding media types, widening the difficulty range, or lowering how much
                  you want to understand.
                </Message>
              </div>
            </template>
          </Card>

          <Card v-else-if="!loadingList">
            <template #content>
              <div class="py-10 text-center">
                <i class="pi pi-compass mb-3 text-4xl opacity-40" />
                <p class="opacity-80">Make your first plan to see what you should pick up next.</p>
              </div>
            </template>
          </Card>
      </div>
    </div>

    <SrsAddDeckDialog
      v-if="studyStep"
      :key="studyStep.deckId"
      :visible="true"
      :preselected-deck="{ deckId: studyStep.deckId, originalTitle: stepTitle(studyStep), coverName: studyStep.coverName }"
      initial-filter-mode="occurrence"
      :initial-min-occurrences="planThreshold"
      @update:visible="closeStepStudy"
    />

    <Dialog
      v-model:visible="bulkOpen"
      modal
      header="Add this plan to your study list"
      :style="{ width: '520px', maxWidth: '95vw' }"
      :pt="{ content: { class: 'p-4' } }"
    >
      <div class="flex flex-col gap-4">
        <p class="text-sm">
          <span v-if="bulkStepsToAdd.length > 0">
            This will add {{ bulkStepsToAdd.length }} {{ bulkStepsToAdd.length === 1 ? 'title' : 'titles' }} to the
            bottom of your study list in the same order as the plan.
          </span>
          <span v-else>Every title in this plan is already in your study list.</span>
          <span v-if="bulkAlreadyCount > 0" class="opacity-80">
            {{ bulkAlreadyCount }} of {{ planSteps.length }} {{ bulkAlreadyCount === 1 ? 'is' : 'are' }} already present
            and will be left alone.
          </span>
        </p>

        <ul
          v-if="bulkStepsToAdd.length > 0"
          class="max-h-44 overflow-y-auto rounded-lg border border-surface-200 dark:border-surface-700"
        >
          <li
            v-for="(planStep, i) in bulkStepsToAdd"
            :key="planStep.deckId"
            class="flex items-center gap-2 border-b border-surface-200 px-3 py-1.5 text-sm last:border-b-0 dark:border-surface-700"
          >
            <span class="w-6 shrink-0 text-right tabular-nums opacity-60">{{ i + 1 }}</span>
            <span class="min-w-0 truncate">{{ stepTitle(planStep) }}</span>
          </li>
        </ul>

        <div>
          <label for="bulk-threshold" class="mb-1 block text-sm font-medium">
            Only include words used at least this many times
          </label>
          <InputNumber
            v-model="bulkThreshold"
            input-id="bulk-threshold"
            :min="1"
            :use-grouping="false"
            class="w-full"
          />
        </div>

        <Message v-if="bulkOverCap" severity="warn" size="small" :closable="false">
          <span class="text-xs">
            You can only have {{ studyDeckCap }} study decks. Only the first {{ bulkFitCount }} of these will be added.
          </span>
        </Message>

        <div class="flex items-center gap-2">
          <Checkbox v-model="bulkAddToTop" input-id="bulk-add-top" binary />
          <label class="cursor-pointer text-sm" for="bulk-add-top">Put them at the top of my study list</label>
        </div>

        <div class="flex items-center gap-2">
          <Checkbox v-model="bulkDeactivateOthers" input-id="bulk-deactivate" binary />
          <label class="cursor-pointer text-sm" for="bulk-deactivate">Turn off my other study decks</label>
        </div>

        <Message v-if="bulkGatheringWarning" severity="warn" :closable="false">
          <div class="flex flex-col items-start gap-2">
            <span class="text-xs">
              New cards are currently drawn from all your decks at once, so the plan's order won't decide what you
              see first.
            </span>
            <Button
              label="Take new cards from the top deck"
              size="small"
              severity="secondary"
              outlined
              :loading="switchingGathering"
              @click="useTopDeckGathering"
            />
          </div>
        </Message>
      </div>

      <template #footer>
        <Button label="Cancel" severity="secondary" text :disabled="bulkSubmitting" @click="bulkOpen = false" />
        <Button
          :label="`Add ${bulkFitCount} ${bulkFitCount === 1 ? 'deck' : 'decks'}`"
          :loading="bulkSubmitting || bulkSettingsLoading"
          :disabled="bulkFitCount === 0 || bulkSettingsLoading"
          @click="addAllToStudy"
        />
      </template>
    </Dialog>
  </div>
</template>

<style scoped>
  /* Matches MediaDeckCard's stat rows so the step metadata reads as the same component family. */
  .stat-row {
    padding: 0.2rem;
    border-radius: var(--radius-sm);
    transition: background-color 0.2s;
  }

  .stat-row:hover {
    background-color: rgba(183, 135, 243, 0.21);
  }

  :deep(.dark) .stat-row:hover {
    background-color: rgba(255, 255, 255, 0.05);
  }

  /* InputNumber's inner <input> carries an intrinsic size that survives `fluid`, which pushes the
     spinner variant past its grid column inside the narrow builder card. */
  :deep(.p-inputnumber) {
    width: 100%;
    min-width: 0;
  }

  :deep(.p-inputnumber-input) {
    width: 100%;
    min-width: 0;
  }

  /* SelectButton sizes each option to its label, so the three toggle rows would otherwise each end at a
     different width and break the column's alignment against the full-width inputs above them. */
  :deep(.p-selectbutton) {
    display: flex;
    width: 100%;
  }

  :deep(.p-selectbutton > .p-togglebutton) {
    flex: 1 1 0;
    justify-content: center;
  }
</style>
