<script setup lang="ts">
  import { ref, computed, onMounted, onUnmounted } from 'vue';
  import { YankiConnect } from 'yanki-connect';
  import { useToast } from 'primevue/usetoast';
  import { useAnkiImportStore, defaultImportSelection } from '~/stores/ankiImportStore';

  const emit = defineEmits<{
    importComplete: [];
  }>();

  const { $api } = useNuxtApp();
  const toast = useToast();
  const ankiImportStore = useAnkiImportStore();

  let currentStep = ref(0);

  let client: YankiConnect;
  let decks: Record<string, number> = {};
  let deckEntries: Array<[string, number]> = [];
  let cantConnect = ref(false);
  let cardsIds: number[] = [];

  const apiKey = ref('');

  const isLoading = ref(false);

  const showSkippedDialog = ref(false);
  const skippedWords = ref<string[]>([]);

  const showErrorDialog = ref(false);
  const errorMessage = ref('');
  const errorDetail = ref('');
  const errorCopied = ref(false);
  const operationActive = ref(false);

  const copyErrorDetails = async () => {
    const text = [errorMessage.value, errorDetail.value].filter(Boolean).join('\n\n');
    try {
      await navigator.clipboard.writeText(text);
      errorCopied.value = true;
      setTimeout(() => (errorCopied.value = false), 2000);
    } catch {
      errorCopied.value = false;
    }
  };

  const reportError = (err: unknown, fallback = 'An unexpected error occurred.') => {
    let message = extractApiError(err, '');
    if (!message) {
      if (err instanceof Error) message = err.message;
      else if (typeof err === 'string') message = err;
    }
    errorMessage.value = message || fallback;
    errorDetail.value = err instanceof Error && err.stack ? err.stack : '';
    errorCopied.value = false;
    showErrorDialog.value = true;
    console.error(err);
  };

  // Safety net: surface any uncaught frontend error/rejection that occurs while an
  // Anki operation is in flight, instead of letting it disappear into the console.
  const handleWindowError = (event: ErrorEvent) => {
    if (!operationActive.value || !event.error) return;
    reportError(event.error);
  };
  const handleRejection = (event: PromiseRejectionEvent) => {
    if (!operationActive.value) return;
    reportError(event.reason);
  };

  onMounted(() => {
    window.addEventListener('error', handleWindowError);
    window.addEventListener('unhandledrejection', handleRejection);
    ankiImportStore.load();
  });
  onUnmounted(() => {
    window.removeEventListener('error', handleWindowError);
    window.removeEventListener('unhandledrejection', handleRejection);
  });

  type KeptReview = { Rating: number; ReviewDateTime: Date; ReviewDuration: number };
  let selectedFieldName = '';
  let selectedReadingFieldName = '';
  let supportsFieldsFilter = false;
  // getReviewsOfCards (per-card review fetch) is the only memory-bounded way to pull reviews. Older
  // AnkiConnect installs lack it; when unavailable we skip review history (cards still import) and warn
  // the user, rather than falling back to the per-deck cardReviews bulk fetch which OOMs large decks.
  let supportsGetReviewsOfCards = false;

  const cardsInfoFields = ['cardId', 'due', 'queue', 'type', 'interval', 'factor', 'reps', 'lapses', 'mod', 'flags', 'fields', 'modelName', 'deckName'];

  // Max review-log entries kept per card. Captures the full history of essentially every normal card
  // (a card maturing over years rarely exceeds ~25 reviews); only clips persistent leeches.
  const MAX_REVIEWS_PER_CARD = 100;

  const stripRuby = (text: string) => text.replace(/\[.*?\]/g, '');

  // Converts an Anki furigana field to its full kana reading. For `下[くだ]さる` the base text
  // before each bracket is dropped and the bracket content kept, leaving plain kana untouched:
  // `下[くだ]さる` → `くださる`. A field that is already full kana is returned as-is (spaces stripped).
  const furiganaToReading = (text: string) =>
    text
      .replace(/&nbsp;/g, ' ')
      .replace(/([^[\]\s]+)\[([^[\]]*)\]/g, '$2')
      .replace(/\s+/g, '')
      .trim();

  // A Date built from corrupt/out-of-range Anki fields can be Invalid (NaN time),
  // and calling toISOString() on it throws and aborts the whole import. Guard every
  // date we serialise so a single malformed card can't take down the batch.
  const isValidDate = (d: Date | null | undefined): d is Date => !!d && !Number.isNaN(d.getTime());

  async function ankiInvoke(action: string, params: Record<string, any> = {}): Promise<any> {
    const body: Record<string, any> = { action, version: 6, params };
    if (apiKey.value) body.key = apiKey.value;
    const res = await fetch('http://127.0.0.1:8765', {
      method: 'POST',
      body: JSON.stringify(body),
    });
    const json = await res.json();
    if (json.error) throw new Error(json.error);
    return json.result;
  }

  async function fetchCardsInfo(cards: number[]): Promise<any[]> {
    if (supportsFieldsFilter) {
      return ankiInvoke('cardsInfo', { cards, fields: cardsInfoFields });
    }
    return client.card.cardsInfo({ cards }) as Promise<any[]>;
  }

  // Fetch the review history for one chunk of cards and reduce it to the kept set: oldest first,
  // capped at MAX_REVIEWS_PER_CARD. Fetching per chunk (rather than the whole deck up front) keeps
  // peak memory proportional to the chunk size — the raw, uncapped history is never all held at once.
  // getReviewsOfCards takes card IDs directly, so subdecks are covered without enumerating deck names.
  async function fetchChunkReviews(chunkCardIds: number[]): Promise<Map<number, KeptReview[]>> {
    const map = new Map<number, KeptReview[]>();
    // Card IDs MUST be sent as numbers, not strings. AnkiConnect's getReviewsOfCards keys its internal
    // results by the integer cid from the DB but then re-looks them up by the exact values we passed,
    // so passing strings makes every lookup miss and returns empty reviews for every card. We call via
    // the raw ankiInvoke because yanki-connect's typings declare string[] (which triggers that bug).
    const chunkReviews = (await ankiInvoke('getReviewsOfCards', { cards: chunkCardIds })) as Record<
      string,
      Array<{ ease: number; id: number; time: number }>
    >;
    for (const [cardIdStr, reviews] of Object.entries(chunkReviews)) {
      if (!reviews || reviews.length === 0) continue;
      const mapped: KeptReview[] = reviews.map((r) => ({
        Rating: r.ease,
        ReviewDateTime: new Date(r.id),
        ReviewDuration: r.time,
      }));
      mapped.sort((a, b) => a.ReviewDateTime.getTime() - b.ReviewDateTime.getTime());
      map.set(Number(cardIdStr), mapped.length > MAX_REVIEWS_PER_CARD ? mapped.slice(0, MAX_REVIEWS_PER_CARD) : mapped);
    }
    return map;
  }

  // Run an async mapper over items with a bounded number of concurrent tasks, preserving order.
  // Caps how many chunks' raw review responses are in flight at once, so peak memory stays bounded
  // regardless of deck size while still overlapping enough requests to stay fast.
  async function mapWithConcurrency<T, R>(items: T[], limit: number, fn: (item: T, index: number) => Promise<R>): Promise<R[]> {
    const results: R[] = new Array(items.length);
    let next = 0;
    const worker = async () => {
      while (next < items.length) {
        const i = next++;
        results[i] = await fn(items[i], i);
      }
    };
    await Promise.all(Array.from({ length: Math.min(limit, items.length) }, worker));
    return results;
  }

  const fetchProgress = ref(0);
  const uploadProgress = ref(0);
  const importPhase = ref<'fetch' | 'upload'>('fetch');
  const importResults = ref({ imported: 0, updated: 0, skipped: 0, reviewLogs: 0 });

  const selectedDeck = ref<number>(0);
  const selectedField = ref<number>(0);
  const selectedReadingField = ref<number>(-1);
  const fields = ref<Array<[string, { order: number; value: string }]>>([]);
  const overwriteExisting = ref(false);
  const parseWords = ref(false);
  const importReviewHistory = ref(true);

  // --- Remembered import settings (device-local; issue #402) ---

  // The deck list arrives as [name, id] pairs; the chosen deck's name is derived from its id.
  const selectedDeckName = computed(() => deckEntries.find(([, id]) => id === selectedDeck.value)?.[0] ?? '');

  let lastLoadedDeckId = 0;

  const persistImportSettings = () => {
    if (!selectedDeck.value) return;
    const fieldName = fields.value[selectedField.value]?.[0];
    if (!fieldName) return;
    const deckName = selectedDeckName.value;
    const readingFieldName = selectedReadingField.value >= 0 ? fields.value[selectedReadingField.value]?.[0] ?? '' : '';
    ankiImportStore.saveDeckSelection(selectedDeck.value, {
      deckName,
      fieldName,
      readingFieldName,
      importReviewHistory: importReviewHistory.value,
      overwriteExisting: overwriteExisting.value,
      parseWords: parseWords.value,
    });
  };

  // @change on the field/reading Selects and the option Checkboxes: persist this deck's selection
  // immediately on a user change, so it survives any later navigation and never leaks into another deck.
  const onSelectionChanged = () => persistImportSettings();

  const fieldsOptions = computed(() =>
    (fields.value || []).map((entry, idx) => ({
      label: entry[0] + (entry[1].value ? ` (${stripRuby(entry[1].value).substring(0, 20)})` : ''),
      value: idx,
    }))
  );

  // Reading-field options add an explicit "None" entry (the reading is optional) and preview the
  // extracted reading rather than the bracket-stripped surface, so a furigana field like
  // `下[くだ]さる` previews as `くださる` — the reading we actually keep.
  const readingFieldsOptions = computed(() => [
    { label: 'None (optional)', value: -1 },
    ...(fields.value || []).map((entry, idx) => ({
      label: entry[0] + (entry[1].value ? ` (${furiganaToReading(entry[1].value).substring(0, 20)})` : ''),
      value: idx,
    })),
  ]);

  const Connect = async () => {
    operationActive.value = true;
    try {
      client = new YankiConnect(apiKey.value ? { key: apiKey.value } : {});
      decks = await client.deck.deckNamesAndIds();
      deckEntries = Object.entries(decks);
      cantConnect.value = false;

      // Pre-select the last-used deck (by id, then stored name as a backup). The step-2 restore
      // re-validates id+name before applying any saved config.
      const lastDeckId = ankiImportStore.findLastUsedDeckId(deckEntries);
      if (lastDeckId) selectedDeck.value = lastDeckId;

      try {
        await ankiInvoke('cardsInfo', { cards: [], fields: cardsInfoFields });
        supportsFieldsFilter = true;
      } catch {
        supportsFieldsFilter = false;
      }

      try {
        // Empty cards list runs no SQL and returns {} on supported versions; throws if the action
        // is missing (older AnkiConnect), in which case review history is skipped and the user warned.
        await ankiInvoke('getReviewsOfCards', { cards: [] });
        supportsGetReviewsOfCards = true;
      } catch {
        supportsGetReviewsOfCards = false;
      }

      await NextStep();
    } catch (e) {
      cantConnect.value = true;
      console.log(e);
    } finally {
      operationActive.value = false;
    }
  };

  const PreviousStep = () => {
    currentStep.value -= 2;
    NextStep();
  };

  type SkipStats = { suspended: number; newCard: number; missingField: number; emptyWord: number };

  // Helper to build a single card payload from Anki card info
  const buildCardPayload = (card: any, fieldName: string, readingFieldName: string, reviewsByCard: Map<number, KeptReview[]>, stats?: SkipStats) => {
    if (card.queue === -1) { if (stats) stats.suspended++; return null; } // suspended
    if (card.queue === 0) { if (stats) stats.newCard++; return null; } // new/forgotten

    const field = card.fields[fieldName];
    if (field === undefined && stats) stats.missingField++; // selected field absent on this note type
    const word = stripRuby(field?.value?.trim() || '');
    if (!word) { if (stats && field !== undefined) stats.emptyWord++; return null; }

    // Optional reading field, used server-side to disambiguate same-surface words.
    let reading = '';
    if (readingFieldName) {
      const readingField = card.fields[readingFieldName];
      reading = furiganaToReading(readingField?.value?.trim() || '');
    }

    const reviews = reviewsByCard.get(card.cardId) ?? [];

    // Convert Anki state to FSRS state
    let state: number;
    if (card.queue === 1 || card.queue === 3) state = 1; // Learning
    else state = 2; // Review

    const stability = card.interval > 0 ? card.interval : 0;
    const difficulty = Math.max(1, Math.min(10, 10 - (card.factor - 1300) / 170.0));

    // Reviews are ordered oldest-first, so the most recent review — used to reconstruct LastReview and
    // the Review/day-learning due date — is the last element. When review history isn't imported (or a
    // studied card has no logs), fall back to Anki's card modification time so the card still carries a
    // LastReview and stays schedulable, instead of being dropped server-side for having none.
    const mostRecentReview = reviews.length > 0 ? reviews[reviews.length - 1].ReviewDateTime : null;
    const modReview = card.mod ? new Date(card.mod * 1000) : null;
    const lastReview = isValidDate(mostRecentReview) ? mostRecentReview : isValidDate(modReview) ? modReview : null;

    let due: Date | null;
    // Only intraday learning (queue 1) stores `due` as a Unix timestamp. Review (2) and
    // interday day-learning (3) store it as a day-number relative to collection creation,
    // which we can't convert here — so reconstruct their due from lastReview/mod + interval.
    if (card.queue === 1) {
      due = new Date(card.due * 1000);
    } else if (lastReview) {
      due = new Date(lastReview.getTime() + card.interval * 86400000);
    } else {
      due = new Date(card.mod * 1000 + card.interval * 86400000);
    }
    // Due is required server-side; if corrupt fields produced an invalid instant,
    // fall back to the last review, then to now, rather than throwing.
    if (!isValidDate(due)) due = lastReview ?? new Date();

    return {
      Card: {
        Word: word,
        Reading: reading || undefined,
        Stability: stability,
        Difficulty: difficulty,
        Reps: card.reps,
        Lapses: card.lapses,
        Due: due.toISOString(),
        State: state,
        LastReview: lastReview?.toISOString(),
      },
      ReviewLogs: reviews
        .filter((r) => isValidDate(r.ReviewDateTime))
        .map((r) => ({
          Rating: r.Rating,
          ReviewDateTime: r.ReviewDateTime.toISOString(),
          ReviewDuration: r.ReviewDuration,
        })),
    };
  };

  const NextStep = async () => {
    currentStep.value++;

    if (currentStep.value == 2) {
      if (selectedDeck.value == null) {
        currentStep.value--;
        return;
      }

      isLoading.value = true;
      operationActive.value = true;
      try {
        // Search by deck name rather than `did:`, because `did:` matches the exact deck only.
        // Anki's `deck:"Name"` is recursive, so selecting a parent also pulls in every subdeck.
        const deckName = selectedDeckName.value;
        const query = deckName ? `deck:"${deckName.replace(/["*_\\]/g, '\\$&')}"` : `did:${selectedDeck.value}`;
        cardsIds = await client.card.findCards({ query });
        // Sample several cards rather than just the first: a field (e.g. ExpressionReading) may be
        // empty on the first card while populated on others, which would leave it without a preview.
        const previewCards = await fetchCardsInfo(cardsIds.slice(0, 20));
        // Only a reload of the SAME deck keeps the in-session selection (e.g. clicking Back from the
        // options step); switching to a different deck restores that deck's own saved selection below.
        const sameDeckReload = selectedDeck.value === lastLoadedDeckId;
        const prevFieldName = sameDeckReload ? fields.value[selectedField.value]?.[0] : undefined;
        const prevReadingName = sameDeckReload && selectedReadingField.value >= 0 ? fields.value[selectedReadingField.value]?.[0] : '';

        selectedField.value = 0;
        selectedReadingField.value = -1;
        if (previewCards && previewCards.length > 0) {
          // Merge across the sample: keep each field's first non-empty value so every field shows a preview.
          const merged = new Map<string, { order: number; value: string }>();
          for (const c of previewCards) {
            for (const [name, info] of Object.entries(c.fields || {}) as Array<[string, { order: number; value: string }]>) {
              const existing = merged.get(name);
              if (!existing) merged.set(name, { order: info.order, value: info.value || '' });
              else if (!existing.value && info.value) existing.value = info.value;
            }
          }
          fields.value = [...merged.entries()].sort((a, b) => a[1].order - b[1].order);
        } else {
          fields.value = [];
        }
        lastLoadedDeckId = selectedDeck.value;

        // Re-apply selections by NAME (the index was just rebuilt). A same-deck reload keeps the
        // in-session selection. Switching to a different deck restores THAT deck's own saved config —
        // fields AND options — but only when it is the EXACT same deck (entry keyed by this id AND the
        // stored name matches); otherwise it starts from defaults so the previous deck can't leak in.
        let wantFieldName = prevFieldName;
        let wantReadingName = prevReadingName;
        if (!sameDeckReload) {
          const saved = ankiImportStore.resolveDeckSelection(selectedDeck.value, selectedDeckName.value) ?? defaultImportSelection();
          wantFieldName = saved.fieldName || undefined;
          wantReadingName = saved.readingFieldName;
          importReviewHistory.value = saved.importReviewHistory;
          overwriteExisting.value = saved.overwriteExisting;
          parseWords.value = saved.parseWords;
        }

        if (wantFieldName) {
          const wordIdx = fields.value.findIndex(([name]) => name === wantFieldName);
          if (wordIdx >= 0) selectedField.value = wordIdx;
        }
        if (wantReadingName) {
          const readingIdx = fields.value.findIndex(([name]) => name === wantReadingName);
          if (readingIdx >= 0) selectedReadingField.value = readingIdx;
        }
      } catch (e) {
        reportError(e, 'Failed to load deck from Anki.');
        currentStep.value = 1;
      } finally {
        isLoading.value = false;
        operationActive.value = false;
      }
    }

    if (currentStep.value == 3) {
      // Step 3 is now instant - just store the field name
      const fieldEntry = fields.value[selectedField.value];
      selectedFieldName = fieldEntry ? fieldEntry[0] : '';
      if (!selectedFieldName) {
        console.warn('No field selected for mapping');
        currentStep.value--;
        return;
      }
      const readingFieldEntry = selectedReadingField.value >= 0 ? fields.value[selectedReadingField.value] : undefined;
      selectedReadingFieldName = readingFieldEntry ? readingFieldEntry[0] : '';
      // Save this deck's field mapping now, so switching between decks remembers each one even before
      // an import is run (issue #402). Options are re-saved with their final values at step 4.
      persistImportSettings();
      // No API calls - all heavy work is deferred to Step 4
    }

    if (currentStep.value == 4) {
      // Remember these choices for next time (issue #402); saved even if the import later errors.
      persistImportSettings();
      isLoading.value = true;
      operationActive.value = true;
      fetchProgress.value = 0;
      uploadProgress.value = 0;
      importPhase.value = 'fetch';
      importResults.value = { imported: 0, updated: 0, skipped: 0, reviewLogs: 0 };

      const allSkippedWords: string[] = [];
      let skippedCountNoReviews = 0;
      const skipStats: SkipStats = { suspended: 0, newCard: 0, missingField: 0, emptyWord: 0 };

      try {
        // Reviews are fetched per chunk (see fetchChunkReviews), oldest first and capped, rather than
        // for the whole deck up front — so peak memory stays bounded on large decks and the raw,
        // uncapped history is never all in memory at once. Card IDs already cover subdecks (the deck
        // search above is recursive), so no deck-name enumeration is needed.
        const ankiChunkSize = supportsFieldsFilter ? 2000 : 500;
        const chunks: number[][] = [];
        for (let i = 0; i < cardsIds.length; i += ankiChunkSize) {
          chunks.push(cardsIds.slice(i, i + ankiChunkSize));
        }

        // Reviews are fetched only via the per-card getReviewsOfCards path, which keeps memory bounded.
        // We deliberately do NOT fall back to the older per-deck cardReviews bulk fetch — it OOMs the
        // browser on large decks. On an AnkiConnect too old to support getReviewsOfCards, cards still
        // import (with their Anki FSRS state) but without review logs, and the user is warned afterwards.
        const reviewsUnsupported = importReviewHistory.value && !supportsGetReviewsOfCards;
        const reviewsForChunk = async (chunkIds: number[]): Promise<Map<number, KeptReview[]>> => {
          if (!importReviewHistory.value || !supportsGetReviewsOfCards) return new Map();
          return fetchChunkReviews(chunkIds);
        };

        const aggregateResult = (result: any) => {
          if (!result) return;
          importResults.value = {
            imported: importResults.value.imported + (result.imported || 0),
            updated: importResults.value.updated + (result.updated || 0),
            skipped: importResults.value.skipped + (result.skipped || 0),
            reviewLogs: importResults.value.reviewLogs + (result.reviewLogs || 0),
          };
          if (result.skippedWords) allSkippedWords.push(...result.skippedWords);
          skippedCountNoReviews += result.skippedCountNoReviews || 0;
        };

        if (supportsFieldsFilter) {
          // Optimized path: fetch cards + reviews per chunk with bounded concurrency and build the
          // chunk's payloads immediately, so each chunk's raw reviews are released before the next.
          const FETCH_CONCURRENCY = 5;
          let completedFetches = 0;
          const allChunkPayloads = await mapWithConcurrency(chunks, FETCH_CONCURRENCY, async (chunkIds) => {
            const cards = await fetchCardsInfo(chunkIds);
            const chunkReviews = await reviewsForChunk(chunkIds);
            const payloads: any[] = [];
            for (const card of cards || []) {
              const payload = buildCardPayload(card, selectedFieldName, selectedReadingFieldName, chunkReviews, skipStats);
              if (payload) payloads.push(payload);
            }
            completedFetches++;
            fetchProgress.value = Math.round((completedFetches / chunks.length) * 100);
            return payloads;
          });

          const seenWords = new Set<string>();
          const allPayloads: any[] = [];
          for (const chunkPayloads of allChunkPayloads) {
            for (const payload of chunkPayloads) {
              // Dedup by surface + reading so homographs with different readings both survive.
              const dedupKey = payload.Card.Word + ' ' + (payload.Card.Reading || '');
              if (seenWords.has(dedupKey)) continue;
              seenWords.add(dedupKey);
              allPayloads.push(payload);
            }
          }

          const apiChunkSize = 2000;
          const apiChunks: any[][] = [];
          for (let i = 0; i < allPayloads.length; i += apiChunkSize) {
            apiChunks.push(allPayloads.slice(i, i + apiChunkSize));
          }

          importPhase.value = 'upload';
          let completedUploads = 0;
          const apiResults = await Promise.all(
            apiChunks.map(async (chunkPayload) => {
              const result = await $api<any>('user/vocabulary/import-from-anki', {
                method: 'POST',
                body: JSON.stringify({
                  cards: chunkPayload,
                  overwrite: overwriteExisting.value,
                  parseWords: parseWords.value,
                }),
                headers: { 'Content-Type': 'application/json' },
              });
              completedUploads++;
              uploadProgress.value = Math.round((completedUploads / apiChunks.length) * 100);
              return result;
            }),
          );
          for (const result of apiResults) aggregateResult(result);
        } else {
          // Standard path: sequential fetch + upload to keep memory low
          for (let i = 0; i < chunks.length; i++) {
            const chunkCards = await fetchCardsInfo(chunks[i]);
            const chunkReviews = await reviewsForChunk(chunks[i]);
            fetchProgress.value = Math.round(((i + 1) / chunks.length) * 100);

            const chunkPayload: any[] = [];
            for (const card of chunkCards || []) {
              const payload = buildCardPayload(card, selectedFieldName, selectedReadingFieldName, chunkReviews, skipStats);
              if (payload) chunkPayload.push(payload);
            }

            if (chunkPayload.length === 0) continue;

            importPhase.value = 'upload';
            const result = await $api<any>('user/vocabulary/import-from-anki', {
              method: 'POST',
              body: JSON.stringify({
                cards: chunkPayload,
                overwrite: overwriteExisting.value,
                parseWords: parseWords.value,
              }),
              headers: { 'Content-Type': 'application/json' },
            });
            uploadProgress.value = Math.round(((i + 1) / chunks.length) * 100);
            aggregateResult(result);
            importPhase.value = 'fetch';
          }
        }

        // Show final results
        const r = importResults.value;
        let message = '';
        if (r.imported > 0) {
          message += `Imported ${r.imported} new card${r.imported === 1 ? '' : 's'}`;
        }
        if (r.updated > 0) {
          if (message) message += ', ';
          message += `updated ${r.updated} existing card${r.updated === 1 ? '' : 's'}`;
        }
        if (r.reviewLogs) {
          message += ` with ${r.reviewLogs} review log${r.reviewLogs === 1 ? '' : 's'}`;
        }
        if (r.skipped > 0) {
          if (message) message += '. ';
          message += `${r.skipped} card${r.skipped === 1 ? '' : 's'} skipped`;
        }
        if (skippedCountNoReviews > 0) {
          if (message) message += '. ';
          message += `${skippedCountNoReviews} card${skippedCountNoReviews === 1 ? '' : 's'} skipped (no reviews)`;
        }
        if (!message) {
          // Nothing was imported: explain why by reporting how each card was filtered out.
          console.log('AnkiConnect import: 0 cards imported. Skip breakdown:', skipStats, 'field:', selectedFieldName);
          const reasons: string[] = [];
          if (skipStats.missingField > 0) reasons.push(`${skipStats.missingField} missing the "${selectedFieldName}" field (different note type?)`);
          if (skipStats.emptyWord > 0) reasons.push(`${skipStats.emptyWord} with an empty "${selectedFieldName}" field`);
          if (skipStats.newCard > 0) reasons.push(`${skipStats.newCard} new/unstudied`);
          if (skipStats.suspended > 0) reasons.push(`${skipStats.suspended} suspended`);
          message = reasons.length > 0 ? `No cards were imported — ${reasons.join(', ')}.` : 'No cards were imported.';
        } else {
          message += '.';
        }

        toast.add({
          severity: 'success',
          summary: 'Anki Data Imported',
          detail: message,
          life: 6000,
        });

        if (reviewsUnsupported) {
          toast.add({
            severity: 'warn',
            summary: 'Review history not imported',
            detail: 'Your AnkiConnect add-on is too old to import review history. Cards were imported with their scheduling state; update AnkiConnect to also bring in review logs.',
            life: 10000,
          });
        }

        if (allSkippedWords.length > 0) {
          skippedWords.value = allSkippedWords;
          showSkippedDialog.value = true;
        }

        // Notify parent to refresh vocabulary counts
        emit('importComplete');
      } catch (error) {
        reportError(error, 'Failed to import data.');
      } finally {
        isLoading.value = false;
        operationActive.value = false;
        currentStep.value = 1;
      }
    }
  };
