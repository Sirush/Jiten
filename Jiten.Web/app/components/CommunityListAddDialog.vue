<script setup lang="ts">
  import { useToast } from 'primevue/usetoast';
  import { CommunityListStatus } from '~/types';
  import type { CommunityListMembership, CommunityListQuota } from '~/types/types';
  import { COMMUNITY_LIST_STATUS_TEXT, usePendingListAdd } from '~/composables/useCommunityLists';
  import { UNDO_TOAST_GROUP, UNDO_VISIBLE_MS, type UndoDetail } from '~/composables/useMediaListSync';
  import { apiErrorMessage } from '~/utils/apiErrorMessage';

  const props = defineProps<{
    deckIds: number[];
    title?: string | null;
  }>();

  const visible = defineModel<boolean>('visible', { required: true });

  const api = useCommunityLists();
  const toast = useToast();
  const pendingAdd = usePendingListAdd();

  const loadState = ref<'loading' | 'ready' | 'failed'>('loading');
  const lists = ref<CommunityListMembership[]>([]);
  const quota = ref<CommunityListQuota | null>(null);
  const busy = ref(new Set<number>());

  const FILTER_THRESHOLD = 6;
  const filter = ref('');
  const visibleLists = computed(() => {
    const needle = filter.value.trim().toLowerCase();
    return needle ? lists.value.filter((l) => l.name.toLowerCase().includes(needle)) : lists.value;
  });

  const total = computed(() => props.deckIds.length);
  const single = computed(() => total.value === 1);
  const header = computed(() => (single.value ? 'Add to a community list' : `Add ${total.value} titles to a community list`));

  async function load() {
    loadState.value = 'loading';
    filter.value = '';
    try {
      const result = await api.membership(props.deckIds);
      lists.value = result.lists;
      quota.value = result.quota;
      loadState.value = 'ready';
    } catch {
      loadState.value = 'failed';
    }
  }

  watch(
    visible,
    (open) => {
      if (open) void load();
    },
    { immediate: true }
  );

  type RowState = { kind: 'in' } | { kind: 'partial' | 'out'; missing: number } | { kind: 'full'; room: number };

  function rowState(list: CommunityListMembership): RowState {
    const missing = total.value - list.contained;
    if (missing === 0) return { kind: 'in' };
    const room = Math.max(0, (quota.value?.maxItems ?? 0) - list.itemCount);
    if (missing > room) return { kind: 'full', room };
    return { kind: list.contained > 0 ? 'partial' : 'out', missing };
  }

  /** Bulk selections only add; taking many titles out of a list belongs in its editor, where the order and notes are visible. */
  function isDisabled(list: CommunityListMembership) {
    const state = rowState(list);
    return busy.value.has(list.listId) || state.kind === 'full' || (state.kind === 'in' && !single.value);
  }

  function ariaChecked(list: CommunityListMembership): 'true' | 'false' | 'mixed' {
    const kind = rowState(list).kind;
    return kind === 'in' ? 'true' : list.contained > 0 ? 'mixed' : 'false';
  }

  function hint(list: CommunityListMembership): string | null {
    const state = rowState(list);
    if (state.kind === 'full') {
      if (state.room === 0) return 'Full';
      return `Room for ${state.room} more`;
    }
    if (single.value) return null;
    if (state.kind === 'in') return `Has all ${total.value}`;
    if (state.kind === 'partial') return `Has ${list.contained} of ${total.value}`;
    return null;
  }

  const titleCount = (count: number) => `${count} ${count === 1 ? 'title' : 'titles'}`;

  async function withBusy(listId: number, run: () => Promise<void>) {
    busy.value.add(listId);
    try {
      await run();
    } catch (e) {
      toast.add({ severity: 'error', summary: apiErrorMessage(e, 'Could not update the list'), life: 6000 });
    } finally {
      busy.value.delete(listId);
    }
  }

  function toggle(list: CommunityListMembership) {
    if (isDisabled(list)) return;
    if (rowState(list).kind === 'in') return remove(list);
    return add(list);
  }

  function add(list: CommunityListMembership) {
    return withBusy(list.listId, async () => {
      const result = await api.addItems(
        list.listId,
        props.deckIds.map((deckId) => ({ deckId, note: null }))
      );
      list.contained = total.value;
      list.itemCount = result.itemCount;
      toast.add({
        severity: 'success',
        summary: single.value ? `Added to ${list.name}` : `Added ${titleCount(result.added)} to ${list.name}`,
        life: 2500,
      });
    });
  }

  function remove(list: CommunityListMembership) {
    const deckId = props.deckIds[0]!;
    return withBusy(list.listId, async () => {
      const removed = await api.removeItem(list.listId, deckId);
      list.contained = 0;
      list.itemCount = removed.itemCount;

      const undo: UndoDetail = {
        run: async () => {
          try {
            const result = await api.addItems(list.listId, [{ deckId, note: removed.note }], removed.position);
            list.contained = 1;
            list.itemCount = result.itemCount;
          } catch (e) {
            toast.add({ severity: 'error', summary: apiErrorMessage(e, 'Could not put the title back'), life: 6000 });
          }
        },
      };
      toast.removeGroup(UNDO_TOAST_GROUP);
      toast.add({ group: UNDO_TOAST_GROUP, severity: 'secondary', summary: `Removed from ${list.name}`, detail: undo, life: UNDO_VISIBLE_MS });
    });
  }

  function startNewList() {
    pendingAdd.value = [...props.deckIds];
    visible.value = false;
    void navigateTo('/lists/new');
  }

  const tooBigForNewList = computed(() => !!quota.value && total.value > quota.value.maxItems);
</script>

