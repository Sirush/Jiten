<script setup lang="ts">
  import { useConfirm } from 'primevue/useconfirm';
  import { useToast } from 'primevue/usetoast';
  import { CommunityListStatus, type Genre } from '~/types';
  import type { CommunityListEdit, CommunityListEditItem, CommunityListQuota, MediaSuggestion, Tag } from '~/types/types';
  import type { OwnMediaListRow } from '~/components/CommunityListMediaListPicker.vue';
  import { COMMUNITY_LIST_MAX_DESCRIPTION, COMMUNITY_LIST_MAX_NAME, COMMUNITY_LIST_MAX_NOTE, communityListPath } from '~/composables/useCommunityLists';
  import { UNDO_TOAST_GROUP, UNDO_VISIBLE_MS, type UndoDetail } from '~/composables/useMediaListSync';
  import { apiErrorMessage, apiStatusCode } from '~/utils/apiErrorMessage';
  import { coverUrl } from '~/utils/coverImage';
  import { suggestGenres, suggestTags } from '~/utils/communityListSuggestions';
  import { getAllGenres, getGenreText } from '~/utils/genreMapper';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';

  definePageMeta({
    middleware: ['auth'],
    validate: (route) => /^\d+$/.test(String(route.params.id)),
  });

  const FILTER_THRESHOLD = 20;

  const route = useRoute();
  const toast = useToast();
  const confirm = useConfirm();
  const lists = useCommunityLists();
  const localiseTitle = useLocaliseTitle();

  const listId = Number(route.params.id);

  const loadState = ref<'loading' | 'ready' | 'missing' | 'failed'>('loading');
  const slug = ref('');
  const favouriteCount = ref(0);
  const quota = ref<CommunityListQuota | null>(null);
  const name = ref('');
  const description = ref('');
  const status = ref<CommunityListStatus>(CommunityListStatus.Draft);
  const genres = ref<Genre[]>([]);
  const tagIds = ref<number[]>([]);
  const items = ref<CommunityListEditItem[]>([]);
  const savedDetails = ref('');
  const savedItems = ref('');
  const saving = ref(false);
  const nameError = ref<string | null>(null);
  let leaving = false;

  useSeoMeta({ title: 'Edit list', robots: 'noindex, nofollow' });

  const detailsSnapshot = () =>
    JSON.stringify({
      name: name.value.trim(),
      description: description.value.trim(),
      status: status.value,
      genres: [...genres.value].sort(),
      tagIds: [...tagIds.value].sort(),
    });
  const itemsSnapshot = () => JSON.stringify(items.value.map((i) => [i.deckId, i.note?.trim() || null]));
  const detailsDirty = computed(() => loadState.value === 'ready' && detailsSnapshot() !== savedDetails.value);
  const itemsDirty = computed(() => loadState.value === 'ready' && itemsSnapshot() !== savedItems.value);
  const dirty = computed(() => detailsDirty.value || itemsDirty.value);

  function apply(edit: CommunityListEdit) {
    slug.value = edit.slug;
    favouriteCount.value = edit.favouriteCount;
    quota.value = edit.quota;
    name.value = edit.name;
    description.value = edit.description ?? '';
    status.value = edit.status;
    genres.value = [...edit.genres];
    tagIds.value = edit.tags.map((t) => t.tagId);
    items.value = edit.items.map((i) => ({ ...i }));
    savedDetails.value = detailsSnapshot();
    savedItems.value = itemsSnapshot();
  }

  onMounted(async () => {
    try {
      apply(await lists.getForEdit(listId));
      loadState.value = 'ready';
    } catch (e) {
      loadState.value = apiStatusCode(e) === 404 ? 'missing' : 'failed';
    }
  });

  const isUnlisted = computed(() => status.value === CommunityListStatus.Unlisted);
  const maxItems = computed(() => quota.value?.maxItems ?? 0);
  const maxGenres = computed(() => quota.value?.maxGenres ?? 0);
  const maxTags = computed(() => quota.value?.maxTags ?? 0);
  const remaining = computed(() => Math.max(0, maxItems.value - items.value.length));
  const overItemCap = computed(() => quota.value !== null && items.value.length > maxItems.value);
  const deckIdSet = computed(() => new Set(items.value.map((i) => i.deckId)));

  const { data: availableTags } = useApiFetch<Tag[]>('media-deck/tags', { server: false, lazy: true });
  const tagNameById = computed(() => new Map((availableTags.value ?? []).map((t) => [t.tagId, t.name])));
  const genreOptions = getAllGenres();

  const genreSuggestions = computed(() => (genres.value.length >= maxGenres.value ? [] : suggestGenres(items.value, genres.value)));
  const tagSuggestions = computed(() => (tagIds.value.length >= maxTags.value ? [] : suggestTags(items.value, tagIds.value)));

  const percent = (share: number) => `${Math.round(share * 100)}%`;

  function addGenre(genre: Genre) {
    if (genres.value.length < maxGenres.value && !genres.value.includes(genre)) genres.value = [...genres.value, genre];
  }
  function addTag(tagId: number) {
    if (tagIds.value.length < maxTags.value && !tagIds.value.includes(tagId)) tagIds.value = [...tagIds.value, tagId];
  }

  const addMode = ref<'search' | 'mine'>('search');
  const searchText = ref('');
  const { suggestions: searchResults, isLoading: searching, fetchSuggestions, clearSuggestions } = useMediaSuggestions({ limit: 10 });
  watch(searchText, (value) => {
    const query = value.trim();
    if (query.length < 2) clearSuggestions();
    else fetchSuggestions(query);
  });

  const positionById = computed(() => new Map(items.value.map((item, index) => [item.deckId, index + 1])));
  const positionOf = (deckId: number) => positionById.value.get(deckId) ?? 0;
  const positionTextClass = (position: number) => (position >= 1000 ? '!text-xs' : position >= 100 ? '!text-sm' : undefined);

  function appendItems(rows: Pick<CommunityListEditItem, 'deckId' | 'originalTitle' | 'romajiTitle' | 'englishTitle' | 'mediaType' | 'coverName'>[]) {
    const fresh = rows.filter((r) => !deckIdSet.value.has(r.deckId)).slice(0, remaining.value);
    items.value = [...items.value, ...fresh.map((r) => ({ ...r, note: null, genres: [], tags: [] }))];
    return fresh.length;
  }

  function addFromSearch(suggestion: MediaSuggestion) {
    appendItems([
      {
        deckId: suggestion.deckId,
        originalTitle: suggestion.originalTitle,
        romajiTitle: suggestion.romajiTitle ?? null,
        englishTitle: suggestion.englishTitle ?? null,
        mediaType: suggestion.mediaType,
        coverName: suggestion.coverName,
      },
    ]);
  }

  function addFromMediaList(rows: OwnMediaListRow[]) {
    const added = appendItems(rows);
    toast.add({ severity: 'success', summary: `Added ${added} ${added === 1 ? 'title' : 'titles'} to the end`, life: 2500 });
  }

  const itemFilter = ref('');
  const filterActive = computed(() => !!itemFilter.value.trim());
  const visibleItems = computed(() => {
    const needle = itemFilter.value.trim().toLowerCase();
    if (!needle) return items.value;
    return items.value.filter((i) => [i.originalTitle, i.romajiTitle, i.englishTitle, i.note].some((t) => t?.toLowerCase().includes(needle)));
  });

  const NUDGE_BUTTON_CLASS =
    'flex h-7 w-8 cursor-pointer items-center justify-center rounded text-muted-color hover:bg-surface-100 focus-visible:outline-2 focus-visible:outline-primary-500 disabled:cursor-default disabled:opacity-30 disabled:hover:bg-transparent dark:hover:bg-surface-800';

  function moveItem(from: number, to: number) {
    if (from === to || from < 0 || from >= items.value.length) return;
    const next = [...items.value];
    const [moved] = next.splice(from, 1);
    next.splice(Math.min(Math.max(to, 0), next.length), 0, moved!);
    items.value = next;
  }

  function setPosition(item: CommunityListEditItem, raw: string | number) {
    const target = Number(raw);
    if (!Number.isInteger(target)) return;
    moveItem(positionOf(item.deckId) - 1, Math.min(Math.max(target, 1), items.value.length) - 1);
  }

  async function nudgeItem(item: CommunityListEditItem, delta: -1 | 1, event: MouseEvent) {
    const row = (event.currentTarget as HTMLElement).closest('li');
    const topBefore = row?.getBoundingClientRect().top ?? 0;
    const from = positionOf(item.deckId) - 1;
    moveItem(from, from + delta);
    await nextTick();
    if (!row) return;
    window.scrollBy(0, row.getBoundingClientRect().top - topBefore);
    const arrows = [...row.querySelectorAll<HTMLButtonElement>('[data-nudge]')];
    (arrows.find((b) => b.dataset.nudge === String(delta) && !b.disabled) ?? arrows.find((b) => !b.disabled))?.focus();
  }

  function removeItem(item: CommunityListEditItem) {
    const index = items.value.findIndex((i) => i.deckId === item.deckId);
    if (index < 0) return;
    items.value = items.value.filter((i) => i.deckId !== item.deckId);
    const undo: UndoDetail = { run: async () => restoreItem(item, index) };
    toast.removeGroup(UNDO_TOAST_GROUP);
    toast.add({ group: UNDO_TOAST_GROUP, severity: 'secondary', summary: `Removed ${localiseTitle(item)}`, detail: undo, life: UNDO_VISIBLE_MS });
  }

  function restoreItem(item: CommunityListEditItem, index: number) {
    if (deckIdSet.value.has(item.deckId)) return;
    if (remaining.value <= 0) {
      toast.add({ severity: 'warn', summary: `This list is full, so ${localiseTitle(item)} can't be put back`, life: 4000 });
      return;
    }
    const next = [...items.value];
    next.splice(Math.min(index, next.length), 0, item);
    items.value = next;
  }

  const itemListRef = ref<HTMLElement | null>(null);
  const { dragIndex, dropIndex, handlePointerDown } = useTouchReorder({ containerRef: itemListRef, onReorder: moveItem });

  async function save() {
    nameError.value = null;
    if (!name.value.trim()) {
      nameError.value = 'Give the list a name.';
      return;
    }
    saving.value = true;
    try {
      if (itemsDirty.value) {
        await lists.replaceItems(
          listId,
          items.value.map((i) => ({ deckId: i.deckId, note: i.note?.trim() || null }))
        );
        savedItems.value = itemsSnapshot();
      }
      if (detailsDirty.value) {
        const result = await lists.updateDetails(listId, {
          name: name.value.trim(),
          description: description.value.trim() || null,
          status: status.value,
          genres: genres.value,
          tagIds: tagIds.value,
        });
        slug.value = result.slug;
        savedDetails.value = detailsSnapshot();
      }
      toast.add({ severity: 'success', summary: 'Saved', life: 2000 });
      apply(await lists.getForEdit(listId));
    } catch (e) {
      toast.add({ severity: 'error', summary: apiErrorMessage(e, 'Could not save the list'), life: 6000 });
    } finally {
      saving.value = false;
    }
  }

  function discard() {
    confirm.require({
      header: 'Discard changes?',
      message: 'Everything since your last save will be lost.',
      rejectProps: { label: 'Keep editing', severity: 'secondary', outlined: true },
      acceptProps: { label: 'Discard', severity: 'danger' },
      accept: async () => {
        apply(await lists.getForEdit(listId));
      },
    });
  }

  function deleteList() {
    confirm.require({
      header: 'Delete this list?',
      message:
        favouriteCount.value > 0
          ? `The list and its ${favouriteCount.value} ${favouriteCount.value === 1 ? 'favourite' : 'favourites'} will be deleted for everyone.`
          : 'The list will be deleted for everyone.',
      rejectProps: { label: 'Cancel', severity: 'secondary', outlined: true },
      acceptProps: { label: 'Delete', severity: 'danger' },
      accept: async () => {
        try {
          await lists.remove(listId);
          leaving = true;
          await navigateTo({ path: '/lists', query: { tab: 'mine' } });
        } catch (e) {
          toast.add({ severity: 'error', summary: apiErrorMessage(e, 'Could not delete the list'), life: 6000 });
        }
      },
    });
  }

  onBeforeRouteLeave(() => {
    if (leaving || !dirty.value) return true;
    return new Promise<boolean>((resolve) => {
      let settled = false;
      const done = (value: boolean) => {
        if (settled) return;
        settled = true;
        resolve(value);
      };
      confirm.require({
        header: 'Leave without saving?',
        message: 'Your unsaved changes will be lost.',
        acceptLabel: 'Leave',
        rejectLabel: 'Stay',
        acceptClass: 'p-button-danger',
        accept: () => done(true),
        reject: () => done(false),
        onHide: () => done(false),
      });
    });
  });

  function onBeforeUnload(event: BeforeUnloadEvent) {
    if (!dirty.value) return;
    event.preventDefault();
  }
  onMounted(() => window.addEventListener('beforeunload', onBeforeUnload));
  onBeforeUnmount(() => window.removeEventListener('beforeunload', onBeforeUnload));

  const saveBarRef = ref<HTMLElement | null>(null);
  const saveBarHeight = ref(0);
  watch(saveBarRef, (el, _, onCleanup) => {
    if (!el) return;
    const observer = new ResizeObserver(([entry]) => (saveBarHeight.value = entry?.borderBoxSize[0]?.blockSize ?? 0));
    observer.observe(el);
    onCleanup(() => observer.disconnect());
  });

  useHead({ htmlAttrs: { style: computed(() => (saveBarHeight.value ? `--undo-toast-bottom: ${saveBarHeight.value + 12}px` : undefined)) } });

  const viewPath = computed(() => communityListPath({ listId, slug: slug.value }));
