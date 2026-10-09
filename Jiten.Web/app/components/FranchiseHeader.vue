<script setup lang="ts">
  import Skeleton from 'primevue/skeleton';
  import InputText from 'primevue/inputtext';
  import type { Franchise, FranchiseNode, MediaGroupStats, MediaType } from '~/types';
  import { useAuthStore } from '~/stores/authStore';
  import { useJitenStore } from '~/stores/jitenStore';
  import { FRANCHISE_NAME_MAX_LENGTH, useFranchiseRename } from '~/composables/useFranchiseRename';
  import { franchiseEntryCountLabel, franchiseFirstNode, franchiseYearRange } from '~/utils/franchiseLayout';
  import { mediaGroupKindWord, type ScopeChip } from '~/utils/mediaGroup';

  const props = defineProps<{
    franchise: Franchise & { franchiseId: number };
    name: string;
    chips: ScopeChip[];
    activeChip: ScopeChip | null;
    scopedNodes: FranchiseNode[];
    selectedNodes: FranchiseNode[];
    stats: MediaGroupStats | null;
    statsStatus: 'loading' | 'ready' | 'error';
    currentDeckId?: number | null;
  }>();

  const mediaTypes = defineModel<MediaType[]>('mediaTypes', { required: true });

  const emit = defineEmits<{ selectScope: [chip: ScopeChip]; renamed: []; retryStats: [] }>();

  const authStore = useAuthStore();
  const jitenStore = useJitenStore();
  const showAdminTools = computed(() => authStore.isAdmin && jitenStore.displayAdminFunctions);

  const years = computed(() => franchiseYearRange(props.selectedNodes));
  const entryLabel = computed(() => franchiseEntryCountLabel(props.selectedNodes.length, props.franchise.nodes.length));
  const builderLink = computed(() => {
    const deckId = props.franchise.nodes.some((n) => n.deckId === props.currentDeckId)
      ? props.currentDeckId
      : franchiseFirstNode(props.franchise.nodes)?.deckId;
    return deckId ? `/dashboard/franchise/${deckId}` : null;
  });
  const scopeWord = computed(() => mediaGroupKindWord(props.activeChip?.scope?.kind).toLowerCase());
  const hasChipRow = computed(() => props.chips.length > 1 || new Set(props.scopedNodes.map((n) => n.mediaType)).size > 1);

  const renaming = ref(false);
  const rename = useFranchiseRename({
    saved: async () => {
      emit('renamed');
      await stopRename();
    },
    close: () => stopRename(),
  });
  const { value: renameValue, busy: renameBusy, error: renameError } = rename;
  const renameInput = ref<{ $el?: HTMLElement } | null>(null);
  const renameButton = ref<{ $el?: HTMLElement } | null>(null);

  const storedName = computed(() => props.franchise.name || props.name);

  async function startRename() {
    rename.reset(storedName.value);
    renaming.value = true;
    await nextTick();
    const el = renameInput.value?.$el;
    const input = el instanceof HTMLInputElement ? el : el?.querySelector('input');
    input?.focus();
    input?.select();
  }

  async function stopRename() {
    renaming.value = false;
    await nextTick();
    renameButton.value?.$el?.focus();
  }

  function saveName(name: string | null) {
    rename.save(props.franchise.franchiseId, name);
  }

  function submitRename() {
    rename.submit({ franchiseId: props.franchise.franchiseId, name: storedName.value, nameIsManual: props.franchise.nameIsManual });
  }
</script>

