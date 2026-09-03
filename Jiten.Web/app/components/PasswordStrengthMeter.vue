<script setup lang="ts">
  import { PASSWORD_REQUIREMENTS, passwordStrength } from '~/utils/passwordStrength';

  const props = defineProps<{ value: string; error?: string | null }>();

  const strength = computed(() => passwordStrength(props.value));

  const barClasses: Record<string, string> = {
    none: 'bg-transparent',
    weak: 'bg-red-500',
    medium: 'bg-amber-500',
    strong: 'bg-emerald-500',
  };

  const labelClasses: Record<string, string> = {
    none: 'text-gray-600 dark:text-gray-400',
    weak: 'text-red-600 dark:text-red-400',
    medium: 'text-amber-600 dark:text-amber-400',
    strong: 'text-emerald-600 dark:text-emerald-400',
  };

  const barWidth = computed(() => `${(strength.value.score / 4) * 100}%`);
</script>

<template>
  <div class="flex flex-col gap-1 pt-2" role="status" aria-live="polite">
    <div class="h-1 w-full rounded-sm bg-gray-200 dark:bg-gray-700 overflow-hidden" aria-hidden="true">
      <div class="h-full transition-[width] duration-200 motion-reduce:transition-none" :class="barClasses[strength.level]" :style="{ width: barWidth }" />
    </div>
    <small v-if="error" class="block text-xs leading-tight min-h-[2lh] text-red-500">{{ error }}</small>
    <small v-else class="block text-xs leading-tight min-h-[2lh]" :class="labelClasses[strength.level]">
      <template v-if="strength.level === 'none'">{{ PASSWORD_REQUIREMENTS }}</template>
      <template v-else-if="strength.level === 'strong'">{{ strength.label }}</template>
      <template v-else>{{ strength.label }}. {{ PASSWORD_REQUIREMENTS }}</template>
    </small>
  </div>
</template>
