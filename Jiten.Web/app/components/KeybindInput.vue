<script setup lang="ts">
  import { displayKeyName, mouseToken, normalizeKey } from '~/composables/useStudyKeyboard';

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


  function toggleListening() {
    listening.value = !listening.value;
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

  // Chromium navigates back/forward on mouseup, so the release after a capture must be swallowed too.
  let swallowRelease = false;
  let swallowContextMenu = false;

  function handleMousedown(e: MouseEvent) {
    if (!listening.value) return;
    const token = mouseToken(e.button);
    if (!token) return;
    e.preventDefault();
    e.stopPropagation();
    swallowRelease = true;
    swallowContextMenu = e.button === 2;
    emit('update:modelValue', token);
    listening.value = false;
  }

  function handleMouseup(e: MouseEvent) {
    if (!swallowRelease && !listening.value) return;
    if (!mouseToken(e.button)) return;
    e.preventDefault();
    swallowRelease = false;
  }

  function handleContextMenu(e: MouseEvent) {
    if (listening.value || swallowContextMenu) e.preventDefault();
    swallowContextMenu = false;
  }

  onMounted(() => {
    window.addEventListener('mouseup', handleMouseup, true);
    window.addEventListener('contextmenu', handleContextMenu, true);
  });

  onUnmounted(() => {
    window.removeEventListener('mouseup', handleMouseup, true);
    window.removeEventListener('contextmenu', handleContextMenu, true);
  });
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
      @click="toggleListening"
      @keydown="handleKeydown"
      @mousedown="handleMousedown"
      @auxclick.prevent
      @blur="stopListening"
    >
      {{ listening ? 'Press a key or mouse button...' : displayKeyName(modelValue) }}
    </button>
    <span v-if="conflict" class="text-xs text-orange-500 dark:text-orange-400">Conflicts with {{ conflict }}</span>
  </div>
</template>
