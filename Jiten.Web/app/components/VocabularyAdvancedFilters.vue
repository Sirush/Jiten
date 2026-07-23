<script setup lang="ts">
  import { posCategories } from '~/utils/posCategories';
  import type { TagState } from '~/components/TriStateTag.vue';

  const emit = defineEmits<{
    reset: [];
  }>();

  const includePos = defineModel<string[]>('includePos', { required: true });
  const excludePos = defineModel<string[]>('excludePos', { required: true });
  const hideKanaOnly = defineModel<boolean>('hideKanaOnly', { required: true });

  const popover = ref();
  const searchQuery = ref('');
  const openPanels = ref<string[]>([]);

  const filteredCategories = computed(() => {
    if (!searchQuery.value) return posCategories;
    const query = searchQuery.value.toLowerCase();
    return posCategories
      .map((cat) => ({
        ...cat,
        tags: cat.tags.filter(
          (tag) => tag.label.toLowerCase().includes(query) || tag.value.toLowerCase().includes(query),
        ),
      }))
      .filter((cat) => cat.tags.length > 0);
  });

  // A search that only narrows the tags inside collapsed panels hides its own results.
  watch(searchQuery, (query) => {
    openPanels.value = query ? filteredCategories.value.map((cat) => cat.key) : [];
  });

  const activeFilterCount = computed(() => {
    let count = includePos.value.length + excludePos.value.length;
    if (hideKanaOnly.value) count++;
    return count;
  });

  // Counts come from the full category, not the search-filtered copy, so narrowing
  // the search never makes a category look less selected than it is.
  const categorySelectedCount = (key: string) => {
    const tags = posCategories.find((cat) => cat.key === key)?.tags ?? [];
    return tags.reduce((n, t) => (includePos.value.includes(t.value) || excludePos.value.includes(t.value) ? n + 1 : n), 0);
  };

  const getTagState = (tagValue: string): TagState => {
    if (includePos.value.includes(tagValue)) return 'include';
    if (excludePos.value.includes(tagValue)) return 'exclude';
    return 'neutral';
  };

  const updateTagState = (tagValue: string, state: TagState) => {
    if (state === 'include') {
      if (!includePos.value.includes(tagValue)) {
        includePos.value.push(tagValue);
      }
      excludePos.value = excludePos.value.filter((t) => t !== tagValue);
    } else if (state === 'exclude') {
      includePos.value = includePos.value.filter((t) => t !== tagValue);
      if (!excludePos.value.includes(tagValue)) {
        excludePos.value.push(tagValue);
      }
    } else {
      includePos.value = includePos.value.filter((t) => t !== tagValue);
      excludePos.value = excludePos.value.filter((t) => t !== tagValue);
    }
  };

  const clearPos = () => {
    includePos.value = [];
    excludePos.value = [];
  };

  const handleReset = () => {
    searchQuery.value = '';
    openPanels.value = [];
    emit('reset');
  };

  const toggle = (event: Event) => {
    popover.value.toggle(event);
  };

  defineExpose({ toggle });
</script>

<template>
  <div class="relative">
    <Button @click="toggle($event)">
      <Icon name="material-symbols:filter-list" size="1.25em" />
      Filters
    </Button>
    <Badge v-if="activeFilterCount > 0" :value="activeFilterCount" severity="warn" class="absolute -top-2 -right-2 pointer-events-none" />
  </div>

  <Popover ref="popover">
    <div class="flex flex-col gap-3 p-3 w-[min(32rem,calc(100vw_-_2rem))] max-h-[min(56rem,90vh)]">
      <div class="flex items-center gap-2">
        <Checkbox v-model="hideKanaOnly" class="flex-shrink-0" input-id="hideKanaOnly" binary />
        <label for="hideKanaOnly" class="text-sm font-medium text-gray-600 dark:text-gray-300">Hide kana-only words</label>
      </div>

      <div class="border-t border-gray-200 dark:border-gray-700" />

      <div class="flex items-center justify-between gap-2">
        <span class="text-sm font-semibold text-gray-700 dark:text-gray-200">Parts of Speech &amp; Usage</span>
        <button
          v-if="includePos.length + excludePos.length > 0"
          type="button"
          class="text-xs text-purple-600 dark:text-purple-400 hover:underline cursor-pointer"
          @click="clearPos"
        >
          Clear {{ includePos.length + excludePos.length }}
        </button>
      </div>

      <IconField class="w-full">
        <InputIcon>
          <Icon name="material-symbols:search-rounded" />
        </InputIcon>
        <InputText v-model="searchQuery" type="text" placeholder="Search tags..." class="w-full" />
        <InputIcon v-if="searchQuery" class="cursor-pointer" @click="searchQuery = ''">
          <Icon name="material-symbols:close" />
        </InputIcon>
      </IconField>

      <div class="flex-1 min-h-[min(32rem,45vh)] overflow-y-auto -mr-1 pr-1">
        <Accordion v-model:value="openPanels" multiple>
          <AccordionPanel v-for="category in filteredCategories" :key="category.key" :value="category.key">
            <AccordionHeader class="!py-2.5">
              <span class="flex flex-1 items-center justify-between gap-2 pr-2">
                <span class="text-sm">{{ category.label }}</span>
                <Badge
                  v-if="categorySelectedCount(category.key) > 0"
                  :value="categorySelectedCount(category.key)"
                  severity="secondary"
                />
              </span>
            </AccordionHeader>
            <AccordionContent>
              <div class="flex flex-wrap gap-2 p-1">
                <TriStateTag
                  v-for="tag in category.tags"
                  :key="tag.value"
                  :label="tag.label"
                  :state="getTagState(tag.value)"
                  @update:state="(state) => updateTagState(tag.value, state)"
                />
              </div>
            </AccordionContent>
          </AccordionPanel>
        </Accordion>

        <p v-if="filteredCategories.length === 0" class="py-4 text-center text-sm text-gray-500 dark:text-gray-400">
          No tags match "{{ searchQuery }}"
        </p>
      </div>

      <div class="flex justify-end pt-3 border-t border-gray-200 dark:border-gray-700">
        <Button severity="danger" size="small" @click="handleReset">
          <Icon name="material-symbols:refresh" class="mr-1" />
          Reset Filters
        </Button>
      </div>
    </div>
  </Popover>
</template>
