<script setup lang="ts">
  defineProps<{ inline?: boolean }>();

  const { $reloadForUpdate } = useNuxtApp();
  const updateAvailable = useState('build-update-available', () => false);
  const dismissed = ref(false);
</script>

<template>
  <div
    v-if="updateAvailable && !dismissed"
    role="status"
    :class="
      inline
        ? 'flex items-center justify-center gap-3 px-3 py-1 text-xs border-b border-surface-200 dark:border-surface-800 bg-surface-50 dark:bg-surface-900 text-surface-600 dark:text-surface-300'
        : 'fixed z-50 bottom-3 left-3 max-w-[calc(100vw-1.5rem)] flex items-center gap-3 pl-4 pr-2 py-2 text-sm rounded-lg border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 text-surface-700 dark:text-surface-200 shadow-md'
    "
  >
    <span>A new version of Jiten is available.</span>
    <button
      class="font-semibold text-primary-600 dark:text-primary-400 hover:underline cursor-pointer rounded focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary-500"
      :class="inline ? 'py-1' : 'px-1 py-1'"
      @click="$reloadForUpdate()"
    >
      Refresh
    </button>
    <button
      class="leading-none text-surface-400 hover:text-surface-700 dark:hover:text-surface-100 cursor-pointer rounded p-1 focus-visible:outline-2 focus-visible:outline-primary-500"
      :class="inline ? 'text-base' : 'text-lg'"
      aria-label="Dismiss"
      @click="dismissed = true"
    >
      &times;
    </button>
  </div>
</template>
