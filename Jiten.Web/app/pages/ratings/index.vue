<script setup lang="ts">
  import { useConfirm } from 'primevue/useconfirm';
  import type { ComparisonSuggestionDto, DeckSummaryDto, DifficultyVoteDto, VotingStatsDto } from '~/types/types';
  import { TitleLanguage, areComparable } from '~/types';
  import type { AutoCompleteCompleteEvent } from 'primevue/autocomplete';

  definePageMeta({
    middleware: ['auth'],
  });

  useHead({ title: 'Compare Difficulty - Jiten' });

  const confirm = useConfirm();
  const { fetchSuggestions, fetchStats, fetchMySkipped, deleteSkip, fetchUnratedDecks, fetchCompletedDecks, deleteRating } = useDifficultyVotes();
  const jitenStore = useJitenStore();

  const suggestions = ref<ComparisonSuggestionDto[]>([]);
  const currentIndex = ref(0);
  const stats = ref<VotingStatsDto | null>(null);
  const isLoading = ref(true);
  const voteTimestamps = reactive<number[]>([]);
  const sessionVotedKeys = new Set<string>();
  const sessionSkippedKeys = new Set<string>();
  let nextBatchPromise: Promise<ComparisonSuggestionDto[]> | null = null;

  function pairKey(a: number, b: number): string {
    return `${Math.min(a, b)}-${Math.max(a, b)}`;
  }

  function isSeenPair(key: string): boolean {
    return sessionVotedKeys.has(key) || sessionSkippedKeys.has(key);
  }

  function filterSeen(pairs: ComparisonSuggestionDto[]): ComparisonSuggestionDto[] {
    return pairs.filter((p) => !isSeenPair(pairKey(p.deckA.id, p.deckB.id)));
  }

  const currentPair = computed(() => {
    if (currentIndex.value < suggestions.value.length) {
      return suggestions.value[currentIndex.value];
    }
    return null;
  });

  const hasMore = computed(() => currentIndex.value < suggestions.value.length);

  async function loadSuggestions(showSpinner = true) {
    if (showSpinner) isLoading.value = true;
    suggestions.value = filterSeen(await fetchSuggestions());
    currentIndex.value = 0;
    isLoading.value = false;
  }

  async function loadStats() {
    stats.value = await fetchStats();
  }

  function prefetchIfNeeded() {
    const remaining = suggestions.value.length - currentIndex.value - 1;
    if (remaining <= 1 && !nextBatchPromise) {
      nextBatchPromise = fetchSuggestions();
    }
  }

  async function advance() {
    let nextIndex = currentIndex.value + 1;
    while (nextIndex < suggestions.value.length) {
      if (!isSeenPair(pairKey(suggestions.value[nextIndex].deckA.id, suggestions.value[nextIndex].deckB.id))) break;
      nextIndex++;
    }

    if (nextIndex < suggestions.value.length) {
      currentIndex.value = nextIndex;
      prefetchIfNeeded();
    } else {
      const batch = nextBatchPromise ?? fetchSuggestions();
      nextBatchPromise = null;
      const newSuggestions = filterSeen(await batch);
      if (newSuggestions.length > 0) {
        suggestions.value = newSuggestions;
        currentIndex.value = 0;
        prefetchIfNeeded();
      } else {
        suggestions.value = [];
        currentIndex.value = 0;
      }
    }
  }

  function onVoted() {
    voteTimestamps.push(Date.now());
    const pair = currentPair.value;
    if (pair) sessionVotedKeys.add(pairKey(pair.deckA.id, pair.deckB.id));
    if (stats.value) stats.value.totalComparisons++;
    advance();
  }

  function onSkipped(_permanent: boolean) {
    const pair = currentPair.value;
    if (pair) sessionSkippedKeys.add(pairKey(pair.deckA.id, pair.deckB.id));
    advance();
    loadSkipped();
  }

  async function onBlocked(_deckId: number) {
    nextBatchPromise = null;
    await loadSuggestions();
    fetchCompletedDecks().then((r) => {
      completedDecks.value = r.decks;
      votedPairSet.value = new Set(r.votedPairs.map((p) => `${p[0]}-${p[1]}`));
    });
  }

  const unratedCollapsed = ref(true);
  const skippedCollapsed = ref(true);

  // Unrated decks
  const unratedDecks = ref<DeckSummaryDto[]>([]);
  const ratedUndoId = ref<number | null>(null);
  const ratedUndoLabel = ref('');
  const ratingLabels = ['Beginner', 'Easy', 'Average', 'Hard', 'Expert'];
  let undoTimer: ReturnType<typeof setTimeout> | undefined;

  async function loadUnrated() {
    unratedDecks.value = await fetchUnratedDecks();
  }

  function clearUndo() {
    clearTimeout(undoTimer);
    if (ratedUndoId.value !== null) {
      unratedDecks.value = unratedDecks.value.filter((d) => d.id !== ratedUndoId.value);
      ratedUndoId.value = null;
    }
  }

  function onUnratedRated(deckId: number, rating: number) {
    clearUndo();
    ratedUndoId.value = deckId;
    ratedUndoLabel.value = ratingLabels[rating] ?? '';
    loadStats();
    undoTimer = setTimeout(clearUndo, 30_000);
  }

  async function undoRating() {
    const deckId = ratedUndoId.value;
    if (deckId == null) return;
    clearTimeout(undoTimer);
    const success = await deleteRating(deckId);
    if (success) {
      ratedUndoId.value = null;
      loadStats();
    }
  }

  let statsInterval: ReturnType<typeof setInterval> | undefined;
  onMounted(() => {
    statsInterval = setInterval(loadStats, 60_000);
  });
  onUnmounted(() => {
    clearTimeout(undoTimer);
    clearInterval(statsInterval);
  });

  // Skipped pairs
  const skippedPairs = ref<DifficultyVoteDto[]>([]);
  const expandedSkipId = ref<number | null>(null);

  async function loadSkipped() {
    const result = await fetchMySkipped({ limit: 50 });
    skippedPairs.value = result?.data ?? [];
  }

  function removeSkip(id: number) {
    confirm.require({
      message: 'Are you sure you want to remove this skipped pair?',
      header: 'Remove Skipped Pair',
      icon: 'pi pi-exclamation-triangle',
      rejectProps: { label: 'Cancel', severity: 'secondary', outlined: true },
      acceptProps: { label: 'Remove', severity: 'danger' },
      accept: async () => {
        const success = await deleteSkip(id);
        if (success) {
          skippedPairs.value = skippedPairs.value.filter((s) => s.id !== id);
        }
      },
    });
  }

  function onSkippedVoted(id: number) {
    voteTimestamps.push(Date.now());
    const skip = skippedPairs.value.find((s) => s.id === id);
    if (skip) sessionVotedKeys.add(pairKey(skip.deckA.id, skip.deckB.id));
    if (stats.value) stats.value.totalComparisons++;
    skippedPairs.value = skippedPairs.value.filter((s) => s.id !== id);
    expandedSkipId.value = null;
  }

  // Manual comparison
  const manualCollapsed = ref(false);
  const completedDecks = ref<DeckSummaryDto[]>([]);
  const votedPairSet = ref(new Set<string>());
  const manualDeckA = ref<DeckSummaryDto | null>(null);
  const manualDeckB = ref<DeckSummaryDto | null>(null);
  const filteredDecksA = ref<DeckSummaryDto[]>([]);
  const filteredDecksB = ref<DeckSummaryDto[]>([]);

  function getDeckDisplayTitle(deck: DeckSummaryDto): string {
    if (jitenStore.titleLanguage === TitleLanguage.English) return deck.englishTitle ?? deck.romajiTitle ?? deck.title;
    if (jitenStore.titleLanguage === TitleLanguage.Romaji) return deck.romajiTitle ?? deck.title;
    return deck.title;
  }

  function matchesDeck(deck: DeckSummaryDto, query: string): boolean {
    const q = query.toLowerCase();
    return (
      deck.title.toLowerCase().includes(q) || (deck.romajiTitle?.toLowerCase().includes(q) ?? false) || (deck.englishTitle?.toLowerCase().includes(q) ?? false)
    );
  }

  function searchDeckA(event: AutoCompleteCompleteEvent) {
    filteredDecksA.value = completedDecks.value.filter((d) => matchesDeck(d, event.query));
  }

  function searchDeckB(event: AutoCompleteCompleteEvent) {
    if (!manualDeckA.value) {
      filteredDecksB.value = [];
      return;
    }
    filteredDecksB.value = completedDecks.value.filter(
      (d) =>
        d.id !== manualDeckA.value!.id &&
        areComparable(manualDeckA.value!.mediaType, d.mediaType) &&
        !votedPairSet.value.has(pairKey(manualDeckA.value!.id, d.id)) &&
        matchesDeck(d, event.query)
    );
  }

  function onManualDeckAChange() {
    manualDeckB.value = null;
  }

  function onManualVoted() {
    voteTimestamps.push(Date.now());
    if (manualDeckA.value && manualDeckB.value) {
      const key = pairKey(manualDeckA.value.id, manualDeckB.value.id);
      votedPairSet.value.add(key);
      sessionVotedKeys.add(key);
    }
    if (stats.value) stats.value.totalComparisons++;
    manualDeckA.value = null;
    manualDeckB.value = null;
  }

  function onManualSkipped() {
    manualDeckA.value = null;
    manualDeckB.value = null;
  }

  onMounted(async () => {
    await Promise.all([
      loadSuggestions(),
      loadStats(),
      loadSkipped(),
      loadUnrated(),
      fetchCompletedDecks().then((r) => {
        completedDecks.value = r.decks;
        votedPairSet.value = new Set(r.votedPairs.map((p) => `${p[0]}-${p[1]}`));
      }),
    ]);
    suggestions.value = filterSeen(suggestions.value);
  });
