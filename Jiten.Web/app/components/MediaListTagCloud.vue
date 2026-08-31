<script setup lang="ts">
  import type { TagState } from '~/components/TriStateTag.vue';

  export type TagCloudEntry = { id: number; label: string; state: TagState; dimmed?: boolean };

  withDefaults(
    defineProps<{
      entries: TagCloudEntry[];
      selectedEntries?: TagCloudEntry[];
      placeholder?: string;
      showSearch?: boolean;
      listClass?: string;
      emptyLabel?: string;
    }>(),
    { selectedEntries: () => [], placeholder: 'Search', showSearch: true, listClass: '', emptyLabel: 'Nothing matches that search.' }
  );

  const emit = defineEmits<{ set: [id: number, state: TagState] }>();

  const search = defineModel<string>('search', { required: true });
</script>

<template>
  <div class="flex min-h-0 flex-col gap-2">
    <IconField v-if="showSearch" class="shrink-0">
      <InputIcon>
        <Icon name="material-symbols:search-rounded" />
      </InputIcon>
      <InputText v-model="search" type="text" :placeholder="placeholder" :aria-label="placeholder" class="w-full" size="small" />
      <InputIcon v-if="search" class="cursor-pointer" @click="search = ''">
        <Icon name="material-symbols:close" />
      </InputIcon>
    </IconField>

    <div v-if="selectedEntries.length" class="shrink-0 border-b border-surface-200 pb-2 dark:border-surface-700">
      <div class="mb-1.5 text-[11px] font-semibold tracking-wider text-surface-500 uppercase">Selected</div>
      <div class="flex flex-wrap gap-1.5">
        <TriStateTag
          v-for="entry in selectedEntries"
          :key="entry.id"
          :label="entry.label"
          :state="entry.state"
          @update:state="(state) => emit('set', entry.id, state)"
        />
      </div>
    </div>

    <div :class="['[scrollbar-width:thin]', listClass]">
      <div class="flex flex-wrap gap-1.5 p-1">
        <TriStateTag
          v-for="entry in entries"
          :key="entry.id"
          :label="entry.label"
          :state="entry.state"
          :class="entry.dimmed ? 'pointer-events-none opacity-45' : ''"
          :aria-disabled="entry.dimmed || undefined"
          @update:state="(state) => emit('set', entry.id, state)"
        />
        <p v-if="!entries.length" class="px-1 py-2 text-sm text-surface-400">{{ emptyLabel }}</p>
      </div>
    </div>
  </div>
</template>
