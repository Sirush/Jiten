<script setup lang="ts">
  const props = defineProps<{
    allOnPageSelected: boolean;
    selectedCount: number;
    pageCount: number;
    matchingCount?: number;
    allMatchingSelected?: boolean;
  }>();

  const emit = defineEmits<{ togglePage: []; selectAllMatching: [] }>();

  const canSelectAllMatching = computed(() => props.matchingCount !== undefined && props.matchingCount > props.pageCount && !props.allMatchingSelected);
</script>

<template>
  <div class="flex items-center gap-3 px-3 py-2 text-sm text-surface-500 dark:text-surface-400">
    <Checkbox :model-value="allOnPageSelected" :binary="true" @change="emit('togglePage')" />
    <span class="text-xs cursor-pointer select-none" @click="emit('togglePage')">
      {{ selectedCount > 0 ? `${selectedCount} selected` : `Select page (${pageCount})` }}
    </span>
    <button
      v-if="canSelectAllMatching"
      class="text-xs text-primary-600 dark:text-primary-400 hover:underline cursor-pointer"
      @click="emit('selectAllMatching')"
    >
      Select all {{ matchingCount }}
    </button>
  </div>
</template>
