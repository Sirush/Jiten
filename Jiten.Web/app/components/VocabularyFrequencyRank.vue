<script setup lang="ts">
  import Popover from 'primevue/popover';
  import type { WordFrequencyRanks } from '~/types';

  const props = defineProps<{
    ranks: WordFrequencyRanks | null | undefined;
    /** Global rank from the cached word payload, shown until the per-source ranks arrive. */
    fallbackRank: number;
    listsLoading?: boolean;
    align?: 'left' | 'right';
  }>();

  const emit = defineEmits<{ requestLists: []; changed: [] }>();

  const toast = useToast();
  const confirm = useConfirm();
  const authStore = useAuthStore();
  const srsStore = useSrsStore();

  const op = ref<InstanceType<typeof Popover> | null>(null);
  const saving = ref(false);

  const resolved = computed(() => props.ranks?.resolved);
  const rank = computed(() => resolved.value?.rank ?? props.fallbackRank);

  const sourceLabel = computed(() => {
    const r = resolved.value;
    if (!r || (r.source === 'global' && !r.isFallback)) return null;
    if (r.isFallback) return 'global';
    if (r.source === 'mediaType' && r.mediaType != null) return getMediaTypeText(r.mediaType);
    return r.listName ?? 'your list';
  });

  const fallbackHint = computed(() => {
    const r = resolved.value;
    if (!r?.isFallback || r.mediaType == null) return null;
    return `Not seen in ${getMediaTypeText(r.mediaType)} yet, so this is the global rank.`;
  });

  const typeRows = computed(() =>
    Object.entries(props.ranks?.byType ?? {})
      .map(([mediaType, entry]) => ({ value: Number(mediaType), label: getMediaTypeText(Number(mediaType)), rank: entry.rank }))
      .sort((a, b) => a.rank - b.rank),
  );

  const globalAndTypeRows = computed(() => [
    { value: 0, label: 'Global', rank: props.ranks?.global.rank ?? props.fallbackRank },
    ...typeRows.value,
  ]);

  const listRows = computed(() =>
    (props.ranks?.lists ?? []).map((list) => ({ value: -list.id, label: list.name, rank: list.rank })),
  );

  const currentValue = computed(() => frequencySourceValue(resolved.value));

  function toggle(event: Event) {
    if (!props.ranks?.lists && authStore.isAuthenticated) emit('requestLists');
    op.value?.toggle(event);
  }

  function labelFor(value: number) {
    if (value === 0) return 'Global';
    if (value > 0) return getMediaTypeText(value);
    return listRows.value.find((l) => l.value === value)?.label ?? 'your list';
  }

  function choose(value: number) {
    if (!authStore.isAuthenticated || value === currentValue.value) return;

    op.value?.hide();
    confirm.require({
      message: `Make ${labelFor(value)} your default frequency source? Ranks everywhere on Jiten and in connected apps will use it.`,
      header: 'Change frequency source',
      icon: 'pi pi-sort-numeric-down',
      accept: () => save(value),
    });
  }

  async function save(value: number) {
    saving.value = true;
    try {
      await srsStore.fetchSettings();
      await srsStore.updateSettings({ ...srsStore.studySettings, ...frequencySourcePatch(value) });
      toast.add({ severity: 'success', summary: `Ranks now come from ${labelFor(value)}`, life: 2000 });
      emit('changed');
    } catch (e) {
      toast.add({
        severity: 'error',
        summary: 'Could not save',
        detail: extractApiError(e, 'Your frequency source was not changed.'),
        life: 5000,
      });
    } finally {
      saving.value = false;
    }
  }
</script>

<template>
  <span class="inline-flex flex-col leading-tight" :class="align === 'right' ? 'items-end' : 'items-start'">
    <span class="inline-flex items-center gap-1 whitespace-nowrap">
      <span>Rank #{{ rank > 0 ? rank.toLocaleString() : '—' }}</span>
      <button
        type="button"
        class="inline-flex h-5 w-5 shrink-0 items-center justify-center rounded text-surface-500 dark:text-surface-400 hover:bg-surface-100 dark:hover:bg-surface-700 cursor-pointer"
        :aria-label="`Frequency source: ${sourceLabel ?? 'Global'}`"
        :disabled="saving"
        @click="toggle"
      >
        <i class="pi pi-chevron-down text-[0.6rem]" />
      </button>
    </span>
    <Tooltip v-if="sourceLabel && fallbackHint" :content="fallbackHint">
      <span class="text-xs whitespace-nowrap cursor-help">in {{ sourceLabel }}</span>
    </Tooltip>
    <span v-else-if="sourceLabel" class="text-xs whitespace-nowrap">in {{ sourceLabel }}</span>

    <Popover ref="op" :pt="{ content: { class: 'p-1' } }">
      <div class="flex flex-col min-w-56 text-left">
        <span class="px-3 py-1 text-xs font-semibold text-surface-400 uppercase tracking-wide">Rank source</span>

        <button
          v-for="row in globalAndTypeRows"
          :key="row.value"
          type="button"
          class="flex items-center gap-2 rounded-md px-3 py-1.5 text-sm"
          :class="[
            authStore.isAuthenticated ? 'cursor-pointer hover:bg-surface-100 dark:hover:bg-surface-700' : 'cursor-default',
            row.value === currentValue
              ? 'font-semibold text-primary-600 dark:text-primary-300'
              : 'text-surface-700 dark:text-surface-300',
          ]"
          @click="choose(row.value)"
        >
          <i class="w-3 text-center text-[0.65rem]" :class="row.value === currentValue ? 'pi pi-check' : ''" />
          <span class="truncate">{{ row.label }}</span>
          <span class="ml-auto pl-3 tabular-nums text-xs">#{{ row.rank.toLocaleString() }}</span>
        </button>

        <template v-if="authStore.isAuthenticated">
          <div class="border-t border-surface-200 dark:border-surface-700 my-1" />
          <span class="px-3 py-1 text-xs font-semibold text-surface-400 uppercase tracking-wide">Your lists</span>

          <div v-if="listsLoading" class="flex justify-center py-2">
            <i class="pi pi-spin pi-spinner text-surface-400" />
          </div>
          <span v-else-if="listRows.length === 0" class="px-3 py-1.5 text-sm text-surface-400 italic">
            No saved frequency lists
          </span>
          <template v-else>
            <button
              v-for="row in listRows"
              :key="row.value"
              type="button"
              class="flex items-center gap-2 rounded-md px-3 py-1.5 text-sm cursor-pointer hover:bg-surface-100 dark:hover:bg-surface-700"
              :class="
                row.value === currentValue
                  ? 'font-semibold text-primary-600 dark:text-primary-300'
                  : 'text-surface-700 dark:text-surface-300'
              "
              @click="choose(row.value)"
            >
              <i class="w-3 text-center text-[0.65rem]" :class="row.value === currentValue ? 'pi pi-check' : ''" />
              <span class="truncate max-w-40">{{ row.label }}</span>
              <span class="ml-auto pl-3 tabular-nums text-xs" :class="row.rank === 0 ? 'text-surface-400 italic' : ''">
                {{ row.rank > 0 ? `#${row.rank.toLocaleString()}` : 'not in list' }}
              </span>
            </button>
          </template>
        </template>
        <span v-else class="px-3 py-1.5 text-xs text-surface-400">Sign in to make one of these your default.</span>
      </div>
    </Popover>
  </span>
</template>
