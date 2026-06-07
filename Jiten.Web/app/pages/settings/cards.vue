<script setup lang="ts">
  import { ref, computed } from 'vue';
  import { useConfirm } from 'primevue/useconfirm';
  import { useToast } from 'primevue/usetoast';
  import type { FsrsCardWithWordDto } from '~/types/types';
  import { FsrsState } from '~/types/enums';

  definePageMeta({
    middleware: ['auth'],
  });

  useHead({ title: 'Cards - Settings' });

  const { $api } = useNuxtApp();
  const toast = useToast();
  const confirm = useConfirm();
  const { subscriptions, fetchSubscriptions } = useWordSets();

  const cards = ref<FsrsCardWithWordDto[]>([]);
  const loading = ref(true);
  const searchQuery = ref('');
  const activeFilter = ref<FsrsState | 'all' | 'due'>('all');
  const sortBy = ref<'due' | 'state' | 'rank' | 'lapses' | 'stability' | 'difficulty'>('due');
  const sortAsc = ref(true);
  const selectedIds = ref(new Set<string>());
  const bulkLoading = ref(false);

  // Bulk forget confirmation
  const FORGET_ACK_THRESHOLD = 25;
  const forgetDialogVisible = ref(false);
  const forgetCount = ref(0);
  const forgetAck = ref(false);
  const forgetNeedsAck = computed(() => forgetCount.value > FORGET_ACK_THRESHOLD);
  const actionsOpenFor = ref<string | null>(null);
  const expandedCards = ref(new Set<string>());
  const page = ref(1);
  const pageSize = 50;

  const wordSetTotalWords = computed(() => subscriptions.value.reduce((sum, s) => sum + s.wordCount, 0));
  const hasWordSetSubscriptions = computed(() => subscriptions.value.length > 0);

  onMounted(async () => {
    await fetchCards();
    fetchSubscriptions();
  });

  async function fetchCards() {
    loading.value = true;
    try {
      const fetched = await $api<FsrsCardWithWordDto[]>('user/vocabulary/cards');
      cards.value = fetched.map((card) => ({
        ...card,
        wordTextPlain: stripRuby(card.wordText),
      }));
    } catch {
      toast.add({ severity: 'error', summary: 'Error', detail: 'Failed to load cards', life: 5000 });
    } finally {
      loading.value = false;
    }
  }

  function cardKey(card: FsrsCardWithWordDto) {
    return `${card.wordId}-${card.readingIndex}`;
  }

  // Stats
  const stats = computed(() => {
    const total = cards.value.length;
    const now = new Date();
    const byState: Record<number, number> = {};
    let dueCount = 0;
    for (const c of cards.value) {
      byState[c.state] = (byState[c.state] || 0) + 1;
      if ((c.state === FsrsState.Learning || c.state === FsrsState.Review || c.state === FsrsState.Relearning) && new Date(c.due) <= now) {
        dueCount++;
      }
    }
    return {
      total,
      due: dueCount,
      learning: byState[FsrsState.Learning] || 0,
      review: byState[FsrsState.Review] || 0,
      relearning: byState[FsrsState.Relearning] || 0,
      mastered: byState[FsrsState.Mastered] || 0,
      suspended: byState[FsrsState.Suspended] || 0,
      blacklisted: byState[FsrsState.Blacklisted] || 0,
    };
  });

  const filterChips = computed(() => [
    { key: 'all' as const, label: 'All', count: stats.value.total, color: '' },
    { key: 'due' as const, label: 'Due', count: stats.value.due, color: 'text-red-600 dark:text-red-400' },
    { key: FsrsState.Learning, label: 'Learning', count: stats.value.learning, color: 'text-yellow-600 dark:text-yellow-400' },
    { key: FsrsState.Review, label: 'Review', count: stats.value.review, color: 'text-green-600 dark:text-green-400' },
    { key: FsrsState.Relearning, label: 'Relearning', count: stats.value.relearning, color: 'text-orange-600 dark:text-orange-400' },
    { key: FsrsState.Mastered, label: 'Mastered', count: stats.value.mastered, color: 'text-emerald-600 dark:text-emerald-400' },
    { key: FsrsState.Suspended, label: 'Suspended', count: stats.value.suspended, color: 'text-gray-500 dark:text-gray-400' },
    { key: FsrsState.Blacklisted, label: 'Blacklisted', count: stats.value.blacklisted, color: 'text-gray-500 dark:text-gray-400' },
  ]);

  function setFilter(key: FsrsState | 'all' | 'due') {
    activeFilter.value = activeFilter.value === key ? 'all' : key;
    page.value = 1;
    selectedIds.value.clear();
  }

  // Filtering
  const filteredCards = computed(() => {
    let result = cards.value;
    const now = new Date();

    if (activeFilter.value === 'due') {
      result = result.filter(
        (c) => (c.state === FsrsState.Learning || c.state === FsrsState.Review || c.state === FsrsState.Relearning) && new Date(c.due) <= now
      );
    } else if (activeFilter.value !== 'all') {
      result = result.filter((c) => c.state === activeFilter.value);
    }

    if (searchQuery.value) {
      const q = searchQuery.value.toLowerCase();
      result = result.filter((c) => c.wordTextPlain?.toLowerCase().includes(q));
    }

    return result;
  });

  // Sorting
  const sortedCards = computed(() => {
    const arr = [...filteredCards.value];
    const dir = sortAsc.value ? 1 : -1;

    arr.sort((a, b) => {
      switch (sortBy.value) {
        case 'due':
          return dir * (new Date(a.due).getTime() - new Date(b.due).getTime());
        case 'state':
          return dir * (a.state - b.state);
        case 'rank':
          return dir * ((a.frequencyRank || 999999) - (b.frequencyRank || 999999));
        case 'lapses':
          return dir * ((a.lapses || 0) - (b.lapses || 0));
        case 'stability':
          return dir * ((a.stability ?? 0) - (b.stability ?? 0));
        case 'difficulty':
          return dir * ((a.difficulty ?? 0) - (b.difficulty ?? 0));
        default:
          return 0;
      }
    });

    return arr;
  });

  // Pagination
  const totalPages = computed(() => Math.max(1, Math.ceil(sortedCards.value.length / pageSize)));
  const paginatedCards = computed(() => {
    const start = (page.value - 1) * pageSize;
    return sortedCards.value.slice(start, start + pageSize);
  });

  function prevPage() {
    if (page.value > 1) {
      page.value--;
      selectedIds.value.clear();
    }
  }
  function nextPage() {
    if (page.value < totalPages.value) {
      page.value++;
      selectedIds.value.clear();
    }
  }

  // Relative due date
  function relativeDue(due: Date | undefined): { text: string; severity: 'overdue' | 'today' | 'soon' | 'later' | 'none' } {
    if (!due) return { text: '-', severity: 'none' };
    const now = new Date();
    const d = new Date(due);
    const diffMs = d.getTime() - now.getTime();
    const diffDays = Math.floor(diffMs / 86400000);

    if (diffDays < -1) return { text: `Overdue ${Math.abs(diffDays)}d`, severity: 'overdue' };
    if (diffDays < 0) return { text: 'Overdue 1d', severity: 'overdue' };
    if (diffMs <= 0) return { text: 'Due now', severity: 'overdue' };
    if (diffDays === 0) return { text: 'Due today', severity: 'today' };
    if (diffDays === 1) return { text: 'Tomorrow', severity: 'soon' };
    if (diffDays < 7) return { text: `In ${diffDays}d`, severity: 'soon' };
    if (diffDays < 30) return { text: `In ${Math.floor(diffDays / 7)}w`, severity: 'later' };
    if (diffDays < 365) return { text: `In ${Math.floor(diffDays / 30)}mo`, severity: 'later' };
    return { text: `In ${Math.floor(diffDays / 365)}y`, severity: 'later' };
  }

  function dueClass(severity: string) {
    switch (severity) {
      case 'overdue':
        return 'text-red-600 dark:text-red-400 font-semibold';
      case 'today':
        return 'text-orange-600 dark:text-orange-400 font-semibold';
      case 'soon':
        return 'text-blue-600 dark:text-blue-400';
      case 'later':
        return 'text-surface-500 dark:text-surface-400';
      default:
        return 'text-surface-400';
    }
  }

  function formatFullDateTime(date: Date | undefined): string {
    if (!date) return '-';
    const d = new Date(date);
    return d.toLocaleString(undefined, { year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
  }

  function formatFullDate(date: Date | undefined): string {
    if (!date) return '-';
    const d = new Date(date);
    return d.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
  }

  function formatStability(days: number | undefined): string {
    if (days === undefined || days === null) return '-';
    if (days < 1) return `${Math.round(days * 24)}h`;
    if (days < 30) return `${days.toFixed(1)}d`;
    if (days < 365) return `${(days / 30).toFixed(1)}mo`;
    return `${(days / 365).toFixed(1)}y`;
  }

  function formatDifficulty(d: number | undefined): string {
    if (d === undefined || d === null) return '-';
    return `${d.toFixed(1)}/10`;
  }

  function toggleExpand(key: string) {
    if (expandedCards.value.has(key)) expandedCards.value.delete(key);
    else expandedCards.value.add(key);
  }

  // Selection
  const allOnPageSelected = computed(() => paginatedCards.value.length > 0 && paginatedCards.value.every((c) => selectedIds.value.has(cardKey(c))));

  function toggleSelectAll() {
    if (allOnPageSelected.value) {
      for (const c of paginatedCards.value) selectedIds.value.delete(cardKey(c));
    } else {
      for (const c of paginatedCards.value) selectedIds.value.add(cardKey(c));
    }
  }

  function toggleSelect(card: FsrsCardWithWordDto) {
    const key = cardKey(card);
    if (selectedIds.value.has(key)) selectedIds.value.delete(key);
    else selectedIds.value.add(key);
  }

  const selectedCards = computed(() => cards.value.filter((c) => selectedIds.value.has(cardKey(c))));

  function stateAfterAction(action: string): FsrsState | null {
    switch (action) {
      case 'neverForget-add':
        return FsrsState.Mastered;
      case 'neverForget-remove':
        return FsrsState.Review;
      case 'blacklist-add':
        return FsrsState.Blacklisted;
      case 'blacklist-remove':
        return FsrsState.Review;
      case 'suspend-add':
        return FsrsState.Suspended;
      case 'suspend-remove':
        return FsrsState.Review;
      default:
        return null;
    }
  }

  async function setVocabularyState(card: FsrsCardWithWordDto, state: string) {
    const prevState = card.state;
    const newState = stateAfterAction(state);
    if (newState !== null) card.state = newState;

    try {
      await $api('srs/set-vocabulary-state', {
        method: 'POST',
        body: { wordId: card.wordId, readingIndex: card.readingIndex, state },
      });
    } catch {
      card.state = prevState;
      toast.add({ severity: 'error', summary: 'Error', detail: 'Failed to update card', life: 5000 });
    }
  }

  function getActions(card: FsrsCardWithWordDto) {
    const actions: { label: string; icon: string; action: () => void; severity?: string }[] = [];

    if (card.state === FsrsState.Mastered) {
      actions.push({ label: 'Unmaster', icon: 'pi pi-replay', action: () => setVocabularyState(card, 'neverForget-remove') });
    } else if (card.state === FsrsState.Blacklisted) {
      actions.push({ label: 'Unblacklist', icon: 'pi pi-replay', action: () => setVocabularyState(card, 'blacklist-remove') });
    } else if (card.state === FsrsState.Suspended) {
      actions.push({ label: 'Resume', icon: 'pi pi-play', action: () => setVocabularyState(card, 'suspend-remove') });
    } else {
      actions.push({ label: 'Master', icon: 'pi pi-check-circle', action: () => setVocabularyState(card, 'neverForget-add') });
      actions.push({ label: 'Suspend', icon: 'pi pi-pause', action: () => setVocabularyState(card, 'suspend-add') });
      actions.push({ label: 'Blacklist', icon: 'pi pi-ban', action: () => setVocabularyState(card, 'blacklist-add') });
    }

    actions.push({
      label: 'Forget',
      icon: 'pi pi-trash',
      severity: 'danger',
      action: () => confirmForget(card),
    });

    return actions;
  }

  function confirmForget(card: FsrsCardWithWordDto) {
    const plain = stripRuby(card.wordText);
    confirm.require({
      message: `Forget "${plain}"? This permanently deletes its review history and cannot be undone.`,
      header: 'Forget Card',
      icon: 'pi pi-exclamation-triangle',
      acceptClass: 'p-button-danger',
      accept: () => deleteCard(card),
    });
  }

  async function deleteCard(card: FsrsCardWithWordDto) {
    const key = cardKey(card);
    const idx = cards.value.indexOf(card);
    cards.value.splice(idx, 1);
    selectedIds.value.delete(key);

    try {
      await $api(`user/vocabulary/remove/${card.wordId}/${card.readingIndex}`, { method: 'POST' });
    } catch {
      cards.value.splice(idx, 0, card);
      toast.add({ severity: 'error', summary: 'Error', detail: 'Failed to forget card', life: 5000 });
    }
  }

  // Bulk actions
  function confirmBulkAction(label: string, state: string) {
    const count = selectedIds.value.size;
    confirm.require({
      message: `Are you sure you want to ${label.toLowerCase()} ${count} card${count !== 1 ? 's' : ''}?`,
      header: `Bulk ${label}`,
      acceptLabel: label,
      rejectLabel: 'Cancel',
      acceptClass: 'p-button-danger',
      accept: () => bulkSetState(state),
    });
  }

  async function bulkSetState(state: string) {
    bulkLoading.value = true;
    const selected = selectedCards.value;
    const newState = stateAfterAction(state);
    const prevStates = new Map(selected.map((c) => [cardKey(c), c.state]));

    if (newState !== null) {
      for (const c of selected) c.state = newState;
    }
    selectedIds.value.clear();

    try {
      await Promise.all(
        selected.map((c) =>
          $api('srs/set-vocabulary-state', {
            method: 'POST',
            body: { wordId: c.wordId, readingIndex: c.readingIndex, state },
          })
        )
      );
    } catch {
      for (const c of selected) {
        const prev = prevStates.get(cardKey(c));
        if (prev !== undefined) c.state = prev;
      }
      toast.add({ severity: 'error', summary: 'Error', detail: 'Some cards failed to update', life: 5000 });
    } finally {
      bulkLoading.value = false;
    }
  }

  function confirmBulkForget() {
    if (selectedIds.value.size === 0) return;
    forgetCount.value = selectedIds.value.size;
    forgetAck.value = false;
    forgetDialogVisible.value = true;
  }

  async function executeBulkForget() {
    if (forgetNeedsAck.value && !forgetAck.value) return;

    bulkLoading.value = true;
    const selected = [...selectedCards.value];
    const removedWithIndex = selected.map((c) => ({ card: c, idx: cards.value.indexOf(c) }));

    cards.value = cards.value.filter((c) => !selectedIds.value.has(cardKey(c)));
    selectedIds.value.clear();
    forgetDialogVisible.value = false;

    try {
      await Promise.all(selected.map((c) => $api(`user/vocabulary/remove/${c.wordId}/${c.readingIndex}`, { method: 'POST' })));
      toast.add({ severity: 'success', summary: 'Forgotten', detail: `${selected.length} card${selected.length !== 1 ? 's' : ''} forgotten`, life: 3000 });
    } catch {
      for (const { card, idx } of removedWithIndex.sort((a, b) => a.idx - b.idx)) {
        cards.value.splice(idx, 0, card);
      }
      toast.add({ severity: 'error', summary: 'Error', detail: 'Some cards failed to forget', life: 5000 });
    } finally {
      bulkLoading.value = false;
    }
  }

  function toggleActions(key: string) {
    actionsOpenFor.value = actionsOpenFor.value === key ? null : key;
  }

  function closeActions() {
    actionsOpenFor.value = null;
  }

  const sortOptions = [
    { label: 'Due', value: 'due' },
    { label: 'Rank', value: 'rank' },
    { label: 'State', value: 'state' },
    { label: 'Lapses', value: 'lapses' },
    { label: 'Stability', value: 'stability' },
    { label: 'Difficulty', value: 'difficulty' },
  ];

  function onSortChange() {
    page.value = 1;
    selectedIds.value.clear();
  }

  function toggleSortDir() {
    sortAsc.value = !sortAsc.value;
    page.value = 1;
    selectedIds.value.clear();
  }
</script>

<template>
  <div class="container mx-auto p-2 md:p-4 pb-24" @click="closeActions">
    <SrsSubNav />
    <!-- Header -->
    <div class="flex flex-wrap items-center justify-between gap-2 mb-4 min-h-[2.5rem]">
      <div class="flex items-center gap-2">
        <h1 class="text-2xl font-bold">My Cards</h1>
        <span v-if="!loading" class="text-surface-500 text-sm">({{ stats.total }})</span>
      </div>
      <Button icon="pi pi-refresh" severity="secondary" text rounded :loading="loading" @click="fetchCards" />
    </div>

    <!-- Word set notice -->
    <Message v-if="hasWordSetSubscriptions && !loading" severity="info" :closable="true" class="mb-4">
      Word set subscriptions (~{{ wordSetTotalWords }} words) are managed separately.
      <NuxtLink to="/settings/word-sets" class="font-semibold underline ml-1">Manage Word Sets</NuxtLink>
    </Message>

    <!-- Filter chips -->
    <div class="flex gap-2 overflow-x-auto pb-2 mb-3 -mx-2 px-2 no-scrollbar">
      <button
        v-for="chip in filterChips"
        :key="chip.key"
        class="flex items-center gap-1.5 px-3 py-1.5 rounded-full text-sm font-medium whitespace-nowrap transition-all border shrink-0"
        :class="
          activeFilter === chip.key
            ? 'bg-primary-600 dark:bg-primary-500 text-white border-primary-600 dark:border-primary-500'
            : 'bg-surface-0 dark:bg-surface-800 border-surface-200 dark:border-surface-700 hover:border-surface-400 dark:hover:border-surface-500'
        "
        @click="setFilter(chip.key)"
      >
        <span :class="activeFilter !== chip.key ? chip.color : ''">{{ chip.label }}</span>
        <span v-if="chip.count > 0" class="text-xs tabular-nums" :class="activeFilter === chip.key ? 'opacity-80' : 'opacity-60'">{{ chip.count }}</span>
      </button>
    </div>

    <!-- Search + Sort -->
    <div class="flex gap-2 mb-3">
      <IconField class="flex-1">
        <InputIcon class="pi pi-search" />
        <InputText
          v-model="searchQuery"
          placeholder="Search cards..."
          class="w-full"
          @input="
            page = 1;
            selectedIds.clear();
          "
        />
        <InputIcon
          v-if="searchQuery"
          class="pi pi-times cursor-pointer"
          @click="
            searchQuery = '';
            page = 1;
            selectedIds.clear();
          "
        />
      </IconField>
      <Select v-model="sortBy" :options="sortOptions" option-label="label" option-value="value" class="shrink-0 w-36" @change="onSortChange" />
      <Button
        :icon="sortAsc ? 'pi pi-sort-amount-up' : 'pi pi-sort-amount-down'"
        severity="secondary"
        text
        size="small"
        class="shrink-0"
        @click="toggleSortDir"
      />
    </div>

    <!-- Loading -->
    <div v-if="loading" class="flex flex-col gap-2">
      <div
        v-for="i in 8"
        :key="i"
        class="flex items-center gap-3 p-3 rounded-lg bg-surface-0 dark:bg-surface-900 border border-surface-200 dark:border-surface-700"
      >
        <Skeleton width="1.25rem" height="1.25rem" borderRadius="4px" />
        <Skeleton width="5rem" height="1.5rem" />
        <div class="flex-1" />
        <Skeleton width="4rem" height="1rem" />
        <Skeleton width="3.5rem" height="1.5rem" borderRadius="12px" />
        <Skeleton width="1.5rem" height="1.5rem" shape="circle" />
      </div>
    </div>

    <!-- Empty state -->
    <div
      v-else-if="filteredCards.length === 0"
      class="rounded-xl border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 p-12 text-center"
    >
      <template v-if="cards.length === 0 && hasWordSetSubscriptions">
        <p class="text-surface-500 mb-2">No individual cards.</p>
        <p class="text-surface-400 text-sm mb-4">
          Your vocabulary includes ~{{ wordSetTotalWords }} words from {{ subscriptions.length }} word set{{ subscriptions.length === 1 ? '' : 's' }}.
        </p>
        <NuxtLink to="/settings/word-sets">
          <Button icon="pi pi-cog" label="Manage Word Sets" severity="secondary" size="small" />
        </NuxtLink>
      </template>
      <template v-else-if="cards.length === 0">
        <p class="text-surface-400 mb-2">No cards yet</p>
        <p class="text-surface-400 text-sm">Import vocabulary or add study decks to get started.</p>
      </template>
      <template v-else>
        <p class="text-surface-400">No cards match your filters</p>
      </template>
    </div>

    <!-- Card list -->
    <template v-else>
      <!-- Select all row -->
      <div class="flex items-center gap-3 px-3 py-2 text-sm text-surface-500">
        <Checkbox :model-value="allOnPageSelected" :binary="true" @change="toggleSelectAll" />
        <span class="text-xs cursor-pointer select-none" @click="toggleSelectAll">
          {{ selectedCards.length > 0 ? `${selectedCards.length} selected` : `Select page (${paginatedCards.length})` }}
        </span>
      </div>

      <div
        class="rounded-xl border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 shadow-sm overflow-visible divide-y divide-surface-100 dark:divide-surface-800"
      >
        <div v-for="card in paginatedCards" :key="cardKey(card)">
          <!-- Main row -->
          <div
            class="flex items-center gap-2 sm:gap-3 px-3 py-2 sm:py-2.5 hover:bg-surface-50 dark:hover:bg-surface-800/50 transition-colors cursor-pointer select-none"
            :class="{
              'bg-primary-50 dark:bg-primary-900/20': selectedIds.has(cardKey(card)),
              'bg-surface-50 dark:bg-surface-800/30': expandedCards.has(cardKey(card)) && !selectedIds.has(cardKey(card)),
            }"
            @click="toggleExpand(cardKey(card))"
          >
            <Checkbox :model-value="selectedIds.has(cardKey(card))" :binary="true" @change="toggleSelect(card)" @click.stop />

            <!-- Two-line grid on mobile, single-line flex on desktop -->
            <div class="flex-1 min-w-0 grid grid-cols-[auto_1fr_auto] gap-x-2 gap-y-0.5 items-center sm:flex sm:items-center sm:gap-3">
              <span
                class="font-medium text-primary-600 dark:text-primary-400 truncate min-w-0 col-start-1 col-span-2 row-start-2 sm:hidden"
                v-html="sanitiseHtml(card.wordText)"
              />
              <NuxtLink
                :to="`/vocabulary/${card.wordId}/${card.readingIndex}`"
                target="_blank"
                class="font-medium text-primary-600 dark:text-primary-400 hover:underline truncate min-w-0 hidden sm:inline sm:order-1 sm:shrink"
                v-html="sanitiseHtml(card.wordText)"
                @click.stop
              />

              <span v-if="card.frequencyRank" class="text-xs text-surface-400 tabular-nums row-start-1 col-start-1 sm:order-2 sm:shrink-0">
                #{{ card.frequencyRank.toLocaleString() }}
              </span>

              <div class="hidden sm:block sm:flex-1 sm:order-3" />

              <div class="row-start-2 col-start-3 sm:order-4 sm:shrink-0">
                <Tooltip :content="formatFullDateTime(card.due)" placement="top">
                  <span class="text-xs whitespace-nowrap" :class="dueClass(relativeDue(card.due).severity)">
                    {{ relativeDue(card.due).text }}
                  </span>
                </Tooltip>
              </div>

              <Tag
                :value="getFsrsStateName(card.state)"
                :severity="getFsrsStateSeverity(card.state)"
                class="shrink-0 !text-xs row-start-1 col-start-3 sm:order-5"
              />

              <i
                class="pi text-xs text-surface-400 shrink-0 transition-transform duration-200 hidden sm:inline sm:order-6"
                :class="expandedCards.has(cardKey(card)) ? 'pi-chevron-up' : 'pi-chevron-down'"
              />
            </div>

            <div class="relative shrink-0" @click.stop>
              <Button icon="pi pi-ellipsis-v" text rounded size="small" severity="secondary" class="!w-8 !h-8" @click="toggleActions(cardKey(card))" />

              <Transition name="fade">
                <div
                  v-if="actionsOpenFor === cardKey(card)"
                  class="absolute right-0 top-full mt-1 z-30 min-w-40 max-w-[calc(100vw-1rem)] rounded-lg border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 shadow-lg py-1"
                >
                  <button
                    v-for="action in getActions(card)"
                    :key="action.label"
                    class="w-full flex items-center gap-2 px-3 py-2 text-sm hover:bg-surface-100 dark:hover:bg-surface-800 transition-colors text-left"
                    :class="action.severity === 'danger' ? 'text-red-600 dark:text-red-400' : ''"
                    @click="
                      action.action();
                      closeActions();
                    "
                  >
                    <i :class="action.icon" class="text-xs" />
                    {{ action.label }}
                  </button>
                </div>
              </Transition>
            </div>
          </div>

          <!-- Expanded detail panel -->
          <Transition name="expand">
            <div v-if="expandedCards.has(cardKey(card))" class="px-3 pb-3 pt-1 bg-surface-50 dark:bg-surface-800/30">
              <NuxtLink
                :to="`/vocabulary/${card.wordId}/${card.readingIndex}`"
                target="_blank"
                class="inline-flex items-center gap-1 text-sm text-primary-600 dark:text-primary-400 hover:underline pl-9 mb-1.5 sm:hidden"
              >
                View word <i class="pi pi-external-link text-xs" />
              </NuxtLink>
              <div class="grid grid-cols-2 sm:grid-cols-3 gap-x-6 gap-y-2 text-sm pl-9">
                <div>
                  <div class="text-surface-400 text-xs">State</div>
                  <div class="font-medium">{{ getFsrsStateName(card.state) }}</div>
                </div>
                <div>
                  <div class="text-surface-400 text-xs">Stability</div>
                  <div class="font-medium">{{ formatStability(card.stability) }}</div>
                </div>
                <div>
                  <div class="text-surface-400 text-xs">Difficulty</div>
                  <div class="font-medium">{{ formatDifficulty(card.difficulty) }}</div>
                </div>
                <div>
                  <div class="text-surface-400 text-xs">Lapses</div>
                  <div class="font-medium" :class="card.lapses >= 8 ? 'text-red-600 dark:text-red-400' : ''">{{ card.lapses }}</div>
                </div>
                <div>
                  <div class="text-surface-400 text-xs">Due</div>
                  <div class="font-medium">{{ formatFullDate(card.due) }}</div>
                </div>
                <div>
                  <div class="text-surface-400 text-xs">Created</div>
                  <div class="font-medium">{{ formatFullDate(card.createdAt) }}</div>
                </div>
                <div>
                  <div class="text-surface-400 text-xs">Last review</div>
                  <div class="font-medium">{{ formatFullDate(card.lastReview) }}</div>
                </div>
              </div>
            </div>
          </Transition>
        </div>
      </div>

      <!-- Pagination -->
      <div v-if="totalPages > 1" class="flex items-center justify-between mt-4 px-1">
        <span class="text-sm text-surface-500">
          {{ (page - 1) * pageSize + 1 }}&ndash;{{ Math.min(page * pageSize, sortedCards.length) }} of {{ sortedCards.length }}
        </span>
        <div class="flex gap-1">
          <Button icon="pi pi-chevron-left" severity="secondary" text size="small" :disabled="page <= 1" @click="prevPage" />
          <span class="flex items-center px-2 text-sm text-surface-500 tabular-nums">{{ page }} / {{ totalPages }}</span>
          <Button icon="pi pi-chevron-right" severity="secondary" text size="small" :disabled="page >= totalPages" @click="nextPage" />
        </div>
      </div>
    </template>

    <!-- Bulk action bar -->
    <Transition name="slide-up">
      <div
        v-if="selectedIds.size > 0"
        class="fixed bottom-0 left-0 right-0 z-40 bg-surface-0 dark:bg-surface-900 border-t border-surface-200 dark:border-surface-700 shadow-[0_-4px_12px_rgba(0,0,0,0.1)] px-4 py-3"
      >
        <div class="container mx-auto flex items-center justify-between gap-3">
          <div class="flex items-center gap-2">
            <span class="text-sm font-medium">{{ selectedIds.size }} selected</span>
            <Button label="Clear" text size="small" severity="secondary" @click="selectedIds.clear()" />
          </div>
          <div class="flex gap-2 flex-wrap justify-end">
            <Button
              icon="pi pi-check-circle"
              label="Master"
              size="small"
              severity="success"
              :loading="bulkLoading"
              class="!hidden sm:!inline-flex"
              @click="confirmBulkAction('Master', 'neverForget-add')"
            />
            <Button
              icon="pi pi-check-circle"
              size="small"
              severity="success"
              :loading="bulkLoading"
              class="sm:!hidden"
              @click="confirmBulkAction('Master', 'neverForget-add')"
            />
            <Button
              icon="pi pi-pause"
              label="Suspend"
              size="small"
              severity="warn"
              :loading="bulkLoading"
              class="!hidden sm:!inline-flex"
              @click="confirmBulkAction('Suspend', 'suspend-add')"
            />
            <Button
              icon="pi pi-pause"
              size="small"
              severity="warn"
              :loading="bulkLoading"
              class="sm:!hidden"
              @click="confirmBulkAction('Suspend', 'suspend-add')"
            />
            <Button
              icon="pi pi-ban"
              label="Blacklist"
              size="small"
              severity="secondary"
              :loading="bulkLoading"
              class="!hidden sm:!inline-flex"
              @click="confirmBulkAction('Blacklist', 'blacklist-add')"
            />
            <Button
              icon="pi pi-ban"
              size="small"
              severity="secondary"
              :loading="bulkLoading"
              class="sm:!hidden"
              @click="confirmBulkAction('Blacklist', 'blacklist-add')"
            />
            <Button
              icon="pi pi-trash"
              label="Forget"
              size="small"
              severity="danger"
              :loading="bulkLoading"
              class="!hidden sm:!inline-flex"
              @click="confirmBulkForget"
            />
            <Button icon="pi pi-trash" size="small" severity="danger" :loading="bulkLoading" class="sm:!hidden" @click="confirmBulkForget" />
          </div>
        </div>
      </div>
    </Transition>

    <!-- Bulk forget confirmation -->
    <Dialog
      v-model:visible="forgetDialogVisible"
      modal
      :header="`Forget ${forgetCount} card${forgetCount !== 1 ? 's' : ''}`"
      :style="{ width: '30rem' }"
      :breakpoints="{ '500px': '95vw' }"
      :dismissable-mask="!bulkLoading"
    >
      <div class="flex flex-col gap-4">
        <div class="flex items-start gap-3">
          <i class="pi pi-exclamation-triangle text-2xl text-red-500 shrink-0 mt-0.5" />
          <div class="text-sm text-surface-700 dark:text-surface-300">
            <p class="mb-2">
              You are about to permanently forget
              <span class="font-bold text-red-600 dark:text-red-400">{{ forgetCount }}</span>
              card{{ forgetCount !== 1 ? 's' : '' }}.
            </p>
            <p>
              This deletes all FSRS review history for {{ forgetCount === 1 ? 'this card' : 'these cards' }} and <strong>cannot be undone</strong>. Fewer review
              logs also reduce the data available to the FSRS parameter optimiser, which can make future scheduling less accurate.
            </p>
          </div>
        </div>

        <label
          v-if="forgetNeedsAck"
          class="flex items-start gap-2 cursor-pointer rounded-lg border border-red-200 dark:border-red-900/40 bg-red-50 dark:bg-red-900/10 p-3"
        >
          <Checkbox v-model="forgetAck" :binary="true" class="mt-0.5" />
          <span class="text-sm text-surface-700 dark:text-surface-300">
            I understand this permanently deletes the review history for {{ forgetCount }} cards.
          </span>
        </label>
      </div>

      <template #footer>
        <Button label="Cancel" severity="secondary" text :disabled="bulkLoading" @click="forgetDialogVisible = false" />
        <Button
          label="Forget"
          icon="pi pi-trash"
          severity="danger"
          :loading="bulkLoading"
          :disabled="forgetNeedsAck && !forgetAck"
          @click="executeBulkForget"
        />
      </template>
    </Dialog>
  </div>
</template>

<style scoped>
  .no-scrollbar::-webkit-scrollbar {
    display: none;
  }
  .no-scrollbar {
    -ms-overflow-style: none;
    scrollbar-width: none;
  }

  .fade-enter-active,
  .fade-leave-active {
    transition: opacity 0.15s;
  }
  .fade-enter-from,
  .fade-leave-to {
    opacity: 0;
  }

  .slide-up-enter-active {
    transition:
      transform 0.2s ease-out,
      opacity 0.2s ease-out;
  }
  .slide-up-leave-active {
    transition:
      transform 0.15s ease-in,
      opacity 0.15s ease-in;
  }
  .slide-up-enter-from {
    transform: translateY(100%);
    opacity: 0;
  }
  .slide-up-leave-to {
    transform: translateY(100%);
    opacity: 0;
  }

  .expand-enter-active {
    transition:
      max-height 0.2s ease-out,
      opacity 0.2s ease-out;
    overflow: hidden;
  }
  .expand-leave-active {
    transition:
      max-height 0.15s ease-in,
      opacity 0.15s ease-in;
    overflow: hidden;
  }
  .expand-enter-from {
    max-height: 0;
    opacity: 0;
  }
  .expand-enter-to {
    max-height: 10rem;
  }
  .expand-leave-from {
    max-height: 10rem;
  }
  .expand-leave-to {
    max-height: 0;
    opacity: 0;
  }
</style>
