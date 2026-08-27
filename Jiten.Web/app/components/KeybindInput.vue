<script setup lang="ts">
  import { displayKeyName, normalizeKey } from '~/composables/useStudyKeyboard';

  const props = defineProps<{
    modelValue: string;
    label: string;
    conflict?: string | null;
  }>();

  const emit = defineEmits<{
    'update:modelValue': [value: string];
  }>();

  const listening = ref(false);
  const btnRef = ref<HTMLButtonElement>();

  function startListening() {
    listening.value = true;
  }

  function stopListening() {
    listening.value = false;
  }

  function handleKeydown(e: KeyboardEvent) {
    if (!listening.value) return;
    e.preventDefault();
    e.stopPropagation();

    if (e.key === 'Escape') {
      listening.value = false;
      return;
    }

    if (e.ctrlKey || e.altKey || e.metaKey) return;

    emit('update:modelValue', normalizeKey(e));
    listening.value = false;
  }
</script>

<template>
  <div class="flex items-center gap-2">
    <span class="text-sm min-w-[160px]">{{ label }}</span>
    <button
      ref="btnRef"
      class="keybind-btn px-3 py-1.5 rounded border text-sm font-mono min-w-[80px] text-center transition-all duration-150"
      :class="
        listening
          ? 'border-primary-500 bg-primary-50 dark:bg-primary-900/20 animate-pulse'
          : 'border-surface-300 dark:border-surface-600 bg-surface-50 dark:bg-surface-800 hover:border-surface-400 dark:hover:border-surface-500'
      "
      @click="startListening"
      @keydown="handleKeydown"
      @blur="stopListening"
    >
      {{ listening ? 'Press a key...' : displayKeyName(modelValue) }}
    </button>
    <span v-if="conflict" class="text-xs text-orange-500 dark:text-orange-400">Conflicts with {{ conflict }}</span>
  </div>
</template>
