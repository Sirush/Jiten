<script setup lang="ts">
  export interface VocabularyOption {
    key: string;
    label: string;
    desc: string;
    icon: string;
  }

  defineProps<{
    options: VocabularyOption[];
    modelValue: string | null;
  }>();

  const emit = defineEmits<{
    'update:modelValue': [value: string];
  }>();
</script>

<template>
  <div class="grid grid-cols-2 sm:grid-cols-3 gap-3">
    <div
      v-for="opt in options"
      :key="opt.key"
      @click="emit('update:modelValue', opt.key)"
      class="border rounded-lg p-3 cursor-pointer transition-all duration-200 flex flex-col gap-1 items-start relative hover:border-gray-400 hover:dark:border-gray-500 hover:shadow-sm"
      :class="
        modelValue === opt.key
          ? 'bg-primary-50 dark:bg-gray-600 border-primary dark:border-gray-700 ring-1 ring-primary'
          : 'bg-white dark:bg-gray-800 border-gray-200 dark:border-gray-700'
      "
    >
      <div class="flex items-center gap-2 w-full">
        <i :class="[opt.icon, modelValue === opt.key ? 'text-primary' : 'text-gray-400 dark:text-gray-400']" class="text-lg" />
        <span
          class="font-semibold text-sm"
          :class="modelValue === opt.key ? 'text-primary-900 dark:text-primary-300' : 'text-gray-700 dark:text-gray-300'"
        >
          {{ opt.label }}
        </span>
      </div>
      <span class="text-[10px] leading-tight text-gray-500 dark:text-gray-400">{{ opt.desc }}</span>
      <i v-if="modelValue === opt.key" class="pi pi-check-circle text-primary absolute top-2 right-2 text-sm" />
    </div>
  </div>
</template>
