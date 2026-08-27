<script setup lang="ts">
  import type { Poll } from '~/types';
  import { useToast } from 'primevue/usetoast';

  const props = defineProps<{
    poll: Poll;
    labeled?: boolean;
  }>();

  const emit = defineEmits<{ (e: 'update:poll', poll: Poll): void }>();

  const { $api } = useNuxtApp();
  const toast = useToast();

  const selected = ref<number[]>([...props.poll.myOptionIds]);
  const editing = ref(false);
  const submitting = ref(false);

  watch(
    () => props.poll,
    (poll) => {
      selected.value = [...poll.myOptionIds];
      editing.value = false;
    }
  );

  const singleChoice = computed(() => props.poll.maxSelections === 1);
  const showResults = computed(() => props.poll.resultsVisible && !editing.value);
  const atCap = computed(() => selected.value.length >= props.poll.maxSelections);
  const hasVoted = computed(() => props.poll.myOptionIds.length > 0);

  const singleSelection = computed({
    get: () => selected.value[0] ?? null,
    set: (value: number | null) => {
      selected.value = value === null ? [] : [value];
    },
  });

  const sortedOptions = computed(() => [...props.poll.options].sort((a, b) => a.sortOrder - b.sortOrder));

  const formatDate = (date: string) => new Date(date).toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' });

  const closedLabel = computed(() => (props.poll.closesAt ? `Closed ${formatDate(props.poll.closesAt)}` : 'Closed'));

  const votersLabel = computed(() => {
    const total = props.poll.totalVoters ?? 0;
    return total === 1 ? '1 voter' : `${total} voters`;
  });

  function share(optionId: number) {
    const total = props.poll.totalVoters ?? 0;
    const count = sortedOptions.value.find((o) => o.id === optionId)?.voteCount ?? 0;
    return total === 0 ? 0 : (count / total) * 100;
  }

  function isMine(optionId: number) {
    return props.poll.myOptionIds.includes(optionId);
  }

  function isDisabled(optionId: number) {
    return atCap.value && !selected.value.includes(optionId);
  }

  async function submit() {
    if (selected.value.length === 0 || submitting.value) return;

    try {
      submitting.value = true;
      const updated = await $api<Poll>(`polls/${props.poll.id}/vote`, {
        method: 'PUT',
        body: { optionIds: selected.value },
      });
      editing.value = false;
      emit('update:poll', updated);
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(e, 'Could not record your vote'), life: 5000 });
    } finally {
      submitting.value = false;
    }
  }

  function cancelEdit() {
    selected.value = [...props.poll.myOptionIds];
    editing.value = false;
  }
</script>

<template>
  <section class="rounded-xl border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 p-4 shadow-sm">
    <div class="flex items-start gap-3">
      <span
        v-if="labeled"
        class="shrink-0 flex items-center justify-center w-9 h-9 rounded-lg bg-primary-50 dark:bg-primary-900/40 text-primary-600 dark:text-primary-300"
      >
        <Icon name="material-symbols:how-to-vote" size="1.35em" />
      </span>
      <div class="flex-1 min-w-0">
        <span v-if="labeled" class="block text-[10px] font-semibold uppercase tracking-wide text-surface-400 dark:text-surface-400">Polls</span>
        <h3 :id="`poll-${poll.id}-question`" class="text-base font-semibold text-surface-900 dark:text-surface-0">
          {{ poll.question }}
        </h3>
      </div>
      <Tag v-if="poll.isClosed" :value="closedLabel" severity="secondary" class="shrink-0" />
    </div>

    <div v-if="poll.descriptionMarkdown" class="mt-2 text-sm">
      <Suspense>
        <MarkdownBody :source="poll.descriptionMarkdown" />
        <template #fallback>
          <span class="sr-only">Loading description</span>
        </template>
      </Suspense>
    </div>

    <p v-if="!poll.isClosed && poll.closesAt" class="mt-1 text-xs text-surface-500 dark:text-surface-400">Closes {{ formatDate(poll.closesAt) }}</p>

    <div v-if="showResults" class="mt-3 flex flex-col gap-2">
      <div v-for="option in sortedOptions" :key="option.id" class="flex flex-col gap-1">
        <div class="flex items-baseline justify-between gap-2 text-sm">
          <span class="min-w-0 flex items-center gap-1.5" :class="isMine(option.id) ? 'font-semibold' : ''">
            <Icon v-if="isMine(option.id)" name="material-symbols:check-circle-rounded" class="shrink-0 text-primary-500" size="1em" />
            <span class="min-w-0 break-words">{{ option.text }}</span>
          </span>
          <span class="shrink-0 tabular-nums text-surface-600 dark:text-surface-300">
            {{ option.voteCount ?? 0 }} &middot; {{ share(option.id).toFixed(0) }}%
          </span>
        </div>
        <div class="relative h-2.5 w-full overflow-hidden rounded bg-surface-200 dark:bg-surface-700">
          <div
            class="absolute inset-y-0 left-0 rounded transition-all duration-500"
            :class="isMine(option.id) ? 'bg-primary-500' : 'bg-surface-400 dark:bg-surface-500'"
            :style="{ width: share(option.id).toFixed(1) + '%' }"
          />
        </div>
      </div>

      <div class="mt-1 flex flex-wrap items-center justify-between gap-2">
        <span class="text-xs text-surface-500 dark:text-surface-400">
          {{ votersLabel }}
          <template v-if="poll.maxSelections > 1">&middot; up to {{ poll.maxSelections }} picks each</template>
        </span>
        <Button v-if="!poll.isClosed" label="Change vote" text size="small" @click="editing = true" />
      </div>
    </div>

    <div v-else class="mt-3 flex flex-col gap-2">
      <p v-if="!singleChoice" class="text-xs text-surface-500 dark:text-surface-400">Choose up to {{ poll.maxSelections }}</p>

      <div class="flex flex-col gap-1.5" role="group" :aria-labelledby="`poll-${poll.id}-question`">
        <label
          v-for="option in sortedOptions"
          :key="option.id"
          :for="`poll-${poll.id}-option-${option.id}`"
          class="flex items-center gap-2 rounded-lg border border-surface-200 dark:border-surface-700 p-2 text-sm hover:bg-surface-50 dark:hover:bg-surface-800"
          :class="!singleChoice && isDisabled(option.id) ? 'cursor-not-allowed opacity-60' : 'cursor-pointer'"
        >
          <RadioButton
            v-if="singleChoice"
            v-model="singleSelection"
            :input-id="`poll-${poll.id}-option-${option.id}`"
            :value="option.id"
            :name="`poll-${poll.id}`"
          />
          <Checkbox v-else v-model="selected" :input-id="`poll-${poll.id}-option-${option.id}`" :value="option.id" :disabled="isDisabled(option.id)" />
          <span class="min-w-0 break-words">{{ option.text }}</span>
        </label>
      </div>

      <div class="flex items-center justify-between gap-2">
        <span class="text-xs text-surface-500 dark:text-surface-400">Votes are anonymous</span>
        <div class="flex items-center gap-2">
          <Button v-if="hasVoted" label="Cancel" text size="small" @click="cancelEdit" />
          <Button :label="hasVoted ? 'Save vote' : 'Vote'" size="small" :loading="submitting" :disabled="selected.length === 0 || submitting" @click="submit" />
        </div>
      </div>
    </div>

    <div v-if="$slots.footer" class="mt-3 border-t border-surface-200 dark:border-surface-700 pt-2">
      <slot name="footer" />
    </div>
  </section>
</template>
