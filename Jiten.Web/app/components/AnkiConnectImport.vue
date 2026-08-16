<script setup lang="ts">
  import { ref, computed, onMounted, onUnmounted } from 'vue';
  import { YankiConnect } from 'yanki-connect';
  import { useToast } from 'primevue/usetoast';
  import { useAnkiImportStore, defaultImportSelection } from '~/stores/ankiImportStore';
  import { extractSentenceFromField } from '~/utils/ankiSentenceExtract';
  import { base64ToBytes, extractAudioRef, extractImageRef } from '~/utils/ankiMediaExtract';
  import type { MediaConflictMode, MediaKind } from '~/composables/useAnkiMediaImport';

  const props = withDefaults(defineProps<{ mediaOnly?: boolean }>(), { mediaOnly: false });

  const emit = defineEmits<{
    importComplete: [];
  }>();

  const { $api } = useNuxtApp();
  const toast = useToast();
  const ankiImportStore = useAnkiImportStore();
  const { limits: planLimits, isPlus } = useJitenPlus();

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

  // Closing the tab mid-import abandons the run silently; the browser confirm gives a way out.
  const handleBeforeUnload = (event: BeforeUnloadEvent) => {
    if (currentStep.value !== 4) return;
    event.preventDefault();
  };

  onMounted(() => {
    window.addEventListener('error', handleWindowError);
    window.addEventListener('unhandledrejection', handleRejection);
    window.addEventListener('beforeunload', handleBeforeUnload);
    ankiImportStore.load();
  });
  onUnmounted(() => {
    window.removeEventListener('error', handleWindowError);
    window.removeEventListener('unhandledrejection', handleRejection);
    window.removeEventListener('beforeunload', handleBeforeUnload);
    clearMediaPreview('image');
    clearMediaPreview('audio');
  });

  type KeptReview = { Rating: number; ReviewDateTime: Date; ReviewDuration: number };
  type CardReviews = { kept: KeptReview[]; lastReview: Date | null };
  let selectedFieldName = '';
  let selectedReadingFieldName = '';
  let selectedSentenceFieldName = '';
  let selectedImageFieldName = '';
  let selectedAudioFieldName = '';
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
  async function fetchChunkReviews(chunkCardIds: number[]): Promise<Map<number, CardReviews>> {
    const map = new Map<number, CardReviews>();
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
      // The window MUST start at the card's first review: the optimiser builds its first training entry at
      // deltaT 0 and the SRS replay starts from a New card, so a head-truncated history cannot be replayed.
      map.set(Number(cardIdStr), {
        kept: mapped.length > MAX_REVIEWS_PER_CARD ? mapped.slice(0, MAX_REVIEWS_PER_CARD) : mapped,
        lastReview: mapped[mapped.length - 1]!.ReviewDateTime,
      });
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
  const importPhase = ref<'fetch' | 'upload' | 'sentences' | 'media'>('fetch');
  const importResults = ref({ imported: 0, updated: 0, skipped: 0, reviewLogs: 0 });

  const sentenceImport = useAnkiSentenceImport();
  const showSentenceSummary = ref(false);

  const sentenceSkippedTotal = computed(() => {
    const s = sentenceImport.stats.value;
    return s.duplicate + s.limitReached + s.noHighlight + s.unresolved + s.empty + s.tooLong + s.invalid;
  });

  const mediaImport = useAnkiMediaImport();
  const mediaConflictMode = ref<MediaConflictMode>('skip');
  const showMediaConflicts = ref(false);
  const showMediaSummary = ref(false);
  const conflictsResolved = ref(0);
  const mediaStopping = ref(false);

  const stopMediaImport = () => {
    mediaStopping.value = true;
    mediaImport.cancel();
  };

  const mediaEtaText = computed(() => {
    const seconds = mediaImport.etaSeconds.value;
    if (seconds === null) return '';
    if (seconds < 60) return `${Math.max(5, Math.ceil(seconds / 5) * 5)} seconds`;
    const minutes = Math.ceil(seconds / 60);
    return minutes === 1 ? '1 minute' : `${minutes} minutes`;
  });

  const fetchAnkiMedia = (filename: string) =>
    ankiInvoke('retrieveMediaFile', { filename }) as Promise<string | false>;

  // fetch surfaces transport failures as TypeError; an AnkiConnect action error means Anki is alive
  // and retrying cannot change the answer.
  const isTransportError = (error: unknown) => error instanceof TypeError;

  /**
   * A transport failure during a long run can be the OS out of socket buffers (ERR_NO_BUFFER_SPACE)
   * while Anki is fine — so it only propagates, aborting the run, once a liveness probe fails too.
   */
  const withAnkiAlive = async (call: () => Promise<Array<string | false>>): Promise<Array<string | false>> => {
    for (let attempt = 0; ; attempt++) {
      try {
        return await call();
      } catch (error) {
        if (!isTransportError(error) || attempt >= 2) throw error;
        // The pause gives the OS time to release closed sockets before adding more traffic.
        await new Promise(resolve => setTimeout(resolve, 2000 * (attempt + 1)));
        try {
          await ankiInvoke('version');
        } catch {
          throw error;
        }
      }
    }
  };

  /** One multi call per batch keeps localhost connection churn low; per-file POSTs exhausted socket buffers on large decks. */
  const fetchAnkiMediaBatch = (filenames: string[]) =>
    withAnkiAlive(async () => {
      const entries = await ankiInvoke('multi', {
        actions: filenames.map(filename => ({ action: 'retrieveMediaFile', version: 6, params: { filename } })),
      }) as unknown[];

      return entries.map((entry): string | false => {
        // Versioned sub-actions answer wrapped ({result, error}); older servers answer bare values.
        const value = entry !== null && typeof entry === 'object'
          ? (() => {
              const wrapped = entry as { result?: unknown; error?: unknown };
              if (wrapped.error) throw new Error(String(wrapped.error));
              return wrapped.result;
            })()
          : entry;

        if (typeof value === 'string' || value === false) return value;
        // A malformed response must abort, not count as missing — only an explicit false means absent.
        throw new Error('Unexpected retrieveMediaFile result from AnkiConnect.');
      });
    });

  // The review dialog opens as soon as partitioning finds a conflict, while the rest keeps uploading.
  watch(() => mediaImport.conflicts.value.length, count => {
    if (count > 0 && mediaImport.running.value) showMediaConflicts.value = true;
  });

  const onConflictResolved = (useAnki: boolean) => {
    conflictsResolved.value++;
    mediaImport.resolveConflict(useAnki);
    if (mediaImport.conflicts.value.length === 0) showMediaConflicts.value = false;
  };

  const onConflictResolveAll = (useAnki: boolean) => {
    conflictsResolved.value += mediaImport.conflicts.value.length;
    mediaImport.resolveAllConflicts(useAnki);
    showMediaConflicts.value = false;
  };

  const selectedDeck = ref<number>(0);
  const selectedField = ref<number>(0);
  const selectedReadingField = ref<number>(-1);
  const selectedSentenceField = ref<number>(-1);
  const selectedImageField = ref<number>(-1);
  const selectedAudioField = ref<number>(-1);
  const fields = ref<Array<[string, { order: number; value: string }]>>([]);
  const overwriteExisting = ref(false);
  const parseWords = ref(false);
  const importReviewHistory = ref(true);

  const mediaPreviews = reactive<Record<MediaKind, { url: string; error: string; loading: boolean }>>({
    image: { url: '', error: '', loading: false },
    audio: { url: '', error: '', loading: false },
  });

  // <audio> won't play a typeless blob in every browser, so the type is guessed from the extension.
  const PREVIEW_MIME: Record<string, string> = {
    jpg: 'image/jpeg', jpeg: 'image/jpeg', png: 'image/png', webp: 'image/webp', gif: 'image/gif', avif: 'image/avif', bmp: 'image/bmp',
    mp3: 'audio/mpeg', ogg: 'audio/ogg', opus: 'audio/ogg', wav: 'audio/wav', m4a: 'audio/mp4', aac: 'audio/aac', flac: 'audio/flac', webm: 'audio/webm',
  };

  // The previewed file is the one named in the select's own label: the sample cards' first reference.
  const previewFilename = (kind: MediaKind): string | null => {
    const index = kind === 'image' ? selectedImageField.value : selectedAudioField.value;
    const html = index >= 0 ? fields.value[index]?.[1].value || '' : '';
    if (!html) return null;
    const mediaRef = kind === 'image' ? extractImageRef(html) : extractAudioRef(html);
    return mediaRef?.filename ?? null;
  };

  const imagePreviewAvailable = computed(() => previewFilename('image') !== null);
  const audioPreviewAvailable = computed(() => previewFilename('audio') !== null);

  const clearMediaPreview = (kind: MediaKind) => {
    const preview = mediaPreviews[kind];
    if (preview.url) URL.revokeObjectURL(preview.url);
    preview.url = '';
    preview.error = '';
    preview.loading = false;
  };

  const togglePreview = async (kind: MediaKind) => {
    const preview = mediaPreviews[kind];
    if (preview.url || preview.error) {
      clearMediaPreview(kind);
      return;
    }
    const filename = previewFilename(kind);
    if (!filename || preview.loading) return;

    preview.loading = true;
    try {
      const base64 = await fetchAnkiMedia(filename);
      if (!base64) {
        preview.error = "File not found in Anki's media folder.";
        return;
      }
      const extension = filename.split('.').pop()?.toLowerCase() ?? '';
      const blob = new Blob([base64ToBytes(base64) as BlobPart], { type: PREVIEW_MIME[extension] ?? '' });
      preview.url = URL.createObjectURL(blob);
    } catch {
      preview.error = 'Could not reach Anki.';
    } finally {
      preview.loading = false;
    }
  };

  const onMediaSelectionChanged = (kind: MediaKind) => {
    clearMediaPreview(kind);
    onSelectionChanged();
  };

  watch(currentStep, step => {
    if (step !== 2) {
      clearMediaPreview('image');
      clearMediaPreview('audio');
    }
  });

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
    const fieldNameAt = (index: number) => (index >= 0 ? fields.value[index]?.[0] ?? '' : '');
    ankiImportStore.saveDeckSelection(selectedDeck.value, {
      deckName,
      fieldName,
      readingFieldName,
      sentenceFieldName: fieldNameAt(selectedSentenceField.value),
      imageFieldName: fieldNameAt(selectedImageField.value),
      audioFieldName: fieldNameAt(selectedAudioField.value),
      mediaConflictMode: mediaConflictMode.value,
      importReviewHistory: importReviewHistory.value,
      overwriteExisting: overwriteExisting.value,
      parseWords: parseWords.value,
    });
  };

  // @change on the field/reading Selects and the option Checkboxes: persist this deck's selection
  // immediately on a user change, so it survives any later navigation and never leaks into another deck.
  const onSelectionChanged = () => persistImportSettings();

  // First sample value appears in the select's own label; the rest feed the "view more" lists.
  const SAMPLE_VALUES_PER_FIELD = 4;
  const fieldSamples = ref(new Map<string, string[]>());
  const showWordSamples = ref(false);
  const showReadingSamples = ref(false);
  const showSentenceSamples = ref(false);

  const extraSamplesFor = (index: number, transform: (value: string) => string) => {
    const name = index >= 0 ? fields.value[index]?.[0] : undefined;
    if (!name) return [];
    return (fieldSamples.value.get(name) ?? [])
      .slice(1)
      .map(transform)
      .filter(value => value.length > 0)
      .map(value => (value.length > 80 ? `${value.substring(0, 80)}…` : value));
  };

  const wordExtraSamples = computed(() => extraSamplesFor(selectedField.value, stripRuby));
  const readingExtraSamples = computed(() => extraSamplesFor(selectedReadingField.value, furiganaToReading));
  const sentenceExtraSamples = computed(() => extraSamplesFor(selectedSentenceField.value, html => extractSentenceFromField(html).text));

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

  // Sentence-field options preview the cleaned sentence, since that is what would be stored, and count
  // how much of the sample actually carries one so a mis-mapped field is obvious before importing.
  const sentenceFieldsOptions = computed(() => [
    { label: 'None (optional)', value: -1 },
    ...(fields.value || []).map((entry, idx) => {
      const preview = extractSentenceFromField(entry[1].value || '').text;
      return { label: entry[0] + (preview ? ` (${preview.substring(0, 20)})` : ''), value: idx };
    }),
  ]);

  // Media fields are named after what they reference, so the option shows the referenced filename.
  const imageFieldsOptions = computed(() => [
    { label: 'None (optional)', value: -1 },
    ...(fields.value || []).map((entry, idx) => {
      const ref = extractImageRef(entry[1].value || '');
      return { label: entry[0] + (ref ? ` (${ref.filename.substring(0, 24)})` : ''), value: idx };
    }),
  ]);

  const audioFieldsOptions = computed(() => [
    { label: 'None (optional)', value: -1 },
    ...(fields.value || []).map((entry, idx) => {
      const ref = extractAudioRef(entry[1].value || '');
      return { label: entry[0] + (ref ? ` (${ref.filename.substring(0, 24)})` : ''), value: idx };
    }),
  ]);

  const conflictModeOptions = [
    { label: 'Skip it', value: 'skip' },
    { label: 'Replace it', value: 'replace' },
    { label: 'Ask for each', value: 'ask' },
  ];

  const anyMediaFieldSelected = computed(() => selectedImageField.value >= 0 || selectedAudioField.value >= 0);

  const anyExtraFieldSelected = computed(() => anyMediaFieldSelected.value || selectedSentenceField.value >= 0);

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

  type AnkiCardFields = { fields: Record<string, { value?: string } | undefined> };

  const collectExtras = (card: AnkiCardFields, word: string, reading: string) => {
    if (selectedSentenceFieldName) {
      sentenceImport.collect(word, reading, card.fields[selectedSentenceFieldName]?.value || '');
    }
    if (selectedImageFieldName || selectedAudioFieldName) {
      mediaImport.collect(word, reading,
                          selectedImageFieldName ? card.fields[selectedImageFieldName]?.value || '' : '',
                          selectedAudioFieldName ? card.fields[selectedAudioFieldName]?.value || '' : '');
    }
  };

  // Media-only runs import no scheduling, so suspended and unstudied cards still have media worth taking.
  const collectFromCardUnfiltered = (card: AnkiCardFields) => {
    const word = stripRuby(card.fields[selectedFieldName]?.value?.trim() || '');
    if (!word) return;

    let reading = '';
    if (selectedReadingFieldName) {
      reading = furiganaToReading(card.fields[selectedReadingFieldName]?.value?.trim() || '');
    }

    collectExtras(card, word, reading);
  };

  // Helper to build a single card payload from Anki card info
  const buildCardPayload = (card: any, fieldName: string, readingFieldName: string, reviewsByCard: Map<number, CardReviews>, stats?: SkipStats) => {
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

    // In the combined flow sentences and media share the vocabulary card filter: a card whose word is
    // not imported should not quietly contribute a sentence or a file either.
    collectExtras(card, word, reading);

    const cardReviews = reviewsByCard.get(card.cardId);
    const reviews = cardReviews?.kept ?? [];

    // Convert Anki state to FSRS state
    let state: number;
    if (card.queue === 1 || card.queue === 3) state = 1; // Learning
    else state = 2; // Review

    const stability = card.interval > 0 ? card.interval : 0;
    const difficulty = Math.max(1, Math.min(10, 10 - (card.factor - 1300) / 170.0));

    // Taken from the untruncated history, not the kept window, so a clipped leech is not scheduled from a
    // stale last review with an already-past due date. When review history isn't imported (or a studied
    // card has no logs), fall back to Anki's card modification time so the card still carries a
    // LastReview and stays schedulable, instead of being dropped server-side for having none.
    const mostRecentReview = cardReviews?.lastReview ?? null;
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

  /** Sentence and media import without the vocabulary import, for decks whose words are already in Jiten. */
  const RunExtrasOnly = async () => {
    const chunkSize = supportsFieldsFilter ? 2000 : 500;
    for (let i = 0; i < cardsIds.length; i += chunkSize) {
      const cards = await fetchCardsInfo(cardsIds.slice(i, i + chunkSize));
      for (const card of cards || []) collectFromCardUnfiltered(card);
      fetchProgress.value = Math.round(Math.min(i + chunkSize, cardsIds.length) / cardsIds.length * 100);
    }

    await RunExtras();

    if (sentenceImport.collectedCount() === 0 && mediaImport.collectedCount() === 0) {
      toast.add({
        severity: 'warn',
        summary: 'Nothing to import',
        detail: 'No sentences or media were found in the selected fields.',
        life: 6000,
      });
    }

    emit('importComplete');
  };

  /** The sentence and media phases, shared by both flows. */
  const RunExtras = async () => {
    if (selectedSentenceFieldName && sentenceImport.collectedCount() > 0) {
      importPhase.value = 'sentences';
      try {
        await sentenceImport.run({ parseWords: parseWords.value, source: `Anki: ${selectedDeckName.value}` });
      } catch (error) {
        reportError(error, 'Failed to import example sentences.');
      }
      showSentenceSummary.value = true;
    }

    // Media runs last: it is the slow, quota-bound phase, so cancelling it still keeps everything above.
    if (mediaImport.collectedCount() > 0) {
      importPhase.value = 'media';
      conflictsResolved.value = 0;
      mediaStopping.value = false;
      try {
        await mediaImport.run({
          parseWords: parseWords.value,
          mode: mediaConflictMode.value,
          fetchMedia: fetchAnkiMediaBatch,
        });
        showMediaSummary.value = true;
      } finally {
        // A failed run clears its conflict queue, which would otherwise strand an unclosable dialog.
        showMediaConflicts.value = false;
      }
    }
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
        const prevNameAt = (index: number) => (sameDeckReload && index >= 0 ? fields.value[index]?.[0] ?? '' : '');
        const prevSentenceName = prevNameAt(selectedSentenceField.value);
        const prevImageName = prevNameAt(selectedImageField.value);
        const prevAudioName = prevNameAt(selectedAudioField.value);

        selectedField.value = 0;
        selectedReadingField.value = -1;
        selectedSentenceField.value = -1;
        selectedImageField.value = -1;
        selectedAudioField.value = -1;
        if (previewCards && previewCards.length > 0) {
          // Merge across the sample: keep each field's first non-empty value so every field shows a preview.
          const merged = new Map<string, { order: number; value: string }>();
          const samples = new Map<string, string[]>();
          for (const c of previewCards) {
            for (const [name, info] of Object.entries(c.fields || {}) as Array<[string, { order: number; value: string }]>) {
              const existing = merged.get(name);
              if (!existing) merged.set(name, { order: info.order, value: info.value || '' });
              else if (!existing.value && info.value) existing.value = info.value;
              if (info.value) {
                const list = samples.get(name) ?? [];
                if (list.length < SAMPLE_VALUES_PER_FIELD) {
                  list.push(info.value);
                  samples.set(name, list);
                }
              }
            }
          }
          fields.value = [...merged.entries()].sort((a, b) => a[1].order - b[1].order);
          fieldSamples.value = samples;
        } else {
          fields.value = [];
          fieldSamples.value = new Map();
        }
        showWordSamples.value = false;
        showReadingSamples.value = false;
        showSentenceSamples.value = false;
        lastLoadedDeckId = selectedDeck.value;

        // Re-apply selections by NAME (the index was just rebuilt). A same-deck reload keeps the
        // in-session selection. Switching to a different deck restores THAT deck's own saved config —
        // fields AND options — but only when it is the EXACT same deck (entry keyed by this id AND the
        // stored name matches); otherwise it starts from defaults so the previous deck can't leak in.
        let wantFieldName = prevFieldName;
        let wantReadingName = prevReadingName;
        let wantSentenceName = prevSentenceName;
        let wantImageName = prevImageName;
        let wantAudioName = prevAudioName;
        if (!sameDeckReload) {
          const saved = ankiImportStore.resolveDeckSelection(selectedDeck.value, selectedDeckName.value) ?? defaultImportSelection();
          wantFieldName = saved.fieldName || undefined;
          wantReadingName = saved.readingFieldName;
          wantSentenceName = saved.sentenceFieldName;
          wantImageName = saved.imageFieldName;
          wantAudioName = saved.audioFieldName;
          mediaConflictMode.value = saved.mediaConflictMode;
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
        if (wantSentenceName) {
          const sentenceIdx = fields.value.findIndex(([name]) => name === wantSentenceName);
          if (sentenceIdx >= 0) selectedSentenceField.value = sentenceIdx;
        }
        if (wantImageName) {
          const imageIdx = fields.value.findIndex(([name]) => name === wantImageName);
          if (imageIdx >= 0) selectedImageField.value = imageIdx;
        }
        if (wantAudioName) {
          const audioIdx = fields.value.findIndex(([name]) => name === wantAudioName);
          if (audioIdx >= 0) selectedAudioField.value = audioIdx;
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
      const nameAt = (index: number) => (index >= 0 ? fields.value[index]?.[0] ?? '' : '');
      selectedSentenceFieldName = nameAt(selectedSentenceField.value);
      selectedImageFieldName = nameAt(selectedImageField.value);
      selectedAudioFieldName = nameAt(selectedAudioField.value);
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
      showSentenceSummary.value = false;
      sentenceImport.reset();
      mediaImport.reset();

      const allSkippedWords: string[] = [];
      let skippedCountNoReviews = 0;
      const skipStats: SkipStats = { suspended: 0, newCard: 0, missingField: 0, emptyWord: 0 };

      try {
        if (props.mediaOnly) {
          await RunExtrasOnly();
          return;
        }

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
        const reviewsForChunk = async (chunkIds: number[]): Promise<Map<number, CardReviews>> => {
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

        // These run after the vocabulary import so their words are tracked by the time they land.
        await RunExtras();

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
      <Message
        v-if="showSentenceSummary"
        :severity="sentenceImport.stats.value.imported > 0 ? 'success' : 'warn'"
        closable
        class="mb-4"
        @close="showSentenceSummary = false"
      >
        {{ sentenceImport.stats.value.imported }} example sentence{{ sentenceImport.stats.value.imported === 1 ? '' : 's' }} imported<template
          v-if="sentenceSkippedTotal > 0"
        >, {{ sentenceSkippedTotal }} skipped</template>.
      </Message>
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
        <p v-if="mediaOnly">
          Add example sentences, images and audio from Anki using the
          <a href="https://ankiweb.net/shared/info/2055492159" rel="nofollow" target="_blank">Anki Connect plugin</a>. Your vocabulary is left alone.
        </p>
        <p v-else>
          Add words directly from Anki using the <a href="https://ankiweb.net/shared/info/2055492159" rel="nofollow" target="_blank">Anki Connect plugin</a>.
        </p>
        <div class="flex flex-col gap-1 p-4 pb-0 max-w-md">
          <label for="ankiApiKey" class="text-sm text-surface-500 dark:text-surface-400">API key (optional)</label>
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
          <small class="text-surface-500 dark:text-surface-400">Leave blank unless you configured an <code>apiKey</code> in AnkiConnect's config.</small>
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
        <div v-else class="flex flex-col gap-6 mt-2">
          <section class="flex flex-col gap-3">
            <h3 class="text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">Vocabulary</h3>
            <div class="grid grid-cols-1 md:grid-cols-2 gap-4">
              <div class="flex flex-col gap-1">
                <label class="text-sm font-medium">Word field</label>
                <Select v-model="selectedField" :options="fieldsOptions" option-label="label" option-value="value" placeholder="Select a field" class="w-full" @change="onSelectionChanged" />
                <small class="text-surface-500 dark:text-surface-400">The target word, sometimes named expression.</small>
                <button
                  v-if="wordExtraSamples.length > 0"
                  type="button"
                  class="self-start text-xs text-surface-500 dark:text-surface-400 underline underline-offset-2 hover:text-surface-700 dark:hover:text-surface-300"
                  @click="showWordSamples = !showWordSamples"
                >
                  {{ showWordSamples ? 'Hide examples' : 'View more examples' }}
                </button>
                <ul v-if="showWordSamples" class="flex flex-col gap-0.5 text-sm text-surface-500 dark:text-surface-400 font-noto-sans">
                  <li v-for="(sample, index) in wordExtraSamples" :key="index">{{ sample }}</li>
                </ul>
              </div>
              <div class="flex flex-col gap-1">
                <label class="text-sm font-medium">Reading field <span class="font-normal text-surface-400">· optional</span></label>
                <Select v-model="selectedReadingField" :options="readingFieldsOptions" option-label="label" option-value="value" placeholder="None (optional)" class="w-full" @change="onSelectionChanged" />
                <small class="text-surface-500 dark:text-surface-400">Used to tell apart words with the same spelling. Can be with full kana or furigana <br/> (<span class="font-noto-sans">下[くだ]さる</span>).</small>
                <button
                  v-if="readingExtraSamples.length > 0"
                  type="button"
                  class="self-start text-xs text-surface-500 dark:text-surface-400 underline underline-offset-2 hover:text-surface-700 dark:hover:text-surface-300"
                  @click="showReadingSamples = !showReadingSamples"
                >
                  {{ showReadingSamples ? 'Hide examples' : 'View more examples' }}
                </button>
                <ul v-if="showReadingSamples" class="flex flex-col gap-0.5 text-sm text-surface-500 dark:text-surface-400 font-noto-sans">
                  <li v-for="(sample, index) in readingExtraSamples" :key="index">{{ sample }}</li>
                </ul>
              </div>
            </div>
          </section>

          <section class="flex flex-col gap-1">
            <h3 class="text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400 mb-2">Example sentences <span class="font-normal normal-case tracking-normal text-surface-400">· optional</span></h3>
            <Select v-model="selectedSentenceField" :options="sentenceFieldsOptions" option-label="label" option-value="value" placeholder="None (optional)" class="w-full" @change="onSelectionChanged" />
            <small class="text-surface-500 dark:text-surface-400">
              The studied word will be highlighted automatically. Words that are bolded in your note type will keep that highlight. Sentences where it can't be found
              will be skipped.
            </small>
            <button
              v-if="sentenceExtraSamples.length > 0"
              type="button"
              class="self-start text-xs text-surface-500 dark:text-surface-400 underline underline-offset-2 hover:text-surface-700 dark:hover:text-surface-300"
              @click="showSentenceSamples = !showSentenceSamples"
            >
              {{ showSentenceSamples ? 'Hide examples' : 'View more examples' }}
            </button>
            <ul v-if="showSentenceSamples" class="flex flex-col gap-0.5 text-sm text-surface-500 dark:text-surface-400 font-noto-sans">
              <li v-for="(sample, index) in sentenceExtraSamples" :key="index">{{ sample }}</li>
            </ul>
          </section>

          <JitenPlusGate feature="card-media" feature-label="Card media import">
            <section class="flex flex-col gap-3">
              <h3 class="text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">Images &amp; audio <span class="font-normal normal-case tracking-normal text-surface-400">· optional</span></h3>
              <div class="grid grid-cols-1 md:grid-cols-2 gap-4">
                <div class="flex flex-col gap-1">
                  <label class="text-sm font-medium">Image field</label>
                  <Select v-model="selectedImageField" :options="imageFieldsOptions" option-label="label" option-value="value" placeholder="None (optional)" class="w-full" @change="onMediaSelectionChanged('image')" />
                  <div v-if="imagePreviewAvailable" class="flex flex-col items-start gap-1">
                    <Button
                      :label="mediaPreviews.image.url || mediaPreviews.image.error ? 'Hide preview' : 'Preview'"
                      :icon="mediaPreviews.image.url || mediaPreviews.image.error ? 'pi pi-eye-slash' : 'pi pi-eye'"
                      size="small"
                      text
                      :loading="mediaPreviews.image.loading"
                      @click="togglePreview('image')"
                    />
                    <p v-if="mediaPreviews.image.error" class="text-sm text-red-600 dark:text-red-400">{{ mediaPreviews.image.error }}</p>
                    <img
                      v-if="mediaPreviews.image.url"
                      :src="mediaPreviews.image.url"
                      alt="Card image preview"
                      class="max-h-48 max-w-full rounded border border-surface-200 dark:border-surface-700"
                    >
                  </div>
                </div>
                <div class="flex flex-col gap-1">
                  <label class="text-sm font-medium">Audio field</label>
                  <Select v-model="selectedAudioField" :options="audioFieldsOptions" option-label="label" option-value="value" placeholder="None (optional)" class="w-full" @change="onMediaSelectionChanged('audio')" />
                  <div v-if="audioPreviewAvailable" class="flex flex-col items-start gap-1">
                    <Button
                      :label="mediaPreviews.audio.url || mediaPreviews.audio.error ? 'Hide preview' : 'Preview'"
                      :icon="mediaPreviews.audio.url || mediaPreviews.audio.error ? 'pi pi-eye-slash' : 'pi pi-eye'"
                      size="small"
                      text
                      :loading="mediaPreviews.audio.loading"
                      @click="togglePreview('audio')"
                    />
                    <p v-if="mediaPreviews.audio.error" class="text-sm text-red-600 dark:text-red-400">{{ mediaPreviews.audio.error }}</p>
                    <audio v-if="mediaPreviews.audio.url" :src="mediaPreviews.audio.url" controls class="w-full max-w-xs" />
                  </div>
                </div>
              </div>
              <small class="text-surface-500 dark:text-surface-400">Files are read straight from your Anki media folder. Images are recompressed and count towards your card media storage.</small>
            </section>
          </JitenPlusGate>

          <div class="flex flex-col gap-2">
            <div class="flex flex-row gap-2">
              <Button label="Back" :disabled="!selectedDeck" @click="PreviousStep()" />
              <Button label="Next" :disabled="mediaOnly && !anyExtraFieldSelected" @click="NextStep()" />
            </div>
            <p v-if="mediaOnly && !anyExtraFieldSelected" class="text-sm text-surface-500 dark:text-surface-400">
              Select a sentence, image or audio field to continue.
            </p>
          </div>
        </div>
      </div>
      <div v-if="currentStep == 3">
        <p>
          This will import up to <b>{{ cardsIds.length }} cards</b>.
        </p>
        <p v-if="mediaOnly" class="text-sm text-surface-500 dark:text-surface-400 mb-4">
          Only sentences and media are imported; your vocabulary and review history are untouched.
        </p>
        <p v-else class="text-sm text-surface-500 dark:text-surface-400 mb-4">
          Suspended and cards with no history will be skipped during import.
        </p>
        <Message v-if="selectedSentenceField >= 0" severity="info" :closable="false" class="mb-4">
          Adds up to {{ planLimits.customSentencesPerWord }} example sentences per word{{ isPlus ? '' : ` (${planLimits.plus.customSentencesPerWord} with Jiten+)` }};
          duplicates are skipped. Your own sentences will be displayed in priority on study cards.
        </Message>
        <div v-if="anyMediaFieldSelected" class="flex flex-col gap-2 px-4 pt-2">
          <label class="text-sm font-semibold">When a card already has media in Jiten</label>
          <SelectButton
            v-model="mediaConflictMode"
            :options="conflictModeOptions"
            option-label="label"
            option-value="value"
            :allow-empty="false"
            @change="onSelectionChanged"
          />
        </div>
        <div class="flex flex-col gap-3 p-4">
          <div v-if="!mediaOnly" class="flex items-center gap-2">
            <Checkbox v-model="importReviewHistory" inputId="importReviewHistory" :binary="true" @change="onSelectionChanged" />
            <label for="importReviewHistory" class="cursor-pointer">
              Import review history
            </label>
          </div>
          <div v-if="!mediaOnly" class="flex items-center gap-2">
            <Checkbox v-model="overwriteExisting" inputId="overwrite" :binary="true" @change="onSelectionChanged" />
            <label for="overwrite" class="cursor-pointer">
              Update words you already track (adds Anki's review history to Jiten's without removing anything; whichever side you reviewed more recently sets your next review date)
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
            <Button label="Import" :disabled="!selectedDeck || (mediaOnly && !anyExtraFieldSelected)" @click="NextStep()" />
          </div>
        </div>
      </div>
      <div v-if="currentStep == 4">
        <ProgressSpinner
          v-if="importPhase === 'fetch' || importPhase === 'upload'"
          style="width: 50px; height: 50px"
          stroke-width="8px"
          animation-duration=".5s"
        />
        <p v-if="importPhase === 'fetch'" class="font-semibold">Fetching cards from Anki... {{ fetchProgress }}%</p>
        <p v-else-if="importPhase === 'upload'" class="font-semibold">Uploading to server... {{ uploadProgress }}%</p>
        <template v-else-if="importPhase === 'sentences'">
          <p class="font-semibold">Importing example sentences... {{ sentenceImport.done.value }}/{{ sentenceImport.total.value }}</p>
          <ProgressBar
            :value="sentenceImport.total.value > 0 ? Math.round((sentenceImport.done.value / sentenceImport.total.value) * 100) : 0"
            class="my-2 max-w-md"
          />
        </template>
        <template v-else>
          <p class="font-semibold">Importing card media... {{ mediaImport.done.value }}/{{ mediaImport.total.value }} files</p>
          <ProgressBar
            :value="mediaImport.total.value > 0 ? Math.round((mediaImport.done.value / mediaImport.total.value) * 100) : 0"
            class="my-2 max-w-md"
          />
          <p class="text-sm text-surface-500 dark:text-surface-400">
            <span v-if="mediaEtaText">About {{ mediaEtaText }} remaining · </span>Please keep this tab open until the import finishes.
          </p>
          <p v-if="mediaImport.maxBytes.value > 0" class="text-sm text-surface-500 dark:text-surface-400">
            Storage used: {{ formatBytes(mediaImport.usedBytes.value) }} / {{ formatBytes(mediaImport.maxBytes.value) }}
          </p>
          <p class="text-sm text-surface-500 dark:text-surface-400">
            Uploaded: {{ mediaImport.stats.value.uploaded + mediaImport.stats.value.replaced }} |
            Skipped: {{
              mediaImport.stats.value.skippedExisting + mediaImport.stats.value.missingInAnki + mediaImport.stats.value.tooLarge
              + mediaImport.stats.value.invalid + mediaImport.stats.value.notTracked + mediaImport.stats.value.uploadFailed
            }}
          </p>
          <Button
            class="mt-3"
            :label="mediaStopping ? 'Stopping...' : 'Stop import'"
            icon="pi pi-times"
            severity="danger"
            outlined
            size="small"
            :disabled="mediaStopping"
            @click="stopMediaImport"
          />
        </template>
        <p v-if="!mediaOnly && (importPhase === 'fetch' || importPhase === 'upload')" class="text-sm text-surface-500 dark:text-surface-400">
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

  <AnkiMediaConflictDialog
    v-model:visible="showMediaConflicts"
    :conflicts="mediaImport.conflicts.value"
    :resolved="conflictsResolved"
    :fetch-media="fetchAnkiMedia"
    @resolve="onConflictResolved"
    @resolve-all="onConflictResolveAll"
  />

  <Dialog
    v-model:visible="showMediaSummary"
    modal
    header="Card media imported"
    class="w-[95vw] sm:w-[90vw] md:w-[36rem]"
  >
    <div class="flex flex-col gap-3">
      <Message :severity="mediaImport.stats.value.uploaded + mediaImport.stats.value.replaced > 0 ? 'success' : 'warn'" :closable="false">
        {{ mediaImport.stats.value.uploaded }} file{{ mediaImport.stats.value.uploaded === 1 ? '' : 's' }} imported<span
          v-if="mediaImport.stats.value.replaced > 0"
        >, {{ mediaImport.stats.value.replaced }} replaced</span>.
      </Message>
      <Message v-if="mediaImport.stats.value.quotaExceeded > 0" severity="error" :closable="false">
        Your card media storage is full, so the rest was not imported.
        <NuxtLink to="/settings/card-media" class="underline">Manage card media</NuxtLink>
      </Message>
      <p v-if="mediaImport.maxBytes.value > 0" class="text-sm text-surface-500 dark:text-surface-400">
        Storage used: {{ formatBytes(mediaImport.usedBytes.value) }} / {{ formatBytes(mediaImport.maxBytes.value) }}
      </p>
      <ul class="flex flex-col gap-1 text-sm">
        <li v-if="mediaImport.stats.value.skippedExisting > 0">{{ mediaImport.stats.value.skippedExisting }} kept the media already in Jiten</li>
        <li v-if="mediaImport.stats.value.missingInAnki > 0">
          {{ mediaImport.stats.value.missingInAnki }} skipped — missing from your Anki media folder
        </li>
        <li v-if="mediaImport.stats.value.unresolved > 0">{{ mediaImport.stats.value.unresolved }} skipped — word not in the dictionary</li>
        <li v-if="mediaImport.stats.value.notTracked > 0">{{ mediaImport.stats.value.notTracked }} skipped — word not in your collection</li>
        <li v-if="mediaImport.stats.value.tooLarge > 0">{{ mediaImport.stats.value.tooLarge }} skipped — larger than 5 MB</li>
        <li v-if="mediaImport.stats.value.invalid > 0">{{ mediaImport.stats.value.invalid }} skipped — unsupported file type</li>
        <li v-if="mediaImport.stats.value.uploadFailed > 0">
          {{ mediaImport.stats.value.uploadFailed }} failed — the storage service was unavailable; run the import again to retry them
        </li>
        <li v-if="mediaImport.stats.value.duplicateTarget > 0">
          {{ mediaImport.stats.value.duplicateTarget }} skipped — another card already supplied that word's media
        </li>
        <li v-if="mediaImport.stats.value.extraRefsIgnored > 0">
          {{ mediaImport.stats.value.extraRefsIgnored }} extra reference{{ mediaImport.stats.value.extraRefsIgnored === 1 ? '' : 's' }} ignored (only
          the first per card is used)
        </li>
      </ul>
    </div>
    <template #footer>
      <Button label="Close" @click="showMediaSummary = false" />
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
      <p class="text-sm text-surface-500 dark:text-surface-400">Please report these details if you need assistance.</p>
      <details v-if="errorDetail" class="text-sm">
        <summary class="cursor-pointer select-none text-surface-500 dark:text-surface-400">Technical details</summary>
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