<template>
  <header class="flex flex-col gap-3">
    <div class="flex flex-col gap-1">
      <div class="group/title flex min-w-0 items-center gap-x-1">
        <h1 class="min-w-0 break-words text-xl font-bold md:text-2xl">
          <span v-bind="japaneseTextAttrs(name)">{{ name }}</span>
          <span class="font-normal"> Franchise</span>
        </h1>
        <Button
          v-if="showAdminTools && !renaming"
          ref="renameButton"
          icon="pi pi-pencil"
          severity="secondary"
          text
          rounded
          size="small"
          class="!h-11 !w-11 opacity-0 transition-opacity group-hover/title:opacity-100 group-focus-within/title:opacity-100 focus-visible:opacity-100 pointer-coarse:opacity-100"
          aria-label="Rename this franchise"
          @click="startRename"
        />
        <NuxtLink
          v-if="showAdminTools && builderLink"
          :to="builderLink"
          class="ml-auto inline-flex min-h-11 shrink-0 items-center gap-1.5 rounded-md px-2 text-sm text-primary-700 hover:underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary-500 dark:text-primary-300 md:pointer-fine:min-h-9"
        >
          <Icon name="material-symbols:account-tree-outline" aria-hidden="true" />
          Franchise builder
        </NuxtLink>
      </div>

      <div
        aria-live="polite"
        :aria-busy="statsStatus === 'loading'"
        class="-my-1 min-w-0 overflow-hidden py-1 pr-1 text-sm text-surface-600 dark:text-surface-300"
      >
        <div class="-ml-5 flex flex-wrap items-center gap-y-0.5">
          <span class="whitespace-nowrap tabular-nums"><span aria-hidden="true" class="inline-block w-5 text-center">·</span>{{ entryLabel }}</span>
          <span v-if="years" class="whitespace-nowrap tabular-nums"><span aria-hidden="true" class="inline-block w-5 text-center">·</span>{{ years }}</span>
          <template v-if="statsStatus === 'ready' && stats">
            <span class="whitespace-nowrap tabular-nums">
              <span aria-hidden="true" class="inline-block w-5 text-center">·</span>{{ stats.characterCount.toLocaleString() }} characters
            </span>
            <span class="whitespace-nowrap tabular-nums">
              <span aria-hidden="true" class="inline-block w-5 text-center">·</span>{{ stats.uniqueWordCount.toLocaleString() }} unique words
            </span>
            <span class="whitespace-nowrap">
              <span aria-hidden="true" class="inline-block w-5 text-center">·</span>
              <DifficultyDisplay v-if="stats.difficulty >= 0" :difficulty="stats.difficulty" :difficulty-raw="stats.difficulty" />
              <template v-else>Difficulty not rated</template>
            </span>
          </template>
          <span v-else-if="statsStatus === 'error'" class="inline-flex flex-wrap items-center">
            <span aria-hidden="true" class="inline-block w-5 text-center">·</span>The totals for this {{ scopeWord }} failed to load.
            <button
              type="button"
              class="ml-2 inline-flex items-center rounded font-semibold text-primary hover:underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary-500 max-sm:min-h-11"
              @click="emit('retryStats')"
            >
              Retry
            </button>
          </span>
          <div v-else class="inline-flex items-center">
            <span class="sr-only">Loading totals</span>
            <span aria-hidden="true" class="inline-block w-5 text-center">·</span>
            <Skeleton width="16rem" height="0.875rem" class="max-w-[60vw]" />
          </div>
        </div>
      </div>
    </div>

    <form
      v-if="renaming"
      class="flex flex-col gap-2 rounded-md border border-surface-300 p-3 dark:border-surface-700"
      @submit.prevent="submitRename"
      @keydown.esc.prevent="stopRename"
    >
      <label for="franchise-rename" class="text-sm font-medium">Franchise name</label>
      <InputText id="franchise-rename" ref="renameInput" v-model="renameValue" class="w-full" :maxlength="FRANCHISE_NAME_MAX_LENGTH" :invalid="!!renameError" />
      <p class="m-0 text-xs text-surface-600 dark:text-surface-300">
        {{ franchise.nameIsManual ? 'Named by an admin. Syncs keep this name.' : 'Named automatically. Every sync renames it until you set a name.' }}
      </p>
      <p v-if="renameError" class="m-0 text-sm text-red-700 dark:text-red-400" role="alert">{{ renameError }}</p>
      <div class="flex flex-wrap gap-2">
        <Button label="Save name" type="submit" size="small" :loading="renameBusy" />
        <Button
          v-if="franchise.nameIsManual"
          label="Use automatic name"
          type="button"
          severity="secondary"
          size="small"
          :disabled="renameBusy"
          @click="saveName(null)"
        />
        <Button label="Cancel" type="button" severity="secondary" text size="small" :disabled="renameBusy" @click="stopRename" />
      </div>
    </form>

    <div v-if="hasChipRow" class="flex flex-wrap items-start gap-x-6 gap-y-3 max-sm:flex-col max-sm:flex-nowrap max-sm:items-stretch">
      <FranchiseScopeChips :chips="chips" :active-key="activeChip?.key ?? null" @select="(chip) => emit('selectScope', chip)" />
      <FranchiseMediaChips v-model="mediaTypes" :members="scopedNodes" />
    </div>
  </header>
</template>
