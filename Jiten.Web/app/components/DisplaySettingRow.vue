<script setup lang="ts">
  const props = defineProps<{
    label: string;
    /** The control's input id, so clicking the label focuses it. */
    for?: string;
    description?: string;
    /** Label and control on one line, for switches. */
    inline?: boolean;
    /** Extra words the settings search should find this row by. */
    keywords?: string;
  }>();

  const visible = useSettingsSearchItem(() => [props.label, props.description, props.keywords]);
</script>

<template>
  <div v-if="inline" v-show="visible" class="row-span-2 flex flex-col gap-2 self-start">
    <div class="flex items-center justify-between gap-4">
      <div class="min-w-0">
        <label :for="props.for" class="block cursor-pointer text-sm font-medium text-surface-900 dark:text-surface-0">{{ label }}</label>
        <p v-if="description" class="mt-0.5 text-xs text-surface-600 dark:text-surface-400">{{ description }}</p>
      </div>
      <div class="shrink-0">
        <slot />
      </div>
    </div>
    <slot name="preview" />
  </div>
  <div v-else v-show="visible" class="row-span-2 grid min-w-0 grid-rows-subgrid gap-y-2">
    <div class="min-w-0">
      <label :for="props.for" class="block text-sm font-medium text-surface-900 dark:text-surface-0">{{ label }}</label>
      <p v-if="description" class="mt-0.5 text-xs text-surface-600 dark:text-surface-400">{{ description }}</p>
    </div>
    <div class="flex min-w-0 flex-col gap-2">
      <slot />
      <slot name="preview" />
    </div>
  </div>
</template>
