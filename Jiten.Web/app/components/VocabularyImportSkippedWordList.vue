<script setup lang="ts">
  const props = defineProps<{ words: { wordId: number; spelling: string }[] }>();

  const search = ref('');
  const filtered = computed(() => {
    const q = search.value.trim().toLowerCase();
    return q ? props.words.filter((w) => w.spelling.toLowerCase().includes(q)) : props.words;
  });

  const ITEM_HEIGHT = 32;
  const VISIBLE_HEIGHT = 280;

  const scrollTop = ref(0);
  function onScroll(e: Event) {
    scrollTop.value = (e.target as HTMLElement).scrollTop;
  }

  const startIndex = computed(() => Math.floor(scrollTop.value / ITEM_HEIGHT));
  const visibleCount = computed(() => Math.ceil(VISIBLE_HEIGHT / ITEM_HEIGHT) + 2);
  const endIndex = computed(() => Math.min(startIndex.value + visibleCount.value, filtered.value.length));
  const visibleItems = computed(() => filtered.value.slice(startIndex.value, endIndex.value));
  const totalHeight = computed(() => filtered.value.length * ITEM_HEIGHT);
  const offsetY = computed(() => startIndex.value * ITEM_HEIGHT);
</script>

<template>
  <div>
    <div v-if="words.length > 20" class="flex justify-end mb-1">
      <InputText v-model="search" placeholder="Filter..." class="!text-xs !py-1 w-32 sm:w-40" />
    </div>
    <div
      class="rounded border border-gray-200 dark:border-gray-700 overflow-y-auto"
      :style="{ height: `${Math.min(VISIBLE_HEIGHT, Math.max(filtered.length, 1) * ITEM_HEIGHT)}px` }"
      @scroll="onScroll"
    >
      <div v-if="filtered.length === 0" class="px-3 text-sm text-gray-400 flex items-center" :style="{ height: `${ITEM_HEIGHT}px` }">No match.</div>
      <div v-else :style="{ height: `${totalHeight}px`, position: 'relative' }">
        <div :style="{ transform: `translateY(${offsetY}px)` }">
          <div
            v-for="(word, i) in visibleItems"
            :key="`${word.wordId}-${word.spelling}`"
            class="flex items-center justify-between px-3 text-sm hover:bg-gray-100 dark:hover:bg-gray-700/50"
            :class="(startIndex + i) % 2 === 1 ? 'bg-gray-50 dark:bg-gray-800/50' : ''"
            :style="{ height: `${ITEM_HEIGHT}px` }"
          >
            <span class="font-noto-sans truncate" lang="ja">{{ word.spelling || '(no spelling)' }}</span>
            <span class="text-xs text-gray-400 tabular-nums shrink-0">jpdb #{{ word.wordId }}</span>
          </div>
        </div>
      </div>
    </div>
    <div v-if="search && filtered.length !== words.length" class="text-xs text-gray-400 text-right mt-1">{{ filtered.length }} of {{ words.length }} shown</div>
  </div>
</template>
