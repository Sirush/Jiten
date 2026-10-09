<script setup lang="ts">
  import { ref, computed, watch } from 'vue';
  import Button from 'primevue/button';
  import InputText from 'primevue/inputtext';
  import Dialog from 'primevue/dialog';
  import AutoComplete from 'primevue/autocomplete';
  import Message from 'primevue/message';
  import { SeriesKind, type SeriesRef, type SeriesSummary } from '~/types/series';
  import type { SeriesDialogRequest } from '~/utils/franchiseBuilder';
  import { apiErrorMessage } from '~/utils/apiErrorMessage';
  import { seriesKindLabel } from '~/utils/seriesKind';

  const props = defineProps<{ request: SeriesDialogRequest | null }>();
  const emit = defineEmits<{ close: []; picked: [series: SeriesRef]; created: [series: SeriesRef]; renamed: [series: SeriesRef] }>();

  const { $api } = useNuxtApp();
  const { error, busy, create } = useSeriesCreate();
  const name = ref('');
  const existing = ref<SeriesSummary | string | null>(null);
  const kind = computed(() => props.request?.kind ?? SeriesKind.Series);
  const kindWord = computed(() => seriesKindLabel(kind.value).toLowerCase());
  const { suggestions, fetchSuggestions } = useSeriesSuggestions(kind);
  const pickedExisting = computed(() => (existing.value && typeof existing.value === 'object' ? existing.value : null));

  watch(
    () => props.request,
    (r) => {
      name.value = r?.name ?? '';
      existing.value = null;
      error.value = '';
    }
  );

  async function submit() {
    const r = props.request;
    if (!r) return;
    const failMessage = `The ${kindWord.value} could not be saved.`;
    if (r.mode === 'create') {
      if (pickedExisting.value) {
        emit('picked', pickedExisting.value);
        return;
      }
      const created = await create(name.value, r.kind, failMessage);
      if (created) emit('created', created);
      return;
    }
    const trimmed = name.value.trim();
    if (!trimmed) {
      error.value = 'Give it a name.';
      return;
    }
    if (r.seriesId == null) return;
    busy.value = true;
    error.value = '';
    try {
      emit('renamed', await $api<SeriesRef>(`admin/series/${r.seriesId}`, { method: 'PATCH', body: { name: trimmed } }));
    } catch (e) {
      error.value = apiErrorMessage(e, failMessage);
    } finally {
      busy.value = false;
    }
  }
</script>

<template>
  <Dialog
    :visible="!!request"
    :header="request?.mode === 'rename' ? `Rename ${kindWord}` : `New ${kindWord}`"
    modal
    class="w-full max-w-md"
    @update:visible="(v: boolean) => !v && emit('close')"
  >
    <form v-if="request" class="flex flex-col gap-3" @submit.prevent="submit">
      <div class="flex flex-col gap-1.5">
        <label for="fb-series-name" class="text-sm font-medium">Name</label>
        <InputText id="fb-series-name" v-model="name" autofocus class="w-full" :invalid="!!error" />
      </div>
      <div v-if="request.mode === 'create' && request.kind === SeriesKind.Series" class="flex flex-col gap-1.5">
        <label for="fb-series-existing" class="text-sm font-medium">Or use an existing series</label>
        <AutoComplete
          v-model="existing"
          input-id="fb-series-existing"
          :suggestions="suggestions"
          option-label="name"
          placeholder="Search series…"
          class="w-full"
          input-class="w-full"
          @complete="(e: { query: string }) => fetchSuggestions(e.query)"
        >
          <template #option="{ option }">
            <span class="truncate text-sm">{{ option.name }}</span>
            <span class="ml-2 text-xs text-gray-500 dark:text-gray-400">{{ option.deckCount }} decks</span>
          </template>
        </AutoComplete>
        <p class="m-0 text-xs text-gray-500 dark:text-gray-400">An existing series joins the board empty; drop decks on its rail to add them.</p>
      </div>
      <p v-if="request.mode === 'create'" class="m-0 text-xs text-gray-500 dark:text-gray-400">
        {{ request.kind === SeriesKind.Setting ? 'The setting' : 'The series' }} is created right away. Adding decks to it waits until you save.
      </p>
      <Message v-if="error" severity="error" :closable="false">{{ error }}</Message>
      <div class="flex justify-end gap-2">
        <Button label="Cancel" severity="secondary" text type="button" @click="emit('close')" />
        <Button :label="request.mode === 'rename' ? 'Rename' : pickedExisting ? 'Use this series' : 'Create'" type="submit" :loading="busy" />
      </div>
    </form>
  </Dialog>
</template>
