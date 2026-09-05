<script setup lang="ts">
  import { ref, computed } from 'vue';
  import Card from 'primevue/card';
  import Button from 'primevue/button';
  import DataTable from 'primevue/datatable';
  import Column from 'primevue/column';
  import Select from 'primevue/select';
  import DatePicker from 'primevue/datepicker';
  import InputNumber from 'primevue/inputnumber';
  import Toast from 'primevue/toast';
  import { useConfirm } from 'primevue/useconfirm';
  import { useToast } from 'primevue/usetoast';
  import { MediaType } from '~/types/enums';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { formatBytes } from '~/utils/formatBytes';

  interface CardMediaRetained {
    count: number;
    oldBytes: number;
    newBytes: number;
  }

  interface CardMediaPreview {
    totalCount: number;
    totalBytes: number;
    byContentType: { contentType: string; count: number; bytes: number }[];
    items: {
      mediaId: number;
      wordId: number;
      readingIndex: number;
      word: string | null;
      contentType: string;
      fileSizeBytes: number;
      createdAt: string;
    }[];
    truncated: boolean;
    retained: CardMediaRetained | null;
  }

  interface WordReplacementResult {
    deckWordsUpdated: number;
    deckWordsMerged: number;
    exampleSentenceWordsUpdated: number;
    fsrsCardsUpdated: number;
    fsrsCardsSkipped: number;
    affectedDeckCount: number;
    parentDecksQueued: number;
    wasDryRun: boolean;
  }

  interface SplitWordResult {
    deckWordsDeleted: number;
    deckWordsInserted: number;
    deckWordsMerged: number;
    exampleSentenceWordsDeleted: number;
    exampleSentenceWordsInserted: number;
    affectedDeckCount: number;
    parentDecksQueued: number;
    wasDryRun: boolean;
  }

  interface RemoveWordResult {
    deckWordsDeleted: number;
    exampleSentenceWordsDeleted: number;
    affectedDeckCount: number;
    parentDecksQueued: number;
    wasDryRun: boolean;
  }

  interface WordReadingPair {
    wordId: number | null;
    readingIndex: number;
  }

  useHead({
    title: 'Meta Administration - Jiten',
  });

  definePageMeta({
    middleware: ['auth'],
  });

  const { $api } = useNuxtApp();
  const toast = useToast();

  const confirm = useConfirm();
  const selectedMediaType = ref<MediaType | null>(null);
  const selectedCutoffDate = ref<Date | null>(null);
  const isLoading = ref({
    reparse: false,
    reparseBeforeDate: false,
    reparseBySize: false,
    frequencies: false,
    popularity: false,
    coverages: false,
    accomplishments: false,
    kanjiGrids: false,
    difficulties: false,
    difficultyVotes: false,
    speechSpeed: false,
    reviewRollupBackfill: false,
    wordReplacementPreview: false,
    wordReplacementExecute: false,
    splitWordPreview: false,
    splitWordExecute: false,
    removeWordPreview: false,
    removeWordExecute: false,
    cardMediaPreview: false,
    cardMediaRun: false,
    cardMediaRollback: false,
    cardMediaDiscard: false,
  });

  const wordReplacement = ref({
    oldWordId: null as number | null,
    oldReadingIndex: 0,
    newWordId: null as number | null,
    newReadingIndex: 0,
  });
  const wordReplacementResult = ref<WordReplacementResult | null>(null);

  const splitWord = ref({
    oldWordId: null as number | null,
    oldReadingIndex: 0,
    newWords: [
      { wordId: null, readingIndex: 0 },
      { wordId: null, readingIndex: 0 },
    ] as WordReadingPair[],
  });
  const splitWordResult = ref<SplitWordResult | null>(null);

  const removeWord = ref({
    wordId: null as number | null,
    readingIndex: 0,
  });
  const removeWordResult = ref<RemoveWordResult | null>(null);

  const mediaTypes = Object.values(MediaType)
    .filter((value) => typeof value === 'number')
    .map((value) => ({
      value: value as MediaType,
      label: getMediaTypeText(value as MediaType),
    }));

  const confirmReparse = () => {
    if (!selectedMediaType.value) {
      return;
    }

    confirm.require({
      message: `Are you sure you want to reparse all media of type "${getMediaTypeText(selectedMediaType.value as MediaType)}"? This operation may take a long time.`,
      header: 'Confirmation',
      icon: 'pi pi-exclamation-triangle',
      acceptClass: 'p-button-primary',
      rejectClass: 'p-button-secondary',
      accept: () => reparseMedia(),
      reject: () => {},
    });
  };

  const reparseMedia = async () => {
    if (!selectedMediaType.value) {
      return;
    }

    try {
      isLoading.value.reparse = true;
      const data = await $api(`/admin/reparse-media-by-type/${selectedMediaType.value}`, {
        method: 'POST',
      });

      toast.add({
        severity: 'success',
        summary: 'Success',
        detail: `Reparsing ${data.count} media items of type ${getMediaTypeText(selectedMediaType.value as MediaType)}`,
        life: 5000,
      });
    } catch (error) {
      toast.add({
        severity: 'error',
        summary: 'Error',
        detail: 'Failed to reparse media',
        life: 5000,
      });
      console.error('Error reparsing media:', error);
    } finally {
      isLoading.value.reparse = false;
    }
  };

  const confirmReparseBeforeDate = () => {
    if (!selectedCutoffDate.value) {
      return;
    }

    confirm.require({
      message: `Are you sure you want to reparse all decks updated before "${selectedCutoffDate.value.toLocaleString()}"? This operation may take a long time.`,
      header: 'Confirmation',
      icon: 'pi pi-exclamation-triangle',
      acceptClass: 'p-button-primary',
      rejectClass: 'p-button-secondary',
      accept: () => reparseBeforeDate(),
      reject: () => {},
    });
  };

  const reparseBeforeDate = async () => {
    if (!selectedCutoffDate.value) {
      return;
    }

    try {
      isLoading.value.reparseBeforeDate = true;
      const data = await $api(`/admin/reparse-decks-before-date`, {
        method: 'POST',
        body: JSON.stringify(selectedCutoffDate.value.toISOString()),
        headers: {
          'Content-Type': 'application/json',
        },
      });

      toast.add({
        severity: 'success',
        summary: 'Success',
        detail: `Reparsing ${data.count} decks updated before ${selectedCutoffDate.value.toLocaleString()}`,
        life: 5000,
      });
    } catch (error) {
      toast.add({
        severity: 'error',
        summary: 'Error',
        detail: 'Failed to reparse decks',
        life: 5000,
      });
      console.error('Error reparsing decks:', error);
    } finally {
      isLoading.value.reparseBeforeDate = false;
    }
  };

  const confirmRecomputeFrequencies = () => {
    confirm.require({
      message: 'Are you sure you want to recompute all word frequencies? This operation may take a long time.',
      header: 'Confirmation',
      icon: 'pi pi-exclamation-triangle',
      acceptClass: 'p-button-primary',
      rejectClass: 'p-button-secondary',
      accept: () => recomputeFrequencies(),
      reject: () => {},
    });
  };

  const confirmRecomputeKanjiFrequencies = () => {
    confirm.require({
      message: 'Are you sure you want to recompute all kanji frequencies? This operation may take a long time.',
      header: 'Confirmation',
      icon: 'pi pi-exclamation-triangle',
      acceptClass: 'p-button-primary',
      rejectClass: 'p-button-secondary',
      accept: () => recomputeKanjiFrequencies(),
      reject: () => {},
    });
  };

  const recomputePopularity = async () => {
    try {
      isLoading.value.popularity = true;
      await $api('/admin/recompute-popularity', { method: 'POST' });
      toast.add({ severity: 'success', summary: 'Success', detail: 'Popularity recompute job has been queued', life: 5000 });
    } catch (error) {
      toast.add({ severity: 'error', summary: 'Error', detail: 'Failed to queue popularity recompute', life: 5000 });
      console.error('Error recomputing popularity:', error);
    } finally {
      isLoading.value.popularity = false;
    }
  };

  const recomputeFrequencies = async () => {
    try {
      isLoading.value.frequencies = true;
      const data = await $api('/admin/recompute-frequencies', {
        method: 'POST',
      });

      toast.add({
        severity: 'success',
        summary: 'Success',
        detail: 'Recomputing frequencies job has been queued',
        life: 5000,
      });
    } catch (error) {
      toast.add({
        severity: 'error',
        summary: 'Error',
        detail: 'Failed to recompute frequencies',
        life: 5000,
      });
      console.error('Error recomputing frequencies:', error);
    } finally {
      isLoading.value.frequencies = false;
    }
  };

  const recomputeKanjiFrequencies = async () => {
    try {
      isLoading.value.frequencies = true;
      const data = await $api('/admin/recompute-kanji-frequencies', {
        method: 'POST',
      });

      toast.add({
        severity: 'success',
        summary: 'Success',
        detail: 'Recomputing kanji frequencies job has been queued',
        life: 5000,
      });
    } catch (error) {
      toast.add({
        severity: 'error',
        summary: 'Error',
        detail: 'Failed to recompute kanji frequencies',
        life: 5000,
      });
      console.error('Error recomputing kanji frequencies:', error);
    } finally {
      isLoading.value.frequencies = false;
    }
  };

  const confirmReparseBySize = () => {
    confirm.require({
      message: 'Are you sure you want to reparse ALL decks? This will process smallest decks first. This operation may take a very long time.',
      header: 'Confirmation',
      icon: 'pi pi-exclamation-triangle',
      acceptClass: 'p-button-primary',
      rejectClass: 'p-button-secondary',
      accept: () => reparseBySize(),
      reject: () => {},
    });
  };

  const reparseBySize = async () => {
    try {
      isLoading.value.reparseBySize = true;
      const data = await $api<{ count: number }>('/admin/reparse-all-by-size', {
        method: 'POST',
      });

      toast.add({
        severity: 'success',
        summary: 'Success',
        detail: `Queued ${data.count} decks for reparsing (smallest to largest)`,
        life: 5000,
      });
    } catch (error) {
      toast.add({
        severity: 'error',
        summary: 'Error',
        detail: 'Failed to queue reparse jobs',
        life: 5000,
      });
      console.error('Error reparsing by size:', error);
    } finally {
      isLoading.value.reparseBySize = false;
    }
  };

  const confirmRecomputeCoverages = () => {
    confirm.require({
      message: 'Are you sure you want to recompute coverages for all users? This operation may take a long time.',
      header: 'Confirmation',
      icon: 'pi pi-exclamation-triangle',
      acceptClass: 'p-button-primary',
      rejectClass: 'p-button-secondary',
      accept: () => recomputeCoverages(),
      reject: () => {},
    });
  };

  const recomputeCoverages = async () => {
    try {
      isLoading.value.coverages = true;
      const data = await $api('/admin/recompute-coverages', {
        method: 'POST',
      });

      toast.add({
        severity: 'success',
        summary: 'Success',
        detail: 'Recomputing coverages job has been queued',
        life: 5000,
      });
    } catch (error) {
      toast.add({
        severity: 'error',
        summary: 'Error',
        detail: 'Failed to recompute coverages',
        life: 5000,
      });
      console.error('Error recomputing coverages:', error);
    } finally {
      isLoading.value.coverages = false;
    }
  };

  const confirmRecomputeAccomplishments = () => {
    confirm.require({
      message: 'Are you sure you want to recompute accomplishments for all users? This operation may take a long time.',
      header: 'Confirmation',
      icon: 'pi pi-exclamation-triangle',
      acceptClass: 'p-button-primary',
      rejectClass: 'p-button-secondary',
      accept: () => recomputeAccomplishments(),
      reject: () => {},
    });
  };

  const recomputeAccomplishments = async () => {
    try {
      isLoading.value.accomplishments = true;
      const data = await $api<{ count: number }>('/admin/recompute-accomplishments', {
        method: 'POST',
      });

      toast.add({
        severity: 'success',
        summary: 'Success',
        detail: `Recomputing accomplishments for ${data.count} users has been queued`,
        life: 5000,
      });
    } catch (error) {
      toast.add({
        severity: 'error',
        summary: 'Error',
        detail: 'Failed to recompute accomplishments',
        life: 5000,
      });
      console.error('Error recomputing accomplishments:', error);
    } finally {
      isLoading.value.accomplishments = false;
    }
  };

  const confirmRecomputeKanjiGrids = () => {
    confirm.require({
      message: 'Are you sure you want to recompute kanji grids for all users? This operation may take a long time.',
      header: 'Confirmation',
      icon: 'pi pi-exclamation-triangle',
      acceptClass: 'p-button-primary',
      rejectClass: 'p-button-secondary',
      accept: () => recomputeKanjiGrids(),
      reject: () => {},
    });
  };

  const recomputeKanjiGrids = async () => {
    try {
      isLoading.value.kanjiGrids = true;
      const data = await $api<{ count: number }>('/admin/recompute-kanji-grids', {
        method: 'POST',
      });

      toast.add({
        severity: 'success',
        summary: 'Success',
        detail: `Recomputing kanji grids for ${data.count} users has been queued`,
        life: 5000,
      });
    } catch (error) {
      toast.add({
        severity: 'error',
        summary: 'Error',
        detail: 'Failed to recompute kanji grids',
        life: 5000,
      });
      console.error('Error recomputing kanji grids:', error);
    } finally {
      isLoading.value.kanjiGrids = false;
    }
  };

  const fetchMissingMetadata = async () => {
    try {
      const data = await $api(`/admin/fetch-all-missing-metadata`, {
        method: 'POST',
      });

      toast.add({
        severity: 'success',
        summary: 'Success',
        detail: `Fetching missing metadata for ${data.count} decks`,
        life: 5000,
      });
    } catch (error) {
      toast.add({
        severity: 'error',
        summary: 'Error',
        detail: 'Failed to fetch metadata',
        life: 5000,
      });
      console.error('Error fetching metadata:', error);
    }
  };

  const backfillReviewRollup = async () => {
    isLoading.value.reviewRollupBackfill = true;
    try {
      await $api(`/admin/review-rollup/backfill`, {
        method: 'POST',
      });

      toast.add({
        severity: 'success',
        summary: 'Success',
        detail: 'Review rollup backfill queued',
        life: 5000,
      });
    } catch (error) {
      toast.add({
        severity: 'error',
        summary: 'Error',
        detail: 'Failed to queue review rollup backfill',
        life: 5000,
      });
      console.error('Error queueing review rollup backfill:', error);
    } finally {
      isLoading.value.reviewRollupBackfill = false;
    }
  };

  const cardMediaPreview = ref<CardMediaPreview | null>(null);

  const loadCardMediaPreview = async () => {
    isLoading.value.cardMediaPreview = true;
    try {
      cardMediaPreview.value = await $api<CardMediaPreview>('/admin/card-media/renormalize/preview?take=50');
    } catch (error) {
      toast.add({ severity: 'error', summary: 'Error', detail: 'Failed to load card media preview', life: 5000 });
      console.error('Error loading card media preview:', error);
    } finally {
      isLoading.value.cardMediaPreview = false;
    }
  };

  const runCardMediaRenormalize = async (dryRun: boolean) => {
    isLoading.value.cardMediaRun = true;
    try {
      await $api(`/admin/card-media/renormalize?dryRun=${dryRun}`, { method: 'POST' });
      toast.add({
        severity: 'success',
        summary: 'Queued',
        detail: dryRun ? 'Dry run queued. Check the API logs for the measured saving.' : 'Renormalize queued. Originals are kept until you discard them.',
        life: 6000,
      });
    } catch (error) {
      toast.add({ severity: 'error', summary: 'Error', detail: 'Failed to queue renormalize', life: 5000 });
      console.error('Error queueing card media renormalize:', error);
    } finally {
      isLoading.value.cardMediaRun = false;
    }
  };

  const confirmCardMediaRenormalize = () => {
    confirm.require({
      message:
        `Rewrite ${cardMediaPreview.value?.totalCount ?? 0} card media files? ` +
        'Each one is re-encoded to a new path; the original file stays on the CDN and the change can be rolled back.',
      header: 'Run renormalize',
      icon: 'pi pi-exclamation-triangle',
      acceptClass: 'p-button-primary',
      rejectClass: 'p-button-secondary',
      accept: () => runCardMediaRenormalize(false),
      reject: () => {},
    });
  };

  const rollbackCardMedia = async () => {
    isLoading.value.cardMediaRollback = true;
    try {
      const data = await $api<{ eligible: number }>('/admin/card-media/renormalize/rollback', {
        method: 'POST',
      });
      toast.add({
        severity: 'success',
        summary: 'Queued',
        detail: `Restoring ${data.eligible} originals`,
        life: 6000,
      });
    } catch (error) {
      toast.add({ severity: 'error', summary: 'Error', detail: 'Failed to roll back', life: 5000 });
      console.error('Error rolling back card media renormalize:', error);
    } finally {
      isLoading.value.cardMediaRollback = false;
    }
  };

  const confirmCardMediaRollback = () => {
    confirm.require({
      message: 'Point every rewritten card back at its original file? The originals were never deleted, so this is safe.',
      header: 'Roll back renormalize',
      icon: 'pi pi-undo',
      acceptClass: 'p-button-primary',
      rejectClass: 'p-button-secondary',
      accept: () => rollbackCardMedia(),
      reject: () => {},
    });
  };

  const discardCardMediaOriginals = async () => {
    isLoading.value.cardMediaDiscard = true;
    try {
      const data = await $api<{ eligible: number }>('/admin/card-media/renormalize/discard-originals?confirm=true', {
        method: 'POST',
      });
      toast.add({
        severity: 'success',
        summary: 'Queued',
        detail: `Deleting ${data.eligible} superseded files`,
        life: 6000,
      });
    } catch (error) {
      toast.add({ severity: 'error', summary: 'Error', detail: 'Failed to queue discard', life: 5000 });
      console.error('Error discarding card media originals:', error);
    } finally {
      isLoading.value.cardMediaDiscard = false;
    }
  };

  const confirmCardMediaDiscard = () => {
    confirm.require({
      message: `Permanently delete ${cardMediaPreview.value?.retained?.count ?? 0} superseded original files? ` + 'Rollback will no longer be possible.',
      header: 'Discard originals',
      icon: 'pi pi-exclamation-triangle',
      acceptClass: 'p-button-danger',
      rejectClass: 'p-button-secondary',
      accept: () => discardCardMediaOriginals(),
      reject: () => {},
    });
  };

  const flushRedisCache = async () => {
    try {
      const data = await $api(`/admin/flush-redis-cache`, {
        method: 'POST',
      });

      toast.add({
        severity: 'success',
        summary: 'Success',
        detail: `FLUSHALL done`,
        life: 5000,
      });
    } catch (error) {
      toast.add({
        severity: 'error',
        summary: 'Error',
        detail: 'Failed to FLUSHALL',
        life: 5000,
      });
      console.error('Error with FLUSHALL:', error);
    }
  };

  const recomputeAllDeckStats = async () => {
    try {
      const data = await $api(`/admin/recompute-all-deck-stats`, {
        method: 'POST',
      });

      toast.add({
        severity: 'success',
        summary: 'Success',
        detail: `All deck stats recomputation queued`,
        life: 5000,
      });
    } catch (error) {
      toast.add({
        severity: 'error',
        summary: 'Error',
        detail: 'Error with deck stats recomputation',
        life: 5000,
      });
      console.error('Error with deck stats recomputation:', error);
    }
  };

  const recomputeParentSpeechSpeed = async () => {
    try {
      isLoading.value.speechSpeed = true;
      const data = await $api<{ count: number }>('/admin/recompute-parent-speech-speed', {
        method: 'POST',
      });

      toast.add({
        severity: 'success',
        summary: 'Success',
        detail: `Recomputed speech speed for ${data.count} parent decks`,
        life: 5000,
      });
    } catch (error) {
      toast.add({
        severity: 'error',
        summary: 'Error',
        detail: 'Failed to recompute parent speech speed',
        life: 5000,
      });
      console.error('Error recomputing parent speech speed:', error);
    } finally {
      isLoading.value.speechSpeed = false;
    }
  };

  const confirmReaggregateParentDifficulties = () => {
    confirm.require({
      message: 'Are you sure you want to reaggregate parent difficulties from their children? This does not recompute children via the external API.',
      header: 'Confirmation',
      icon: 'pi pi-exclamation-triangle',
      acceptClass: 'p-button-primary',
      rejectClass: 'p-button-secondary',
      accept: () => reaggregateParentDifficulties(),
      reject: () => {},
    });
  };

  const reaggregateParentDifficulties = async () => {
    try {
      isLoading.value.difficulties = true;
      const data = await $api<{ count: number }>('/admin/reaggregate-parent-difficulties', {
        method: 'POST',
      });

      toast.add({
        severity: 'success',
        summary: 'Success',
        detail: `Queued difficulty reaggregation for ${data.count} parent decks`,
        life: 5000,
      });
    } catch (error) {
      toast.add({
        severity: 'error',
        summary: 'Error',
        detail: 'Failed to queue difficulty reaggregation',
        life: 5000,
      });
      console.error('Error reaggregating difficulties:', error);
    } finally {
      isLoading.value.difficulties = false;
    }
  };

  const recomputeDifficultyVotes = async () => {
    try {
      isLoading.value.difficultyVotes = true;
      await $api('/admin/difficulty-votes/recompute', {
        method: 'POST',
      });

      toast.add({
        severity: 'success',
        summary: 'Success',
        detail: 'Queued difficulty vote recomputation',
        life: 5000,
      });
    } catch (error) {
      toast.add({
        severity: 'error',
        summary: 'Error',
        detail: 'Failed to queue difficulty vote recomputation',
        life: 5000,
      });
      console.error('Error recomputing difficulty votes:', error);
    } finally {
      isLoading.value.difficultyVotes = false;
    }
  };

  const canPreviewWordReplacement = computed(() => {
    return (
      wordReplacement.value.oldWordId !== null &&
      wordReplacement.value.newWordId !== null &&
      !(wordReplacement.value.oldWordId === wordReplacement.value.newWordId && wordReplacement.value.oldReadingIndex === wordReplacement.value.newReadingIndex)
    );
  });

  const previewWordReplacement = async () => {
    if (!canPreviewWordReplacement.value) return;

    try {
      isLoading.value.wordReplacementPreview = true;
      wordReplacementResult.value = null;

      const data = await $api<WordReplacementResult>('/admin/replace-word-reading', {
        method: 'POST',
        body: {
          oldWordId: wordReplacement.value.oldWordId,
          oldReadingIndex: wordReplacement.value.oldReadingIndex,
          newWordId: wordReplacement.value.newWordId,
          newReadingIndex: wordReplacement.value.newReadingIndex,
          dryRun: true,
        },
      });

      wordReplacementResult.value = data;

      toast.add({
        severity: 'info',
        summary: 'Preview Complete',
        detail: `Found ${data.affectedDeckCount} affected decks`,
        life: 5000,
      });
    } catch (error) {
      toast.add({
        severity: 'error',
        summary: 'Error',
        detail: 'Failed to preview word replacement',
        life: 5000,
      });
      console.error('Error previewing word replacement:', error);
    } finally {
      isLoading.value.wordReplacementPreview = false;
    }
  };

  const confirmWordReplacement = () => {
    if (!wordReplacementResult.value) return;

    const r = wordReplacementResult.value;
    confirm.require({
      message:
        `This will update ${r.deckWordsUpdated} deck words, merge ${r.deckWordsMerged} entries, ` +
        `update ${r.exampleSentenceWordsUpdated} example sentences, and modify ${r.fsrsCardsUpdated} user vocabulary entries ` +
        `across ${r.affectedDeckCount} decks. Continue?`,
      header: 'Confirm Word Replacement',
      icon: 'pi pi-exclamation-triangle',
      acceptClass: 'p-button-danger',
      rejectClass: 'p-button-secondary',
      accept: () => executeWordReplacement(),
      reject: () => {},
    });
  };

  const executeWordReplacement = async () => {
    if (!canPreviewWordReplacement.value) return;

    try {
      isLoading.value.wordReplacementExecute = true;

      const data = await $api<WordReplacementResult>('/admin/replace-word-reading', {
        method: 'POST',
        body: {
          oldWordId: wordReplacement.value.oldWordId,
          oldReadingIndex: wordReplacement.value.oldReadingIndex,
          newWordId: wordReplacement.value.newWordId,
          newReadingIndex: wordReplacement.value.newReadingIndex,
          dryRun: false,
        },
      });

      wordReplacementResult.value = null;
      wordReplacement.value = {
        oldWordId: null,
        oldReadingIndex: 0,
        newWordId: null,
        newReadingIndex: 0,
      };

      toast.add({
        severity: 'success',
        summary: 'Replacement Complete',
        detail:
          `Updated ${data.deckWordsUpdated} deck words, merged ${data.deckWordsMerged} entries. ` +
          `${data.parentDecksQueued} parent decks queued for recalculation.`,
        life: 10000,
      });
    } catch (error) {
      toast.add({
        severity: 'error',
        summary: 'Error',
        detail: 'Failed to execute word replacement',
        life: 5000,
      });
      console.error('Error executing word replacement:', error);
    } finally {
      isLoading.value.wordReplacementExecute = false;
    }
  };

  const clearWordReplacementResult = () => {
    wordReplacementResult.value = null;
  };

  // Split Word functions
  const canPreviewSplitWord = computed(() => {
    return splitWord.value.oldWordId !== null && splitWord.value.newWords.length >= 2 && splitWord.value.newWords.every((w) => w.wordId !== null);
  });

  const addNewWord = () => {
    splitWord.value.newWords.push({ wordId: null, readingIndex: 0 });
    clearSplitWordResult();
  };

  const removeNewWord = (index: number) => {
    if (splitWord.value.newWords.length > 2) {
      splitWord.value.newWords.splice(index, 1);
      clearSplitWordResult();
    }
  };

  const clearSplitWordResult = () => {
    splitWordResult.value = null;
  };

  const previewSplitWord = async () => {
    if (!canPreviewSplitWord.value) return;

    try {
      isLoading.value.splitWordPreview = true;
      splitWordResult.value = null;

      const data = await $api<SplitWordResult>('/admin/split-word', {
        method: 'POST',
        body: {
          oldWordId: splitWord.value.oldWordId,
          oldReadingIndex: splitWord.value.oldReadingIndex,
          newWords: splitWord.value.newWords.map((w) => ({
            wordId: w.wordId,
            readingIndex: w.readingIndex,
          })),
          dryRun: true,
        },
      });

      splitWordResult.value = data;

      toast.add({
        severity: 'info',
        summary: 'Preview Complete',
        detail: `Found ${data.affectedDeckCount} affected decks`,
        life: 5000,
      });
    } catch (error) {
      toast.add({
        severity: 'error',
        summary: 'Error',
        detail: 'Failed to preview word split',
        life: 5000,
      });
      console.error('Error previewing word split:', error);
    } finally {
      isLoading.value.splitWordPreview = false;
    }
  };

  const confirmSplitWord = () => {
    if (!splitWordResult.value) return;

    const r = splitWordResult.value;
    confirm.require({
      message:
        `This will delete ${r.deckWordsDeleted} deck word entries and insert ${r.deckWordsInserted} new entries ` +
        `(${r.deckWordsMerged} merged). Example sentences: ${r.exampleSentenceWordsDeleted} deleted, ` +
        `${r.exampleSentenceWordsInserted} inserted. Affects ${r.affectedDeckCount} decks. Continue?`,
      header: 'Confirm Word Split',
      icon: 'pi pi-exclamation-triangle',
      acceptClass: 'p-button-danger',
      rejectClass: 'p-button-secondary',
      accept: () => executeSplitWord(),
      reject: () => {},
    });
  };

  const executeSplitWord = async () => {
    if (!canPreviewSplitWord.value) return;

    try {
      isLoading.value.splitWordExecute = true;

      const data = await $api<SplitWordResult>('/admin/split-word', {
        method: 'POST',
        body: {
          oldWordId: splitWord.value.oldWordId,
          oldReadingIndex: splitWord.value.oldReadingIndex,
          newWords: splitWord.value.newWords.map((w) => ({
            wordId: w.wordId,
            readingIndex: w.readingIndex,
          })),
          dryRun: false,
        },
      });

      splitWordResult.value = null;
      splitWord.value = {
        oldWordId: null,
        oldReadingIndex: 0,
        newWords: [
          { wordId: null, readingIndex: 0 },
          { wordId: null, readingIndex: 0 },
        ],
      };

      toast.add({
        severity: 'success',
        summary: 'Split Complete',
        detail:
          `Deleted ${data.deckWordsDeleted}, inserted ${data.deckWordsInserted} deck words. ` +
          `${data.parentDecksQueued} parent decks queued for recalculation.`,
        life: 10000,
      });
    } catch (error) {
      toast.add({
        severity: 'error',
        summary: 'Error',
        detail: 'Failed to execute word split',
        life: 5000,
      });
      console.error('Error executing word split:', error);
    } finally {
      isLoading.value.splitWordExecute = false;
    }
  };

  // Remove Word functions
  const canPreviewRemoveWord = computed(() => {
    return removeWord.value.wordId !== null;
  });

  const clearRemoveWordResult = () => {
    removeWordResult.value = null;
  };

  const previewRemoveWord = async () => {
    if (!canPreviewRemoveWord.value) return;

    try {
      isLoading.value.removeWordPreview = true;
      removeWordResult.value = null;

      const data = await $api<RemoveWordResult>('/admin/remove-word', {
        method: 'POST',
        body: {
          wordId: removeWord.value.wordId,
          readingIndex: removeWord.value.readingIndex,
          dryRun: true,
        },
      });

      removeWordResult.value = data;

      toast.add({
        severity: 'info',
        summary: 'Preview Complete',
        detail: `Found ${data.affectedDeckCount} affected decks`,
        life: 5000,
      });
    } catch (error) {
      toast.add({
        severity: 'error',
        summary: 'Error',
        detail: 'Failed to preview word removal',
        life: 5000,
      });
      console.error('Error previewing word removal:', error);
    } finally {
      isLoading.value.removeWordPreview = false;
    }
  };

  const confirmRemoveWord = () => {
    if (!removeWordResult.value) return;

    const r = removeWordResult.value;
    confirm.require({
      message:
        `WARNING: This will permanently delete ${r.deckWordsDeleted} deck words, ` +
        `${r.exampleSentenceWordsDeleted} example sentence entries, and ${r.fsrsCardsDeleted} user vocabulary entries ` +
        `from ${r.affectedDeckCount} decks. This cannot be undone. Continue?`,
      header: 'Confirm Word Removal',
      icon: 'pi pi-exclamation-triangle',
      acceptClass: 'p-button-danger',
      rejectClass: 'p-button-secondary',
      accept: () => executeRemoveWord(),
      reject: () => {},
    });
  };

  const executeRemoveWord = async () => {
    if (!canPreviewRemoveWord.value) return;

    try {
      isLoading.value.removeWordExecute = true;

      const data = await $api<RemoveWordResult>('/admin/remove-word', {
        method: 'POST',
        body: {
          wordId: removeWord.value.wordId,
          readingIndex: removeWord.value.readingIndex,
          dryRun: false,
        },
      });

      removeWordResult.value = null;
      removeWord.value = {
        wordId: null,
        readingIndex: 0,
      };

      toast.add({
        severity: 'success',
        summary: 'Removal Complete',
        detail:
          `Deleted ${data.deckWordsDeleted} deck words, ${data.exampleSentenceWordsDeleted} example sentences, ` +
          `${data.fsrsCardsDeleted} user vocab entries. ${data.parentDecksQueued} parent decks queued.`,
        life: 10000,
      });
    } catch (error) {
      toast.add({
        severity: 'error',
        summary: 'Error',
        detail: 'Failed to execute word removal',
        life: 5000,
      });
      console.error('Error executing word removal:', error);
    } finally {
      isLoading.value.removeWordExecute = false;
    }
  };
