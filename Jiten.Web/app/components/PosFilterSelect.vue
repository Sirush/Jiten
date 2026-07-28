<script setup lang="ts">
  import { posCategories, type PosCategory } from '~/utils/posCategories';

  const model = defineModel<string[]>({ required: true });

  const searchQuery = ref('');
  const expanded = ref<Record<string, boolean>>({});
  const toggleExpand = (key: string) => {
    expanded.value[key] = !expanded.value[key];
  };

  const filteredCategories = computed(() => {
    if (!searchQuery.value) return posCategories;
    const query = searchQuery.value.toLowerCase();
    return posCategories
      .map((cat) => ({
        ...cat,
        tags: cat.tags.filter((t) => t.label.toLowerCase().includes(query) || t.value.toLowerCase().includes(query)),
      }))
      .filter((cat) => cat.tags.length > 0);
  });

  // A search that only narrows the tags inside collapsed categories hides its own results.
  watch(searchQuery, (query) => {
    expanded.value = query ? Object.fromEntries(filteredCategories.value.map((cat) => [cat.key, true])) : {};
  });

  // Counts come from the full category, not the search-filtered copy, so narrowing
  // the search never makes a category look less selected than it is.
  const categorySelectedCount = (key: string) => {
    const tags = posCategories.find((cat) => cat.key === key)?.tags ?? [];
    return tags.reduce((n, t) => (model.value.includes(t.value) ? n + 1 : n), 0);
  };

  const categoryState = (cat: PosCategory): 'all' | 'some' | 'none' => {
    const total = posCategories.find((c) => c.key === cat.key)?.tags.length ?? 0;
    const c = categorySelectedCount(cat.key);
    if (c === 0) return 'none';
    return c === total ? 'all' : 'some';
  };

  const toggleCategory = (cat: PosCategory) => {
    const tagValues = posCategories.find((c) => c.key === cat.key)?.tags.map((t) => t.value) ?? [];
    if (categoryState(cat) === 'all') {
      model.value = model.value.filter((v) => !tagValues.includes(v));
    } else {
      const set = new Set(model.value);
      tagValues.forEach((v) => set.add(v));
      model.value = [...set];
    }
  };

  const clearAll = () => {
    model.value = [];
  };
</script>

<template>
  <div class="border border-gray-200 dark:border-gray-700 rounded-md overflow-hidden">
    <div class="flex items-center gap-2 px-2 py-2 border-b border-gray-200 dark:border-gray-700">
      <IconField class="flex-1">
        <InputIcon>
          <Icon name="material-symbols:search-rounded" />
        </InputIcon>
        <InputText v-model="searchQuery" type="text" placeholder="Search tags..." size="small" class="w-full" />
        <InputIcon v-if="searchQuery" class="cursor-pointer" @click="searchQuery = ''">
          <Icon name="material-symbols:close" />
        </InputIcon>
      </IconField>
      <button
        v-if="model.length > 0"
        type="button"
        class="shrink-0 text-xs text-purple-600 dark:text-purple-400 hover:underline cursor-pointer"
        @click="clearAll"
      >
        Clear {{ model.length }}
      </button>
    </div>

    <div class="max-h-72 overflow-y-auto divide-y divide-gray-100 dark:divide-gray-800">
      <div v-for="cat in filteredCategories" :key="cat.key">
        <div class="flex items-center gap-2 px-3 py-2">
          <Checkbox
            :model-value="categoryState(cat) === 'all'"
            :indeterminate="categoryState(cat) === 'some'"
            binary
            :input-id="`pos-cat-${cat.key}`"
            @update:model-value="toggleCategory(cat)"
          />
          <button
            type="button"
            class="flex flex-1 items-center justify-between min-w-0 cursor-pointer text-left"
            @click="toggleExpand(cat.key)"
          >
            <span class="text-sm font-medium truncate">
              {{ cat.label }}
              <span class="text-gray-400 font-normal">({{ cat.tags.length }})</span>
            </span>
            <span class="flex items-center gap-2 shrink-0">
              <span v-if="categorySelectedCount(cat.key) > 0" class="text-xs text-purple-600 dark:text-purple-400">
                {{ categorySelectedCount(cat.key) }}
              </span>
              <Icon
                :name="expanded[cat.key] ? 'material-symbols:expand-less' : 'material-symbols:expand-more'"
                size="1.25em"
                class="text-gray-400"
              />
            </span>
          </button>
        </div>

        <div v-if="expanded[cat.key]" class="flex flex-wrap gap-x-4 gap-y-2 px-3 pb-3 pl-9">
          <div v-for="tag in cat.tags" :key="tag.value" class="flex items-center gap-2">
            <Checkbox v-model="model" :value="tag.value" :input-id="`pos-tag-${tag.value}`" />
            <label :for="`pos-tag-${tag.value}`" class="text-sm cursor-pointer">{{ tag.label }}</label>
          </div>
        </div>
      </div>

      <p v-if="filteredCategories.length === 0" class="py-4 text-center text-sm text-gray-500 dark:text-gray-400">
        No tags match "{{ searchQuery }}"
      </p>
    </div>
  </div>
</template>
