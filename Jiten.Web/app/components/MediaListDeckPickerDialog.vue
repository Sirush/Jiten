<script setup lang="ts">
  import { ref, computed, watch } from 'vue';
  import { coverUrl } from '~/utils/coverImage';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { DeckStatus, type MediaSuggestion } from '~/types';

  const props = defineProps<{
    visible: boolean;
    pickedIds?: number[];
  }>();

  const emit = defineEmits<{
    'update:visible': [value: boolean];
    add: [decks: MediaSuggestion[]];
  }>();

  interface MediaListEntry extends MediaSuggestion {
    status: DeckStatus;
    isFavourite: boolean;
  }

  const { $api } = useNuxtApp();
  const localiseTitle = useLocaliseTitle();

  const localVisible = ref(props.visible);
  watch(
    () => props.visible,
    (v) => {
      localVisible.value = v;
    }
  );
  watch(localVisible, (v) => {
    emit('update:visible', v);
  });

  const entries = ref<MediaListEntry[]>([]);
  const loading = ref(false);
  const loadFailed = ref(false);
  const loaded = ref(false);

  type StatusFilter = 'all' | DeckStatus;
  const statusOptions: { label: string; value: StatusFilter }[] = [
    { label: 'All', value: 'all' },
    { label: 'Completed', value: DeckStatus.Completed },
    { label: 'Ongoing', value: DeckStatus.Ongoing },
    { label: 'Planning', value: DeckStatus.Planning },
    { label: 'Dropped', value: DeckStatus.Dropped },
  ];

  const statusFilter = ref<StatusFilter>('all');
  const favouritesOnly = ref(false);
  const search = ref('');
  const selectedIds = ref<number[]>([]);

  async function load() {
    loading.value = true;
    loadFailed.value = false;
    try {
      entries.value = await $api<MediaListEntry[]>('user/media-list');
      loaded.value = true;
    } catch {
      loadFailed.value = true;
    } finally {
      loading.value = false;
    }
  }

  watch(localVisible, (v) => {
    if (!v) return;
    selectedIds.value = [];
    if (!loaded.value && !loading.value) load();
  });

  const pickedSet = computed(() => new Set(props.pickedIds ?? []));

  const filtered = computed(() => {
    const q = search.value.trim().toLowerCase();
    return entries.value
      .filter((e) => {
        if (statusFilter.value !== 'all' && e.status !== statusFilter.value) return false;
        if (favouritesOnly.value && !e.isFavourite) return false;
        if (!q) return true;
        return [e.originalTitle, e.romajiTitle, e.englishTitle].some((t) => t?.toLowerCase().includes(q));
      })
      .sort((a, b) => localiseTitle(a).localeCompare(localiseTitle(b)));
  });

  const selectable = computed(() => filtered.value.filter((e) => !pickedSet.value.has(e.deckId)));

  const allSelected = computed(() => selectable.value.length > 0 && selectable.value.every((e) => selectedIds.value.includes(e.deckId)));

  function toggle(deckId: number) {
    if (pickedSet.value.has(deckId)) return;
    selectedIds.value = selectedIds.value.includes(deckId) ? selectedIds.value.filter((id) => id !== deckId) : [...selectedIds.value, deckId];
  }

  function selectAllFiltered() {
    const ids = new Set(selectedIds.value);
    for (const e of selectable.value) ids.add(e.deckId);
    selectedIds.value = [...ids];
  }

  function clearSelection() {
    selectedIds.value = [];
  }

  function confirmAdd() {
    const chosen = entries.value
      .filter((e) => selectedIds.value.includes(e.deckId))
      .map<MediaSuggestion>((e) => ({
        deckId: e.deckId,
        originalTitle: e.originalTitle,
        romajiTitle: e.romajiTitle,
        englishTitle: e.englishTitle,
        mediaType: e.mediaType,
        coverName: e.coverName,
      }));
    if (chosen.length === 0) return;
    emit('add', chosen);
    localVisible.value = false;
  }
</script>

