<script setup lang="ts" generic="T">
  import type { CheckAnswerOption } from '~/types/check';

  defineProps<{ options: CheckAnswerOption<T>[]; disabled: boolean }>();
  const emit = defineEmits<{ answer: [value: T] }>();
</script>

<template>
  <div class="grid grid-cols-1 sm:grid-cols-3 gap-3">
    <Button
      v-for="option in options"
      :key="option.key"
      :disabled="disabled"
      :severity="option.primary ? undefined : 'secondary'"
      :outlined="!option.primary"
      class="!min-h-14 !text-base"
      :class="option.tone"
      @click="emit('answer', option.value)"
    >
      <span class="flex items-center justify-center gap-2 w-full"
        >{{ option.label }}
        <kbd
          class="hidden sm:inline-flex items-center justify-center min-w-[1.4rem] h-[1.4rem] px-1.5 rounded-sm text-xs font-semibold border"
          :class="
            option.primary
              ? 'border-white/40 bg-white/20'
              : 'border-gray-300 bg-gray-100 text-gray-700 dark:border-neutral-600 dark:bg-neutral-800 dark:text-gray-200'
          "
          >{{ option.key }}</kbd
        ></span
      >
    </Button>
  </div>
</template>
