<script setup lang="ts">
  const props = withDefaults(
    defineProps<{
      tier?: 'any' | 'full' | 'trial';
      link?: boolean;
    }>(),
    {
      tier: 'any',
      link: true,
    },
  );

  const label = computed(() => (props.tier === 'trial' ? 'Jiten+ Trial' : 'Jiten+'));
</script>

<template>
  <component
    :is="link ? 'NuxtLink' : 'span'"
    :to="link ? '/jiten-plus' : undefined"
    class="jiten-plus-badge"
    :class="{ 'jiten-plus-badge--link': link }"
  >
    {{ label }}
  </component>
</template>

<style scoped>
  .jiten-plus-badge {
    display: inline-flex;
    align-items: center;
    padding: 0.1rem 0.5rem;
    border-radius: 0.375rem;
    font-size: 0.7rem;
    font-weight: 600;
    line-height: 1.25;
    white-space: nowrap;
    color: var(--p-primary-700);
    background: var(--p-primary-100);
    text-decoration: none;
  }

  :global(.dark-mode .jiten-plus-badge) {
    color: var(--p-primary-200);
    background: var(--p-primary-900);
  }

  .jiten-plus-badge--link {
    cursor: pointer;
  }

  .jiten-plus-badge--link:hover {
    background: var(--p-primary-200);
  }

  :global(.dark-mode .jiten-plus-badge--link:hover) {
    background: var(--p-primary-800);
  }
</style>
