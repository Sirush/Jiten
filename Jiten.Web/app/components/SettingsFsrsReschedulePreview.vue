<script setup lang="ts">
  import type { ReschedulePreviewResponse } from '~/types';

  const props = defineProps<{
    mode: 'optimise' | 'reschedule';
    preview: ReschedulePreviewResponse;
    version: number;
    reviewCount?: number;
    applying: boolean;
  }>();

  const emit = defineEmits<{
    apply: [choice: { desiredRetention: number; reschedule: boolean }];
  }>();

  const visible = defineModel<boolean>('visible', { required: true });

  const selectedRetention = ref(0.9);
  const reschedule = ref(true);

  watch(
    () => props.preview,
    (preview) => {
      selectedRetention.value = preview.options[0]?.desiredRetention ?? 0.9;
      reschedule.value = true;
    },
    { immediate: true }
  );

  const savedRetention = computed(() => props.preview.options[0]?.desiredRetention);
  const willReschedule = computed(() => props.mode === 'reschedule' || reschedule.value);

  const header = computed(() => (props.mode === 'optimise' ? 'Review optimised parameters' : 'Reschedule all cards'));
  const applyLabel = computed(() => {
    if (props.mode === 'reschedule') return 'Reschedule';
    return reschedule.value ? 'Save and reschedule' : 'Save parameters';
  });

  const percent = (retention: number) => `${Math.round(retention * 1000) / 10}%`;
  const count = (value: number) => value.toLocaleString();
  const delta = (due: number) => {
    const diff = due - props.preview.currentDue;
    if (diff === 0) return 'no change';
    return diff > 0 ? `+${count(diff)}` : `−${count(-diff)}`;
  };

  function apply() {
    emit('apply', { desiredRetention: selectedRetention.value, reschedule: willReschedule.value });
  }
</script>

<template>
  <Dialog v-model:visible="visible" modal :closable="!applying" :close-on-escape="!applying" :header="header" class="w-[95vw] sm:w-[32rem]">
    <div class="flex flex-col gap-4">
      <p v-if="mode === 'optimise'" class="text-sm text-gray-600 dark:text-gray-300">
        New FSRS-{{ version }} parameters fitted to your {{ count(reviewCount ?? 0) }} reviews. They won't be saved until your confirm.
      </p>
      <p v-else class="text-sm text-gray-600 dark:text-gray-300">Every card's due date is recalculated from its review history with your current parameters.</p>

      <div>
        <div class="text-sm mb-2">
          Due now: <span class="font-semibold tabular-nums">{{ count(preview.currentDue) }}</span>
        </div>
        <fieldset class="rounded-lg border border-surface-200 dark:border-surface-700 overflow-hidden" :disabled="applying">
          <legend class="sr-only">Desired retention</legend>
          <div class="flex items-center justify-between px-3 py-2 text-xs text-gray-500 dark:text-gray-400 bg-surface-50 dark:bg-surface-800/40">
            <span>Desired retention</span>
            <span>Due after rescheduling</span>
          </div>
          <label
            v-for="option in preview.options"
            :key="option.desiredRetention"
            :for="`retention-${option.desiredRetention}`"
            class="flex items-center gap-3 px-3 py-2.5 border-t border-surface-200 dark:border-surface-700 cursor-pointer"
            :class="selectedRetention === option.desiredRetention ? 'bg-surface-100 dark:bg-surface-800' : 'hover:bg-surface-50 dark:hover:bg-surface-800/60'"
          >
            <RadioButton v-model="selectedRetention" :input-id="`retention-${option.desiredRetention}`" name="desiredRetention" :value="option.desiredRetention" />
            <span class="text-sm">
              <span class="font-medium tabular-nums">{{ percent(option.desiredRetention) }}</span>
              <span v-if="option.desiredRetention === savedRetention" class="text-gray-500 dark:text-gray-400"> (current)</span>
            </span>
            <span class="ml-auto text-sm tabular-nums text-right">
              <span class="font-semibold">{{ count(option.due) }}</span>
              <span class="text-gray-500 dark:text-gray-400 ml-1.5">{{ delta(option.due) }}</span>
            </span>
          </label>
        </fieldset>
        <p class="text-[11px] text-gray-500 dark:text-gray-400 mt-1.5">Lower retention means fewer reviews but can lead to more forgotten cards.</p>
      </div>

      <div v-if="mode === 'optimise'">
        <div class="flex items-center gap-2">
          <Checkbox v-model="reschedule" input-id="rescheduleAfterOptimise" :binary="true" :disabled="applying" />
          <label for="rescheduleAfterOptimise" class="text-sm cursor-pointer">Reschedule all my cards now</label>
        </div>
        <p v-if="!reschedule" class="text-[11px] text-gray-500 dark:text-gray-400 mt-1.5">
          Due dates stay as they are. New reviews use the new parameters, and you can reschedule later from this page.
        </p>
      </div>
    </div>

    <template #footer>
      <Button label="Cancel" severity="secondary" outlined :disabled="applying" @click="visible = false" />
      <Button :label="applyLabel" :loading="applying" @click="apply" />
    </template>
  </Dialog>
</template>
