<script setup lang="ts">
import type { MediaSuggestion } from '~/types/types';
import type { AutoCompleteCompleteEvent } from 'primevue/autocomplete';
import { debounce } from 'perfect-debounce';
import { getMediaTypeText } from '~/utils/mediaTypeMapper';

const props = withDefaults(defineProps<{
  modelValue: number | null;
  /** Display text shown when the parent knows the selected deck's title but not the full suggestion. */
  label?: string;
  placeholder?: string;
  /** Offers recent decks when the query is too short. Requires admin rights on the API side. */
  showRecent?: boolean;
  /** Accepts a raw deck id typed into the field. */
  allowRawId?: boolean;
  inputId?: string;
}>(), {
  label: '',
  placeholder: 'Search media...',
  showRecent: false,
  allowRawId: true,
  inputId: undefined,
});

const emit = defineEmits<{
  'update:modelValue': [value: number | null];
  select: [suggestion: MediaSuggestion | null];
}>();

const { $api } = useNuxtApp();
const localiseTitle = useLocaliseTitle();

const selected = ref<MediaSuggestion | string | null>(props.label || null);
const suggestions = ref<MediaSuggestion[]>([]);
const queryTooShort = ref(true);

// Set by the parent-driven watcher below: reflecting the parent's own value back to it would clobber it.
let suppressEmit = false;

function setSelectedSilently(value: MediaSuggestion | string | null) {
  suppressEmit = true;
  selected.value = value;
}

watch(selected, (val) => {
  if (suppressEmit) {
    suppressEmit = false;
    return;
  }

  if (val && typeof val === 'object') {
    emit('update:modelValue', val.deckId);
    emit('select', val);
    return;
  }

  if (props.allowRawId && typeof val === 'string' && /^\d+$/.test(val.trim())) {
    emit('update:modelValue', Number(val.trim()));
    emit('select', null);
    return;
  }

  emit('update:modelValue', null);
  emit('select', null);
});

// Keeps the field in sync when the parent clears or presets the selection.
watch(() => props.modelValue, (val) => {
  if (val === null) {
    if (selected.value !== null) setSelectedSilently(null);
    return;
  }

  const current = selected.value;
  if (current && typeof current === 'object' && current.deckId === val) return;
  setSelectedSilently(props.label || String(val));
});

async function fetchRecentDecks(): Promise<MediaSuggestion[]> {
  try {
    return await $api<MediaSuggestion[]>('admin/recent-decks', { query: { limit: 12 } });
  } catch { return []; }
}

const searchDecks = debounce(async (query: string) => {
  try {
    const res = await $api<{ suggestions: MediaSuggestion[] }>('media-deck/search-suggestions', { query: { query, limit: 10 } });
    suggestions.value = res.suggestions ?? [];
  } catch { suggestions.value = []; }
}, 300);

async function onComplete(event: AutoCompleteCompleteEvent) {
  queryTooShort.value = (event.query?.length ?? 0) < 2;
  if (queryTooShort.value) {
    suggestions.value = props.showRecent ? await fetchRecentDecks() : [];
  } else {
    await searchDecks(event.query);
  }
}

function getDeckLabel(item: MediaSuggestion | string): string {
  if (typeof item === 'string') return item;
  return `${localiseTitle(item)} (ID: ${item.deckId})`;
}
</script>

<template>
  <AutoComplete
    v-model="selected"
    :input-id="inputId"
    :suggestions="suggestions"
    :option-label="getDeckLabel"
    :placeholder="placeholder"
    :dropdown="showRecent"
    fluid
    class="w-full"
    @complete="onComplete"
  >
    <template #option="{ option }">
      <div class="flex items-center gap-2">
        <img
          :src="option.coverName && option.coverName !== 'nocover.jpg' ? option.coverName : '/img/nocover.jpg'"
          :alt="getDeckLabel(option)"
          class="h-10 w-7 object-cover rounded shrink-0"
        />
        <span class="truncate text-sm">{{ getDeckLabel(option) }}</span>
        <Tag :value="getMediaTypeText(option.mediaType)" severity="secondary" class="shrink-0 text-xs" />
      </div>
    </template>
    <template #empty>
      <span class="text-sm text-muted-color">
        {{ queryTooShort ? 'Type at least 2 characters to search.' : 'No media found.' }}
      </span>
    </template>
  </AutoComplete>
</template>
