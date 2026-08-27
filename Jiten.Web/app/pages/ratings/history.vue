<script setup lang="ts">
  import { useToast } from 'primevue/usetoast';
  import { useConfirm } from 'primevue/useconfirm';
  import type { DifficultyVoteDto, DifficultyRatingDto, BlacklistedDeckDto, DeckSummaryDto } from '~/types/types';
  import { ComparisonOutcome, TitleLanguage } from '~/types';

  definePageMeta({
    middleware: ['auth'],
  });

  useHead({ title: 'My Votes - Jiten' });

  const toast = useToast();
  const confirm = useConfirm();
  const { fetchMyVotes, deleteVote, deleteSkip, submitRating, fetchBlockedDecks, unblockDeck } = useDifficultyVotes();
  const jitenStore = useJitenStore();

  function localizedTitle(item: { title?: string; deckTitle?: string; romajiTitle?: string; englishTitle?: string }): string {
    const title = item.title ?? item.deckTitle ?? '';
    if (jitenStore.titleLanguage === TitleLanguage.English) return item.englishTitle ?? item.romajiTitle ?? title;
    if (jitenStore.titleLanguage === TitleLanguage.Romaji) return item.romajiTitle ?? title;
    return title;
  }

  function deckTitle(deck: DeckSummaryDto): string {
    return localizedTitle(deck);
  }

  const activeTab = ref(0);
  const limit = 20;

  const comparisons = ref<DifficultyVoteDto[]>([]);
  const comparisonsTotal = ref(0);
  const comparisonsOffset = ref(0);

  const ratings = ref<DifficultyRatingDto[]>([]);
  const ratingsTotal = ref(0);
  const ratingsOffset = ref(0);

  const skipped = ref<DifficultyVoteDto[]>([]);
  const skippedTotal = ref(0);
  const skippedOffset = ref(0);

  const isLoading = ref(false);

  function getVoteDisplay(vote: DifficultyVoteDto) {
    const { outcome, deckA, deckB } = vote;
    if (outcome === ComparisonOutcome.Same) {
      return { harderDeck: null, easierDeck: null, isSame: true, intensity: 'similar' as const };
    }
    const aIsHarder = outcome > 0;
    return {
      harderDeck: aIsHarder ? deckA : deckB,
      easierDeck: aIsHarder ? deckB : deckA,
      isSame: false,
      intensity: (Math.abs(outcome) === 2 ? 'much harder' : 'harder') as 'much harder' | 'harder',
    };
  }

  function formatDate(dateString: string): string {
    const date = new Date(dateString);
    return date.toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
  }

  const ratingOptions = [
    { label: 'Beginner', value: 0, bg: 'rgba(21, 128, 61, 0.8)', bgHover: 'rgba(21, 128, 61, 0.3)' },
    { label: 'Easy', value: 1, bg: 'rgba(34, 197, 94, 0.8)', bgHover: 'rgba(34, 197, 94, 0.3)' },
    { label: 'Average', value: 2, bg: 'rgba(6, 182, 212, 0.8)', bgHover: 'rgba(6, 182, 212, 0.3)' },
    { label: 'Hard', value: 3, bg: 'rgba(217, 119, 6, 0.8)', bgHover: 'rgba(217, 119, 6, 0.3)' },
    { label: 'Expert', value: 4, bg: 'rgba(220, 38, 38, 0.8)', bgHover: 'rgba(220, 38, 38, 0.3)' },
  ];

  async function loadComparisons() {
    isLoading.value = true;
    const result = await fetchMyVotes({ type: 'comparisons', offset: comparisonsOffset.value, limit });
    if (result) {
      comparisons.value = result.data;
      comparisonsTotal.value = result.totalItems;
    }
    isLoading.value = false;
  }

  async function loadRatings() {
    isLoading.value = true;
    const result = await fetchMyVotes({ type: 'ratings', offset: ratingsOffset.value, limit });
    if (result) {
      ratings.value = result.data;
      ratingsTotal.value = result.totalItems;
    }
    isLoading.value = false;
  }

  async function loadSkipped() {
    isLoading.value = true;
    const result = await fetchMyVotes({ type: 'skipped', offset: skippedOffset.value, limit });
    if (result) {
      skipped.value = result.data;
      skippedTotal.value = result.totalItems;
    }
    isLoading.value = false;
  }

  function handleDeleteVote(id: number) {
    confirm.require({
      message: 'Are you sure you want to delete this vote?',
      header: 'Delete Vote',
      icon: 'pi pi-exclamation-triangle',
      rejectProps: { label: 'Cancel', severity: 'secondary', outlined: true },
      acceptProps: { label: 'Delete', severity: 'danger' },
      accept: async () => {
        const success = await deleteVote(id);
        if (success) {
          toast.add({ severity: 'success', summary: 'Vote deleted', life: 2000 });
          loadComparisons();
        }
      },
    });
  }

  async function handleUpdateRating(deckId: number, rating: number, dto: DifficultyRatingDto) {
    if (dto.rating === rating) return;
    const success = await submitRating(deckId, rating);
    if (success) {
      dto.rating = rating;
      toast.add({ severity: 'success', summary: 'Rating updated', life: 2000 });
    }
  }

  function handleDeleteSkipped(id: number) {
    confirm.require({
      message: 'Are you sure you want to remove this skipped pair?',
      header: 'Remove Skipped Pair',
      icon: 'pi pi-exclamation-triangle',
      rejectProps: { label: 'Cancel', severity: 'secondary', outlined: true },
      acceptProps: { label: 'Remove', severity: 'danger' },
      accept: async () => {
        const success = await deleteSkip(id);
        if (success) {
          toast.add({ severity: 'success', summary: 'Skipped pair removed', life: 2000 });
          loadSkipped();
        }
      },
    });
  }

  function onComparisonsPage(event: { first: number }) {
    comparisonsOffset.value = event.first;
    loadComparisons();
  }

  function onRatingsPage(event: { first: number }) {
    ratingsOffset.value = event.first;
    loadRatings();
  }

  function onSkippedPage(event: { first: number }) {
    skippedOffset.value = event.first;
    loadSkipped();
  }

  const blocked = ref<BlacklistedDeckDto[]>([]);
  const blockedTotal = ref(0);
  const blockedOffset = ref(0);

  async function loadBlocked() {
    isLoading.value = true;
    const result = await fetchBlockedDecks({ offset: blockedOffset.value, limit });
    if (result) {
      blocked.value = result.data;
      blockedTotal.value = result.totalItems;
    }
    isLoading.value = false;
  }

  function onBlockedPage(event: { first: number }) {
    blockedOffset.value = event.first;
    loadBlocked();
  }

  function handleUnblock(deckId: number, title: string) {
    confirm.require({
      message: `Unblock "${title}" from comparisons?`,
      header: 'Unblock Deck',
      icon: 'pi pi-question-circle',
      rejectProps: { label: 'Cancel', severity: 'secondary', outlined: true },
      acceptProps: { label: 'Unblock' },
      accept: async () => {
        const success = await unblockDeck(deckId);
        if (success) {
          toast.add({ severity: 'success', summary: 'Deck unblocked', life: 2000 });
          blocked.value = blocked.value.filter((b) => b.deckId !== deckId);
        }
      },
    });
  }

  watch(activeTab, (tab) => {
    if (tab === 0) loadComparisons();
    else if (tab === 1) loadRatings();
    else if (tab === 2) loadSkipped();
    else if (tab === 3) loadBlocked();
  });

  onMounted(() => loadComparisons());
