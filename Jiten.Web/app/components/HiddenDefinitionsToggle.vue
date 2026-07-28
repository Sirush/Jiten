<script setup lang="ts">
  const props = defineProps<{
    wordId: number;
    hiddenBehaviour?: 'gray' | 'hide';
  }>();

  const authStore = useAuthStore();
  const { hiddenFor, ensureLoaded, isEditing, startEditing, stopEditing } = useHiddenDefinitions();

  onMounted(() => ensureLoaded(props.wordId));
  watch(() => props.wordId, (id) => {
    stopEditing();
    ensureLoaded(id);
  });
  onUnmounted(() => stopEditing(props.wordId));

  const editing = computed(() => isEditing(props.wordId));
  const hiddenCount = computed(() => hiddenFor(props.wordId).length);
  const hint = computed(() =>
    props.hiddenBehaviour === 'hide'
      ? 'Unticked meanings are hidden on this card and dimmed in vocabulary pages.'
      : 'Unticked meanings stay dimmed here and are hidden from cards during review.'
  );
</script>

<template>
  <ClientOnly>
    <div v-if="authStore.isAuthenticated" class="flex flex-wrap items-center gap-2">
      <button
        class="text-xs text-surface-400 hover:text-primary-500 transition-colors inline-flex items-center gap-1 cursor-pointer"
        :title="editing ? 'Finish choosing meanings' : 'Choose which meanings are shown for this word'"
        @click.stop="editing ? stopEditing(props.wordId) : startEditing(props.wordId)"
        @pointerdown.stop
      >
        <i class="text-xs" :class="editing ? 'pi pi-check' : 'pi pi-eye-slash'" />
        {{ editing ? 'Done' : 'Edit shown meanings' }}
      </button>
      <span v-if="editing" class="text-xs text-surface-400">{{ hint }}</span>
      <span v-else-if="hiddenCount > 0" class="text-xs text-surface-400">{{ hiddenCount }} hidden</span>
    </div>
  </ClientOnly>
</template>