</script>

<template>
  <div class="mx-auto flex w-full max-w-[56rem] flex-col gap-4 px-2 pt-4 md:px-4">
    <div v-if="loadState === 'loading'" class="flex flex-col gap-4">
      <Skeleton height="2.5rem" width="12rem" />
      <Skeleton height="18rem" />
      <Skeleton height="24rem" />
    </div>

    <div v-else-if="loadState !== 'ready'" class="flex flex-col items-center gap-3 py-12 text-center">
      <p class="m-0 text-muted-color">
        {{ loadState === 'missing' ? "This list doesn't exist, or it isn't yours." : "The list didn't load." }}
      </p>
      <Button v-if="loadState === 'failed'" label="Retry" icon="pi pi-refresh" @click="reloadNuxtApp()" />
      <NuxtLink v-else to="/lists">Back to lists</NuxtLink>
    </div>

    <template v-else>
      <nav aria-label="Breadcrumb" class="flex items-center gap-2 text-sm text-muted-color">
        <NuxtLink to="/lists">Lists</NuxtLink>
        <span aria-hidden="true">/</span>
        <NuxtLink :to="{ path: '/lists', query: { tab: 'mine' } }">My lists</NuxtLink>
        <span aria-hidden="true">/</span>
        <span>Edit</span>
      </nav>

      <div class="flex flex-wrap items-center gap-3">
        <h1 class="m-0 text-2xl font-bold">Edit list</h1>
        <span class="flex-1" />
        <span v-if="quota" class="text-sm text-muted-color">{{ quota.listCount }} of {{ quota.maxLists }} lists</span>
        <NuxtLink :to="viewPath" class="text-sm font-semibold">View list</NuxtLink>
      </div>

      <Message v-if="isUnlisted" severity="warn" :closable="false">
        This list was unlisted by moderation. You can still edit it, but only you and the moderators can see it.
      </Message>

      <section
        aria-labelledby="details-heading"
        class="flex flex-col gap-5 rounded-lg border border-surface-200 bg-surface-0 p-4 md:p-6 dark:border-surface-700 dark:bg-surface-900"
      >
        <h2 id="details-heading" class="m-0 text-lg font-bold">Details</h2>

        <div class="flex flex-col gap-1.5">
          <div class="flex justify-between">
            <label for="listName" class="text-sm font-semibold">Name</label>
            <span class="text-xs text-muted-color tabular-nums">{{ name.length }} / {{ COMMUNITY_LIST_MAX_NAME }}</span>
          </div>
          <InputText id="listName" v-model="name" :maxlength="COMMUNITY_LIST_MAX_NAME" :invalid="!!nameError" class="w-full" />
          <small v-if="nameError" class="text-red-600 dark:text-red-400" role="alert">{{ nameError }}</small>
        </div>

        <div class="flex flex-col gap-1.5">
          <div class="flex justify-between">
            <label for="listDescription" class="text-sm font-semibold">Description</label>
            <span class="text-xs text-muted-color tabular-nums">{{ description.length }} / {{ COMMUNITY_LIST_MAX_DESCRIPTION }}</span>
          </div>
          <Textarea id="listDescription" v-model="description" :maxlength="COMMUNITY_LIST_MAX_DESCRIPTION" rows="3" auto-resize class="w-full" />
        </div>

        <div class="grid grid-cols-1 gap-6 md:grid-cols-2">
          <div class="flex min-w-0 flex-col gap-2">
            <div class="flex justify-between">
              <label for="listGenres" class="text-sm font-semibold">Genres</label>
              <span class="text-xs text-muted-color tabular-nums">{{ genres.length }} / {{ maxGenres }}</span>
            </div>
            <MultiSelect
              v-model="genres"
              input-id="listGenres"
              :options="genreOptions"
              option-label="label"
              option-value="value"
              :selection-limit="maxGenres"
              display="chip"
              :placeholder="`Pick up to ${maxGenres}`"
              class="w-full"
            />
            <div v-if="genreSuggestions.length" class="flex flex-wrap items-center gap-1.5">
              <span class="text-xs text-muted-color">From your titles:</span>
              <Button
                v-for="s in genreSuggestions"
                :key="s.genre"
                :label="`${getGenreText(s.genre)} ${percent(s.share)}`"
                icon="pi pi-plus"
                size="small"
                severity="secondary"
                outlined
                @click="addGenre(s.genre)"
              />
            </div>
          </div>

          <div class="flex min-w-0 flex-col gap-2">
            <div class="flex justify-between">
              <label for="listTags" class="text-sm font-semibold">Tags</label>
              <span class="text-xs text-muted-color tabular-nums">{{ tagIds.length }} / {{ maxTags }}</span>
            </div>
            <MultiSelect
              v-model="tagIds"
              input-id="listTags"
              :options="availableTags ?? []"
              option-label="name"
              option-value="tagId"
              :selection-limit="maxTags"
              display="chip"
              filter
              filter-placeholder="Search tags"
              :placeholder="availableTags ? `Pick up to ${maxTags}` : 'Loading tags...'"
              class="w-full"
            >
              <template #chip="{ value, removeCallback }">
                <Chip :label="tagNameById.get(value) ?? '...'" removable class="text-sm" @remove="removeCallback($event, value)" />
              </template>
            </MultiSelect>
            <div v-if="tagSuggestions.length" class="flex flex-wrap items-center gap-1.5">
              <span class="text-xs text-muted-color">From your titles:</span>
              <Button
                v-for="s in tagSuggestions"
                :key="s.tagId"
                :label="`${s.name} ${percent(s.share)}`"
                icon="pi pi-plus"
                size="small"
                severity="secondary"
                outlined
                @click="addTag(s.tagId)"
              />
            </div>
          </div>
        </div>
      </section>

      <section
        aria-labelledby="items-heading"
        class="flex flex-col gap-4 rounded-lg border border-surface-200 bg-surface-0 p-4 md:p-6 dark:border-surface-700 dark:bg-surface-900"
      >
        <div class="flex flex-wrap items-center gap-3">
          <h2 id="items-heading" class="m-0 text-lg font-bold">Titles</h2>
          <span class="text-sm font-semibold tabular-nums" :class="overItemCap ? 'text-amber-700 dark:text-amber-300' : 'text-muted-color'">
            {{ items.length }} / {{ maxItems }}
          </span>
          <span class="flex-1" />
          <SelectButton
            v-model="addMode"
            :options="[
              { label: 'Search all media', value: 'search' },
              { label: 'From my media list', value: 'mine' },
            ]"
            option-label="label"
            option-value="value"
            :allow-empty="false"
            aria-label="Add titles from"
            size="small"
          />
        </div>

        <Message v-if="overItemCap" severity="warn" :closable="false">
          This list is over the {{ maxItems }}-title limit for your plan. You can still edit, reorder and remove titles, but you can't add new ones.
          <template v-if="quota && maxItems < quota.fullMaxItems">
            <NuxtLink to="/jiten-plus" class="font-semibold">Jiten+</NuxtLink> raises the limit to {{ quota.fullMaxItems.toLocaleString() }}.
          </template>
        </Message>
        <Message v-else-if="remaining === 0" severity="secondary" :closable="false">This list is full. Remove a title to add another.</Message>

        <template v-else>
          <div v-if="addMode === 'search'" class="flex flex-col gap-2">
            <SearchInput
              v-model="searchText"
              placeholder="Search by title..."
              aria-label="Search all media"
              search-icon-class="pi pi-search"
              clear-icon-class="pi pi-times"
            />
            <ul
              v-if="searchResults.length"
              class="m-0 list-none overflow-hidden rounded-lg border border-surface-200 p-0 dark:border-surface-700"
              aria-label="Search results"
            >
              <li
                v-for="result in searchResults"
                :key="result.deckId"
                class="flex items-center gap-3 border-b border-surface-100 px-3 py-2 last:border-b-0 dark:border-surface-800"
              >
                <img :src="coverUrl(result.coverName, 'xs')" alt="" class="h-[45px] w-8 shrink-0 rounded-sm object-cover" />
                <span class="flex min-w-0 flex-1 flex-col">
                  <span class="truncate font-medium" v-bind="japaneseTextAttrs(localiseTitle(result))">{{ localiseTitle(result) }}</span>
                  <span class="text-xs text-muted-color">{{ getMediaTypeText(result.mediaType) }}</span>
                </span>
                <span v-if="deckIdSet.has(result.deckId)" class="shrink-0 text-sm text-muted-color">In this list, #{{ positionOf(result.deckId) }}</span>
                <Button v-else label="Add" size="small" class="shrink-0" @click="addFromSearch(result)" />
              </li>
            </ul>
            <p v-else-if="searchText.trim().length >= 2 && !searching" class="m-0 text-sm text-muted-color">No media matches that title.</p>
          </div>
          <KeepAlive>
            <CommunityListMediaListPicker v-if="addMode === 'mine'" :existing-deck-ids="deckIdSet" :remaining="remaining" @add="addFromMediaList" />
          </KeepAlive>
        </template>

        <div v-if="items.length > FILTER_THRESHOLD" class="sm:max-w-xs">
          <SearchInput v-model="itemFilter" size="small" placeholder="Find in this list..." aria-label="Find in this list" />
        </div>

        <p
          v-if="items.length === 0"
          class="m-0 rounded-lg border border-dashed border-surface-300 px-4 py-8 text-center text-muted-color dark:border-surface-600"
        >
          No titles yet. Search above, or add them from your media list.
        </p>

        <ol ref="itemListRef" class="m-0 flex list-none flex-col gap-2 p-0">
          <li
            v-for="(item, index) in visibleItems"
            :key="item.deckId"
            class="flex items-center gap-2 rounded-lg border p-2 sm:gap-3 sm:p-3"
            :class="{
              'border-primary-400 bg-primary-50/50 dark:border-primary-600 dark:bg-primary-900/20': dropIndex === index && dragIndex !== index,
              'opacity-50': dragIndex === index,
              'border-surface-200 dark:border-surface-700': dropIndex !== index && dragIndex !== index,
            }"
          >
            <div v-if="!filterActive" class="flex shrink-0 flex-col">
              <button
                type="button"
                data-nudge="-1"
                :disabled="positionOf(item.deckId) === 1"
                :class="NUDGE_BUTTON_CLASS"
                :aria-label="`Move ${localiseTitle(item)} up`"
                @click="nudgeItem(item, -1, $event)"
              >
                <i class="pi pi-chevron-up text-xs" aria-hidden="true" />
              </button>
              <button
                type="button"
                class="flex h-7 w-8 cursor-grab touch-none items-center justify-center rounded text-muted-color hover:bg-surface-100 active:cursor-grabbing dark:hover:bg-surface-800"
                :aria-label="`Drag ${localiseTitle(item)} to reorder`"
                @pointerdown="handlePointerDown($event, index)"
              >
                <i class="pi pi-bars" aria-hidden="true" />
              </button>
              <button
                type="button"
                data-nudge="1"
                :disabled="positionOf(item.deckId) === items.length"
                :class="NUDGE_BUTTON_CLASS"
                :aria-label="`Move ${localiseTitle(item)} down`"
                @click="nudgeItem(item, 1, $event)"
              >
                <i class="pi pi-chevron-down text-xs" aria-hidden="true" />
              </button>
            </div>
            <InputText
              :model-value="String(positionOf(item.deckId))"
              type="number"
              inputmode="numeric"
              :min="1"
              :max="items.length"
              :aria-label="`Position of ${localiseTitle(item)}`"
              class="w-14 shrink-0 !px-1 text-center font-bold tabular-nums [appearance:textfield] [&::-webkit-inner-spin-button]:appearance-none [&::-webkit-outer-spin-button]:appearance-none"
              :class="positionTextClass(positionOf(item.deckId))"
              @change="setPosition(item, ($event.target as HTMLInputElement).value)"
            />
            <img :src="coverUrl(item.coverName, 'xs')" alt="" loading="lazy" class="hidden h-14 w-10 shrink-0 rounded-sm object-cover sm:block" />
            <div class="flex min-w-0 flex-1 flex-col gap-1.5">
              <div class="flex min-w-0 flex-wrap items-baseline gap-x-2">
                <span class="truncate font-semibold" v-bind="japaneseTextAttrs(localiseTitle(item))">{{ localiseTitle(item) }}</span>
                <span class="text-xs text-muted-color">{{ getMediaTypeText(item.mediaType) }}</span>
              </div>
              <div class="relative">
                <Textarea
                  v-model="item.note"
                  :maxlength="COMMUNITY_LIST_MAX_NOTE"
                  placeholder="Add a note (optional)"
                  :aria-label="`Note for ${localiseTitle(item)}`"
                  size="small"
                  rows="1"
                  auto-resize
                  class="block w-full resize-none pr-16!"
                  @keydown.enter.prevent
                />
                <span class="pointer-events-none absolute right-2.5 bottom-2 text-xs text-muted-color tabular-nums" aria-hidden="true">
                  {{ item.note?.length ?? 0 }} / {{ COMMUNITY_LIST_MAX_NOTE }}
                </span>
              </div>
            </div>
            <Button icon="pi pi-trash" severity="secondary" text :aria-label="`Remove ${localiseTitle(item)}`" class="shrink-0" @click="removeItem(item)" />
          </li>
        </ol>
      </section>

      <div class="flex flex-wrap items-center justify-between gap-3 px-1 pb-2">
        <span class="text-sm text-muted-color">Deleting the list can't be undone.</span>
        <Button label="Delete list" icon="pi pi-trash" severity="danger" outlined size="small" @click="deleteList" />
      </div>

      <div ref="saveBarRef" class="sticky bottom-0 z-10 -mx-2 border-t border-surface-200 bg-surface-0 px-3 py-3 md:-mx-4 md:px-4 dark:border-surface-700 dark:bg-surface-900">
        <div class="flex flex-wrap items-center gap-3">
          <span v-if="dirty" class="flex items-center gap-2 text-sm font-semibold">
            <span class="h-2 w-2 rounded-full bg-amber-500" aria-hidden="true" />Unsaved changes
          </span>
          <span v-else class="text-sm text-muted-color">All changes saved</span>
          <span class="flex-1" />
          <SelectButton
            v-if="!isUnlisted"
            v-model="status"
            :options="[
              { label: 'Draft', value: CommunityListStatus.Draft },
              { label: 'Public', value: CommunityListStatus.Public },
            ]"
            option-label="label"
            option-value="value"
            :allow-empty="false"
            aria-label="Visibility"
          />
          <Button v-if="dirty" label="Discard" severity="secondary" text @click="discard" />
          <Button label="Save" icon="pi pi-check" :loading="saving" :disabled="!dirty" @click="save" />
        </div>
        <p v-if="status === CommunityListStatus.Public && items.length === 0" class="m-0 mt-2 text-sm text-muted-color">
          Add at least one title before publishing.
        </p>
      </div>
    </template>
  </div>
</template>