</script>

<template>
  <div class="container mx-auto p-2 md:p-4">
    <div class="flex items-center justify-between mb-6">
      <h1 class="text-2xl font-bold">My Votes</h1>
      <NuxtLink to="/ratings" class="text-primary-500 hover:underline font-semibold">Compare more media →</NuxtLink>
    </div>

    <Tabs v-model:value="activeTab" :show-navigators="false">
      <TabList>
        <Tab :value="0">Comparisons</Tab>
        <Tab :value="1">Ratings</Tab>
        <Tab :value="2">Skipped</Tab>
        <Tab :value="3">Blocked</Tab>
      </TabList>
    </Tabs>

    <div v-if="isLoading" class="flex justify-center py-12">
      <ProgressSpinner style="width: 50px; height: 50px" />
    </div>

    <!-- Comparisons tab -->
    <div v-else-if="activeTab === 0" class="mt-4">
      <div v-if="comparisons.length === 0" class="text-center py-12 text-muted-color">
        <i class="pi pi-inbox text-4xl mb-3" />
        <p>No comparisons yet.</p>
        <Button label="Start comparing" icon="pi pi-arrow-right" icon-pos="right" text class="mt-2" as="router-link" to="/ratings" />
      </div>
      <div v-else class="flex flex-col gap-3">
        <Card v-for="vote in comparisons" :key="vote.id" class="shadow-sm">
          <template #content>
            <div class="flex flex-col md:flex-row items-start md:items-center gap-3">
              <div class="flex-1 min-w-0">
                <div v-if="getVoteDisplay(vote).isSame" class="flex items-center gap-2 flex-wrap">
                  <NuxtLink :to="`/decks/media/${vote.deckA.id}/detail`" class="font-semibold hover:text-primary-500">
                    {{ deckTitle(vote.deckA) }}
                  </NuxtLink>
                  <span class="text-muted-color">≈ similar to</span>
                  <NuxtLink :to="`/decks/media/${vote.deckB.id}/detail`" class="font-semibold hover:text-primary-500">
                    {{ deckTitle(vote.deckB) }}
                  </NuxtLink>
                </div>
                <div v-else class="flex items-center gap-2 flex-wrap">
                  <NuxtLink :to="`/decks/media/${getVoteDisplay(vote).harderDeck!.id}/detail`" class="font-bold vote-harder hover:text-orange-400">
                    {{ deckTitle(getVoteDisplay(vote).harderDeck!) }}
                  </NuxtLink>
                  <span class="text-muted-color">◄ {{ getVoteDisplay(vote).intensity }} than</span>
                  <NuxtLink :to="`/decks/media/${getVoteDisplay(vote).easierDeck!.id}/detail`" class="font-semibold hover:text-primary-500">
                    {{ deckTitle(getVoteDisplay(vote).easierDeck!) }}
                  </NuxtLink>
                </div>
                <div class="text-sm text-muted-color mt-1">{{ formatDate(vote.createdAt) }}</div>
              </div>
              <Button v-tooltip.left="'Delete vote'" icon="pi pi-trash" severity="danger" text size="small" @click="handleDeleteVote(vote.id)" />
            </div>
          </template>
        </Card>
      </div>
      <Paginator
        v-if="comparisonsTotal > limit"
        :rows="limit"
        :total-records="comparisonsTotal"
        :first="comparisonsOffset"
        class="mt-4"
        @page="onComparisonsPage"
      />
    </div>

    <!-- Ratings tab -->
    <div v-else-if="activeTab === 1" class="mt-4">
      <div v-if="ratings.length === 0" class="text-center py-12 text-muted-color">
        <i class="pi pi-inbox text-4xl mb-3" />
        <p>No ratings yet.</p>
      </div>
      <div v-else class="flex flex-col gap-3">
        <Card v-for="rating in ratings" :key="rating.id" class="shadow-sm">
          <template #content>
            <div class="flex items-center gap-3">
              <NuxtLink :to="`/decks/media/${rating.deckId}/detail`" class="shrink-0">
                <img :src="rating.coverUrl || '/img/nocover.jpg'" :alt="localizedTitle(rating)" class="h-16 w-11 object-cover rounded" />
              </NuxtLink>
              <div class="flex flex-col md:flex-row md:items-center gap-3 flex-1 min-w-0">
                <div class="flex-1 min-w-0">
                  <div class="flex items-center gap-2 min-w-0">
                    <NuxtLink :to="`/decks/media/${rating.deckId}/detail`" class="font-semibold hover:text-primary-500 truncate">
                      {{ localizedTitle(rating) }}
                    </NuxtLink>
                    <Tag :value="getMediaTypeText(rating.mediaType)" severity="secondary" class="shrink-0" />
                  </div>
                  <div class="text-sm text-muted-color mt-1">{{ formatDate(rating.createdAt) }}</div>
                </div>
                <div class="flex flex-wrap gap-2">
                  <button
                    v-for="opt in ratingOptions"
                    :key="opt.value"
                    class="difficulty-btn"
                    :class="{ 'is-selected': rating.rating === opt.value }"
                    :style="{ '--diff-bg': opt.bg, '--diff-bg-hover': opt.bgHover }"
                    @click="handleUpdateRating(rating.deckId, opt.value, rating)"
                  >
                    {{ opt.label }}
                  </button>
                </div>
              </div>
            </div>
          </template>
        </Card>
      </div>
      <Paginator v-if="ratingsTotal > limit" :rows="limit" :total-records="ratingsTotal" :first="ratingsOffset" class="mt-4" @page="onRatingsPage" />
    </div>

    <!-- Skipped tab -->
    <div v-else-if="activeTab === 2" class="mt-4">
      <div v-if="skipped.length === 0" class="text-center py-12 text-muted-color">
        <i class="pi pi-inbox text-4xl mb-3" />
        <p>No skipped pairs.</p>
      </div>
      <div v-else class="flex flex-col gap-3">
        <Card v-for="item in skipped" :key="item.id" class="shadow-sm">
          <template #content>
            <div class="flex flex-col md:flex-row items-start md:items-center gap-3">
              <div class="flex-1 min-w-0">
                <div class="flex items-center gap-2 flex-wrap">
                  <NuxtLink :to="`/decks/media/${item.deckA.id}/detail`" class="font-semibold hover:text-primary-500">
                    {{ deckTitle(item.deckA) }}
                  </NuxtLink>
                  <span class="text-muted-color">vs</span>
                  <NuxtLink :to="`/decks/media/${item.deckB.id}/detail`" class="font-semibold hover:text-primary-500">
                    {{ deckTitle(item.deckB) }}
                  </NuxtLink>
                </div>
                <div class="text-sm text-muted-color mt-1">{{ formatDate(item.createdAt) }}</div>
              </div>
              <div class="flex gap-2">
                <Button label="Vote now" icon="pi pi-arrow-right" icon-pos="right" size="small" as="router-link" to="/ratings" />
                <Button v-tooltip.left="'Remove'" icon="pi pi-trash" severity="danger" text size="small" @click="handleDeleteSkipped(item.id)" />
              </div>
            </div>
          </template>
        </Card>
      </div>
      <Paginator v-if="skippedTotal > limit" :rows="limit" :total-records="skippedTotal" :first="skippedOffset" class="mt-4" @page="onSkippedPage" />
    </div>

    <!-- Blocked tab -->
    <div v-else-if="activeTab === 3" class="mt-4">
      <div v-if="blocked.length === 0" class="text-center py-12 text-muted-color">
        <i class="pi pi-inbox text-4xl mb-3" />
        <p>No blocked decks.</p>
      </div>
      <div v-else class="flex flex-col gap-3">
        <Card v-for="item in blocked" :key="item.deckId" class="shadow-sm">
          <template #content>
            <div class="flex items-center gap-3">
              <NuxtLink :to="`/decks/media/${item.deckId}/detail`" class="shrink-0">
                <img :src="item.coverUrl || '/img/nocover.jpg'" :alt="localizedTitle(item)" class="h-16 w-11 object-cover rounded" />
              </NuxtLink>
              <div class="flex-1 min-w-0">
                <div class="flex items-center gap-2 min-w-0">
                  <NuxtLink :to="`/decks/media/${item.deckId}/detail`" class="font-semibold hover:text-primary-500 truncate">
                    {{ localizedTitle(item) }}
                  </NuxtLink>
                  <Tag :value="getMediaTypeText(item.mediaType)" severity="secondary" class="shrink-0" />
                </div>
                <div class="text-sm text-muted-color mt-1">Blocked {{ formatDate(item.createdAt) }}</div>
              </div>
              <Button
                v-tooltip.left="'Unblock'"
                icon="pi pi-replay"
                severity="secondary"
                text
                size="small"
                @click="handleUnblock(item.deckId, localizedTitle(item))"
              />
            </div>
          </template>
        </Card>
      </div>
      <Paginator v-if="blockedTotal > limit" :rows="limit" :total-records="blockedTotal" :first="blockedOffset" class="mt-4" @page="onBlockedPage" />
    </div>
  </div>
</template>

<style scoped>
  .difficulty-btn {
    padding: 0.375rem 0.75rem;
    border-radius: var(--radius-md);
    border: 1px solid var(--diff-bg);
    background: transparent;
    color: inherit;
    font-size: 0.875rem;
    cursor: pointer;
    transition: background-color 0.2s;
  }

  .difficulty-btn:hover {
    background: var(--diff-bg-hover);
  }

  .vote-harder {
    color: var(--orange-500);
  }

  .difficulty-btn.is-selected {
    background: var(--diff-bg);
    color: white;
  }
</style>
