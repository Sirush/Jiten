<script setup lang="ts">
  import { ref, computed, onMounted, onUnmounted } from 'vue';
  import { YankiConnect } from 'yanki-connect';
  import { useToast } from 'primevue/usetoast';

  const emit = defineEmits<{
    importComplete: [];
  }>();

  const { $api } = useNuxtApp();
  const toast = useToast();

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
  });
  onUnmounted(() => {
    window.removeEventListener('error', handleWindowError);
    window.removeEventListener('unhandledrejection', handleRejection);
  });

  let reviewByCard: Map<number, Array<{ Rating: number; ReviewDateTime: Date; ReviewDuration: number }>> = new Map();
  let selectedFieldName = '';
  let supportsFieldsFilter = false;

  const cardsInfoFields = ['cardId', 'due', 'queue', 'type', 'interval', 'factor', 'reps', 'lapses', 'mod', 'flags', 'fields', 'modelName', 'deckName'];

  const stripRuby = (text: string) => text.replace(/\[.*?\]/g, '');

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

  const fetchProgress = ref(0);
  const uploadProgress = ref(0);
  const importPhase = ref<'fetch' | 'upload'>('fetch');
  const importResults = ref({ imported: 0, updated: 0, skipped: 0, reviewLogs: 0 });

  const selectedDeck = ref<number>(0);
  const selectedField = ref<number>(0);
  const fields = ref<Array<[string, { order: number; value: string }]>>([]);
  const overwriteExisting = ref(false);
  const parseWords = ref(false);
  const importReviewHistory = ref(true);

  const fieldsOptions = computed(() =>
    (fields.value || []).map((entry, idx) => ({
      label: entry[0] + (entry[1].value ? ` (${stripRuby(entry[1].value).substring(0, 20)})` : ''),
      value: idx,
    }))
  );

  const Connect = async () => {
    operationActive.value = true;
    try {
      client = new YankiConnect(apiKey.value ? { key: apiKey.value } : {});
      decks = await client.deck.deckNamesAndIds();
      deckEntries = Object.entries(decks);
      cantConnect.value = false;

      try {
        await ankiInvoke('cardsInfo', { cards: [], fields: cardsInfoFields });
        supportsFieldsFilter = true;
      } catch {
        supportsFieldsFilter = false;
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
  const buildCardPayload = (card: any, fieldName: string, stats?: SkipStats) => {
    if (card.queue === -1) { if (stats) stats.suspended++; return null; } // suspended
    if (card.queue === 0) { if (stats) stats.newCard++; return null; } // new/forgotten

    const field = card.fields[fieldName];
    if (field === undefined && stats) stats.missingField++; // selected field absent on this note type
    const word = stripRuby(field?.value?.trim() || '');
    if (!word) { if (stats && field !== undefined) stats.emptyWord++; return null; }

    const reviews = reviewByCard.get(card.cardId) ?? [];

    // Convert Anki state to FSRS state
    let state: number;
    if (card.queue === 1 || card.queue === 3) state = 1; // Learning
    else state = 2; // Review

    const stability = card.interval > 0 ? card.interval : 0;
    const difficulty = Math.max(1, Math.min(10, 10 - (card.factor - 1300) / 170.0));

    const lastReview = reviews.length > 0 ? reviews[0].ReviewDateTime : null;

    let due: Date;
    if (card.queue === 1 || card.queue === 3) {
      due = new Date(card.due * 1000);
    } else {
      if (lastReview) {
        due = new Date(lastReview.getTime() + card.interval * 86400000);
      } else {
        due = new Date(card.mod * 1000 + card.interval * 86400000);
      }
    }

    return {
      Card: {
        Word: word,
        Stability: stability,
        Difficulty: difficulty,
        Reps: card.reps,
        Lapses: card.lapses,
        Due: due.toISOString(),
        State: state,
        LastReview: lastReview?.toISOString(),
      },
      ReviewLogs: reviews.map((r) => ({
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
        const deckName = deckEntries.find(([_, id]) => id === selectedDeck.value)?.[0];
        const query = deckName ? `deck:"${deckName.replace(/["*_\\]/g, '\\$&')}"` : `did:${selectedDeck.value}`;
        cardsIds = await client.card.findCards({ query });
        const previewCards = await fetchCardsInfo([cardsIds[0]]);
        selectedField.value = 0;
        if (previewCards && previewCards.length > 0) {
          fields.value = Object.entries(previewCards[0].fields || {});
        } else {
          fields.value = [];
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
      // No API calls - all heavy work is deferred to Step 4
    }

    if (currentStep.value == 4) {
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
        // Fetch reviews only if requested. cardReviews filters on the exact deck only, so to cover
        // subdecks we query the selected deck plus every descendant (Anki encodes hierarchy as
        // "Parent::Child" deck names). This mirrors the recursive deck:"Name" card search above.
        reviewByCard = new Map();
        if (importReviewHistory.value) {
          const selectedName = deckEntries.find(([_, id]) => id === selectedDeck.value)?.[0];
          if (selectedName) {
            const deckNames = deckEntries
              .map(([name]) => name)
              .filter((name) => name === selectedName || name.startsWith(selectedName + '::'));

            for (const name of deckNames) {
              const reviews = await client.statistic.cardReviews({ deck: name, startID: 1 });

              for (const [reviewTime, cardID, _usn, buttonPressed, _newInterval, _previousInterval, _newFactor, reviewDuration, _reviewType] of reviews) {
                const existing = reviewByCard.get(cardID) ?? [];
                existing.push({
                  Rating: buttonPressed,
                  ReviewDateTime: new Date(reviewTime),
                  ReviewDuration: reviewDuration,
                });
                reviewByCard.set(cardID, existing);
              }

              // Clear raw array immediately to help GC
              reviews.length = 0;
            }

            // Sort and limit to 10 most recent reviews per card
            for (const [key, arr] of reviewByCard.entries()) {
              arr.sort((a, b) => b.ReviewDateTime.getTime() - a.ReviewDateTime.getTime());
              reviewByCard.set(key, arr.slice(0, 10));
            }
          }
        }

        const ankiChunkSize = supportsFieldsFilter ? 2000 : 500;
        const chunks: number[][] = [];
        for (let i = 0; i < cardsIds.length; i += ankiChunkSize) {
          chunks.push(cardsIds.slice(i, i + ankiChunkSize));
        }

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
          // Optimized path: fetch all in parallel, dedup, upload in parallel
          let completedFetches = 0;
          const allChunkCards = await Promise.all(
            chunks.map(async (chunkIds) => {
              const cards = await fetchCardsInfo(chunkIds);
              completedFetches++;
              fetchProgress.value = Math.round((completedFetches / chunks.length) * 100);
              return cards;
            }),
          );

          const seenWords = new Set<string>();
          const allPayloads: any[] = [];
          for (const chunkCards of allChunkCards) {
            for (const card of chunkCards || []) {
              const payload = buildCardPayload(card, selectedFieldName, skipStats);
              if (!payload) continue;
              if (seenWords.has(payload.Card.Word)) continue;
              seenWords.add(payload.Card.Word);
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
            fetchProgress.value = Math.round(((i + 1) / chunks.length) * 100);

            const chunkPayload: any[] = [];
            for (const card of chunkCards || []) {
              const payload = buildCardPayload(card, selectedFieldName, skipStats);
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

        if (allSkippedWords.length > 0) {
          skippedWords.value = allSkippedWords;
          showSkippedDialog.value = true;
        }

        // Notify parent to refresh vocabulary counts
        emit('importComplete');

        // Clear review map to free memory
        reviewByCard = new Map();
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
          Selected deck: <b>{{ deckEntries.find((d) => d[1] === selectedDeck)?.[0] || '—' }}</b>
        </p>
        <div v-if="isLoading">
          <ProgressSpinner style="width: 50px; height: 50px" stroke-width="8px" animation-duration=".5s" />
          <p>Loading your deck...</p>
        </div>
        <div v-else>
          <p>Select the correct field containing the words WITHOUT furigana</p>
          <Select v-model="selectedField" :options="fieldsOptions" optionLabel="label" optionValue="value" placeholder="Select a field" class="w-full" />
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
            <Checkbox v-model="importReviewHistory" inputId="importReviewHistory" :binary="true" />
            <label for="importReviewHistory" class="cursor-pointer">
              Import review history (uncheck for large decks with memory issues)
            </label>
          </div>
          <div class="flex items-center gap-2">
            <Checkbox v-model="overwriteExisting" inputId="overwrite" :binary="true" />
            <label for="overwrite" class="cursor-pointer">
              Overwrite existing cards (replace cards you already have with Anki versions, even if they are more recent)
            </label>
          </div>
          <div class="flex items-center gap-2">
            <Checkbox v-model="parseWords" inputId="parseWords" :binary="true" />
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
