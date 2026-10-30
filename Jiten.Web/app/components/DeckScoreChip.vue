<script setup lang="ts">
  import { formatScore } from '~/utils/formatScore';

  const props = defineProps<{
    score: number;
    editable?: boolean;
    /** Applied only while editable, so a read-only chip has no hover state. */
    hoverClass?: string;
  }>();

  const emit = defineEmits<{ edit: [] }>();

  function onClick(event: MouseEvent) {
    event.preventDefault();
    event.stopPropagation();
    emit('edit');
  }
</script>

<template>
  <button
    v-if="editable"
    type="button"
    class="shrink-0 whitespace-nowrap rounded px-1.5 font-semibold tabular-nums cursor-pointer transition-colors"
    :class="hoverClass"
    :aria-label="`Score ${formatScore(score)}, edit`"
    @click="onClick"
  >
    Score {{ formatScore(score) }}
  </button>
  <span v-else class="shrink-0 whitespace-nowrap rounded px-1.5 font-semibold tabular-nums">Score {{ formatScore(score) }}</span>
</template>
