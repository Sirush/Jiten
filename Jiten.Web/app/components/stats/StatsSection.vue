<script setup lang="ts">
  import Card from 'primevue/card';

  defineProps<{
    title: string;
    subtitle?: string;
    note?: string;
  }>();
</script>

<template>
  <Card class="min-w-0">
    <template #content>
      <section class="flex flex-col gap-3 min-w-0">
        <header>
          <h2 class="text-lg font-bold">{{ title }}</h2>
          <p v-if="subtitle" class="text-sm text-gray-600 dark:text-gray-400 mt-0.5 mb-0">{{ subtitle }}</p>
        </header>
        <slot />
        <p v-if="note" class="text-xs text-gray-500 dark:text-gray-400 mb-0">{{ note }}</p>
      </section>
    </template>
  </Card>
</template>