<template>
  <Dialog v-model:visible="visible" modal :header="header" :draggable="false" class="w-[28rem] max-w-[calc(100vw-2rem)]">
    <div class="flex flex-col gap-3">
      <p v-if="single && title" class="m-0 truncate text-sm font-semibold" v-bind="japaneseTextAttrs(title)">{{ title }}</p>

      <div v-if="loadState === 'loading'" class="flex flex-col gap-1" aria-busy="true">
        <Skeleton v-for="i in 3" :key="i" height="3.25rem" />
      </div>

      <div v-else-if="loadState === 'failed'" class="flex flex-col items-center gap-3 py-6 text-center">
        <p class="m-0 text-sm text-muted-color">Your lists didn't load.</p>
        <Button label="Retry" icon="pi pi-refresh" size="small" severity="secondary" @click="load" />
      </div>

      <template v-else>
        <p v-if="!lists.length" class="m-0 py-2 text-sm text-muted-color">You don't have any lists yet. Make one to share the titles you'd recommend.</p>

        <IconField v-if="lists.length > FILTER_THRESHOLD">
          <InputIcon class="pi pi-search" />
          <InputText v-model="filter" placeholder="Find a list" aria-label="Find a list" size="small" class="w-full" />
        </IconField>

        <p v-if="lists.length && !visibleLists.length" class="m-0 py-2 text-center text-sm text-muted-color">No list name matches "{{ filter.trim() }}".</p>

        <ul
          v-else-if="lists.length"
          class="m-0 flex h-80 max-h-[50vh] list-none flex-col gap-1 overflow-y-auto p-0"
          :class="{ '!h-auto': lists.length <= FILTER_THRESHOLD }"
        >
          <li
            v-for="list in visibleLists"
            :key="list.listId"
            class="flex items-center gap-1 rounded-lg border border-surface-200 transition-colors dark:border-surface-700"
            :class="{ 'hover:bg-surface-50 dark:hover:bg-surface-800': !isDisabled(list) }"
          >
            <button
              type="button"
              role="checkbox"
              :aria-checked="ariaChecked(list)"
              :aria-disabled="isDisabled(list) || undefined"
              class="flex min-w-0 flex-1 cursor-pointer items-center gap-3 rounded-lg px-3 py-2 text-left focus-visible:outline-2 focus-visible:outline-primary-500 aria-disabled:cursor-default"
              @click="toggle(list)"
            >
              <span
                class="flex h-5 w-5 shrink-0 items-center justify-center rounded border"
                :class="
                  ariaChecked(list) === 'false'
                    ? 'border-surface-300 dark:border-surface-600'
                    : 'border-primary-500 bg-primary-500 text-white dark:border-primary-400 dark:bg-primary-400 dark:text-surface-900'
                "
                aria-hidden="true"
              >
                <i v-if="busy.has(list.listId)" class="pi pi-spin pi-spinner text-[10px]" :class="{ 'text-muted-color': ariaChecked(list) === 'false' }" />
                <i v-else-if="ariaChecked(list) === 'true'" class="pi pi-check text-[10px]" />
                <i v-else-if="ariaChecked(list) === 'mixed'" class="pi pi-minus text-[10px]" />
              </span>
              <span class="flex min-w-0 flex-1 flex-col">
                <span
                  class="truncate text-sm font-medium"
                  :class="{ 'text-muted-color': isDisabled(list) && !busy.has(list.listId) }"
                  v-bind="japaneseTextAttrs(list.name)"
                  >{{ list.name }}</span
                >
                <span class="flex flex-wrap gap-x-2 text-xs text-muted-color">
                  <span :class="{ 'text-amber-700 dark:text-amber-300': list.status === CommunityListStatus.Unlisted }">{{
                    COMMUNITY_LIST_STATUS_TEXT[list.status]
                  }}</span>
                  <span class="tabular-nums">{{ titleCount(list.itemCount) }}</span>
                  <span v-if="hint(list)" class="font-medium">{{ hint(list) }}</span>
                </span>
              </span>
            </button>
            <NuxtLink
              :to="`/lists/${list.listId}/edit`"
              class="mr-1 flex h-8 w-8 shrink-0 items-center justify-center rounded !text-muted-color hover:bg-surface-100 hover:!text-surface-700 focus-visible:outline-2 focus-visible:outline-primary-500 dark:hover:bg-surface-700 dark:hover:!text-surface-100"
              :aria-label="`Edit ${list.name}`"
              @click="visible = false"
            >
              <i class="pi pi-pencil text-xs" aria-hidden="true" />
            </NuxtLink>
          </li>
        </ul>

        <p v-if="quota && lists.some((l) => rowState(l).kind === 'full') && quota.maxItems < quota.fullMaxItems" class="m-0 text-xs text-muted-color">
          Lists hold up to {{ quota.maxItems }} titles on your plan, or {{ quota.fullMaxItems }} with
          <NuxtLink to="/jiten-plus" class="font-semibold">Jiten+</NuxtLink>.
        </p>
      </template>
    </div>

    <template #footer>
      <div class="flex w-full flex-wrap items-center justify-between gap-2">
        <template v-if="loadState === 'ready' && quota">
          <Button
            v-if="quota.canCreate && !tooBigForNewList"
            label="New list"
            icon="pi pi-plus"
            size="small"
            :severity="lists.length ? 'secondary' : undefined"
            :text="!!lists.length"
            @click="startNewList"
          />
          <span v-else-if="!quota.canCreate" class="text-xs text-muted-color">You have {{ quota.listCount }} lists, the most your plan allows.</span>
          <span v-else />
        </template>
        <span v-else />
        <Button label="Done" size="small" severity="secondary" @click="visible = false" />
      </div>
    </template>
  </Dialog>
</template>
