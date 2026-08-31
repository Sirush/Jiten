<script setup lang="ts">
  import { MEDIA_RANGE_SECTIONS, MEDIA_RANGE_SPECS, type MediaRangeKey } from '~/utils/mediaFilterRanges';
  import type { RangeBounds } from '~/utils/rangeFilters';

  const props = withDefaults(
    defineProps<{
      isConnected: boolean;
      charCountSteps: number[];
      statusOptions: { label: string; value: string }[];
      mobile?: boolean;
      split?: boolean;
    }>(),
    { mobile: false, split: false }
  );

  const ranges = defineModel<Record<MediaRangeKey, RangeBounds>>('ranges', { required: true });
  const search = defineModel<string>('search', { required: true });
  const expandedKey = defineModel<MediaRangeKey | null>('expandedKey', { required: true });
  const statusFilter = defineModel<string>('statusFilter', { required: true });
  const excludeSequels = defineModel<boolean | null>('excludeSequels', { required: false });
  const favourite = defineModel<boolean | null>('favourite', { required: false });
  const excludeNotOriginallyJp = defineModel<boolean>('excludeNotOriginallyJp', { required: true });

  const query = computed(() => search.value.trim().toLowerCase());
  const matches = (text: string) => !query.value || text.toLowerCase().includes(query.value);

  const showStatus = computed(() => props.isConnected && matches('Status'));

  const sections = computed(() =>
    MEDIA_RANGE_SECTIONS.map((section) => ({
      ...section,
      specs: MEDIA_RANGE_SPECS.filter(
        (spec) => spec.section === section.key && (!spec.requiresAuth || props.isConnected) && matches(spec.label)
      ),
    })).filter((section) => section.specs.length > 0)
  );

  const sectionHeaderSpacing = (index: number) => {
    if (props.mobile) return 'mt-1.5 mb-0.5';
    if (props.split) return index === 0 && !showStatus.value ? 'mt-1 mb-1' : 'mt-3 mb-1';
    return 'mt-1 mb-0.5';
  };

  const toggleRow = (key: MediaRangeKey) => {
    expandedKey.value = expandedKey.value === key ? null : key;
  };

  const setRange = (key: MediaRangeKey, bounds: RangeBounds) => {
    ranges.value = { ...ranges.value, [key]: bounds };
  };
</script>

<template>
  <div :class="['flex min-h-0 flex-col', mobile ? 'gap-2' : 'gap-1.5']">
    <IconField v-if="!split" class="shrink-0">
      <InputIcon>
        <Icon name="material-symbols:search-rounded" />
      </InputIcon>
      <InputText
        v-model="search"
        type="text"
        placeholder="Find a filter, genre or tag..."
        aria-label="Find a filter, genre or tag"
        class="w-full"
        :size="mobile ? undefined : 'small'"
      />
      <InputIcon v-if="search" class="cursor-pointer" @click="search = ''">
        <Icon name="material-symbols:close" />
      </InputIcon>
    </IconField>

    <slot name="before" />

    <div :class="split ? 'flex flex-1 gap-4' : 'contents'">
      <div :class="split ? 'flex w-2/5 shrink-0 flex-col border-r border-surface-200 pr-4 dark:border-surface-700' : 'contents'">
        <div v-if="showStatus" class="flex shrink-0 items-center gap-2 px-2" :class="mobile ? 'h-11' : 'h-[34px]'">
          <label for="statusFilter" class="shrink-0 text-sm text-surface-700 dark:text-surface-200">Status</label>
          <Select
            v-model="statusFilter"
            :options="statusOptions"
            option-label="label"
            option-value="value"
            input-id="statusFilter"
            :class="['min-w-44', split ? 'flex-1' : 'ml-auto']"
            size="small"
            scroll-height="30vh"
          />
        </div>

        <div v-if="mobile && isConnected" class="flex h-11 shrink-0 items-center gap-2 px-2">
          <Checkbox v-model="favourite" class="shrink-0" input-id="favouriteOnly" binary />
          <label for="favouriteOnly" class="text-sm text-surface-700 dark:text-surface-200">Favourited</label>
        </div>

        <div v-for="(section, index) in sections" :key="section.key" class="shrink-0">
          <div
            :class="[
              'px-1 text-[11px] font-semibold tracking-wider text-surface-500 uppercase',
              sectionHeaderSpacing(index),
              mobile ? '' : 'leading-none',
            ]"
          >
            {{ section.label }}
          </div>
          <div :class="['grid gap-x-5 gap-y-0.5', mobile || split ? 'grid-cols-1' : 'grid-cols-2']">
            <MediaListFilterRow
              v-for="spec in section.specs"
              :key="spec.key"
              :spec="spec"
              :model-value="ranges[spec.key]"
              :steps="spec.key === 'charCount' ? charCountSteps : null"
              :expanded="expandedKey === spec.key"
              :mobile="mobile"
              :stack-editor="split"
              :class="expandedKey === spec.key ? 'col-span-full' : ''"
              @update:model-value="(bounds) => setRange(spec.key, bounds)"
              @toggle="toggleRow(spec.key)"
            />
          </div>
        </div>
      </div>

      <div v-if="split" class="flex min-h-0 flex-1 flex-col">
        <slot name="panes" />
      </div>
    </div>

    <slot v-if="!split" name="after" />

    <div :class="['mt-2 flex shrink-0 px-1', mobile ? 'flex-col gap-1.5' : 'flex-wrap items-center gap-x-6 gap-y-1.5']">
      <div v-if="!mobile && isConnected" class="flex items-center gap-2">
        <Checkbox v-model="favourite" class="shrink-0" input-id="favouriteOnly" binary />
        <label for="favouriteOnly" class="text-sm text-surface-700 dark:text-surface-200">Favourited</label>
      </div>
      <div class="flex items-center gap-2">
        <Checkbox v-model="excludeSequels" class="shrink-0" input-id="excludeSequels" binary />
        <label for="excludeSequels" class="text-sm text-surface-700 dark:text-surface-200">Exclude sequels and fandiscs</label>
      </div>
      <div class="flex items-center gap-2">
        <Checkbox v-model="excludeNotOriginallyJp" class="shrink-0" input-id="excludeNotOriginallyJp" binary />
        <label for="excludeNotOriginallyJp" class="text-sm text-surface-700 dark:text-surface-200">Exclude not originally Japanese media</label>
      </div>
    </div>
  </div>
</template>
