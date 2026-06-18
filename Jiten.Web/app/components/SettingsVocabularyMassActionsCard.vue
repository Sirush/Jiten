<script setup lang="ts">
  const emit = defineEmits<{ changed: [] }>();

  const { $api } = useNuxtApp();
  const toast = useToast();

  const stateOptions = [
    { label: 'Learning', value: 1 },
    { label: 'Review', value: 2 },
    { label: 'Relearning', value: 3 },
    { label: 'Blacklisted', value: 4 },
    { label: 'Mastered', value: 5 },
    { label: 'Suspended', value: 6 },
  ];

  const targetStateOptions = [
    { label: 'Learning', value: 1 },
    { label: 'Blacklisted', value: 4 },
    { label: 'Mastered', value: 5 },
    { label: 'Suspended', value: 6 },
  ];

  const actionOptions = [
    { label: 'Change State', value: 'change-state' },
    { label: 'Push Due Date', value: 'push-due' },
    { label: 'Reset Schedule', value: 'reset-schedule' },
    { label: 'Delete Cards', value: 'delete-cards' },
  ];

  const dateTypeOptions = [
    { label: 'None', value: '' },
    { label: 'Created', value: 'created' },
    { label: 'Due', value: 'due' },
  ];

  const selectedStates = ref<number[]>([]);
  const dateType = ref('');
  const dateFrom = ref<Date | null>(null);
  const dateTo = ref<Date | null>(null);
  const action = ref<string | null>(null);
  const targetState = ref<number | null>(null);
  const pushDays = ref(1);
  const staggerBatchSize = ref<number | null>(null);

  const previewLoading = ref(false);
  const showPreview = ref(false);
  const previewData = ref<{ totalCount: number; cards: any[] } | null>(null);

  function buildRequest(offset = 0, limit = 50) {
    return {
      stateFilter: selectedStates.value.length > 0 ? selectedStates.value : null,
      dateType: dateType.value || null,
      dateFrom: dateFrom.value?.toISOString() ?? null,
      dateTo: dateTo.value?.toISOString() ?? null,
      action: action.value!,
      targetState: action.value === 'change-state' ? targetState.value : null,
      pushDays: action.value === 'push-due' ? pushDays.value : null,
      staggerBatchSize: action.value === 'push-due' ? staggerBatchSize.value : null,
      offset,
      limit,
    };
  }

  const canPreview = computed(() => {
    if (!action.value) return false;
    if (action.value === 'change-state' && targetState.value == null) return false;
    if (action.value === 'push-due' && (pushDays.value === 0 || pushDays.value == null)) return false;
    return true;
  });

  async function preview() {
    previewLoading.value = true;
    try {
      const data = await $api<any>('srs/mass-action/preview', {
        method: 'POST',
        body: buildRequest(0, 50),
      });
      const cards = new Array(data.totalItems);
      for (let i = 0; i < data.data.length; i++) cards[i] = data.data[i];
      previewData.value = { totalCount: data.totalItems, cards };
      showPreview.value = true;
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Preview failed', detail: extractApiError(e, 'Could not load preview.'), life: 5000 });
    } finally {
      previewLoading.value = false;
    }
  }

  async function loadRange(offset: number, limit: number) {
    try {
      const data = await $api<any>('srs/mass-action/preview', {
        method: 'POST',
        body: buildRequest(offset, limit),
      });
      if (previewData.value) {
        const cards = [...previewData.value.cards];
        for (let i = 0; i < data.data.length; i++) cards[offset + i] = data.data[i];
        previewData.value = { totalCount: data.totalItems, cards };
      }
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(e, 'Could not load cards.'), life: 5000 });
    }
  }

  async function execute() {
    try {
      const result = await $api<{ affectedCount: number }>('srs/mass-action/execute', {
        method: 'POST',
        body: buildRequest(),
      });
      showPreview.value = false;
      toast.add({
        severity: 'success',
        summary: 'Mass action completed',
        detail: `${result.affectedCount.toLocaleString()} card${result.affectedCount === 1 ? '' : 's'} affected.`,
        life: 5000,
      });
      emit('changed');
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Execution failed', detail: extractApiError(e, 'Mass action failed.'), life: 5000 });
    }
  }

  const actionDescription = computed(() => {
    switch (action.value) {
      case 'change-state': {
        const label = targetStateOptions.find((o) => o.value === targetState.value)?.label;
        return label ? `Change all matching cards to "${label}"` : '';
      }
      case 'push-due': {
        let desc = `Push due date by ${pushDays.value} day${Math.abs(pushDays.value) === 1 ? '' : 's'}`;
        if (staggerBatchSize.value && staggerBatchSize.value > 0) desc += `, staggered in batches of ${staggerBatchSize.value}`;
        return desc;
      }
      case 'reset-schedule':
        return 'Reset all matching cards to Learning state, clearing scheduling data but keeping review history.';
      case 'delete-cards':
        return 'Permanently delete all matching cards and their review history.';
      default:
        return '';
    }
  });
