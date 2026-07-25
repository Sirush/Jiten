<script setup lang="ts">
  import { NuxtLink } from '#components';

  const props = withDefaults(
    defineProps<{
      icon: string;
      title: string;
      to?: string;
      description?: string;
      status?: string | null;
      plus?: boolean;
    }>(),
    { to: undefined, description: undefined, status: undefined, plus: false },
  );

  const root = computed(() => (props.to ? NuxtLink : 'div'));
</script>

<template>
  <component :is="root" :to="to" class="settings-tile" :class="{ 'settings-tile--link': !!to }">
    <span class="settings-tile__icon">
      <i :class="icon" aria-hidden="true" />
    </span>

    <component :is="to ? 'span' : 'div'" class="min-w-0 flex-1">
      <span class="flex flex-wrap items-center gap-2">
        <span class="font-semibold text-surface-900 dark:text-surface-0">{{ title }}</span>
        <JitenPlusBadge v-if="plus" :link="false" />
      </span>
      <span v-if="description" class="mt-0.5 block text-sm text-surface-500 dark:text-surface-400">{{ description }}</span>
      <span v-if="status" class="mt-1 block text-sm font-medium text-primary-600 dark:text-primary-300">{{ status }}</span>
      <slot />
    </component>

    <i v-if="to" class="pi pi-angle-right settings-tile__chevron" aria-hidden="true" />
  </component>
</template>

<style scoped>
  .settings-tile {
    display: flex;
    align-items: flex-start;
    gap: 0.75rem;
    height: 100%;
    padding: 1rem;
    border-radius: 0.5rem;
    border: 1px solid var(--p-surface-200);
    background: var(--p-surface-0);
    transition:
      border-color 0.15s ease,
      background-color 0.15s ease;
  }

  :global(.dark-mode .settings-tile) {
    border-color: var(--p-surface-700);
    background: var(--p-surface-900);
  }

  /* Beats the global `a:not(.p-button)` blue-link rule. */
  .settings-tile--link,
  .settings-tile--link:hover {
    color: inherit;
    text-decoration: none;
  }

  .settings-tile--link:hover {
    border-color: var(--p-primary-400);
    background: var(--p-surface-50);
  }

  :global(.dark-mode .settings-tile--link:hover) {
    border-color: var(--p-primary-500);
    background: var(--p-surface-800);
  }

  /* primary-400 only clears 3:1 against the dark surface; on white it is 2.6:1. */
  .settings-tile--link:focus-visible {
    outline: 2px solid var(--p-primary-600);
    outline-offset: 2px;
  }

  :global(.dark-mode .settings-tile--link:focus-visible) {
    outline-color: var(--p-primary-400);
  }

  .settings-tile__icon {
    display: flex;
    flex-shrink: 0;
    align-items: center;
    justify-content: center;
    width: 2.25rem;
    height: 2.25rem;
    border-radius: 0.5rem;
    color: var(--p-primary-600);
    background: var(--p-primary-50);
  }

  :global(.dark-mode .settings-tile__icon) {
    color: var(--p-primary-300);
    background: var(--p-primary-950);
  }

  .settings-tile__chevron {
    flex-shrink: 0;
    align-self: center;
    color: var(--p-surface-400);
  }
</style>
