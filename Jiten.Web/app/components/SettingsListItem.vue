<script setup lang="ts">
  defineProps<{
    title?: string;
    description?: string;
    danger?: boolean;
  }>();
</script>

<template>
  <div
    class="rounded-lg border bg-surface-0 p-5 dark:bg-surface-900"
    :class="danger ? 'border-red-300 dark:border-red-900' : 'border-surface-200 dark:border-surface-700'"
  >
    <div v-if="title" class="flex flex-wrap items-center justify-between gap-x-6 gap-y-3">
      <div class="min-w-0 flex-1 basis-72">
        <h3 class="text-lg font-semibold" :class="{ 'text-red-700 dark:text-red-400': danger }">{{ title }}</h3>
        <p v-if="description || $slots.description" class="mt-1 text-sm leading-relaxed text-muted-color">
          <slot name="description">{{ description }}</slot>
        </p>
      </div>
      <div v-if="$slots.action" class="flex flex-wrap items-center gap-3">
        <slot name="action" />
      </div>
    </div>
    <div v-if="$slots.default" :class="{ 'mt-5': title }">
      <slot />
    </div>
  </div>
</template>