</script>

<template>
  <Card>
    <template #title>
      <h2 class="text-xl font-bold">Mass Actions</h2>
    </template>
    <template #content>
      <Message severity="warn" :closable="false" class="mb-4">
        Mass actions are permanent and cannot be undone. Please make a backup before using them, and use them at your own risk.
      </Message>

      <p class="text-sm text-muted-color mb-4">
        Bulk-manage your vocabulary cards. Select filters, choose an action, then preview the affected cards before applying.
      </p>

      <div class="flex flex-col gap-4">
        <Divider />
        <h3 class="text-sm font-semibold uppercase tracking-wider text-muted-color">Filters</h3>

        <div class="flex flex-col md:flex-row gap-4">
          <div class="flex-1">
            <label class="block text-sm font-medium mb-1">Card Status</label>
            <MultiSelect
              v-model="selectedStates"
              :options="stateOptions"
              option-label="label"
              option-value="value"
              placeholder="All statuses"
              class="w-full"
              scroll-height="40vh"
              :max-selected-labels="3"
            />
          </div>
          <div class="flex-1">
            <label class="block text-sm font-medium mb-1">Date Filter</label>
            <SelectButton v-model="dateType" :options="dateTypeOptions" option-value="value" option-label="label" />
          </div>
        </div>

        <div v-if="dateType" class="flex flex-col md:flex-row gap-4">
          <div class="flex-1">
            <label class="block text-sm font-medium mb-1">From</label>
            <DatePicker v-model="dateFrom" class="w-full" placeholder="Start date" show-button-bar />
          </div>
          <div class="flex-1">
            <label class="block text-sm font-medium mb-1">To</label>
            <DatePicker v-model="dateTo" class="w-full" placeholder="End date" show-button-bar />
          </div>
        </div>

        <Divider />
        <h3 class="text-sm font-semibold uppercase tracking-wider text-muted-color">Action</h3>

        <div class="flex flex-col md:flex-row gap-4">
          <div class="flex-1">
            <label class="block text-sm font-medium mb-1">Action Type</label>
            <Select v-model="action" :options="actionOptions" option-label="label" option-value="value" placeholder="Select an action" class="w-full" />
          </div>

          <div v-if="action === 'change-state'" class="flex-1">
            <label class="block text-sm font-medium mb-1">Target State</label>
            <Select
              v-model="targetState"
              :options="targetStateOptions"
              option-label="label"
              option-value="value"
              placeholder="Select target state"
              class="w-full"
            />
          </div>

          <div v-if="action === 'push-due'" class="flex-1">
            <label class="block text-sm font-medium mb-1">Days</label>
            <InputNumber v-model="pushDays" :min="-365" :max="365" show-buttons class="w-full" />
          </div>

          <div v-if="action === 'push-due'" class="flex-1">
            <label class="block text-sm font-medium mb-1">Stagger (cards/batch)</label>
            <InputNumber v-model="staggerBatchSize" :min="1" :max="10000" show-buttons placeholder="Optional" class="w-full" />
            <p class="text-xs text-muted-color mt-1">
              Spreads cards out over time — every N cards get an extra day added to their due date, so reviews don't all land on the same day.
            </p>
          </div>
        </div>

        <Message v-if="action === 'delete-cards'" severity="warn" :closable="false">
          This will permanently delete the matching cards and all their review history. This cannot be undone.
        </Message>
        <Message v-if="action === 'reset-schedule'" severity="info" :closable="false">
          This will reset scheduling data (stability, difficulty) and set cards back to Learning. Review history is preserved.
        </Message>

        <p v-if="actionDescription && canPreview" class="text-sm text-muted-color italic">
          {{ actionDescription }}
        </p>

        <div>
          <Button label="Preview" icon="pi pi-eye" severity="warn" :loading="previewLoading" :disabled="!canPreview" @click="preview" />
        </div>
      </div>

      <MassActionsPreviewDialog
        v-model:visible="showPreview"
        :data="previewData"
        :action-description="actionDescription"
        :push-days="action === 'push-due' ? pushDays : null"
        :stagger-batch-size="action === 'push-due' ? staggerBatchSize : null"
        @load-range="loadRange"
        :on-execute="execute"
      />
    </template>
  </Card>
</template>
