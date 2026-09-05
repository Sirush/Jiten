<script setup lang="ts">
  defineProps<{
    label: string;
    to: string;
    icon: string;
    /** Renders an unread dot next to the label. */
    marked?: boolean;
    /** Short call-to-action text shown before the chevron. */
    cta?: string;
    /** A row inside a grouped card: no border or shadow of its own, tighter padding. */
    compact?: boolean;
  }>();
</script>

<template>
  <NuxtLink
    :to="to"
    class="group flex items-start gap-3 h-full transition-colors !text-inherit !no-underline"
    :class="
      compact
        ? 'p-3 hover:bg-surface-50 dark:hover:bg-surface-800'
        : 'p-4 rounded-xl border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 shadow-sm hover:border-primary-400 dark:hover:border-primary-500'
    "
  >
    <span
      class="shrink-0 flex items-center justify-center rounded-lg bg-primary-50 dark:bg-primary-900/40 text-primary-600 dark:text-primary-300"
      :class="compact ? 'w-8 h-8' : 'w-9 h-9'"
    >
      <Icon :name="icon" :size="compact ? '1.2em' : '1.35em'" />
    </span>

    <span class="flex-1 min-w-0">
      <span class="flex items-center gap-1.5">
        <span class="text-[10px] font-semibold uppercase tracking-wide text-surface-400 dark:text-surface-400">{{ label }}</span>
        <span v-if="marked" class="w-1.5 h-1.5 rounded-full bg-primary-500" aria-label="New" />
      </span>
      <span class="block text-sm text-gray-700 dark:text-gray-300 mt-0.5">
        <slot />
      </span>
    </span>

    <span v-if="cta" class="shrink-0 self-center text-xs font-medium text-primary-600 dark:text-primary-300">{{ cta }}</span>
    <Icon
      name="material-symbols:chevron-right"
      class="shrink-0 self-center text-surface-300 dark:text-surface-400 transition-transform group-hover:translate-x-0.5"
      size="1.25em"
    />
  </NuxtLink>
</template>