</script>

<template>
  <div class="container mx-auto p-4">
    <div class="flex items-center mb-6">
      <Button icon="pi pi-arrow-left" class="p-button-text mr-2" @click="navigateTo('/dashboard')" />
      <h1 class="text-3xl font-bold">Data Management</h1>
    </div>

    <div class="grid grid-cols-1 gap-6">
      <Card class="shadow-md">
        <template #title>Reparse Media</template>
        <template #content>
          <p class="mb-4">Reparse all media of the selected type</p>
          <div class="mb-4">
            <label for="mediaType" class="block text-sm font-medium mb-1">Media Type</label>
            <Select
              id="mediaType"
              v-model="selectedMediaType"
              :options="mediaTypes"
              option-label="label"
              option-value="value"
              placeholder="Select Media Type"
              class="w-full"
            />
          </div>

          <div class="flex justify-center">
            <Button
              label="Reparse All Media of This Type"
              icon="pi pi-refresh"
              class="p-button-warning"
              :disabled="!selectedMediaType || isLoading.reparse"
              :loading="isLoading.reparse"
              @click="confirmReparse"
            />
          </div>
        </template>
      </Card>

      <Card class="shadow-md">
        <template #title>Reparse All Before Date</template>
        <template #content>
          <p class="mb-4">Reparse all decks where the last update is before the specified date/time.</p>
          <div class="mb-4">
            <label for="cutoffDate" class="block text-sm font-medium mb-1">Cutoff Date</label>
            <DatePicker id="cutoffDate" v-model="selectedCutoffDate" show-time hour-format="24" placeholder="Select date and time" class="w-full" />
          </div>

          <div class="flex justify-center">
            <Button
              label="Reparse All Before This Date"
              icon="pi pi-refresh"
              class="p-button-warning"
              :disabled="!selectedCutoffDate || isLoading.reparseBeforeDate"
              :loading="isLoading.reparseBeforeDate"
              @click="confirmReparseBeforeDate"
            />
          </div>
        </template>
      </Card>

      <Card class="shadow-md">
        <template #title>Reparse All (By Size)</template>
        <template #content>
          <p class="mb-4">Reparse all decks, processing smallest decks first and largest decks last.</p>

          <div class="flex justify-center">
            <Button
              label="Reparse All (Smallest First)"
              icon="pi pi-sort-amount-up"
              class="p-button-warning"
              :disabled="isLoading.reparseBySize"
              :loading="isLoading.reparseBySize"
              @click="confirmReparseBySize"
            />
          </div>
        </template>
      </Card>

      <Card class="shadow-md">
        <template #title>Recompute Frequencies</template>
        <template #content>
          <p class="mb-4">Recompute all vocabulary frequencies.</p>

          <div class="flex justify-center">
            <Button
              label="Recompute Frequencies"
              icon="pi pi-chart-bar"
              class="p-button-warning"
              :disabled="isLoading.frequencies"
              :loading="isLoading.frequencies"
              @click="confirmRecomputeFrequencies"
            />
          </div>
        </template>
      </Card>

      <Card class="shadow-md">
        <template #title>Recompute Kanji Frequencies</template>
        <template #content>
          <p class="mb-4">Recompute kanji frequencies.</p>

          <div class="flex justify-center">
            <Button
              label="Recompute Kanji Frequencies"
              icon="pi pi-chart-bar"
              class="p-button-warning"
              :disabled="isLoading.frequencies"
              :loading="isLoading.frequencies"
              @click="confirmRecomputeKanjiFrequencies"
            />
          </div>
        </template>
      </Card>

      <Card class="shadow-md">
        <template #title>Recompute Popularity</template>
        <template #content>
          <p class="mb-4">Rebuild the popularity score behind the default deck sort. Runs nightly on its own.</p>

          <div class="flex justify-center">
            <Button
              label="Recompute Popularity"
              icon="pi pi-sort-amount-down"
              class="p-button-warning"
              :disabled="isLoading.popularity"
              :loading="isLoading.popularity"
              @click="recomputePopularity"
            />
          </div>
        </template>
      </Card>

      <Card class="shadow-md">
        <template #title>Recompute Coverages</template>
        <template #content>
          <p class="mb-4">Recompute coverage for all users.</p>

          <div class="flex justify-center">
            <Button
              label="Recompute Coverages"
              icon="pi pi-users"
              class="p-button-warning"
              :disabled="isLoading.coverages"
              :loading="isLoading.coverages"
              @click="confirmRecomputeCoverages"
            />
          </div>
        </template>
      </Card>

      <Card class="shadow-md">
        <template #title>Recompute Accomplishments</template>
        <template #content>
          <p class="mb-4">Recompute accomplishments for all users.</p>

          <div class="flex justify-center">
            <Button
              label="Recompute Accomplishments"
              icon="pi pi-trophy"
              class="p-button-warning"
              :disabled="isLoading.accomplishments"
              :loading="isLoading.accomplishments"
              @click="confirmRecomputeAccomplishments"
            />
          </div>
        </template>
      </Card>

      <Card class="shadow-md">
        <template #title>Recompute Kanji Grids</template>
        <template #content>
          <p class="mb-4">Recompute kanji grids for all users without recalculating coverage.</p>

          <div class="flex justify-center">
            <Button
              label="Recompute Kanji Grids"
              icon="pi pi-th-large"
              class="p-button-warning"
              :disabled="isLoading.kanjiGrids"
              :loading="isLoading.kanjiGrids"
              @click="confirmRecomputeKanjiGrids"
            />
          </div>
        </template>
      </Card>

      <Card class="shadow-md">
        <template #title>Fetch Missing Metadata</template>
        <template #content>
          <p class="mb-4">Check all decks for missing metadata (release date, description) and fetch them in the appropriate APIs.</p>

          <div class="flex justify-center">
            <Button label="Fetch Missing Metadata" icon="pi pi-table" class="p-button-warning" @click="fetchMissingMetadata" />
          </div>
        </template>
      </Card>

      <Card class="shadow-md">
        <template #title>Recompute ALL decks advanced stats</template>
        <template #content>
          <p class="mb-4">Recompute all decks advanced stats (coverage curve, etc).</p>

          <div class="flex justify-center">
            <Button label="Recompute ALL decks advanced stats" icon="pi pi-table" class="p-button-warning" @click="recomputeAllDeckStats" />
          </div>
        </template>
      </Card>

      <Card class="shadow-md">
        <template #title>Reaggregate Parent Difficulties</template>
        <template #content>
          <p class="mb-4">Reaggregate difficulty scores for parent decks from their children. Does not call the external API.</p>

          <div class="flex justify-center">
            <Button
              label="Reaggregate Parent Difficulties"
              icon="pi pi-calculator"
              class="p-button-warning"
              :disabled="isLoading.difficulties"
              :loading="isLoading.difficulties"
              @click="confirmReaggregateParentDifficulties"
            />
          </div>
        </template>
      </Card>

      <Card class="shadow-md">
        <template #title>Recompute Difficulty Votes</template>
        <template #content>
          <p class="mb-4">Recompute all user difficulty vote adjustments (Bradley-Terry model).</p>

          <div class="flex justify-center">
            <Button
              label="Recompute Difficulty Votes"
              icon="pi pi-chart-bar"
              class="p-button-warning"
              :disabled="isLoading.difficultyVotes"
              :loading="isLoading.difficultyVotes"
              @click="recomputeDifficultyVotes"
            />
          </div>
        </template>
      </Card>

      <Card class="shadow-md">
        <template #title>Recompute Parent Speech Speed</template>
        <template #content>
          <p class="mb-4">Recompute speech speed for all parent decks by averaging their children's speeds.</p>

          <div class="flex justify-center">
            <Button
              label="Recompute Parent Speech Speed"
              icon="pi pi-volume-up"
              class="p-button-warning"
              :disabled="isLoading.speechSpeed"
              :loading="isLoading.speechSpeed"
              @click="recomputeParentSpeechSpeed"
            />
          </div>
        </template>
      </Card>

      <Card class="shadow-md">
        <template #title>Backfill Review Rollup</template>
        <template #content>
          <p class="mb-4">
            Build the review activity rollup for every user with history. Stats endpoints fall back to the log-join query until a user's rebuild lands, so this
            is safe to run at any time.
          </p>

          <div class="flex justify-center">
            <Button
              label="Backfill Review Rollup"
              icon="pi pi-history"
              class="p-button-warning"
              :disabled="isLoading.reviewRollupBackfill"
              :loading="isLoading.reviewRollupBackfill"
              @click="backfillReviewRollup"
            />
          </div>
        </template>
      </Card>

      <Card class="shadow-md">
        <template #title>Renormalize Card Media</template>
        <template #content>
          <p class="mb-4">
            Re-runs upload-time normalization over card media images that were stored unprocessed (anything not <code>image/webp</code>). Each file is
            re-encoded to a new path and the original is left on the CDN, so the rewrite can be rolled back until you discard the originals.
          </p>

          <div class="flex flex-wrap justify-center gap-2 mb-4">
            <Button
              label="Preview Affected"
              icon="pi pi-search"
              class="p-button-secondary"
              :disabled="isLoading.cardMediaPreview"
              :loading="isLoading.cardMediaPreview"
              @click="loadCardMediaPreview"
            />
            <Button
              label="Dry Run"
              icon="pi pi-calculator"
              class="p-button-secondary"
              :disabled="isLoading.cardMediaRun"
              :loading="isLoading.cardMediaRun"
              @click="runCardMediaRenormalize(true)"
            />
            <Button
              label="Run Renormalize"
              icon="pi pi-refresh"
              class="p-button-warning"
              :disabled="isLoading.cardMediaRun || !cardMediaPreview?.totalCount"
              :loading="isLoading.cardMediaRun"
              @click="confirmCardMediaRenormalize"
            />
          </div>

          <div v-if="cardMediaPreview" class="mb-4 p-4 bg-surface-100 dark:bg-surface-800 rounded-lg">
            <h4 class="font-semibold mb-2">
              {{ cardMediaPreview.totalCount.toLocaleString() }} unprocessed files ·
              {{ formatBytes(cardMediaPreview.totalBytes) }}
            </h4>
            <ul class="text-sm">
              <li v-for="group in cardMediaPreview.byContentType" :key="group.contentType">
                <strong>{{ group.contentType }}:</strong>
                {{ group.count.toLocaleString() }} · {{ formatBytes(group.bytes) }}
              </li>
            </ul>

            <div v-if="cardMediaPreview.retained" class="mt-3 pt-3 border-t border-surface-300 dark:border-surface-600 text-sm">
              <p>
                {{ cardMediaPreview.retained.count.toLocaleString() }} rewritten files still hold their original:
                {{ formatBytes(cardMediaPreview.retained.oldBytes) }} → {{ formatBytes(cardMediaPreview.retained.newBytes) }}. Rollback is possible until they
                are discarded.
              </p>
            </div>
          </div>

          <DataTable v-if="cardMediaPreview?.items.length" :value="cardMediaPreview.items" size="small" striped-rows class="mb-4">
            <Column header="Word">
              <template #body="{ data }">
                {{ data.word ?? '?' }}
                <span class="text-xs text-surface-400">#{{ data.wordId }}</span>
              </template>
            </Column>
            <Column field="readingIndex" header="Reading" />
            <Column field="contentType" header="Type" />
            <Column header="Size">
              <template #body="{ data }">{{ formatBytes(data.fileSizeBytes) }}</template>
            </Column>
            <Column header="Uploaded">
              <template #body="{ data }">{{ new Date(data.createdAt).toLocaleDateString() }}</template>
            </Column>
          </DataTable>

          <p v-if="cardMediaPreview?.truncated" class="mb-4 text-xs text-surface-500 dark:text-surface-400">
            Showing the 50 largest of {{ cardMediaPreview.totalCount.toLocaleString() }}.
          </p>

          <div v-if="cardMediaPreview?.retained?.count" class="flex flex-wrap justify-center gap-2">
            <Button
              label="Roll Back"
              icon="pi pi-undo"
              class="p-button-secondary"
              :disabled="isLoading.cardMediaRollback"
              :loading="isLoading.cardMediaRollback"
              @click="confirmCardMediaRollback"
            />
            <Button
              label="Discard Originals"
              icon="pi pi-trash"
              class="p-button-danger"
              :disabled="isLoading.cardMediaDiscard"
              :loading="isLoading.cardMediaDiscard"
              @click="confirmCardMediaDiscard"
            />
          </div>
        </template>
      </Card>

      <Card class="shadow-md">
        <template #title>Flush Redis Cache</template>
        <template #content>
          <p class="mb-4">Instantly flush the redis cache, for parser changes.</p>

          <div class="flex justify-center">
            <Button label="Flush Redis Cache" icon="pi pi-table" class="p-button-warning" @click="flushRedisCache" />
          </div>
        </template>
      </Card>

      <Card class="shadow-md">
        <template #title>Replace Word Reading</template>
        <template #content>
          <p class="mb-4">Replace a misparsed WordId/ReadingIndex with the correct one across all decks.</p>

          <div class="grid grid-cols-1 md:grid-cols-2 gap-4 mb-4">
            <div>
              <h4 class="font-semibold mb-2 text-red-500">Old (Wrong)</h4>
              <div class="mb-2">
                <label for="oldWordId" class="block text-sm font-medium mb-1">Word ID</label>
                <InputNumber
                  id="oldWordId"
                  v-model="wordReplacement.oldWordId"
                  placeholder="e.g. 12345"
                  class="w-full"
                  :use-grouping="false"
                  @update:model-value="clearWordReplacementResult"
                />
              </div>
              <div>
                <label for="oldReadingIndex" class="block text-sm font-medium mb-1">Reading Index</label>
                <InputNumber
                  id="oldReadingIndex"
                  v-model="wordReplacement.oldReadingIndex"
                  :min="0"
                  :max="255"
                  class="w-full"
                  @update:model-value="clearWordReplacementResult"
                />
              </div>
            </div>

            <div>
              <h4 class="font-semibold mb-2 text-green-500">New (Correct)</h4>
              <div class="mb-2">
                <label for="newWordId" class="block text-sm font-medium mb-1">Word ID</label>
                <InputNumber
                  id="newWordId"
                  v-model="wordReplacement.newWordId"
                  placeholder="e.g. 12345"
                  class="w-full"
                  :use-grouping="false"
                  @update:model-value="clearWordReplacementResult"
                />
              </div>
              <div>
                <label for="newReadingIndex" class="block text-sm font-medium mb-1">Reading Index</label>
                <InputNumber
                  id="newReadingIndex"
                  v-model="wordReplacement.newReadingIndex"
                  :min="0"
                  :max="255"
                  class="w-full"
                  @update:model-value="clearWordReplacementResult"
                />
              </div>
            </div>
          </div>

          <div v-if="wordReplacementResult" class="mb-4 p-4 bg-surface-100 dark:bg-surface-800 rounded-lg">
            <h4 class="font-semibold mb-2">Preview Results</h4>
            <ul class="text-sm space-y-1">
              <li>
                <strong>Affected Decks:</strong>
                {{ wordReplacementResult.affectedDeckCount }}
              </li>
              <li>
                <strong>DeckWords to Update:</strong>
                {{ wordReplacementResult.deckWordsUpdated }}
              </li>
              <li>
                <strong>DeckWords to Merge:</strong>
                {{ wordReplacementResult.deckWordsMerged }}
              </li>
              <li>
                <strong>Example Sentences:</strong>
                {{ wordReplacementResult.exampleSentenceWordsUpdated }}
              </li>
              <li>
                <strong>User Vocab to Update:</strong>
                {{ wordReplacementResult.fsrsCardsUpdated }}
              </li>
              <li>
                <strong>User Vocab Skipped:</strong>
                {{ wordReplacementResult.fsrsCardsSkipped }} (user has both)
              </li>
              <li>
                <strong>Parent Decks to Recalc:</strong>
                {{ wordReplacementResult.parentDecksQueued }}
              </li>
            </ul>
          </div>

          <div class="flex justify-center gap-2">
            <Button
              label="Preview Changes"
              icon="pi pi-search"
              class="p-button-info"
              :disabled="!canPreviewWordReplacement || isLoading.wordReplacementPreview"
              :loading="isLoading.wordReplacementPreview"
              @click="previewWordReplacement"
            />
            <Button
              label="Execute Replacement"
              icon="pi pi-check"
              class="p-button-danger"
              :disabled="!wordReplacementResult || isLoading.wordReplacementExecute"
              :loading="isLoading.wordReplacementExecute"
              @click="confirmWordReplacement"
            />
          </div>
        </template>
      </Card>

      <Card class="shadow-md">
        <template #title>Split Word</template>
        <template #content>
          <p class="mb-4">Split a misparsed compound word into multiple correct words across all decks.</p>

          <div class="grid grid-cols-1 md:grid-cols-2 gap-4 mb-4">
            <div>
              <h4 class="font-semibold mb-2 text-red-500">Old (Wrong)</h4>
              <div class="mb-2">
                <label for="splitOldWordId" class="block text-sm font-medium mb-1">Word ID</label>
                <InputNumber
                  id="splitOldWordId"
                  v-model="splitWord.oldWordId"
                  placeholder="e.g. 12345"
                  class="w-full"
                  :use-grouping="false"
                  @update:model-value="clearSplitWordResult"
                />
              </div>
              <div>
                <label for="splitOldReadingIndex" class="block text-sm font-medium mb-1">Reading Index</label>
                <InputNumber
                  id="splitOldReadingIndex"
                  v-model="splitWord.oldReadingIndex"
                  :min="0"
                  :max="255"
                  class="w-full"
                  @update:model-value="clearSplitWordResult"
                />
              </div>
            </div>

            <div>
              <h4 class="font-semibold mb-2 text-green-500">New Words (Correct)</h4>
              <div v-for="(word, index) in splitWord.newWords" :key="index" class="mb-3 p-3 bg-surface-50 dark:bg-surface-900 rounded">
                <div class="flex items-center justify-between mb-2">
                  <span class="text-sm font-medium">Word {{ index + 1 }}</span>
                  <Button
                    v-if="splitWord.newWords.length > 2"
                    icon="pi pi-times"
                    class="p-button-text p-button-danger p-button-sm"
                    @click="removeNewWord(index)"
                  />
                </div>
                <div class="grid grid-cols-2 gap-2">
                  <div>
                    <label :for="`splitNewWordId${index}`" class="block text-xs mb-1">Word ID</label>
                    <InputNumber
                      :id="`splitNewWordId${index}`"
                      v-model="word.wordId"
                      placeholder="ID"
                      class="w-full"
                      :use-grouping="false"
                      @update:model-value="clearSplitWordResult"
                    />
                  </div>
                  <div>
                    <label :for="`splitNewReadingIndex${index}`" class="block text-xs mb-1">Reading Index</label>
                    <InputNumber
                      :id="`splitNewReadingIndex${index}`"
                      v-model="word.readingIndex"
                      :min="0"
                      :max="255"
                      class="w-full"
                      @update:model-value="clearSplitWordResult"
                    />
                  </div>
                </div>
              </div>
              <Button label="Add Word" icon="pi pi-plus" class="p-button-text p-button-sm" @click="addNewWord" />
            </div>
          </div>

          <div v-if="splitWordResult" class="mb-4 p-4 bg-surface-100 dark:bg-surface-800 rounded-lg">
            <h4 class="font-semibold mb-2">Preview Results</h4>
            <ul class="text-sm space-y-1">
              <li>
                <strong>Affected Decks:</strong>
                {{ splitWordResult.affectedDeckCount }}
              </li>
              <li>
                <strong>DeckWords to Delete:</strong>
                {{ splitWordResult.deckWordsDeleted }}
              </li>
              <li>
                <strong>DeckWords to Insert:</strong>
                {{ splitWordResult.deckWordsInserted }}
              </li>
              <li>
                <strong>DeckWords to Merge:</strong>
                {{ splitWordResult.deckWordsMerged }}
              </li>
              <li>
                <strong>Example Sentences to Delete:</strong>
                {{ splitWordResult.exampleSentenceWordsDeleted }}
              </li>
              <li>
                <strong>Example Sentences to Insert:</strong>
                {{ splitWordResult.exampleSentenceWordsInserted }}
              </li>
              <li>
                <strong>Parent Decks to Recalc:</strong>
                {{ splitWordResult.parentDecksQueued }}
              </li>
            </ul>
            <p class="text-xs mt-2 text-surface-500 dark:text-surface-400">Note: User vocabulary (FsrsCards) is not affected by split operations.</p>
          </div>

          <div class="flex justify-center gap-2">
            <Button
              label="Preview Changes"
              icon="pi pi-search"
              class="p-button-info"
              :disabled="!canPreviewSplitWord || isLoading.splitWordPreview"
              :loading="isLoading.splitWordPreview"
              @click="previewSplitWord"
            />
            <Button
              label="Execute Split"
              icon="pi pi-check"
              class="p-button-danger"
              :disabled="!splitWordResult || isLoading.splitWordExecute"
              :loading="isLoading.splitWordExecute"
              @click="confirmSplitWord"
            />
          </div>
        </template>
      </Card>

      <Card class="shadow-md">
        <template #title>Remove Word</template>
        <template #content>
          <p class="mb-4 text-red-500">
            <strong>Warning:</strong>
            Completely remove a word from all decks, example sentences, and user vocabularies. This is destructive and cannot be undone.
          </p>

          <div class="max-w-md mx-auto mb-4">
            <div class="mb-2">
              <label for="removeWordId" class="block text-sm font-medium mb-1">Word ID</label>
              <InputNumber
                id="removeWordId"
                v-model="removeWord.wordId"
                placeholder="e.g. 12345"
                class="w-full"
                :use-grouping="false"
                @update:model-value="clearRemoveWordResult"
              />
            </div>
            <div>
              <label for="removeReadingIndex" class="block text-sm font-medium mb-1">Reading Index</label>
              <InputNumber
                id="removeReadingIndex"
                v-model="removeWord.readingIndex"
                :min="0"
                :max="255"
                class="w-full"
                @update:model-value="clearRemoveWordResult"
              />
            </div>
          </div>

          <div v-if="removeWordResult" class="mb-4 p-4 bg-red-50 dark:bg-red-900/20 rounded-lg border border-red-200 dark:border-red-800">
            <h4 class="font-semibold mb-2 text-red-700 dark:text-red-400">Preview Results</h4>
            <ul class="text-sm space-y-1">
              <li>
                <strong>Affected Decks:</strong>
                {{ removeWordResult.affectedDeckCount }}
              </li>
              <li>
                <strong>DeckWords to Delete:</strong>
                {{ removeWordResult.deckWordsDeleted }}
              </li>
              <li>
                <strong>Example Sentences to Delete:</strong>
                {{ removeWordResult.exampleSentenceWordsDeleted }}
              </li>
              <li class="text-red-600 dark:text-red-400">
                <strong>User Vocab to Delete:</strong>
                {{ removeWordResult.fsrsCardsDeleted }}
              </li>
              <li>
                <strong>Parent Decks to Recalc:</strong>
                {{ removeWordResult.parentDecksQueued }}
              </li>
            </ul>
          </div>

          <div class="flex justify-center gap-2">
            <Button
              label="Preview Deletion"
              icon="pi pi-search"
              class="p-button-warning"
              :disabled="!canPreviewRemoveWord || isLoading.removeWordPreview"
              :loading="isLoading.removeWordPreview"
              @click="previewRemoveWord"
            />
            <Button
              label="Execute Removal"
              icon="pi pi-trash"
              class="p-button-danger"
              :disabled="!removeWordResult || isLoading.removeWordExecute"
              :loading="isLoading.removeWordExecute"
              @click="confirmRemoveWord"
            />
          </div>
        </template>
      </Card>
    </div>
  </div>
</template>

<style scoped></style>
