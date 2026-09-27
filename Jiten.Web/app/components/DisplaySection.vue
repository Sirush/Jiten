<script setup lang="ts">
  const props = defineProps<{
    icon: string;
    title: string;
    description?: string;
    resettable?: boolean;
    keywords?: string;
  }>();
  defineEmits<{ reset: [] }>();

  const visible = useSettingsSearchGroup(
    () => [props.title, props.keywords],
    () => props.description
  );
</script>

<template>
  <section v-show="visible" class="display-section">
    <header class="flex items-start gap-3">
      <span class="display-section__icon">
        <i :class="icon" aria-hidden="true" />
      </span>
      <div class="min-w-0 flex-1">
        <h2 class="text-base font-semibold text-surface-900 dark:text-surface-0">{{ title }}</h2>
        <p v-if="description" class="text-sm text-surface-600 dark:text-surface-400">{{ description }}</p>
      </div>
      <Button
        v-if="resettable"
        label="Reset"
        icon="pi pi-undo"
        text
        size="small"
        severity="secondary"
        class="shrink-0"
        :aria-label="`Reset ${title} to the defaults`"
        @click="$emit('reset')"
      />
    </header>
    <div class="mt-3 flex flex-col">
      <slot />
    </div>
  </section>
</template>

<style scoped>
  .display-section {
    padding: 1rem;
    border-radius: var(--radius-lg);
    border: 1px solid var(--p-surface-200);
    background: var(--p-surface-0);
  }

  :global(.dark-mode .display-section) {
    border-color: var(--p-surface-700);
    background: var(--p-surface-900);
  }

  .display-section__icon {
    display: flex;
    flex-shrink: 0;
    align-items: center;
    justify-content: center;
    width: 2.25rem;
    height: 2.25rem;
    border-radius: var(--radius-lg);
    color: var(--p-primary-600);
    background: var(--p-primary-50);
  }

  :global(.dark-mode .display-section__icon) {
    color: var(--p-primary-300);
    background: var(--p-primary-950);
  }
</style>
