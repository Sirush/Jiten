<script setup lang="ts">
  import { type Deck, DisplayStyle, DeckStatus, MediaType, DeckDownloadType, DeckOrder } from '~/types';
  import { useAuthStore } from '~/stores/authStore';
  import { useDisplayStyleStore } from '~/stores/displayStyleStore';
  import { storeToRefs } from 'pinia';
  import { getDeckStatusText } from '~/utils/deckStatusMapper';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import MediaDeckCompactView from '~/components/MediaDeckCompactView.vue';
  import MediaDeckTableView from '~/components/MediaDeckTableView.vue';

  const route = useRoute();
  const { $api } = useNuxtApp();
  const auth = useAuthStore();
  const displayStore = useDisplayStyleStore();
  const { displayStyle } = storeToRefs(displayStore);

  const targetUsername = computed(() => route.params.username as string);
  const isOwnProfile = computed(() => auth.isAuthenticated && auth.user?.userName?.toLowerCase() === targetUsername.value.toLowerCase());

  const {
    data: decks,
    status,
    error,
  } = await useApiFetch<Deck[]>(() => `user/profile/${targetUsername.value}/media-list`, { watch: [targetUsername] });

  const isLoading = computed(() => status.value === 'pending');
  const notAvailable = computed(() => (error.value as any)?.statusCode === 404);

  // Status groups in reading order; only non-empty ones get a tab.
  const statusOrder: DeckStatus[] = [DeckStatus.Ongoing, DeckStatus.Completed, DeckStatus.Planning, DeckStatus.Dropped];

  // Media-type filter (client-side; the full list is already loaded).
  const mediaTypeFilter = ref<MediaType | null>(null);

  const mediaTypeOptions = computed(() => {
    const present = [...new Set((decks.value ?? []).map((d) => d.mediaType))].sort((a, b) => a - b);
    return [{ label: 'All types', value: null as MediaType | null }, ...present.map((t) => ({ label: getMediaTypeText(t), value: t as MediaType | null }))];
  });

  const filteredDecks = computed(() => {
    const list = decks.value ?? [];
    return mediaTypeFilter.value === null ? list : list.filter((d) => d.mediaType === mediaTypeFilter.value);
  });

  const groups = computed(() =>
    statusOrder
      .map((s) => ({ status: s, label: getDeckStatusText(s), decks: filteredDecks.value.filter((d) => d.status === s) }))
      .filter((g) => g.decks.length > 0)
  );
  
  const selectedTabRaw = ref<string>('');
  const activeGroup = computed(() => {
    const g = groups.value;
    if (!g.length) return undefined;
    return g.find((x) => x.status.toString() === selectedTabRaw.value) ?? g[0];
  });
  const selectedTab = computed({
    get: () => activeGroup.value?.status.toString() ?? '',
    set: (v: string) => {
      selectedTabRaw.value = v;
    },
  });

  // Keep the local list in sync when a card mutates its own status/favourite, so it re-buckets across tabs.
  function updateDeckInList(updated: Deck) {
    if (!decks.value) return;
    const i = decks.value.findIndex((d) => d.deckId === updated.deckId);
    if (i !== -1) decks.value[i] = updated;
  }

  const sentenceMediaTypes = [MediaType.Novel, MediaType.NonFiction, MediaType.VideoGame, MediaType.VisualNovel, MediaType.WebNovel];
  const downloadVisible = ref(false);
  const downloadMediaList = ref<{ apiBase: string; title: string; totalWords: number; hasExampleSentences: boolean } | null>(null);

  const openDownload = () => {
    const g = activeGroup.value;
    if (!g) return;
    const apiBase = `user/profile/${targetUsername.value}/media-list/${g.status}`;

    downloadMediaList.value = {
      apiBase,
      title: `${targetUsername.value} - ${g.label}`,
      totalWords: g.decks.reduce((sum, d) => sum + (d.uniqueWordCount || 0), 0),
      hasExampleSentences: g.decks.some((d) => sentenceMediaTypes.includes(d.mediaType)),
    };
    downloadVisible.value = true;

    $api<number>(`${apiBase}/vocabulary-count`, {
      method: 'POST',
      body: { downloadType: DeckDownloadType.Full, order: DeckOrder.DeckFrequency, minFrequency: 0, maxFrequency: 0 },
    })
      .then((real) => {
        if (typeof real === 'number' && real > 0 && downloadMediaList.value?.apiBase === apiBase) {
          downloadMediaList.value = { ...downloadMediaList.value, totalWords: real };
        }
      })
      .catch(() => {
      });
  };

  useHead(() => ({
    title: `${targetUsername.value} - Media List`,
    meta: [{ name: 'description', content: `Tracked media list for ${targetUsername.value}` }],
  }));
