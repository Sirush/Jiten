<script setup lang="ts">
  import { FsrsState } from '~/types/enums';

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
  }

  const directionOptions = [
    {
      label: 'Components of my known compounds',
      value: 'compound-to-components' as Direction,
      description: 'For every compound you already know, surface its component words. Example: if you know 突っ込む, this lists 突く and 込む.',
    },
    {
      label: 'Compounds whose components I know',
      value: 'components-to-compound' as Direction,
      description: 'Surface compound words where you already know every component. Example: if you know 取り and 付ける, this lists 取り付ける.',
    },
  ];

  const direction = ref<Direction>('compound-to-components');
  const previewLoading = ref(false);
  const totalCount = ref(0);
  const pageSize = 50;
  const currentOffset = ref(0);
  const rows = ref<InferredCard[]>([]);
  const hasPreview = ref(false);
  const bulkLoading = ref(false);

  const directionDescription = computed(
    () => directionOptions.find((o) => o.value === direction.value)?.description ?? '',
  );

  async function loadPage(offset: number) {
    previewLoading.value = true;
    try {
      const data = await $api<{ data: InferredCard[]; totalItems: number }>('srs/composition-inference/preview', {
        method: 'POST',
        body: { direction: direction.value, offset, limit: pageSize },
      });
      totalCount.value = data.totalItems;
      rows.value = data.data;
      currentOffset.value = offset;
      hasPreview.value = true;
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Preview failed', detail: extractApiError(e, 'Could not load preview.'), life: 5000 });
    } finally {
      previewLoading.value = false;
    }
  }

  function preview() {
    return loadPage(0);
  }

  function onPage(event: { first: number; rows: number }) {
    loadPage(event.first);
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
        detail: card.reading,
        life: 2500,
      });
      emit('changed');
      await loadPage(currentOffset.value);
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Action failed', detail: extractApiError(e, 'Could not update word.'), life: 5000 });
    }
  }

  function confirmBulk(target: 'mastered' | 'blacklisted') {
    const verb = target === 'mastered' ? 'master' : 'blacklist';
    const label = target === 'mastered' ? 'Master all' : 'Blacklist all';
    confirm.require({
      header: `${label} (${totalCount.value.toLocaleString()})`,
      message: `This will ${verb} ${totalCount.value.toLocaleString()} word${totalCount.value === 1 ? '' : 's'} inferred from your current selection. Continue?`,
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
        body: { direction: direction.value, targetState: target },
      });
      toast.add({
        severity: 'success',
        summary: target === 'mastered' ? 'Mastered' : 'Blacklisted',
        detail: `${result.affectedCount.toLocaleString()} word${result.affectedCount === 1 ? '' : 's'} updated.`,
        life: 5000,
      });
      emit('changed');
      await loadPage(0);
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Bulk action failed', detail: extractApiError(e, 'Could not apply bulk action.'), life: 5000 });
    } finally {
      bulkLoading.value = false;
    }
  }

  function stateLabel(state: FsrsState): { value: string; severity: string } {
    switch (state) {
      case FsrsState.Learning: return { value: 'Learning', severity: 'info' };
      case FsrsState.Review: return { value: 'Review', severity: 'success' };
      case FsrsState.Relearning: return { value: 'Relearning', severity: 'warn' };
      case FsrsState.Blacklisted: return { value: 'Blacklisted', severity: 'danger' };
      case FsrsState.Mastered: return { value: 'Mastered', severity: 'success' };
      case FsrsState.Suspended: return { value: 'Suspended', severity: 'secondary' };
      default: return { value: 'New', severity: 'secondary' };
    }
  }

  watch(direction, () => {
    hasPreview.value = false;
    rows.value = [];
    totalCount.value = 0;
    currentOffset.value = 0;
  });
</script>

<template>
  <Card>
    <template #title>
      <h2 class="text-xl font-bold">Infer Known Words from Composition</h2>
    </template>
    <template #content>
      <p class="text-sm text-muted-color mb-4">
        Reuse what you already know. If you know a compound word, its components are likely known too — and vice versa.
        Preview the inferred words, then master or blacklist them in bulk or individually.
      </p>

      <div class="flex flex-col gap-4">
        <div>
          <label class="block text-sm font-medium mb-1">Direction</label>
          <SelectButton
            v-model="direction"
            :options="directionOptions"
            option-label="label"
            option-value="value"
            :allow-empty="false"
          />
          <p class="text-xs text-muted-color mt-2 italic">{{ directionDescription }}</p>
        </div>

        <div class="flex justify-end">
          <Button label="Preview" icon="pi pi-eye" severity="warn" :loading="previewLoading" @click="preview" />
        </div>

        <div v-if="hasPreview">
          <Divider />
          <div class="flex flex-col md:flex-row md:items-center md:justify-between gap-2 mb-3">
            <div class="text-sm">
              <span class="font-semibold">{{ totalCount.toLocaleString() }}</span>
              word{{ totalCount === 1 ? '' : 's' }} can be inferred.
            </div>
            <div class="flex gap-2">
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
          </div>

          <div v-if="totalCount === 0" class="text-sm text-muted-color italic py-4 text-center">
            No inferable words found for this direction.
          </div>

          <DataTable
            v-else
            :value="rows"
            :loading="previewLoading"
            lazy
            paginator
            :rows="pageSize"
            :total-records="totalCount"
            :first="currentOffset"
            @page="onPage"
            class="text-sm"
          >
            <Column field="reading" header="Word">
              <template #body="{ data }">
                <NuxtLink
                  v-tooltip.top="data.mainDefinition"
                  :to="`/vocabulary/${data.wordId}/${data.readingIndex}`"
                  target="_blank"
                  class="text-blue-500 hover:underline font-noto-sans"
                >
                  <span v-if="data.reading" v-html="convertToRuby(data.reading)" />
                  <span v-else>—</span>
                </NuxtLink>
              </template>
            </Column>
            <Column field="mainDefinition" header="Meaning">
              <template #body="{ data }">
                <span class="text-muted-color">{{ data.mainDefinition || '—' }}</span>
              </template>
            </Column>
            <Column field="frequencyRank" header="Freq" class="w-24">
              <template #body="{ data }">
                <span v-if="data.frequencyRank > 0">#{{ data.frequencyRank.toLocaleString() }}</span>
                <span v-else class="text-muted-color">—</span>
              </template>
            </Column>
            <Column field="state" header="Status" class="w-32">
              <template #body="{ data }">
                <Tag v-bind="stateLabel(data.state)" />
              </template>
            </Column>
            <Column header="Actions" class="w-40">
              <template #body="{ data }">
                <div class="flex gap-1">
                  <Button
                    v-tooltip.top="'Mark as mastered'"
                    icon="pi pi-check"
                    severity="success"
                    text
                    rounded
                    size="small"
                    @click="setIndividualState(data, 'neverForget-add')"
                  />
                  <Button
                    v-tooltip.top="'Blacklist'"
                    icon="pi pi-ban"
                    severity="danger"
                    text
                    rounded
                    size="small"
                    @click="setIndividualState(data, 'blacklist-add')"
                  />
                </div>
              </template>
            </Column>
          </DataTable>
        </div>
      </div>
    </template>
  </Card>
</template>