</script>

<template>
  <div class="p-2 md:p-4 overflow-hidden">
    <div class="flex flex-col md:flex-row items-start md:items-center justify-between mb-6 gap-3">
      <div>
        <h1 class="text-2xl font-bold">Compare Difficulties</h1>
        <p class="text-muted-color text-sm mt-1">Help improve difficulty ratings by comparing pairs of media you have completed.</p>
      </div>
      <div v-if="stats" class="flex items-center gap-4">
        <div class="text-center">
          <div class="text-2xl font-bold text-primary-500">{{ stats.totalComparisons }}</div>
          <div class="text-xs text-muted-color">Comparisons</div>
        </div>
        <div v-if="stats.percentile !== null" class="text-center">
          <div class="text-2xl font-bold text-primary-500">Top {{ 100 - stats.percentile }}%</div>
          <div class="text-xs text-muted-color">Contributor</div>
        </div>
      </div>
    </div>

    <div v-if="isLoading" class="flex justify-center py-12">
      <ProgressSpinner style="width: 50px; height: 50px" />
    </div>

    <Card v-else-if="!currentPair" class="text-center">
      <template #content>
        <div class="flex flex-col items-center gap-3 py-6">
          <i class="pi pi-check-circle text-green-500 text-5xl" />
          <p class="text-lg font-semibold">No more pairs available right now.</p>
          <p class="text-sm text-muted-color">Check back later or try refreshing.</p>
          <Button label="Refresh" icon="pi pi-refresh" class="mt-2" @click="loadSuggestions" />
        </div>
      </template>
    </Card>

    <Transition v-else name="card-swap" mode="out-in">
      <DifficultyComparison
        :key="`${currentPair.deckA.id}-${currentPair.deckB.id}`"
        :deck-a="currentPair.deckA"
        :deck-b="currentPair.deckB"
        :vote-timestamps="voteTimestamps"
        @voted="onVoted"
        @skipped="onSkipped"
        @blocked="onBlocked"
      />
    </Transition>

    <!-- Ranking flow CTA -->
    <div
      class="mt-6 border border-surface-200 dark:border-surface-700 rounded-lg p-4 flex flex-col sm:flex-row items-start sm:items-center gap-3 bg-surface-50 dark:bg-surface-800"
    >
      <div class="flex-1 min-w-0">
        <div class="font-semibold text-sm">Short on time? Rank in bulk</div>
        <p class="text-xs text-muted-color mt-1">
          Drag your completed titles into a tier list instead of comparing one pair at a time. Cover more ground quickly with the ranking flow.
        </p>
      </div>
      <NuxtLink to="/ratings/ranking">
        <Button label="Rank difficulties" icon="pi pi-list" size="small" />
      </NuxtLink>
    </div>

    <!-- Manual comparison -->
    <Panel v-if="completedDecks.length >= 2" v-model:collapsed="manualCollapsed" toggleable class="mt-6">
      <template #header>
        <div class="flex-1 cursor-pointer select-none" @click="manualCollapsed = !manualCollapsed">
          <span class="font-bold">Compare specific titles</span>
        </div>
      </template>
      <div class="flex flex-col gap-4">
        <div class="flex flex-col sm:flex-row gap-3">
          <div class="flex-1">
            <AutoComplete
              v-model="manualDeckA"
              :suggestions="filteredDecksA"
              placeholder="First title"
              class="w-full"
              force-selection
              dropdown
              :option-label="getDeckDisplayTitle"
              @complete="searchDeckA"
              @change="onManualDeckAChange"
            >
              <template #option="{ option }">
                <div class="flex items-center gap-2">
                  <img :src="option.coverUrl || '/img/nocover.jpg'" :alt="getDeckDisplayTitle(option)" class="h-10 w-7 object-cover rounded shrink-0" />
                  <span class="truncate">{{ getDeckDisplayTitle(option) }}</span>
                  <Tag :value="getMediaTypeText(option.mediaType)" severity="secondary" class="shrink-0" />
                </div>
              </template>
            </AutoComplete>
          </div>
          <div class="flex-1">
            <AutoComplete
              v-model="manualDeckB"
              :suggestions="filteredDecksB"
              :placeholder="manualDeckA ? 'Second title' : 'Select first title'"
              class="w-full"
              force-selection
              dropdown
              :disabled="!manualDeckA"
              :option-label="getDeckDisplayTitle"
              @complete="searchDeckB"
            >
              <template #option="{ option }">
                <div class="flex items-center gap-2">
                  <img :src="option.coverUrl || '/img/nocover.jpg'" :alt="getDeckDisplayTitle(option)" class="h-10 w-7 object-cover rounded shrink-0" />
                  <span class="truncate">{{ getDeckDisplayTitle(option) }}</span>
                  <Tag :value="getMediaTypeText(option.mediaType)" severity="secondary" class="shrink-0" />
                </div>
              </template>
            </AutoComplete>
          </div>
        </div>
        <DifficultyComparison
          v-if="manualDeckA && manualDeckB"
          :key="`manual-${manualDeckA.id}-${manualDeckB.id}`"
          :deck-a="manualDeckA"
          :deck-b="manualDeckB"
          :vote-timestamps="voteTimestamps"
          @voted="onManualVoted"
          @skipped="onManualSkipped"
          @blocked="onBlocked"
        />
      </div>
    </Panel>

    <!-- Rate unrated decks -->
    <Panel v-if="unratedDecks.length > 0" v-model:collapsed="unratedCollapsed" toggleable class="mt-6 min-w-0">
      <template #header>
        <div class="flex-1 cursor-pointer select-none" @click="unratedCollapsed = !unratedCollapsed">
          <span class="font-bold">Rate completed ({{ unratedDecks.filter((d) => d.id !== ratedUndoId).length }})</span>
        </div>
      </template>
      <TransitionGroup name="unrated-list" tag="div" class="flex flex-col gap-3 min-w-0">
        <div
          v-for="deck in unratedDecks"
          :key="deck.id"
          class="unrated-item border border-surface-200 dark:border-surface-700 rounded-lg p-3 overflow-hidden min-w-0"
        >
          <Transition name="unrated-swap" mode="out-in">
            <div v-if="deck.id === ratedUndoId" key="undo" class="flex items-center justify-between gap-2 flex-wrap min-w-0">
              <span class="text-muted-color text-sm min-w-0 break-words">
                Rated
                <strong>{{ getDeckDisplayTitle(deck) }}</strong>
                as {{ ratedUndoLabel }}
              </span>
              <Button label="Undo" icon="pi pi-undo" size="small" text @click="undoRating" />
            </div>
            <div v-else key="card" class="flex flex-col gap-3 min-w-0">
              <div class="flex items-center gap-3 min-w-0">
                <NuxtLink :to="`/decks/media/${deck.id}/detail`" class="shrink-0 hidden sm:block">
                  <img :src="deck.coverUrl || '/img/nocover.jpg'" :alt="getDeckDisplayTitle(deck)" class="h-16 w-11 object-cover rounded" />
                </NuxtLink>
                <div class="flex items-center gap-2 min-w-0">
                  <NuxtLink :to="`/decks/media/${deck.id}/detail`" class="font-medium hover:text-primary-500 truncate">
                    {{ getDeckDisplayTitle(deck) }}
                  </NuxtLink>
                  <Tag :value="getMediaTypeText(deck.mediaType)" severity="secondary" class="shrink-0" />
                </div>
              </div>
              <DifficultyRating :deck-id="deck.id" @rated="(r: number) => onUnratedRated(deck.id, r)" />
            </div>
          </Transition>
        </div>
      </TransitionGroup>
    </Panel>

    <!-- Review skipped pairs -->
    <Panel v-if="skippedPairs.length > 0" v-model:collapsed="skippedCollapsed" toggleable class="mt-6">
      <template #header>
        <div class="flex-1 cursor-pointer select-none" @click="skippedCollapsed = !skippedCollapsed">
          <span class="font-bold">Review skipped pairs ({{ skippedPairs.length }})</span>
        </div>
      </template>
      <div class="flex flex-col gap-3">
        <div v-for="skip in skippedPairs" :key="skip.id" class="border border-surface-200 dark:border-surface-700 rounded-lg p-3">
          <div class="flex items-center justify-between gap-2 flex-wrap">
            <span class="text-sm">
              <strong>{{ getDeckDisplayTitle(skip.deckA) }}</strong>
              <span class="text-muted-color mx-1">vs</span>
              <strong>{{ getDeckDisplayTitle(skip.deckB) }}</strong>
            </span>
            <div class="flex gap-2">
              <Button label="Compare now" icon="pi pi-arrow-right" size="small" text @click="expandedSkipId = expandedSkipId === skip.id ? null : skip.id" />
              <Button label="Remove" icon="pi pi-trash" size="small" text severity="danger" @click="removeSkip(skip.id)" />
            </div>
          </div>
          <div v-if="expandedSkipId === skip.id" class="mt-3">
            <DifficultyComparison
              :deck-a="skip.deckA"
              :deck-b="skip.deckB"
              :vote-timestamps="voteTimestamps"
              @voted="onSkippedVoted(skip.id)"
              @skipped="expandedSkipId = null"
              @blocked="onBlocked"
            />
          </div>
        </div>
      </div>
    </Panel>

    <div class="flex justify-center mt-6">
      <NuxtLink to="/ratings/history" class="text-sm text-muted-color hover:text-primary-500">View your vote history</NuxtLink>
    </div>

    <ConfirmPopup group="blockDeck" />
  </div>
