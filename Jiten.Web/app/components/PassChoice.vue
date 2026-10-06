<script setup lang="ts">
import type {MediaType} from '~/types';
import {mediaWords} from '~/utils/mediaListEntry';

const props = defineProps<{
  kind: 'reread' | 'resume' | 'unfinish';
  mediaType: MediaType;
  backLabel?: string;
  disabled?: boolean;
}>();

const emit = defineEmits<{ choose: [choice: boolean]; back: [] }>();

const words = computed(() => mediaWords(props.mediaType));

const heading = computed(() => {
  if (props.kind === 'reread') return `${words.value.again}?`;
  if (props.kind === 'resume') return 'Pick up where you left off?';
  return `Did you finish your ${words.value.noun}?`;
});

const options = computed(() => {
  if (props.kind === 'reread')
    return [
      {label: `Yes, start a new ${words.value.noun}`, choice: true},
      {label: "No, I haven't finished it yet", choice: false},
    ];
  if (props.kind === 'resume')
    return [
      {label: `Yes, continue my last ${words.value.noun}`, choice: false},
      {label: `No, start a new ${words.value.noun}`, choice: true},
    ];
  return [
    {label: 'Yes, keep it as finished', choice: false},
    {label: 'No, I stopped before the end', choice: true},
  ];
});

const root = ref<HTMLElement>();
onMounted(() => root.value?.focus({preventScroll: true}));

const optionClass = 'px-3 py-1.5 rounded text-left text-sm hover:bg-gray-100 dark:hover:bg-gray-700 focus-visible:bg-gray-100 dark:focus-visible:bg-gray-700 transition-colors cursor-pointer disabled:opacity-50 disabled:cursor-default';
</script>

<template>
  <div ref="root" class="flex flex-col min-w-56 outline-none" role="group" :aria-label="heading" tabindex="-1">
    <div class="flex items-center gap-1 px-1 pb-1">
      <button
          type="button"
          class="p-1.5 rounded hover:bg-gray-100 dark:hover:bg-gray-700 transition-colors cursor-pointer"
          :aria-label="backLabel ?? 'Back to statuses'"
          @click="emit('back')"
      >
        <i class="pi pi-arrow-left text-xs" aria-hidden="true"/>
      </button>
      <span class="text-sm font-semibold">{{ heading }}</span>
    </div>
    <button
        v-for="(option, index) in options"
        :key="option.label"
        type="button"
        :class="[optionClass, index === 0 ? 'font-bold' : '']"
        :disabled="disabled"
        @click="emit('choose', option.choice)"
    >
      {{ option.label }}
    </button>
  </div>
</template>