</script>

<template>
  <div class="container mx-auto px-4 py-6 flex flex-col gap-4">
    <div v-if="isLoading" class="flex justify-center items-center min-h-[50vh]">
      <ProgressSpinner />
    </div>

    <div v-else-if="notAvailable" class="text-center py-16">
      <Card>
        <template #content>
          <div class="flex flex-col items-center gap-4">
            <Icon name="material-symbols:lock" size="4rem" class="text-surface-400" />
            <h2 class="text-xl font-semibold">This media list is private</h2>
            <p class="text-surface-500">This user has chosen to keep their media list private.</p>
            <NuxtLink :to="`/profile/${targetUsername}`">
              <Button label="Back to Profile" icon="pi pi-arrow-left" />
            </NuxtLink>
          </div>
        </template>
      </Card>
    </div>

    <div v-else-if="error" class="text-center py-8">
      <Message severity="error">Failed to load media list.</Message>
    </div>

    <template v-else>
      <div class="flex items-center gap-2">
        <NuxtLink :to="`/profile/${targetUsername}`" class="flex items-center gap-1 text-primary hover:underline">
          <Icon name="material-symbols:arrow-back" />
          Back to Profile
        </NuxtLink>
      </div>

      <h1 class="text-2xl md:text-3xl font-bold">{{ isOwnProfile ? 'My Media List' : `${targetUsername}'s Media List` }}</h1>

      <div v-if="groups.length === 0" class="text-center py-12">
        <Message severity="info">No tracked media yet. Set a status on a title to see it here.</Message>
      </div>

      <template v-else>
        <Tabs v-model:value="selectedTab">
          <TabList class="flex-wrap">
            <Tab v-for="g in groups" :key="g.status" :value="g.status.toString()">
              {{ g.label }}
              <span class="text-xs text-surface-400 ml-1">{{ g.decks.length }}</span>
            </Tab>
          </TabList>
        </Tabs>

        <!-- Toolbar -->
        <div class="flex justify-between items-center gap-3 flex-wrap">
          <Button
            v-if="activeGroup"
            :label="`Download ${activeGroup.label} vocab`"
            icon="pi pi-download"
            size="small"
            @click="openDownload"
          />
          <div class="ml-auto flex items-center gap-2">
            <Select
              v-if="mediaTypeOptions.length > 2"
              v-model="mediaTypeFilter"
              :options="mediaTypeOptions"
              option-label="label"
              option-value="value"
              placeholder="All types"
              size="small"
              class="w-40"
            />
            <DisplayStyleSelector />
          </div>
        </div>

        <template v-if="activeGroup">
          <!-- Card view -->
          <div v-if="displayStyle === DisplayStyle.Card" class="flex flex-col gap-2">
            <MediaDeckCard v-for="deck in activeGroup.decks" :key="deck.deckId" :deck="deck" @update:deck="updateDeckInList" />
          </div>

          <!-- Compact view -->
          <div v-else-if="displayStyle === DisplayStyle.Compact" class="flex flex-wrap gap-4 justify-center">
            <MediaDeckCompactView v-for="deck in activeGroup.decks" :key="deck.deckId" :deck="deck" />
          </div>

          <!-- Table view -->
          <div v-else-if="displayStyle === DisplayStyle.Table" class="flex flex-col gap-0.5">
            <MediaDeckTableView v-for="deck in activeGroup.decks" :key="deck.deckId" :deck="deck" />
          </div>
        </template>
      </template>
    </template>

    <MediaDeckDownloadDialog
      v-if="downloadMediaList"
      :key="downloadMediaList.apiBase"
      :media-list="downloadMediaList"
      :visible="downloadVisible"
      @update:visible="downloadVisible = $event"
    />
  </div>
</template>
