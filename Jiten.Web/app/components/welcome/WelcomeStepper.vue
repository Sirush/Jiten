<script setup lang="ts">
  export interface WelcomeStepDef {
    key: string;
    label: string;
  }

  const props = defineProps<{ steps: WelcomeStepDef[]; current: string }>();

  const currentIndex = computed(() => props.steps.findIndex((s) => s.key === props.current));
</script>

<template>
  <ol aria-label="Setup steps" class="flex items-center gap-2 sm:gap-3 text-[13px]">
    <template v-for="(step, i) in steps" :key="step.key">
      <li
        v-if="i > 0"
        aria-hidden="true"
        class="flex-1 h-px min-w-3"
        :class="i <= currentIndex ? 'bg-primary-300 dark:bg-primary-700' : 'bg-gray-300 dark:bg-neutral-700'"
      />
      <li
        class="flex items-center gap-2 shrink-0"
        :class="
          i === currentIndex
            ? 'font-semibold text-primary-700 dark:text-primary-300'
            : i < currentIndex
              ? 'text-gray-600 dark:text-gray-300'
              : 'text-gray-500 dark:text-gray-400'
        "
        :aria-current="i === currentIndex ? 'step' : undefined"
      >
        <span
          class="w-6 h-6 rounded-full flex items-center justify-center text-xs shrink-0"
          :class="
            i < currentIndex
              ? 'bg-primary-600 text-white'
              : i === currentIndex
                ? 'border-2 border-primary-600 dark:border-primary-400'
                : 'border border-gray-300 dark:border-neutral-600'
          "
        >
          <Icon v-if="i < currentIndex" name="material-symbols:check-rounded" size="1.1em" />
          <template v-else>{{ i + 1 }}</template>
        </span>
        <span :class="i === currentIndex ? '' : 'hidden md:inline'">{{ step.label }}</span>
      </li>
    </template>
  </ol>
</template>
