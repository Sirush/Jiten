<script setup lang="ts">
  import { debounce } from 'perfect-debounce';
  import type { JpdbDeck, JpdbImportSummary } from '~/composables/useJpdbApi';
  import { StudyDeckType } from '~/types';

  const emit = defineEmits<{ changed: [] }>();

  const { $api } = useNuxtApp();
  const toast = useToast();
  const { JpdbApiClient } = useJpdbApi();
  const srsStore = useSrsStore();
  const confirm = useConfirm();

  const isLoading = ref(false);
  const jpdbApiKey = ref('');
  const jpdbProgress = ref('');
  const summary = ref<JpdbImportSummary | null>(null);
  const showSummary = ref(false);

  const importKnownWords = ref(true);
  const importReviews = ref(false);
  const importDecks = ref(true);

  const overwriteCardStates = ref(true);
  const reviewsFile = ref<File | null>(null);

  const UNTICKED_DECKS_KEY = 'jpdb-word-list-unticked';
  const jpdbDecks = ref<JpdbDeck[]>([]);
  const decksLoadedForKey = ref('');
  const untickedDeckIds = ref<Set<number>>(new Set());
  const isLoadingDecks = ref(false);
  const deckLoadError = ref('');

  const confirmedReplaceIds = ref<Set<number>>(new Set());

  const existingListNames = computed(() => new Set(srsStore.studyDecks.filter((d) => d.deckType === StudyDeckType.StaticWordList).map((d) => d.name.trim())));

  function replacesExistingList(deck: JpdbDeck) {
    return existingListNames.value.has(deck.name.trim().slice(0, 200));
  }

  const tickedDecks = computed(() => jpdbDecks.value.filter(isDeckTicked));
  const collidingDecks = computed(() => jpdbDecks.value.filter(replacesExistingList));

  const blockingReason = computed(() => {
    if (!jpdbApiKey.value) return 'Enter your API key to continue.';
    if (!importKnownWords.value && !importReviews.value && !importDecks.value) return 'Tick at least one thing to import.';
    if (importReviews.value && !reviewsFile.value) return 'Choose a reviews.json file, or untick review history.';
    if (importDecks.value) {
      if (isLoadingDecks.value) return 'Loading your decks...';
      if (deckLoadError.value) return deckLoadError.value;
      if (jpdbDecks.value.length === 0) return 'No decks found on this JPDB account.';
      if (tickedDecks.value.length === 0) return 'Tick at least one deck, or untick word lists.';
    }
    return '';
  });

  const canImport = computed(() => !blockingReason.value && !isLoading.value);

  function onReviewsFileSelect(event: { files: File[] }) {
    reviewsFile.value = event.files?.[0] ?? null;
    if (reviewsFile.value) importReviews.value = true;
  }

  function onReviewsFileClear() {
    reviewsFile.value = null;
  }

  function readUntickedDecks() {
    try {
      const raw = localStorage.getItem(UNTICKED_DECKS_KEY);
      const ids = raw ? JSON.parse(raw) : [];
      untickedDeckIds.value = new Set(Array.isArray(ids) ? ids.filter((id) => typeof id === 'number') : []);
    } catch {
      untickedDeckIds.value = new Set();
    }
  }

  function persistUntickedDecks() {
    try {
      localStorage.setItem(UNTICKED_DECKS_KEY, JSON.stringify([...untickedDeckIds.value]));
    } catch {}
  }

  function isDeckTicked(deck: JpdbDeck) {
    if (replacesExistingList(deck) && !confirmedReplaceIds.value.has(deck.id)) return false;
    return !untickedDeckIds.value.has(deck.id);
  }

  function tick(decks: JpdbDeck[], ticked: boolean) {
    const next = new Set(untickedDeckIds.value);
    for (const d of decks) {
      if (ticked) next.delete(d.id);
      else next.add(d.id);
    }
    untickedDeckIds.value = next;
    persistUntickedDecks();
  }

  function confirmReplace(decks: JpdbDeck[], onAccept: () => void) {
    const names = decks.map((d) => `"${d.name}"`);
    const shown = names.slice(0, 5).join(', ') + (names.length > 5 ? ` and ${names.length - 5} more` : '');
    confirm.require({
      header: decks.length === 1 ? 'Replace an existing word list?' : `Replace ${decks.length} existing word lists?`,
      message:
        decks.length === 1
          ? `You already have a word list named ${shown}. Importing will replace its words with the JPDB deck. Words you added on Jiten will be removed from it.`
          : `You already have word lists named ${shown}. Importing will replace their words with the JPDB decks. Words you added on Jiten will be removed from them.`,
      icon: 'pi pi-exclamation-triangle',
      acceptLabel: 'Replace',
      rejectLabel: 'Cancel',
      acceptProps: { severity: 'danger' },
      accept: onAccept,
    });
  }

  function setDeckTicked(deck: JpdbDeck, ticked: boolean) {
    if (ticked && replacesExistingList(deck) && !confirmedReplaceIds.value.has(deck.id)) {
      confirmReplace([deck], () => {
        confirmedReplaceIds.value = new Set([...confirmedReplaceIds.value, deck.id]);
        tick([deck], true);
      });
      return;
    }
    tick([deck], ticked);
  }

  function setAllDecks(ticked: boolean) {
    if (!ticked) {
      tick(jpdbDecks.value, false);
      return;
    }
    const pending = collidingDecks.value.filter((d) => !confirmedReplaceIds.value.has(d.id));
    tick(
      jpdbDecks.value.filter((d) => !pending.includes(d)),
      true
    );
    if (pending.length === 0) return;
    confirmReplace(pending, () => {
      confirmedReplaceIds.value = new Set([...confirmedReplaceIds.value, ...pending.map((d) => d.id)]);
      tick(pending, true);
    });
  }

  async function loadJpdbDecks() {
    const key = jpdbApiKey.value;
    if (!key || decksLoadedForKey.value === key) return;
    try {
      isLoadingDecks.value = true;
      deckLoadError.value = '';
      readUntickedDecks();
      confirmedReplaceIds.value = new Set();
      const client = new JpdbApiClient(key);
      const [decks] = await Promise.all([client.listDecks(), srsStore.fetchStudyDecks()]);
      if (jpdbApiKey.value !== key) return;
      jpdbDecks.value = decks;
      decksLoadedForKey.value = key;
    } catch (error) {
      console.error('Error listing JPDB decks:', error);
      jpdbDecks.value = [];
      decksLoadedForKey.value = '';
      deckLoadError.value = 'Could not load your decks. Check the API key.';
    } finally {
      isLoadingDecks.value = false;
    }
  }

  const loadDecksDebounced = debounce(loadJpdbDecks, 600);

  watch([jpdbApiKey, importDecks], ([key, wantDecks]) => {
    if (!key) {
      jpdbDecks.value = [];
      decksLoadedForKey.value = '';
      deckLoadError.value = '';
      return;
    }
    if (wantDecks) loadDecksDebounced();
  });

  async function importFromJpdb() {
    if (!canImport.value) return;
    const result: JpdbImportSummary = {};

    try {
      isLoading.value = true;

      if (importReviews.value) result.reviews = await importReviewsFile();
      if (importKnownWords.value) result.knownWords = await importViaApi();
      if (importDecks.value) result.wordLists = await importDecksAsWordLists();

      emit('changed');
      summary.value = result;
      showSummary.value = true;
    } catch (error) {
      console.error('Error importing from JPDB:', error);
      const errorMessage = error instanceof Error ? error.message : 'Failed to import from JPDB. Please check your input and try again.';
      toast.add({ severity: 'error', summary: 'Error', detail: errorMessage, life: 6000 });
    } finally {
      isLoading.value = false;
      jpdbProgress.value = '';
    }
  }

  async function importViaApi(): Promise<NonNullable<JpdbImportSummary['knownWords']>> {
    jpdbProgress.value = 'Fetching known words from JPDB...';
    await new Promise((resolve) => setTimeout(resolve, 100));

    const client = new JpdbApiClient(jpdbApiKey.value);
    const cards = await client.getStudiedCards();
    if (cards.length === 0) return { added: 0, skipped: 0, unmatched: 0, unmatchedWords: [] };

    jpdbProgress.value = 'Sending vocabulary to your account...';
    await new Promise((resolve) => setTimeout(resolve, 100));

    const result: NonNullable<JpdbImportSummary['knownWords']> = await $api('user/vocabulary/import-from-ids', {
      method: 'POST',
      body: JSON.stringify({ cards }),
      headers: { 'Content-Type': 'application/json' },
    });

    return { added: result.added, skipped: result.skipped, unmatched: result.unmatched ?? 0, unmatchedWords: result.unmatchedWords ?? [] };
  }

  async function importReviewsFile(): Promise<NonNullable<JpdbImportSummary['reviews']>> {
    jpdbProgress.value = 'Reading reviews file...';
    await new Promise((resolve) => setTimeout(resolve, 100));

    const text = await reviewsFile.value!.text();
    const data = JSON.parse(text);

    const vocabCards = data.cards_vocabulary_jp_en;
    if (!Array.isArray(vocabCards) || vocabCards.length === 0)
      return { cardsInFile: 0, cardsProcessed: 0, reviewsImported: 0, reviewsUpdated: 0, skipped: 0, archivedRedundant: 0, skippedWords: [] };

    const cards = vocabCards.map((card: { vid: number; spelling: string; reviews: { timestamp: number; grade: string }[] }) => ({
      wordId: card.vid,
      spelling: card.spelling,
      reviews: card.reviews.map((r) => ({ timestamp: r.timestamp, grade: r.grade })),
    }));

    jpdbProgress.value = `Importing ${cards.length} cards with review history...`;
    await new Promise((resolve) => setTimeout(resolve, 100));

    const result: Omit<NonNullable<JpdbImportSummary['reviews']>, 'cardsInFile'> = await $api('user/vocabulary/import-jpdb-reviews', {
      method: 'POST',
      body: JSON.stringify({ cards, overwriteCardStates: overwriteCardStates.value }),
      headers: { 'Content-Type': 'application/json' },
    });

    return { cardsInFile: cards.length, ...result, skippedWords: result.skippedWords ?? [] };
  }

  async function importDecksAsWordLists(): Promise<NonNullable<JpdbImportSummary['wordLists']>> {
    const selected = tickedDecks.value;
    const client = new JpdbApiClient(jpdbApiKey.value);
    const payload: { jpdbDeckId: number; name: string; words: { wordId: number; spelling: string; occurrences: number }[] }[] = [];

    for (let i = 0; i < selected.length; i++) {
      const deck = selected[i]!;
      jpdbProgress.value = `Fetching "${deck.name}" (${i + 1}/${selected.length})...`;
      payload.push({ jpdbDeckId: deck.id, name: deck.name.slice(0, 200), words: await client.getDeckWords(deck.id) });
    }

    jpdbProgress.value = `Creating ${selected.length} word ${selected.length === 1 ? 'list' : 'lists'}...`;
    const result = await srsStore.importJpdbDecks(payload);
    return result.decks;
  }