</script>

<template>
  <Card>
    <template #title>AnkiConnect</template>
    <template #content>
      <div v-if="cantConnect" class="text-red-800 dark:text-red-400">
        <p>Couldn't connect to Anki.</p>
        <p>
          Make sure you have the <a href="https://ankiweb.net/shared/info/2055492159" rel="nofollow" target="_blank">Anki Connect plugin</a> installed and
          enabled.
        </p>
        <p>Make sure Anki is running</p>
        <p>
          Go to Anki > Tools > Add-ons > AnkiConnect > Config and add the following line to webCorsOriginList, "https://jiten.moe" so it looks like the
          following screenshot:
        </p>
        <p>
          If you use Brave, please disable Brave Shields for this website. You can do so by clicking on the shield icon at the right of the URL bar.
        </p>
        <img src="/assets/img/ankiconnect.jpg" alt="Anki Connect Config" class="w-full" />
      </div>
      <div v-if="currentStep == 0">
        <p>
          Add words directly from Anki using the <a href="https://ankiweb.net/shared/info/2055492159" rel="nofollow" target="_blank">Anki Connect plugin</a>.
        </p>
        <div class="flex flex-col gap-1 p-4 pb-0 max-w-md">
          <label for="ankiApiKey" class="text-sm text-surface-500">API key (optional)</label>
          <InputText
            v-model="apiKey"
            inputId="ankiApiKey"
            name="ankiApiKey"
            autocomplete="off"
            data-1p-ignore
            data-lpignore="true"
            placeholder="Only if you set an apiKey in AnkiConnect"
            class="w-full"
            @keyup.enter="Connect()"
          />
          <small class="text-surface-500">Leave blank unless you configured an <code>apiKey</code> in AnkiConnect's config.</small>
        </div>
        <div class="p-4">
          <Button label="Connect to Anki" @click="Connect()" />
        </div>
      </div>

      <div v-if="currentStep == 1 && deckEntries.length > 0">
        <p>Select a deck to add words from.</p>
        <Select v-model="selectedDeck" :options="deckEntries" optionLabel="0" optionValue="1" placeholder="Select a deck" class="w-full" />
        <div class="flex flex-row gap-2 p-4">
          <Button label="Next" :disabled="!selectedDeck" @click="NextStep()" />
        </div>
      </div>
      <div v-if="currentStep == 2">
        <p>
          Selected deck: <b>{{ selectedDeckName || '—' }}</b>
        </p>
        <div v-if="isLoading">
          <ProgressSpinner style="width: 50px; height: 50px" stroke-width="8px" animation-duration=".5s" />
          <p>Loading your deck...</p>
        </div>
        <div v-else>
          <p>Select the correct field containing the words WITHOUT furigana</p>
          <Select v-model="selectedField" :options="fieldsOptions" optionLabel="label" optionValue="value" placeholder="Select a field" class="w-full" @change="onSelectionChanged" />
          <p class="mt-4">Optional: select a reading field to disambiguate words that share the same spelling but have different readings.</p>
          <p class="text-sm text-surface-500 mb-1">Full kana or furigana (e.g. <span class="font-noto-sans">下[くだ]さる</span>) both work. The most common word matching the reading is used; if none match, the most common word is kept.</p>
          <Select v-model="selectedReadingField" :options="readingFieldsOptions" optionLabel="label" optionValue="value" placeholder="None (optional)" class="w-full" @change="onSelectionChanged" />
          <div class="flex flex-row gap-2 p-4">
            <Button label="Back" :disabled="!selectedDeck" @click="PreviousStep()" />
            <Button label="Next" @click="NextStep()" />
          </div>
        </div>
      </div>
      <div v-if="currentStep == 3">
        <p>
          This will import up to <b>{{ cardsIds.length }} cards</b>.
        </p>
        <p class="text-sm text-surface-500 mb-4">
          Suspended and empty cards will be skipped during import.
        </p>
        <div class="flex flex-col gap-3 p-4">
          <div class="flex items-center gap-2">
            <Checkbox v-model="importReviewHistory" inputId="importReviewHistory" :binary="true" @change="onSelectionChanged" />
            <label for="importReviewHistory" class="cursor-pointer">
              Import review history
            </label>
          </div>
          <div class="flex items-center gap-2">
            <Checkbox v-model="overwriteExisting" inputId="overwrite" :binary="true" @change="onSelectionChanged" />
            <label for="overwrite" class="cursor-pointer">
              Overwrite existing cards (replace cards you already have with Anki versions, even if they are more recent)
            </label>
          </div>
          <div class="flex items-center gap-2">
            <Checkbox v-model="parseWords" inputId="parseWords" :binary="true" @change="onSelectionChanged" />
            <label for="parseWords" class="cursor-pointer">
              Parse words instead of importing them directly (only use if you have conjugated verbs instead of the dictionary form, less accurate)
            </label>
          </div>
          <div class="flex flex-row gap-2">
            <Button label="Back" :disabled="!selectedDeck" @click="PreviousStep()" />
            <Button label="Import" :disabled="!selectedDeck" @click="NextStep()" />
          </div>
        </div>
      </div>
      <div v-if="currentStep == 4">
        <ProgressSpinner style="width: 50px; height: 50px" stroke-width="8px" animation-duration=".5s" />
        <p v-if="importPhase === 'fetch'" class="font-semibold">Fetching cards from Anki... {{ fetchProgress }}%</p>
        <p v-else class="font-semibold">Uploading to server... {{ uploadProgress }}%</p>
        <p class="text-sm text-surface-500">
          Imported: {{ importResults.imported }} |
          Updated: {{ importResults.updated }} |
          Skipped: {{ importResults.skipped }}
        </p>
      </div>
    </template>
  </Card>

  <Dialog
    v-model:visible="showSkippedDialog"
    modal
    header="Some words could not be imported"
    class="w-[95vw] sm:w-[90vw] md:w-[36rem]"
  >
    <div class="flex flex-col gap-3">
      <Message severity="warn" :closable="false">
        {{ skippedWords.length }} word{{ skippedWords.length === 1 ? '' : 's' }} could not be parsed or {{ skippedWords.length === 1 ? 'was' : 'were' }} not
        found in the dictionary.
      </Message>
      <div class="max-h-[50vh] overflow-y-auto rounded border border-surface-200 dark:border-surface-700 p-3">
        <ul class="flex flex-col gap-1">
          <li v-for="(word, index) in skippedWords" :key="index" class="font-noto-sans">{{ word }}</li>
        </ul>
      </div>
    </div>
    <template #footer>
      <Button label="Close" @click="showSkippedDialog = false" />
    </template>
  </Dialog>

  <Dialog
    v-model:visible="showErrorDialog"
    modal
    header="An error occurred during import"
    class="w-[95vw] sm:w-[90vw] md:w-[36rem]"
  >
    <div class="flex flex-col gap-3">
      <Message severity="error" :closable="false">{{ errorMessage }}</Message>
      <p class="text-sm text-surface-500">Please report these details if you need assistance.</p>
      <details v-if="errorDetail" class="text-sm">
        <summary class="cursor-pointer select-none text-surface-500">Technical details</summary>
        <pre
          class="mt-2 max-h-[40vh] overflow-auto whitespace-pre-wrap break-words rounded border border-surface-200 dark:border-surface-700 p-3 text-xs"
        >{{ errorDetail }}</pre>
      </details>
    </div>
    <template #footer>
      <Button
        :label="errorCopied ? 'Copied' : 'Copy details'"
        :icon="errorCopied ? 'pi pi-check' : 'pi pi-copy'"
        :severity="errorCopied ? 'success' : 'secondary'"
        @click="copyErrorDetails"
      />
      <Button label="Close" @click="showErrorDialog = false" />
    </template>
  </Dialog>
</template>

<style scoped></style>
