<script setup lang="ts">
  import { posCategories, type PosCategory } from '~/utils/posCategories';

  const model = defineModel<string[]>({ required: true });

  const expanded = ref<Record<string, boolean>>({});
  const toggleExpand = (key: string) => {
    expanded.value[key] = !expanded.value[key];
  };

  const categorySelectedCount = (cat: PosCategory) =>
    cat.tags.reduce((n, t) => (model.value.includes(t.value) ? n + 1 : n), 0);

  const categoryState = (cat: PosCategory): 'all' | 'some' | 'none' => {
    const c = categorySelectedCount(cat);
    if (c === 0) return 'none';
    return c === cat.tags.length ? 'all' : 'some';
  };

  const toggleCategory = (cat: PosCategory) => {
    const tagValues = cat.tags.map((t) => t.value);
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
    <div
      v-if="model.length > 0"
      class="flex items-center justify-between px-3 py-1.5 text-xs bg-gray-50 dark:bg-gray-800 border-b border-gray-200 dark:border-gray-700"
    >
      <span class="text-gray-600 dark:text-gray-300">{{ model.length }} selected</span>
      <button type="button" class="text-purple-600 dark:text-purple-400 hover:underline cursor-pointer" @click="clearAll">
        Clear
      </button>
    </div>

    <div class="max-h-64 overflow-y-auto divide-y divide-gray-100 dark:divide-gray-800">
      <div v-for="cat in posCategories" :key="cat.key">
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
              <span v-if="categorySelectedCount(cat) > 0" class="text-xs text-purple-600 dark:text-purple-400">
                {{ categorySelectedCount(cat) }}
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
    </div>
  </div>
</template>
