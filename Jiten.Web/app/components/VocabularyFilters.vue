<script setup lang="ts">
  import VocabularyAdvancedFilters from '~/components/VocabularyAdvancedFilters.vue';

  const props = defineProps<{
    sortByOptions: { label: string; value: string }[];
    sortBy: string;
    sortDescending: boolean;
    displayFilter: string;
    showDisplayFilter?: boolean;
    sortByWidth?: string;
    search?: string;
  }>();

  const emit = defineEmits<{
    'update:sortBy': [value: string];
    'update:sortDescending': [value: boolean];
    'update:displayFilter': [value: string];
    'update:search': [value: string];
  }>();

  const includePos = defineModel<string[]>('includePos', { default: () => [] });
  const excludePos = defineModel<string[]>('excludePos', { default: () => [] });
  const hideKanaOnly = defineModel<boolean>('hideKanaOnly', { default: false });

  const displayOptions = [
    { label: 'All', value: 'all' },
    { label: 'In My List', value: 'known' },
    { label: 'Only Young', value: 'young' },
    { label: 'Only Mature', value: 'mature' },
    { label: 'Only Mastered', value: 'mastered' },
    { label: 'Only Blacklisted', value: 'blacklisted' },
    { label: 'Only Unknown', value: 'unknown' },
  ];

  const sortByModel = computed({
    get: () => props.sortBy,
    set: (v) => emit('update:sortBy', v),
  });

  const displayModel = computed({
    get: () => props.displayFilter,
    set: (v) => emit('update:displayFilter', v),
  });

  const sortByWidthClass = computed(() => props.sortByWidth ?? 'md:w-56');

  const resetAdvancedFilters = () => {
    includePos.value = [];
    excludePos.value = [];
    hideKanaOnly.value = false;
  };

  const sortPopover = ref();
  const displayPopover = ref();

  const sortLabel = computed(() => props.sortByOptions.find((o) => o.value === props.sortBy)?.label ?? 'Sort');
  const displayLabel = computed(() => displayOptions.find((o) => o.value === props.displayFilter)?.label ?? 'All');

  // Listbox emits null when the selected row is tapped again; keep the current value instead.
  const onSortPicked = (value: unknown) => {
    if (value == null) return;
    emit('update:sortBy', value as string);
    sortPopover.value?.hide();
  };

  const onDisplayPicked = (value: unknown) => {
    if (value == null) return;
    emit('update:displayFilter', value as string);
    displayPopover.value?.hide();
  };
</script>

<template>
  <div
    class="flex gap-2 md:w-full max-md:flex-row max-md:flex-wrap max-md:items-center md:flex-row max-md:sticky max-md:top-0 max-md:z-20 max-md:-mx-4 max-md:border-b max-md:border-surface-200 max-md:bg-[var(--p-neutral-50)] max-md:px-4 max-md:py-2 max-md:dark:border-surface-800 max-md:dark:bg-black"
  >
    <div class="hidden md:flex gap-2">
      <FloatLabel variant="on">
        <Select
          v-model="sortByModel"
          :options="sortByOptions"
          option-label="label"
          option-value="value"
          placeholder="Sort by"
          input-id="sortBy"
          :class="['w-full', sortByWidthClass]"
        />
        <label for="sortBy">Sort by</label>
      </FloatLabel>
      <Button @click="emit('update:sortDescending', !sortDescending)" class="min-w-12 w-12">
        <Icon v-if="sortDescending" name="mingcute:az-sort-descending-letters-line" size="1.25em" />
        <Icon v-else name="mingcute:az-sort-ascending-letters-line" size="1.25em" />
      </Button>
    </div>

    <IconField v-if="search !== undefined" class="flex-1 max-md:min-w-32 md:min-w-48">
      <InputIcon>
        <Icon name="material-symbols:search-rounded" />
      </InputIcon>
      <InputText :model-value="search" @update:model-value="$emit('update:search', $event)" placeholder="Search words or definitions..." class="w-full" />
      <InputIcon v-if="search" class="cursor-pointer" @click="$emit('update:search', '')">
        <Icon name="material-symbols:close" />
      </InputIcon>
    </IconField>

    <div class="md:hidden shrink-0">
      <Button class="px-2!" :aria-label="`Sort by ${sortLabel}, ${sortDescending ? 'descending' : 'ascending'}`" @click="sortPopover.toggle($event)">
        <Icon name="material-symbols:sort-rounded" size="1.25em" />
      </Button>
    </div>

    <Popover ref="sortPopover" class="md:hidden">
      <div class="flex w-56 flex-col gap-3">
        <SelectButton
          :model-value="sortDescending"
          :options="[{ label: 'Ascending', value: false }, { label: 'Descending', value: true }]"
          option-label="label"
          option-value="value"
          :allow-empty="false"
          size="small"
          class="w-full"
          @update:model-value="emit('update:sortDescending', $event)"
        />
        <Listbox
          :model-value="sortBy"
          :options="sortByOptions"
          option-label="label"
          option-value="value"
          scroll-height="50vh"
          class="w-full border-0!"
          @update:model-value="onSortPicked"
        />
      </div>
    </Popover>

    <div v-if="showDisplayFilter" class="md:hidden shrink-0">
      <Button class="px-2!" severity="secondary" :aria-label="`Display: ${displayLabel}`" @click="displayPopover.toggle($event)">
        <Icon name="material-symbols:visibility-outline-rounded" size="1.25em" />
      </Button>
    </div>

    <Popover ref="displayPopover" class="md:hidden">
      <Listbox
        :model-value="displayFilter"
        :options="displayOptions"
        option-label="label"
        option-value="value"
        scroll-height="50vh"
        class="w-48 border-0!"
        @update:model-value="onDisplayPicked"
      />
    </Popover>

    <div class="max-md:order-last max-md:w-full md:contents">
      <slot />
    </div>

    <div v-if="showDisplayFilter" class="hidden md:block">
      <FloatLabel variant="on">
        <Select
          v-model="displayModel"
          :options="displayOptions"
          option-label="label"
          option-value="value"
          placeholder="display"
          input-id="display"
          class="w-full md:w-56"
          scroll-height="50vh"
        />
        <label for="display">Display</label>
      </FloatLabel>
    </div>

    <VocabularyAdvancedFilters
      v-model:include-pos="includePos"
      v-model:exclude-pos="excludePos"
      v-model:hide-kana-only="hideKanaOnly"
      @reset="resetAdvancedFilters"
    />
  </div>
</template>
