<script setup lang="ts">
  import type { FsrsState } from '~/types/enums';

  const emit = defineEmits<{ changed: [] }>();

  const { $api } = useNuxtApp();
  const toast = useToast();
  const confirm = useConfirm();
  const convertToRuby = useConvertToRuby();

  type Direction = 'compound-to-components' | 'components-to-compound';

  interface InferredCard {
    wordId: number;
    readingIndex: number;
    reading: string;
    mainDefinition: string | null;
    frequencyRank: number;
    state: FsrsState;
    category: 'new' | 'learning' | 'mature';
  }

  const directionOptions = [
    {
      label: 'Components of my known compounds',
      value: 'compound-to-components' as Direction,
      description: 'Mark components as known from compound words that you already know. Example: if you know 突っ込む, this lists 突く and 込む.',
    },
    {
      label: 'Compounds whose components I know',
      value: 'components-to-compound' as Direction,
      description: 'Mark compound words as known when you already know all their components. Example: if you know 取り and 付ける, this lists 取り付ける.',
    },
  ];

  const direction = ref<Direction>('compound-to-components');
  const previewLoading = ref(false);
  const totalCount = ref(0);
  const CHUNK_SIZE = 50;
  const allCards = ref<(InferredCard | undefined)[]>([]);
  const loadedChunks = ref(new Set<number>());
  const showPreview = ref(false);
  const bulkLoading = ref(false);

  const showNew = ref(true);
  const showLearning = ref(false);
  const showMature = ref(false);
  const counts = ref({ new: 0, learning: 0, mature: 0 });
  const anyFilterOn = computed(() => showNew.value || showLearning.value || showMature.value);

  const directionDescription = computed(() => directionOptions.find((o) => o.value === direction.value)?.description ?? '');

  async function loadChunk(offset: number, limit: number) {
    try {
      const data = await $api<{
        data: InferredCard[];
        totalItems: number;
        newCount: number;
        learningCount: number;
        matureCount: number;
      }>('srs/composition-inference/preview', {
        method: 'POST',
        body: {
          direction: direction.value,
          offset,
          limit,
          showNew: showNew.value,
          showLearning: showLearning.value,
          showMature: showMature.value,
        },
      });
      totalCount.value = data.totalItems;
      counts.value = { new: data.newCount, learning: data.learningCount, mature: data.matureCount };
      const cards = [...allCards.value];
      if (cards.length !== data.totalItems) cards.length = data.totalItems;
      for (let i = 0; i < data.data.length; i++) cards[offset + i] = data.data[i];
      allCards.value = cards;
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Preview failed', detail: extractApiError(e, 'Could not load preview.'), life: 5000 });
    }
  }

  async function preview() {
    previewLoading.value = true;
    await reloadFirst();
    showPreview.value = true;
    previewLoading.value = false;
  }

  async function reloadFirst() {
    allCards.value = [];
    loadedChunks.value = new Set([0]);
    if (!anyFilterOn.value) {
      totalCount.value = 0;
      return;
    }
    await loadChunk(0, CHUNK_SIZE);
  }

  function onLazyLoad(event: { first: number; last: number }) {
    const { first, last } = event;
    const startChunk = Math.floor(first / CHUNK_SIZE);
    const endChunk = Math.floor((last - 1) / CHUNK_SIZE);
    for (let chunk = startChunk; chunk <= endChunk; chunk++) {
      if (!loadedChunks.value.has(chunk)) {
        loadedChunks.value.add(chunk);
        loadChunk(chunk * CHUNK_SIZE, CHUNK_SIZE);
      }
    }
  }

  async function setIndividualState(card: InferredCard, state: 'neverForget-add' | 'blacklist-add') {
    try {
      await $api('srs/set-vocabulary-state', {
        method: 'POST',
        body: { wordId: card.wordId, readingIndex: card.readingIndex, state },
      });
      toast.add({
        severity: 'success',
        summary: state === 'neverForget-add' ? 'Marked as mastered' : 'Blacklisted',
        detail: stripRuby(card.reading),
        life: 2500,
      });
      emit('changed');
      allCards.value = allCards.value.filter((c) => c !== card);
      totalCount.value = Math.max(0, totalCount.value - 1);
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Action failed', detail: extractApiError(e, 'Could not update word.'), life: 5000 });
    }
  }

  function confirmBulk(target: 'mastered' | 'blacklisted') {
    const verb = target === 'mastered' ? 'master' : 'blacklist';
    const label = target === 'mastered' ? 'Master all' : 'Blacklist all';
    confirm.require({
      header: `${label} (${totalCount.value.toLocaleString()})`,
      message: `This will ${verb} ${totalCount.value.toLocaleString()} word${totalCount.value === 1 ? '' : 's'}. Continue?`,
      icon: 'pi pi-exclamation-triangle',
      acceptLabel: label,
      rejectLabel: 'Cancel',
      acceptProps: { severity: target === 'mastered' ? 'success' : 'danger' },
      accept: () => executeBulk(target),
    });
  }

  async function executeBulk(target: 'mastered' | 'blacklisted') {
    bulkLoading.value = true;
    try {
      const result = await $api<{ affectedCount: number }>('srs/composition-inference/execute', {
        method: 'POST',
        body: {
          direction: direction.value,
          targetState: target,
          showNew: showNew.value,
          showLearning: showLearning.value,
          showMature: showMature.value,
        },
      });
      toast.add({
        severity: 'success',
        summary: target === 'mastered' ? 'Mastered' : 'Blacklisted',
        detail: `${result.affectedCount.toLocaleString()} word${result.affectedCount === 1 ? '' : 's'} updated.`,
        life: 5000,
      });
      showPreview.value = false;
      emit('changed');
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Bulk action failed', detail: extractApiError(e, 'Could not apply bulk action.'), life: 5000 });
    } finally {
      bulkLoading.value = false;
    }
  }

  function categoryTag(card: InferredCard): { value: string; severity: string } {
    switch (card.category) {
      case 'new':
        return { value: 'New', severity: 'secondary' };
      case 'learning':
        return { value: 'Learning', severity: 'info' };
      case 'mature':
        return { value: 'Mature', severity: 'success' };
      default:
        return fsrsStateLabel(card.state);
    }
  }

  watch(direction, () => {
    allCards.value = [];
    totalCount.value = 0;
    counts.value = { new: 0, learning: 0, mature: 0 };
    loadedChunks.value = new Set();
    if (showPreview.value) reloadFirst();
  });

  watch([showNew, showLearning, showMature], () => {
    if (showPreview.value) reloadFirst();
  });
