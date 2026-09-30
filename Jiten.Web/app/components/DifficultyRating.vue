<script setup lang="ts">
  import { useToast } from 'primevue/usetoast';

  const props = defineProps<{
    deckId: number;
    currentRating?: number | null;
  }>();

  const emit = defineEmits<{
    rated: [rating: number];
  }>();

  const { submitRating, error: ratingError } = useDifficultyVotes();
  const toast = useToast();
  const isSubmitting = ref(false);
  const selectedRating = ref<number | null>(props.currentRating ?? null);

  watch(
    () => props.currentRating,
    (val) => {
      selectedRating.value = val ?? null;
    }
  );

  const { chartColour } = useDifficultyColours();
  const ratingOptions = computed(() =>
    ['Beginner', 'Easy', 'Average', 'Hard', 'Expert'].map((label, value) => ({ label, value, bg: chartColour(value), bgHover: chartColour(value, 0.3) }))
  );

  async function rate(rating: number) {
    if (isSubmitting.value) return;
    isSubmitting.value = true;
    const success = await submitRating(props.deckId, rating);
    isSubmitting.value = false;

    if (success) {
      selectedRating.value = rating;
      emit('rated', rating);
    } else {
      toast.add({
        severity: 'error',
        summary: 'Rating failed',
        detail: extractApiError(ratingError.value, 'Could not save your rating. Please try again.'),
        life: 5000,
      });
    }
  }
</script>

<template>
  <div class="flex flex-wrap gap-2">
    <button
      v-for="opt in ratingOptions"
      :key="opt.value"
      class="difficulty-btn"
      :class="{ 'is-selected': selectedRating === opt.value }"
      :style="{ '--diff-bg': opt.bg, '--diff-bg-hover': opt.bgHover }"
      :disabled="isSubmitting"
      @click="rate(opt.value)"
    >
      {{ opt.label }}
    </button>
  </div>
</template>

<style scoped>
  .difficulty-btn {
    padding: 0.375rem 0.75rem;
    border-radius: var(--radius-md);
    border: 1px solid var(--diff-bg);
    background: transparent;
    color: inherit;
    font-size: 0.875rem;
    cursor: pointer;
    transition: background-color 0.2s;
  }

  .difficulty-btn:hover:not(:disabled) {
    background: var(--diff-bg-hover);
  }

  .difficulty-btn.is-selected {
    background: var(--diff-bg);
    color: white;
  }

  .difficulty-btn:disabled {
    opacity: 0.5;
    cursor: not-allowed;
  }
</style>
