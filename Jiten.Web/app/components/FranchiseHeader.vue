<script setup lang="ts">
  import Skeleton from 'primevue/skeleton';
  import type { Franchise, FranchiseNode, MediaGroupStats, MediaGroupTitles, MediaType } from '~/types';
  import { useAuthStore } from '~/stores/authStore';
  import { useJitenStore } from '~/stores/jitenStore';
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
  const renameButton = ref<{ $el?: HTMLElement } | null>(null);

  const storedTitles = computed<MediaGroupTitles>(() => {
    const f = props.franchise;
    return f.originalTitle
      ? { originalTitle: f.originalTitle, romajiTitle: f.romajiTitle, englishTitle: f.englishTitle }
      : { originalTitle: props.name, romajiTitle: null, englishTitle: null };
  });

  async function stopRename() {
    renaming.value = false;
    await nextTick();
    renameButton.value?.$el?.focus();
  }

  async function onRenamed() {
    emit('renamed');
    await stopRename();
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
          @click="renaming = true"
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

    <FranchiseRenameForm
      v-if="renaming"
      :franchise-id="franchise.franchiseId"
      :titles="storedTitles"
      :name-is-manual="franchise.nameIsManual"
      @saved="onRenamed"
      @close="stopRename"
    />

    <div v-if="hasChipRow" class="flex flex-wrap items-start gap-x-6 gap-y-3 max-sm:flex-col max-sm:flex-nowrap max-sm:items-stretch">
      <FranchiseScopeChips :chips="chips" :active-key="activeChip?.key ?? null" @select="(chip) => emit('selectScope', chip)" />
      <FranchiseMediaChips v-model="mediaTypes" :members="scopedNodes" />
    </div>
  </header>
</template>