</template>

<style scoped>
  :deep(.p-panel-content-container) {
    grid-template-columns: minmax(0, 1fr);
  }

  .card-swap-enter-active {
    transition:
      opacity 0.25s ease,
      transform 0.25s ease;
  }
  .card-swap-leave-active {
    transition:
      opacity 0.15s ease,
      transform 0.15s ease;
  }
  .card-swap-enter-from {
    opacity: 0;
    transform: translateY(12px);
  }
  .card-swap-leave-to {
    opacity: 0;
    transform: translateY(-8px);
  }

  /* Unrated list item removal */
  .unrated-list-move,
  .unrated-list-enter-active,
  .unrated-list-leave-active {
    transition: all 0.35s ease;
  }
  .unrated-list-enter-from,
  .unrated-list-leave-to {
    opacity: 0;
    max-height: 0;
    padding-top: 0;
    padding-bottom: 0;
    margin-top: 0;
    margin-bottom: 0;
    overflow: hidden;
  }
  .unrated-list-leave-active {
    overflow: hidden;
  }

  /* Card ↔ undo swap */
  .unrated-swap-enter-active {
    transition: opacity 0.2s ease 0.05s;
  }
  .unrated-swap-leave-active {
    transition: opacity 0.15s ease;
  }
  .unrated-swap-enter-from,
  .unrated-swap-leave-to {
    opacity: 0;
  }
</style>
