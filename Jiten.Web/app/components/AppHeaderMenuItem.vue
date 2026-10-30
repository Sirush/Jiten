<script setup lang="ts">
  import type { MenuItem } from 'primevue/menuitem';

  const props = defineProps<{ item: MenuItem }>();

  const route = useRoute();
  const active = computed(() => route.path.startsWith(props.item.route));
</script>

<template>
  <NuxtLink
    :to="item.route"
    class="flex items-center gap-3 rounded-md px-3 py-2 !no-underline transition-colors"
    :class="active ? 'bg-purple-50 dark:bg-purple-900/30' : 'hover:bg-purple-50 dark:hover:bg-purple-900/20'"
  >
    <Icon :name="item.icon ?? ''" size="20" class="shrink-0 text-purple-600 dark:text-purple-300" />
    <span class="flex flex-col leading-tight">
      <span class="text-sm font-semibold" :class="active ? 'text-purple-700 dark:text-purple-200' : 'text-surface-900 dark:text-surface-0'">{{ item.label }}</span>
      <span class="text-xs text-surface-500 dark:text-surface-400">{{ item.hint }}</span>
    </span>
  </NuxtLink>
</template>