</script>

<template>
  <Card>
    <template #title>
      <h3 class="text-lg font-semibold">Import from JPDB</h3>
    </template>
    <template #content>
      <div class="flex flex-col gap-5">
        <div>
          <span class="p-float-label">
            <InputText id="jpdbApiKey" v-model="jpdbApiKey" class="w-full" type="password" autocomplete="off" />
            <label for="jpdbApiKey">JPDB API Key</label>
          </span>
          <p class="mt-2 text-sm text-gray-600 dark:text-gray-400">
            Found at the bottom of
            <a href="https://jpdb.io/settings" target="_blank" rel="nofollow" class="text-primary-500 hover:underline">jpdb.io/settings</a>. It is used for this
            import only and never stored.
          </p>
        </div>

        <div>
          <h4 class="font-medium mb-2">What to import</h4>
          <div class="flex flex-col divide-y divide-surface-200 dark:divide-surface-700 rounded border border-surface-200 dark:border-surface-700">
            <div class="px-3 py-3">
              <div class="flex items-start gap-3">
                <Checkbox id="jpdbKnownWords" v-model="importKnownWords" :binary="true" class="mt-0.5" />
                <label for="jpdbKnownWords" class="flex-1 cursor-pointer">
                  <span class="block">Known words</span>
                  <span class="block text-sm text-gray-600 dark:text-gray-400"
                    >Known and never-forget words are marked as mastered; blacklisted and suspended keep those states. Reviews are not imported.</span
                  >
                </label>
              </div>
            </div>

            <div class="px-3 py-3">
              <div class="flex items-start gap-3">
                <Checkbox id="jpdbReviews" v-model="importReviews" :binary="true" class="mt-0.5" />
                <label for="jpdbReviews" class="flex-1 cursor-pointer">
                  <span class="block">Review history</span>
                  <span class="block text-sm text-gray-600 dark:text-gray-400"
                    >Using your reviews.json exported from JPDB, import all your reviews and learning card states.</span
                  >
                </label>
              </div>
              <div v-if="importReviews" class="mt-3 md:ml-8 flex flex-col gap-2">
                <FileUpload
                  mode="basic"
                  accept=".json"
                  :auto="false"
                  choose-label="Choose reviews.json"
                  @select="onReviewsFileSelect"
                  @clear="onReviewsFileClear"
                />
                <div class="flex items-center">
                  <Checkbox id="overwriteCardStates" v-model="overwriteCardStates" :binary="true" />
                  <label for="overwriteCardStates" class="ml-2 text-sm">Overwrite existing card states (mastered, blacklisted, suspended)</label>
                </div>
              </div>
            </div>

            <div class="px-3 py-3">
              <div class="flex items-start gap-3">
                <Checkbox id="jpdbDecks" v-model="importDecks" :binary="true" class="mt-0.5" />
                <label for="jpdbDecks" class="flex-1 cursor-pointer">
                  <span class="block">JPDB decks</span>
                  <span class="block text-sm text-gray-600 dark:text-gray-400">
                    Import your JPDB decks as word list, one per deck. Re-importing a list replaces it. This does not import the state of any of your words.
                  </span>
                </label>
              </div>
              <div v-if="importDecks" class="mt-3 md:ml-8">
                <p v-if="!jpdbApiKey" class="text-sm text-gray-500 dark:text-gray-400">Your decks appear here once the key is entered.</p>
                <p v-else-if="isLoadingDecks" class="text-sm text-gray-500 dark:text-gray-400"><i class="pi pi-spin pi-spinner mr-1" />Loading your decks...</p>
                <p v-else-if="deckLoadError" class="text-sm text-red-600 dark:text-red-400">{{ deckLoadError }}</p>
                <p v-else-if="jpdbDecks.length === 0" class="text-sm text-gray-500 dark:text-gray-400">No decks found on this account.</p>
                <div v-else class="flex flex-col gap-2">
                  <Message v-if="collidingDecks.length > 0" severity="warn" :closable="false" class="text-sm">
                    {{ collidingDecks.length === 1 ? 'One deck has' : `${collidingDecks.length} decks have` }} the same name as a word list you already have on
                    Jiten. If you want to import {{ collidingDecks.length === 1 ? 'it' : 'them' }} and don't want
                    {{ collidingDecks.length === 1 ? 'it' : 'them' }} to replace your existing lists, then you need to rename
                    {{ collidingDecks.length === 1 ? 'it' : 'them' }} on Jiten.
                  </Message>
                  <div class="flex flex-wrap items-center gap-3 text-sm">
                    <span>{{ tickedDecks.length }} of {{ jpdbDecks.length }} selected</span>
                    <button type="button" class="text-primary-500 hover:underline" @click="setAllDecks(true)">Select all</button>
                    <button type="button" class="text-primary-500 hover:underline" @click="setAllDecks(false)">Clear</button>
                  </div>
                  <ul
                    class="max-h-72 overflow-y-auto rounded border border-surface-200 dark:border-surface-700 divide-y divide-surface-200 dark:divide-surface-700"
                  >
                    <li v-for="deck in jpdbDecks" :key="deck.id" class="flex items-center gap-3 px-3 py-1.5">
                      <Checkbox
                        :input-id="`jpdb-deck-${deck.id}`"
                        :model-value="isDeckTicked(deck)"
                        :binary="true"
                        @update:model-value="(v: boolean) => setDeckTicked(deck, v)"
                      />
                      <label :for="`jpdb-deck-${deck.id}`" class="flex-1 min-w-0 truncate cursor-pointer">{{ deck.name }}</label>
                      <span v-if="replacesExistingList(deck)" class="text-xs text-amber-700 dark:text-amber-400 shrink-0 inline-flex items-center gap-1">
                        <i class="pi pi-exclamation-triangle text-[10px]" />replaces your list
                      </span>
                      <span v-if="deck.isBuiltIn" class="text-xs text-gray-500 dark:text-gray-400 shrink-0">built-in</span>
                      <span class="text-sm text-gray-600 dark:text-gray-400 shrink-0 tabular-nums"
                        >{{ deck.vocabularyCount.toLocaleString() }}<span class="hidden sm:inline"> words</span></span
                      >
                    </li>
                  </ul>
                </div>
              </div>
            </div>
          </div>
        </div>

        <div class="flex flex-col md:flex-row md:items-center gap-3">
          <Button label="Import" icon="pi pi-download" :disabled="!canImport" class="w-full md:w-auto" @click="importFromJpdb" />
          <span v-if="blockingReason" class="text-sm text-gray-600 dark:text-gray-400">{{ blockingReason }}</span>
        </div>
      </div>
    </template>
  </Card>

  <VocabularyImportJpdbSummaryDialog v-model:visible="showSummary" :summary="summary" />

  <LoadingOverlay :visible="isLoading">
    <p v-if="jpdbProgress">{{ jpdbProgress }}</p>
    <p v-else>Processing your data...</p>
  </LoadingOverlay>
</template>