<template>
  <Dialog
    v-model:visible="localVisible"
    modal
    header="Add from your media list"
    :style="{ width: '44rem' }"
    :breakpoints="{ '900px': '95vw' }"
    dismissable-mask
  >
    <div class="flex flex-col gap-3">
      <div class="flex flex-col gap-2">
        <div class="-mx-1 px-1 overflow-x-auto">
          <SelectButton
            v-model="statusFilter"
            :options="statusOptions"
            option-label="label"
            option-value="value"
            :allow-empty="false"
            aria-label="Filter by status"
          />
        </div>
        <div class="flex flex-wrap items-center gap-2">
          <IconField class="flex-1 min-w-[12rem]">
            <InputIcon class="pi pi-search" />
            <InputText v-model="search" placeholder="Filter titles" class="w-full" aria-label="Filter titles" />
          </IconField>
          <ToggleButton
            v-model="favouritesOnly"
            on-icon="pi pi-star-fill"
            off-icon="pi pi-star"
            on-label="Favourites"
            off-label="Favourites"
            aria-label="Show favourites only"
          />
        </div>
      </div>

      <div v-if="loading" class="flex justify-center py-10">
        <i class="pi pi-spin pi-spinner text-2xl text-surface-400" />
      </div>

      <div v-else-if="loadFailed" class="flex flex-col items-center gap-3 py-8 text-center">
        <p class="text-sm text-surface-600 dark:text-surface-300">Your media list could not be loaded.</p>
        <Button label="Try again" icon="pi pi-refresh" size="small" outlined @click="load" />
      </div>

      <p v-else-if="entries.length === 0" class="py-8 text-center text-sm text-surface-500 dark:text-surface-400">
        Nothing is tracked yet. Set a status on a deck page and it will show up here.
      </p>

      <template v-else>
        <div class="flex flex-wrap items-center gap-2">
          <Button
            :label="allSelected ? 'All shown selected' : `Select all ${selectable.length}`"
            icon="pi pi-check-square"
            size="small"
            text
            :disabled="selectable.length === 0 || allSelected"
            @click="selectAllFiltered"
          />
          <Button
            label="Clear selection"
            icon="pi pi-times"
            size="small"
            text
            severity="secondary"
            :disabled="selectedIds.length === 0"
            @click="clearSelection"
          />
          <span class="ml-auto text-xs text-surface-500 dark:text-surface-400">{{ filtered.length }} of {{ entries.length }} shown</span>
        </div>

        <p v-if="filtered.length === 0" class="py-8 text-center text-sm text-surface-500 dark:text-surface-400">No tracked media matches these filters.</p>

        <ul v-else class="flex flex-col gap-1.5 max-h-[55vh] overflow-y-auto pr-1">
          <li v-for="e in filtered" :key="e.deckId">
            <label
              class="flex items-center gap-3 rounded-lg border border-surface-200 dark:border-surface-700 p-2 min-w-0"
              :class="pickedSet.has(e.deckId) ? 'opacity-60' : 'cursor-pointer hover:bg-surface-100 dark:hover:bg-surface-800 transition-colors'"
            >
              <Checkbox
                :model-value="pickedSet.has(e.deckId) || selectedIds.includes(e.deckId)"
                :disabled="pickedSet.has(e.deckId)"
                binary
                @update:model-value="toggle(e.deckId)"
              />
              <img
                :src="coverUrl(e.coverName)"
                alt=""
                class="h-14 w-10 object-cover rounded shrink-0"
                @error="(ev) => ((ev.target as HTMLImageElement).src = '/img/nocover.jpg')"
              />
              <div class="flex flex-col min-w-0 flex-1 gap-1">
                <span class="text-sm font-medium truncate" :title="localiseTitle(e)">{{ localiseTitle(e) }}</span>
                <div class="flex items-center gap-1.5 flex-wrap">
                  <Tag :value="getMediaTypeText(e.mediaType)" severity="secondary" />
                  <i v-if="e.isFavourite" class="pi pi-star-fill text-xs text-amber-500" aria-label="Favourite" />
                  <span v-if="pickedSet.has(e.deckId)" class="text-xs text-surface-500 dark:text-surface-400">Already added</span>
                </div>
              </div>
            </label>
          </li>
        </ul>
      </template>
    </div>

    <template #footer>
      <Button label="Cancel" text severity="secondary" @click="localVisible = false" />
      <Button
        :label="selectedIds.length === 1 ? 'Add 1 deck' : `Add ${selectedIds.length} decks`"
        icon="pi pi-plus"
        :disabled="selectedIds.length === 0"
        @click="confirmAdd"
      />
    </template>
  </Dialog>
</template>