</script>

<template>
  <Card>
    <template #title>
      <h2 class="text-xl font-bold">Mark Words as Known via Composition</h2>
    </template>
    <template #content>
      <p class="text-sm text-muted-color mb-4">
        If you know a compound word, you're likely to know its parts too. Preview the results, then master or blacklist them individually or all at once.
      </p>

      <div class="flex flex-col gap-4">
        <div>
          <label class="block text-sm font-medium mb-1">Direction</label>
          <SelectButton v-model="direction" :options="directionOptions" option-label="label" option-value="value" :allow-empty="false" />
          <p class="text-xs text-muted-color mt-2 italic">{{ directionDescription }}</p>
        </div>

        <div>
          <Button label="Preview" icon="pi pi-eye" :loading="previewLoading" @click="preview" />
        </div>
      </div>

      <Dialog
        v-model:visible="showPreview"
        modal
        :header="`Preview — ${totalCount.toLocaleString()} word${totalCount === 1 ? '' : 's'}`"
        :style="{ width: '900px', maxWidth: '95vw' }"
      >
        <p class="text-sm text-muted-color mb-3 italic">{{ directionDescription }}</p>

        <div class="flex flex-wrap items-center gap-4 mb-3">
          <span class="text-sm font-medium">Show:</span>
          <div class="flex items-center gap-2">
            <Checkbox v-model="showNew" input-id="ci-new" binary />
            <label for="ci-new" class="text-sm cursor-pointer">
              New
              <span class="text-muted-color">({{ counts.new.toLocaleString() }})</span>
            </label>
          </div>
          <div class="flex items-center gap-2">
            <Checkbox v-model="showLearning" input-id="ci-learning" binary />
            <label for="ci-learning" class="text-sm cursor-pointer">
              Learning
              <span class="text-muted-color">({{ counts.learning.toLocaleString() }})</span>
            </label>
          </div>
          <div class="flex items-center gap-2">
            <Checkbox v-model="showMature" input-id="ci-mature" binary />
            <label for="ci-mature" class="text-sm cursor-pointer">
              Mature
              <span class="text-muted-color">({{ counts.mature.toLocaleString() }})</span>
            </label>
          </div>
        </div>
        <p class="text-xs text-muted-color mb-3">
          Sorted by frequency (most common first).
          <strong>Mature</strong>
          words are already known via SRS — mastering them resets their review schedule, so they're hidden by default.
        </p>

        <div v-if="!anyFilterOn" class="text-sm text-muted-color italic py-4 text-center">Select at least one category to show.</div>
        <div v-else-if="totalCount === 0" class="text-sm text-muted-color italic py-4 text-center">No words found for the selected categories.</div>

        <DataTable
          v-else
          :value="allCards"
          scrollable
          scroll-height="60vh"
          :virtual-scroller-options="{ lazy: true, onLazyLoad, itemSize: 46, showLoader: true }"
          striped-rows
          class="p-datatable-sm"
        >
          <Column header="Word" style="min-width: 150px">
            <template #body="{ data: card }">
              <template v-if="card">
                <NuxtLink
                  v-tooltip.top="card.mainDefinition"
                  :to="`/vocabulary/${card.wordId}/${card.readingIndex}`"
                  target="_blank"
                  class="text-blue-500 hover:underline font-noto-sans"
                >
                  <span v-if="card.reading" lang="ja" v-html="convertToRuby(card.reading)" />
                  <span v-else>—</span>
                </NuxtLink>
              </template>
              <Skeleton v-else width="6rem" height="1.2rem" />
            </template>
          </Column>
          <Column header="Meaning" style="min-width: 150px">
            <template #body="{ data: card }">
              <template v-if="card">
                <span class="text-muted-color">{{ card.mainDefinition || '—' }}</span>
              </template>
              <Skeleton v-else width="8rem" height="1.2rem" />
            </template>
          </Column>
          <Column header="Freq" style="width: 80px">
            <template #body="{ data: card }">
              <template v-if="card">
                <span v-if="card.frequencyRank > 0">#{{ card.frequencyRank.toLocaleString() }}</span>
                <span v-else class="text-muted-color">—</span>
              </template>
              <Skeleton v-else width="3rem" height="1.2rem" />
            </template>
          </Column>
          <Column header="Status" style="width: 110px">
            <template #body="{ data: card }">
              <template v-if="card">
                <Tag v-bind="categoryTag(card)" />
              </template>
              <Skeleton v-else width="4rem" height="1.2rem" />
            </template>
          </Column>
          <Column header="Actions" style="width: 100px">
            <template #body="{ data: card }">
              <template v-if="card">
                <div class="flex gap-1">
                  <Button
                    v-tooltip.top="'Mark as mastered'"
                    icon="pi pi-check"
                    severity="success"
                    text
                    rounded
                    size="small"
                    @click="setIndividualState(card, 'neverForget-add')"
                  />
                  <Button
                    v-tooltip.top="'Blacklist'"
                    icon="pi pi-ban"
                    severity="danger"
                    text
                    rounded
                    size="small"
                    @click="setIndividualState(card, 'blacklist-add')"
                  />
                </div>
              </template>
              <Skeleton v-else width="4rem" height="1.2rem" />
            </template>
          </Column>
        </DataTable>

        <template #footer>
          <div class="flex justify-end gap-2">
            <Button label="Cancel" severity="secondary" @click="showPreview = false" />
            <Button
              label="Master all"
              icon="pi pi-check"
              severity="success"
              :disabled="totalCount === 0 || bulkLoading"
              :loading="bulkLoading"
              @click="confirmBulk('mastered')"
            />
            <Button
              label="Blacklist all"
              icon="pi pi-ban"
              severity="danger"
              :disabled="totalCount === 0 || bulkLoading"
              :loading="bulkLoading"
              @click="confirmBulk('blacklisted')"
            />
          </div>
        </template>
      </Dialog>
    </template>
  </Card>
</template>
