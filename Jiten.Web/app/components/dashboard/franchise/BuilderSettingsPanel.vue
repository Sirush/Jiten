<script setup lang="ts">
  import { ref, computed, watch } from 'vue';
  import Button from 'primevue/button';
  import AutoComplete from 'primevue/autocomplete';
  import Select from 'primevue/select';
  import { SeriesKind, type SeriesSummary } from '~/types/series';
  import { activeMembers, type BuilderDropTarget, type BuilderSeries, type BuilderState } from '~/utils/franchiseBuilder';

  const props = defineProps<{
    settings: BuilderSeries[];
    state: BuilderState;
    title: (deckId: number) => string;
    order: (deckIds: number[]) => number[];
    dropTarget: BuilderDropTarget | null;
  }>();

  const emit = defineEmits<{
    create: [];
    rename: [seriesId: number];
    pick: [setting: SeriesSummary];
    add: [seriesId: number, deckId: number];
    leave: [seriesId: number, deckId: number];
  }>();

  const localiseTitle = useLocaliseTitle();
  const query = ref<SeriesSummary | string | null>(null);
  const { suggestions, fetchSuggestions } = useSeriesSuggestions(SeriesKind.Setting);
  watch(query, (v) => {
    if (v && typeof v === 'object') {
      emit('pick', v);
      query.value = null;
    }
  });

  const rows = computed(() =>
    props.settings.map((s) => ({
      setting: s,
      name: localiseTitle(s),
      members: props.state.members.filter((m) => m.seriesId === s.seriesId && props.state.board.includes(m.deckId)),
      outside: s.outsideCount ?? 0,
      options: props
        .order(props.state.board.filter((id) => !activeMembers(props.state).some((m) => m.seriesId === s.seriesId && m.deckId === id)))
        .map((id) => ({ value: id, label: props.title(id) })),
    }))
  );
</script>

<template>
  <section class="flex flex-col gap-2.5 rounded-md border border-gray-200 bg-white p-3.5 dark:border-gray-800 dark:bg-gray-900" aria-labelledby="fb-settings-h">
    <div class="flex items-center justify-between gap-2">
      <h2 id="fb-settings-h" class="m-0 text-sm font-bold">Settings</h2>
      <Button label="New setting" size="small" severity="secondary" text @click="emit('create')" />
    </div>
    <p class="m-0 text-xs text-gray-500 dark:text-gray-400">Shared worlds. A setting lists decks together but never merges their franchises.</p>
    <AutoComplete
      v-model="query"
      :suggestions="suggestions"
      :option-label="localiseTitle"
      placeholder="Find an existing setting…"
      class="w-full"
      input-class="w-full"
      @complete="(e: { query: string }) => fetchSuggestions(e.query)"
    >
      <template #option="{ option }">
        <span class="truncate text-sm" v-bind="japaneseTextAttrs(localiseTitle(option))">{{ localiseTitle(option) }}</span>
        <span class="ml-2 text-xs text-gray-500 dark:text-gray-400">{{ option.deckCount }} decks</span>
      </template>
    </AutoComplete>
    <p v-if="!rows.length" class="m-0 text-xs text-gray-500 dark:text-gray-400">No settings in this franchise.</p>
    <div
      v-for="row in rows"
      :key="row.setting.seriesId"
      :data-drop="`setting:${row.setting.seriesId}`"
      class="flex flex-col gap-2 rounded border p-2.5 transition-colors"
      :class="
        dropTarget?.kind === 'setting' && dropTarget.id === row.setting.seriesId
          ? 'border-primary-500 bg-primary-50 outline-2 outline-dashed outline-primary-500 dark:bg-primary-950/40'
          : 'border-gray-200 dark:border-gray-700'
      "
    >
      <div class="flex items-center justify-between gap-2">
        <span class="truncate text-[13px] font-semibold" v-bind="japaneseTextAttrs(row.name)">{{ row.name }}</span>
        <Tooltip content="Rename">
          <Button size="small" text severity="secondary" :aria-label="`Rename ${row.name}`" @click="emit('rename', row.setting.seriesId)">
            <Icon name="material-symbols:edit-outline-rounded" />
          </Button>
        </Tooltip>
      </div>
      <ul v-if="row.members.length" class="m-0 flex list-none flex-wrap gap-1 p-0">
        <li
          v-for="m in row.members"
          :key="m.deckId"
          class="inline-flex max-w-full items-center gap-1 rounded border px-1.5 py-0.5 text-xs"
          :class="
            m.status === 'removed'
              ? 'border-dashed border-red-300 text-gray-500 line-through dark:border-red-800 dark:text-gray-400'
              : m.status === 'new'
                ? 'border-primary-400 bg-primary-50 dark:border-primary-700 dark:bg-primary-950/40'
                : 'border-gray-200 bg-gray-50 dark:border-gray-700 dark:bg-gray-800'
          "
        >
          <span class="truncate" v-bind="japaneseTextAttrs(title(m.deckId))">{{ title(m.deckId) }}</span>
          <button
            v-if="m.status !== 'removed'"
            type="button"
            class="grid h-4 w-4 place-items-center rounded-sm text-gray-500 hover:bg-red-50 hover:text-red-700 dark:text-gray-400 dark:hover:bg-red-950 dark:hover:text-red-400"
            :aria-label="`Take ${title(m.deckId)} out of ${row.name}`"
            @click="emit('leave', row.setting.seriesId, m.deckId)"
          >
            <Icon name="material-symbols:close-rounded" size="12" />
          </button>
        </li>
      </ul>
      <p v-if="row.outside" class="m-0 text-xs text-gray-500 dark:text-gray-400">
        Also {{ row.outside }} {{ row.outside === 1 ? 'deck' : 'decks' }} outside this franchise.
      </p>
      <Select
        :model-value="null"
        :options="row.options"
        option-label="label"
        option-value="value"
        filter
        placeholder="Add a deck from the board"
        size="small"
        class="w-full"
        :aria-label="`Add a deck to ${row.name}`"
        @update:model-value="(id: number | null) => id != null && emit('add', row.setting.seriesId, id)"
      />
    </div>
  </section>
</template>
