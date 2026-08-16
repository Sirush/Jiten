<script setup lang="ts">
  interface MatchedWord {
    wordId: number;
    readingIndex: number;
    text: string;
    reading: string;
    occurrences?: number;
  }

  const props = defineProps<{
    matched: MatchedWord[];
    unmatched: string[];
    excludedWordIds: Set<number>;
  }>();

  const emit = defineEmits<{
    toggleExclude: [wordId: number];
  }>();

  const search = ref('');

  const filteredMatched = computed(() => {
    if (!search.value.trim()) return props.matched;
    const q = search.value.trim().toLowerCase();
    return props.matched.filter(w =>
      w.text.toLowerCase().includes(q) || w.reading.toLowerCase().includes(q),
    );
  });

  const includedCount = computed(() => props.matched.length - props.excludedWordIds.size);

  const ITEM_HEIGHT = 32;
  const VISIBLE_HEIGHT = 280;

  const scrollTop = ref(0);
  const containerRef = ref<HTMLElement | null>(null);

  function onScroll(e: Event) {
    scrollTop.value = (e.target as HTMLElement).scrollTop;
  }

  const startIndex = computed(() => Math.floor(scrollTop.value / ITEM_HEIGHT));
  const visibleCount = computed(() => Math.ceil(VISIBLE_HEIGHT / ITEM_HEIGHT) + 2);
  const endIndex = computed(() => Math.min(startIndex.value + visibleCount.value, filteredMatched.value.length));
  const visibleItems = computed(() => filteredMatched.value.slice(startIndex.value, endIndex.value));
  const totalHeight = computed(() => filteredMatched.value.length * ITEM_HEIGHT);
  const offsetY = computed(() => startIndex.value * ITEM_HEIGHT);
</script>

<template>
  <div class="grid grid-cols-2 gap-2 sm:gap-4 mb-3 sm:mb-4">
    <div class="rounded-lg border border-green-200 dark:border-green-800 bg-green-50 dark:bg-green-900/20 p-2 sm:p-3 text-center">
      <div class="text-xl sm:text-2xl font-bold text-green-600 dark:text-green-400">{{ includedCount }}</div>
      <div class="text-xs text-green-600 dark:text-green-400">{{ excludedWordIds.size > 0 ? `Matched (${excludedWordIds.size} excl.)` : 'Matched' }}</div>
    </div>
    <div class="rounded-lg border border-red-200 dark:border-red-800 bg-red-50 dark:bg-red-900/20 p-2 sm:p-3 text-center">
      <div class="text-xl sm:text-2xl font-bold text-red-500 dark:text-red-400">{{ unmatched.length }}</div>
      <div class="text-xs text-red-500 dark:text-red-400">Unmatched</div>
    </div>
  </div>

  <div v-if="matched.length > 0" class="mb-3">
    <div class="flex items-center justify-between gap-2 mb-1">
      <div class="text-sm font-medium shrink-0">Matched words</div>
      <div v-if="matched.length > 20" class="w-32 sm:w-40">
        <InputText v-model="search" placeholder="Filter..." class="!text-xs !py-1 w-full" />
      </div>
    </div>
    <div
      ref="containerRef"
      class="rounded border border-gray-200 dark:border-gray-700 overflow-y-auto"
      :style="{ height: `${Math.min(VISIBLE_HEIGHT, filteredMatched.length * ITEM_HEIGHT)}px` }"
      @scroll="onScroll"
    >
      <div :style="{ height: `${totalHeight}px`, position: 'relative' }">
        <div :style="{ transform: `translateY(${offsetY}px)` }">
          <div
            v-for="(word, i) in visibleItems"
            :key="`${word.wordId}-${word.readingIndex}`"
            class="flex items-center justify-between px-3 text-sm cursor-pointer hover:bg-gray-100 dark:hover:bg-gray-700/50"
            :class="[
              excludedWordIds.has(word.wordId) ? 'opacity-40 line-through' : '',
              (startIndex + i) % 2 === 1 ? 'bg-gray-50 dark:bg-gray-800/50' : '',
            ]"
            :style="{ height: `${ITEM_HEIGHT}px` }"
            @click="emit('toggleExclude', word.wordId)"
          >
            <span class="font-noto-sans truncate" lang="ja">{{ word.text }}</span>
            <div class="flex items-center gap-2 shrink-0">
              <span v-if="word.occurrences && word.occurrences > 1" class="text-xs text-purple-500 dark:text-purple-400 tabular-nums">{{ word.occurrences }}x</span>
              <span class="text-gray-400 font-noto-sans" lang="ja">{{ word.reading }}</span>
              <Icon
                :name="excludedWordIds.has(word.wordId) ? 'material-symbols:add-circle-outline' : 'material-symbols:remove-circle-outline'"
                size="16"
                :class="excludedWordIds.has(word.wordId) ? 'text-green-500' : 'text-red-400'"
              />
            </div>
          </div>
        </div>
      </div>
    </div>
    <div class="flex items-center justify-between mt-1">
      <div v-if="excludedWordIds.size > 0" class="text-xs text-gray-500 dark:text-gray-400">
        {{ excludedWordIds.size }} excluded (click to re-include)
      </div>
      <div v-if="search && filteredMatched.length !== matched.length" class="text-xs text-gray-400 ml-auto">
        {{ filteredMatched.length }} of {{ matched.length }} shown
      </div>
    </div>
  </div>

  <div v-if="unmatched.length > 0" class="mb-3">
    <div class="text-sm font-medium mb-1 text-red-500">Unmatched ({{ unmatched.length }})</div>
    <div class="max-h-[100px] overflow-y-auto rounded border border-red-200 dark:border-red-800">
      <div
        v-for="(word, i) in unmatched.slice(0, 50)"
        :key="i"
        class="px-3 py-1 text-sm text-red-400 font-noto-sans" lang="ja"
      >
        {{ word }}
      </div>
      <div v-if="unmatched.length > 50" class="px-3 py-1 text-xs text-gray-400 text-center">
        ... and {{ unmatched.length - 50 }} more
      </div>
    </div>
  </div>
</template>
