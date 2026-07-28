<script setup lang="ts">
  // MDC block: ::tip … ::
  defineProps<{ icon?: string }>();
</script>

<template>
  <div class="guide-callout guide-callout--tip my-3 flex gap-3 rounded-lg border-l-4 p-4">
    <Icon :name="icon || 'material-symbols-light:lightbulb-outline'" class="mt-0.5 shrink-0 text-xl" />
    <div class="prose-callout min-w-0">
      <slot />
    </div>
  </div>
</template>
