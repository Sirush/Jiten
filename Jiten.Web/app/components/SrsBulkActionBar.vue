<script setup lang="ts" generic="T">
  import { useConfirm } from 'primevue/useconfirm';
  import { useToast } from 'primevue/usetoast';
  import { type BulkActionDef, type BulkCardAction, type BulkCardInfo, bulkActionEligibility } from '~/utils/srsBulkActions';

  const props = withDefaults(
    defineProps<{
      items: T[];
      describe: (item: T) => BulkCardInfo;
      applyOptimistic: (action: BulkCardAction, items: T[]) => () => void;
      extraActions?: BulkActionDef[];
      busy?: boolean;
      noun?: string;
    }>(),
    { extraActions: () => [], busy: false, noun: 'card' }
  );

  const emit = defineEmits<{ clear: [] }>();

  const { $api } = useNuxtApp();
  const toast = useToast();
  const confirm = useConfirm();

  const BULK_CHUNK = 5000;
  const FORGET_ACK_THRESHOLD = 25;

  const loading = ref(false);
  const isBusy = computed(() => loading.value || props.busy);

  const plural = (n: number) => `${props.noun}${n !== 1 ? 's' : ''}`;

  const BULK_ACTIONS = Object.keys(bulkActionEligibility) as BulkCardAction[];

  const eligibleByAction = computed(() => {
    const result = Object.fromEntries(BULK_ACTIONS.map((action) => [action, [] as T[]])) as Record<BulkCardAction, T[]>;
    for (const item of props.items) {
      const info = props.describe(item);
      for (const action of BULK_ACTIONS) if (bulkActionEligibility[action](info)) result[action].push(item);
    }
    return result;
  });

  const toKey = (item: T) => {
    const { wordId, readingIndex } = props.describe(item);
    return { wordId, readingIndex };
  };

  const actions = computed<BulkActionDef[]>(() => {
    const defs: BulkActionDef[] = [{ label: 'Add to list', icon: 'pi pi-list', severity: 'secondary', run: openAddToList }];
    const stateAction = (label: string, icon: string, severity: string, action: BulkCardAction) => {
      if (eligibleByAction.value[action].length > 0) defs.push({ label, icon, severity, run: () => confirmAction(label, action) });
    };

    stateAction('Master', 'pi pi-check-circle', 'success', 'neverForget-add');
    stateAction('Unmaster', 'pi pi-replay', 'success', 'neverForget-remove');
    stateAction('Suspend', 'pi pi-pause', 'warn', 'suspend-add');
    stateAction('Resume', 'pi pi-play', 'warn', 'suspend-remove');
    stateAction('Blacklist', 'pi pi-ban', 'secondary', 'blacklist-add');
    stateAction('Unblacklist', 'pi pi-undo', 'secondary', 'blacklist-remove');
    stateAction('Clear lapses', 'pi pi-eraser', 'info', 'clear-lapses');
    stateAction('Reset', 'pi pi-history', 'info', 'reset-schedule');
    defs.push(...props.extraActions);
    if (eligibleByAction.value['forget-add'].length > 0) defs.push({ label: 'Forget', icon: 'pi pi-trash', severity: 'danger', run: openForget });
    return defs;
  });

  function skippedNote(eligible: number) {
    const skipped = props.items.length - eligible;
    if (skipped <= 0) return '';
    return ` ${skipped} selected ${plural(skipped)} ${skipped !== 1 ? 'are' : 'is'} not applicable and will be skipped.`;
  }

  function confirmAction(label: string, action: BulkCardAction) {
    const eligible = eligibleByAction.value[action];
    if (eligible.length === 0) return;

    const n = eligible.length;
    const message =
      action === 'reset-schedule'
        ? `Reset ${n} ${plural(n)}? This clears their scheduling (stability, difficulty, lapses) and puts them back into Learning. Review history is kept.`
        : action === 'clear-lapses'
          ? `Clear the lapse count on ${n} ${plural(n)}? They stop being leeches; scheduling and review history are kept.`
          : `Are you sure you want to ${label.toLowerCase()} ${n} ${plural(n)}?`;

    confirm.require({
      message: message + skippedNote(n),
      header: `Bulk ${label}`,
      acceptLabel: label,
      rejectLabel: 'Cancel',
      acceptClass: 'p-button-danger',
      accept: () => execute(action, eligible),
    });
  }

  async function execute(action: BulkCardAction, eligible: T[]) {
    if (eligible.length === 0) return;

    loading.value = true;
    const rollback = props.applyOptimistic(action, eligible);
    emit('clear');

    try {
      for (let i = 0; i < eligible.length; i += BULK_CHUNK) {
        await $api('srs/set-vocabulary-state-bulk', {
          method: 'POST',
          body: { state: action, items: eligible.slice(i, i + BULK_CHUNK).map(toKey) },
        });
      }
      if (action === 'forget-add') {
        toast.add({ severity: 'success', summary: 'Forgotten', detail: `${eligible.length} ${plural(eligible.length)} forgotten`, life: 3000 });
      }
    } catch {
      rollback();
      toast.add({ severity: 'error', summary: 'Error', detail: `Some ${props.noun}s failed to update`, life: 5000 });
    } finally {
      loading.value = false;
    }
  }

  const forgetVisible = ref(false);
  const forgetItems = shallowRef<T[]>([]);
  const forgetSkipped = ref(0);
  const forgetAck = ref(false);
  const forgetCount = computed(() => forgetItems.value.length);
  const forgetNeedsAck = computed(() => forgetCount.value > FORGET_ACK_THRESHOLD);

  function openForget() {
    forgetItems.value = eligibleByAction.value['forget-add'];
    if (forgetItems.value.length === 0) return;
    forgetSkipped.value = props.items.length - forgetItems.value.length;
    forgetAck.value = false;
    forgetVisible.value = true;
  }

  async function executeForget() {
    if (forgetNeedsAck.value && !forgetAck.value) return;
    forgetVisible.value = false;
    await execute('forget-add', forgetItems.value);
  }

  const addToListVisible = ref(false);
  const addToListCards = ref<{ wordId: number; readingIndex: number }[]>([]);

  function openAddToList() {
    addToListCards.value = props.items.map(toKey);
    addToListVisible.value = true;
  }
