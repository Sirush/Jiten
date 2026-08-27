<script setup lang="ts">
  import { useConfirm } from 'primevue/useconfirm';

  const props = defineProps<{
    visible: boolean;
    data: { totalCount: number; cards: any[] } | null;
    actionDescription: string;
    pushDays?: number | null;
    staggerBatchSize?: number | null;
    onExecute: () => Promise<void>;
  }>();

  const isPushDue = computed(() => props.pushDays != null && props.pushDays !== 0);

  function getNewDueDate(card: any, globalIndex: number): string {
    const due = new Date(card.due);
    let days = props.pushDays!;
    if (props.staggerBatchSize && props.staggerBatchSize > 0) {
      days += Math.floor(globalIndex / props.staggerBatchSize);
    }
    due.setDate(due.getDate() + days);
    return formatDate(due.toISOString());
  }

  const emit = defineEmits<{
    'update:visible': [value: boolean];
    'load-range': [offset: number, limit: number];
  }>();

  const confirm = useConfirm();
  const convertToRuby = useConvertToRuby();
  const executing = ref(false);
  const loadedRanges = ref(new Set<number>());
  const CHUNK_SIZE = 50;

  const allCards = computed(() => {
    if (!props.data) return [];
    const arr = new Array(props.data.totalCount);
    for (let i = 0; i < props.data.cards.length; i++) {
      arr[i] = props.data.cards[i];
    }
    return arr;
  });

  function onLazyLoad(event: any) {
    const { first, last } = event;
    const startChunk = Math.floor(first / CHUNK_SIZE);
    const endChunk = Math.floor((last - 1) / CHUNK_SIZE);

    for (let chunk = startChunk; chunk <= endChunk; chunk++) {
      if (!loadedRanges.value.has(chunk)) {
        loadedRanges.value.add(chunk);
        emit('load-range', chunk * CHUNK_SIZE, CHUNK_SIZE);
      }
    }
  }

  const formatDate = formatDateShort;

  function confirmExecute() {
    confirm.require({
      message: `This will affect ${props.data?.totalCount.toLocaleString()} card(s). Are you sure?`,
      header: 'Confirm Mass Action',
      icon: 'pi pi-exclamation-triangle',
      acceptClass: 'p-button-danger',
      rejectClass: 'p-button-secondary',
      accept: async () => {
        executing.value = true;
        try {
          await props.onExecute();
        } finally {
          executing.value = false;
        }
      },
    });
  }

  watch(
    () => props.visible,
    (v) => {
      if (v) loadedRanges.value = new Set([0]);
    }
  );
</script>

<template>
  <Dialog
    :visible="visible"
    modal
    :header="`Preview — ${data?.totalCount.toLocaleString() ?? 0} card(s)`"
    :style="{ width: '900px', maxWidth: '95vw' }"
    :closable="true"
    @update:visible="emit('update:visible', $event)"
  >
    <p class="text-sm text-muted-color mb-3 italic">{{ actionDescription }}</p>

    <DataTable
      :value="allCards"
      scrollable
      scroll-height="60vh"
      :virtual-scroller-options="{ lazy: true, onLazyLoad, itemSize: 46, showLoader: true }"
      striped-rows
      class="p-datatable-sm"
    >
      <Column header="#" style="width: 55px">
        <template #body="{ index }">
          <span class="text-sm text-muted-color">{{ (index + 1).toLocaleString() }}</span>
        </template>
      </Column>
      <Column header="Word" style="min-width: 150px">
        <template #body="{ data: card }">
          <template v-if="card">
            <NuxtLink
              v-tooltip.top="card.mainDefinition"
              :to="`/vocabulary/${card.wordId}/${card.readingIndex}`"
              target="_blank"
              class="text-blue-500 hover:underline"
            >
              <span class="text-base font-bold" lang="ja" v-html="convertToRuby(card.reading)" />
            </NuxtLink>
          </template>
          <Skeleton v-else width="6rem" height="1.2rem" />
        </template>
      </Column>
      <Column header="State" style="width: 110px">
        <template #body="{ data: card }">
          <template v-if="card">
            <Tag :severity="getFsrsStateSeverity(card.state)" :value="getFsrsStateName(card.state)" />
          </template>
          <Skeleton v-else width="4rem" height="1.2rem" />
        </template>
      </Column>
      <Column header="Due" style="width: 150px">
        <template #body="{ data: card }">
          <template v-if="card">
            <span class="text-sm whitespace-nowrap">{{ formatDate(card.due) }}</span>
          </template>
          <Skeleton v-else width="5rem" height="1.2rem" />
        </template>
      </Column>
      <Column v-if="isPushDue" header="New Due" style="width: 150px">
        <template #body="{ data: card, index }">
          <template v-if="card">
            <span class="text-sm font-semibold text-primary-500 whitespace-nowrap">{{ getNewDueDate(card, index) }}</span>
          </template>
          <Skeleton v-else width="5rem" height="1.2rem" />
        </template>
      </Column>
      <Column header="Created" style="width: 150px">
        <template #body="{ data: card }">
          <template v-if="card">
            <span class="text-sm whitespace-nowrap">{{ formatDate(card.createdAt) }}</span>
          </template>
          <Skeleton v-else width="5rem" height="1.2rem" />
        </template>
      </Column>
      <Column header="Rank" style="width: 80px">
        <template #body="{ data: card }">
          <template v-if="card">
            <span class="text-sm text-muted-color">{{ card.frequencyRank > 0 ? `#${card.frequencyRank.toLocaleString()}` : '—' }}</span>
          </template>
          <Skeleton v-else width="3rem" height="1.2rem" />
        </template>
      </Column>
    </DataTable>

    <template #footer>
      <div class="flex justify-end gap-2">
        <Button label="Cancel" severity="secondary" @click="emit('update:visible', false)" />
        <Button
          :label="`Execute (${data?.totalCount.toLocaleString() ?? 0} cards)`"
          severity="danger"
          icon="pi pi-check"
          :loading="executing"
          :disabled="!data?.totalCount"
          @click="confirmExecute"
        />
      </div>
    </template>
  </Dialog>
</template>
