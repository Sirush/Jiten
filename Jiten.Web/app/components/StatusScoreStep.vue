<script setup lang="ts">
  import type { MediaListDeck } from '~/types';

  const props = defineProps<{
    deck: MediaListDeck;
    afterCompletion: boolean;
    backLabel?: string;
  }>();

  const emit = defineEmits<{ done: []; back: [] }>();

  const draft = useDeckDetailsDraft(() => props.deck, knownDeckDetails(props.deck));
  const { score, loading } = draft;
  const loaded = draft.details.value ? Promise.resolve() : draft.load();
  const saving = ref(false);
  const scoreInput = ref();

  onMounted(async () => {
    await loaded;
    await nextTick();
    scoreInput.value?.focus();
  });

  async function save() {
    if (saving.value) return;
    saving.value = true;
    try {
      await nextTick();
      if (score.value === (draft.details.value?.score ?? null) || (await draft.save({ score: score.value }))) emit('done');
    } finally {
      saving.value = false;
    }
  }
</script>

<template>
  <form class="flex flex-col gap-2 min-w-64 p-1" @submit.prevent="save">
    <div class="flex items-center gap-1">
      <button
        v-if="!afterCompletion"
        type="button"
        class="p-1.5 rounded hover:bg-gray-100 dark:hover:bg-gray-700 transition-colors cursor-pointer"
        :aria-label="backLabel ?? 'Back to statuses'"
        @click="emit('back')"
      >
        <i class="pi pi-arrow-left text-xs" aria-hidden="true" />
      </button>
      <span id="status-score-label" class="text-sm font-semibold px-1">{{ afterCompletion ? 'Completed. How much did you enjoy it?' : 'Your enjoyment score' }}</span>
    </div>
    <EntryScoreInput ref="scoreInput" v-model="score" labelled-by="status-score-label" :disabled="loading" class="px-1" @keydown.enter.prevent="save" />
    <div class="flex justify-end gap-2 pt-1">
      <Button v-if="afterCompletion" label="Skip" text size="small" severity="secondary" @click="emit('done')" />
      <Button type="submit" label="Save" size="small" :loading="saving" :disabled="loading" />
    </div>
  </form>
</template>