</script>

<template>
  <Transition name="slide-up">
    <div
      v-if="items.length > 0"
      class="fixed bottom-0 left-0 right-0 z-40 bg-surface-0 dark:bg-surface-900 border-t border-surface-200 dark:border-surface-700 shadow-[0_-4px_12px_rgba(0,0,0,0.1)] px-4 py-3"
    >
      <div class="container mx-auto flex items-center justify-between gap-3">
        <div class="flex items-center gap-2">
          <span class="text-sm font-medium">{{ items.length }} selected</span>
          <Button label="Clear" text size="small" severity="secondary" @click="emit('clear')" />
        </div>
        <div class="flex gap-2 flex-wrap justify-end">
          <Button
            v-for="action in actions"
            :key="action.label"
            :icon="action.icon"
            :label="action.label"
            :aria-label="action.label"
            size="small"
            :severity="action.severity"
            :loading="isBusy"
            :pt="{ label: { class: 'max-sm:hidden' } }"
            @click="action.run()"
          />
        </div>
      </div>
    </div>
  </Transition>

  <Dialog
    v-model:visible="forgetVisible"
    modal
    :header="`Forget ${forgetCount} ${plural(forgetCount)}`"
    :style="{ width: '30rem' }"
    :breakpoints="{ '500px': '95vw' }"
    :dismissable-mask="!isBusy"
  >
    <div class="flex flex-col gap-4">
      <div class="flex items-start gap-3">
        <i class="pi pi-exclamation-triangle text-2xl text-amber-500 shrink-0 mt-0.5" />
        <div class="text-sm text-surface-700 dark:text-surface-300">
          <p class="mb-2">
            You are about to forget
            <span class="font-bold">{{ forgetCount }}</span>
            {{ plural(forgetCount) }}.
            <template v-if="forgetSkipped > 0">{{ forgetSkipped }} selected {{ plural(forgetSkipped) }} without a card will be skipped.</template>
          </p>
          <p class="mb-2">
            {{ forgetCount === 1 ? 'It leaves' : 'They leave' }} your collection but your review history is kept, so your activity study chart, streaks and
            retention stay as they are, and the FSRS optimiser still trains on it.
          </p>
          <p>
            Restore {{ forgetCount === 1 ? 'it' : 'them' }} any time from
            <NuxtLink to="/settings/vocabulary" class="text-blue-500 hover:underline">Recently Removed</NuxtLink>
            in vocabulary settings, or delete the history completely there.
          </p>
        </div>
      </div>

      <label
        v-if="forgetNeedsAck"
        class="flex items-start gap-2 cursor-pointer rounded-lg border border-amber-200 dark:border-amber-900/40 bg-amber-50 dark:bg-amber-900/10 p-3"
      >
        <Checkbox v-model="forgetAck" :binary="true" class="mt-0.5" />
        <span class="text-sm text-surface-700 dark:text-surface-300"
          >I understand this removes {{ forgetCount }} {{ plural(forgetCount) }} from my collection.</span
        >
      </label>
    </div>

    <template #footer>
      <Button label="Cancel" severity="secondary" text :disabled="isBusy" @click="forgetVisible = false" />
      <Button label="Forget" icon="pi pi-trash" severity="danger" :loading="isBusy" :disabled="forgetNeedsAck && !forgetAck" @click="executeForget" />
    </template>
  </Dialog>

  <SrsAddToListDialog v-model:visible="addToListVisible" :cards="addToListCards" @added="emit('clear')" />
</template>

