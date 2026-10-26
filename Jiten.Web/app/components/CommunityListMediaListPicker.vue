<script setup lang="ts">
  import { DeckStatus, type MediaType } from '~/types';
  import { coverUrl } from '~/utils/coverImage';
  import { DECK_STATUS_DISPLAY_ORDER as STATUS_ORDER, getDeckStatusText } from '~/utils/deckStatusMapper';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';

  export interface OwnMediaListRow {
    deckId: number;
    originalTitle: string;
    romajiTitle: string | null;
    englishTitle: string | null;
    mediaType: MediaType;
    coverName: string;
    status: DeckStatus;
  }

  const props = defineProps<{ existingDeckIds: Set<number>; remaining: number }>();
  const emit = defineEmits<{ add: [rows: OwnMediaListRow[]] }>();


  const { $api } = useNuxtApp();
  const localiseTitle = useLocaliseTitle();

  const rows = ref<OwnMediaListRow[] | null>(null);
  const loadError = ref(false);
  const status = ref<DeckStatus>(DeckStatus.Completed);
  const hideExisting = ref(true);
  const search = ref('');
  const selected = ref(new Set<number>());

  onMounted(async () => {
    try {
      rows.value = await $api<OwnMediaListRow[]>('user/media-list');
      const firstWithRows = STATUS_ORDER.find((s) => rows.value!.some((r) => r.status === s));
      if (firstWithRows !== undefined) status.value = firstWithRows;
    } catch {
      loadError.value = true;
    }
  });

  const counts = computed(() => {
    const result = new Map<DeckStatus, number>();
    for (const row of rows.value ?? []) result.set(row.status, (result.get(row.status) ?? 0) + 1);
    return result;
  });
  const statusOptions = computed(() => STATUS_ORDER.filter((s) => (counts.value.get(s) ?? 0) > 0));

  const visible = computed(() => {
    const needle = search.value.trim().toLowerCase();
    return (rows.value ?? []).filter(
      (row) =>
        row.status === status.value &&
        !(hideExisting.value && props.existingDeckIds.has(row.deckId)) &&
        (!needle || [row.originalTitle, row.romajiTitle, row.englishTitle].some((t) => t?.toLowerCase().includes(needle)))
    );
  });

  const selectableCount = computed(() => [...selected.value].filter((id) => !props.existingDeckIds.has(id)).length);
  const overCap = computed(() => selectableCount.value > props.remaining);

  function toggle(deckId: number) {
    const next = new Set(selected.value);
    if (next.has(deckId)) next.delete(deckId);
    else next.add(deckId);
    selected.value = next;
  }

  function addSelected() {
    const picked = (rows.value ?? []).filter((row) => selected.value.has(row.deckId) && !props.existingDeckIds.has(row.deckId));
    emit('add', picked);
    selected.value = new Set();
  }
</script>

<template>
  <div class="overflow-hidden rounded-lg border border-surface-200 dark:border-surface-700">
    <p v-if="loadError" class="m-0 p-4 text-sm text-muted-color">Your media list didn't load. Close this and try again.</p>
    <div v-else-if="rows === null" class="flex flex-col gap-2 p-3">
      <Skeleton v-for="n in 3" :key="n" height="2.75rem" />
    </div>
    <p v-else-if="rows.length === 0" class="m-0 p-4 text-sm text-muted-color">
      Your media list is empty. Track titles from their pages, or import your list from AniList or VNDB.
    </p>
    <template v-else>
      <div class="flex flex-col gap-2 border-b border-surface-200 p-3 dark:border-surface-700">
        <div class="flex flex-wrap gap-1.5" role="group" aria-label="Status">
          <Button
            v-for="option in statusOptions"
            :key="option"
            :label="`${getDeckStatusText(option)} ${counts.get(option)}`"
            size="small"
            :outlined="status !== option"
            :severity="status === option ? undefined : 'secondary'"
            :aria-pressed="status === option"
            @click="status = option"
          />
        </div>
        <div class="flex flex-col gap-2 sm:flex-row sm:items-center">
          <SearchInput v-model="search" size="small" placeholder="Filter by title..." aria-label="Filter your media list" class="min-w-0 flex-1" />
          <div class="flex items-center gap-2">
            <Checkbox v-model="hideExisting" input-id="hideExisting" binary />
            <label for="hideExisting" class="cursor-pointer text-sm">Hide titles already in this list</label>
          </div>
        </div>
      </div>

      <ul class="m-0 max-h-80 list-none overflow-y-auto p-0">
        <li v-for="row in visible" :key="row.deckId" class="border-b border-surface-100 last:border-b-0 dark:border-surface-800">
          <label class="flex cursor-pointer items-center gap-3 px-3 py-2 hover:bg-surface-50 dark:hover:bg-surface-800">
            <Checkbox
              :model-value="selected.has(row.deckId) || existingDeckIds.has(row.deckId)"
              :disabled="existingDeckIds.has(row.deckId)"
              binary
              @update:model-value="toggle(row.deckId)"
            />
            <img :src="coverUrl(row.coverName, 'xs')" alt="" loading="lazy" class="h-[45px] w-8 shrink-0 rounded-sm object-cover" />
            <span class="flex min-w-0 flex-1 flex-col">
              <span class="truncate font-medium" v-bind="japaneseTextAttrs(localiseTitle(row))">{{ localiseTitle(row) }}</span>
              <span class="text-xs text-muted-color">
                {{ getMediaTypeText(row.mediaType) }}<template v-if="existingDeckIds.has(row.deckId)">, already in this list</template>
              </span>
            </span>
          </label>
        </li>
        <li v-if="visible.length === 0" class="px-3 py-4 text-sm text-muted-color">Nothing here matches.</li>
      </ul>

      <div class="flex flex-wrap items-center justify-between gap-2 border-t border-surface-200 p-3 dark:border-surface-700">
        <span class="text-sm" :class="overCap ? 'text-red-600 dark:text-red-400' : 'text-muted-color'">
          <template v-if="overCap">Only {{ remaining }} more {{ remaining === 1 ? 'title fits' : 'titles fit' }} in this list.</template>
          <template v-else>{{ selectableCount }} selected</template>
        </span>
        <Button
          :label="selectableCount ? `Add ${selectableCount} to the end` : 'Add to the end'"
          size="small"
          :disabled="selectableCount === 0 || overCap"
          @click="addSelected"
        />
      </div>
    </template>
  </div>
</template>
