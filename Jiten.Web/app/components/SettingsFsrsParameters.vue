<script setup lang="ts">
  import { useToast } from 'primevue/usetoast';
  import { useConfirm } from 'primevue/useconfirm';
  import { type FsrsParametersResponse } from '~/types';

  const { $api } = useNuxtApp();
  const toast = useToast();
  const confirm = useConfirm();

  const expectedCount = 21;
  const defaultDesiredRetention = 0.9;
  const parametersCsv = ref('');
  const desiredRetention = ref(defaultDesiredRetention);
  const isDefault = ref(false);
  const isLoading = ref(true);
  const isSaving = ref(false);
  const isRecomputing = ref(false);
  const isResetting = ref(false);
  const isOptimising = ref(false);
  const hasUserEdited = ref(false);
  const optimiseError = ref<string | null>(null);
  const rescheduleAfterOptimise = ref(true);
  const showBreakdown = ref(false);
  const showAdvanced = ref(false);
  const reviewCount = ref(0);
  const minimumReviews = ref(50);

  const canOptimise = computed(() => reviewCount.value >= minimumReviews.value);

  const parsedState = computed(() => {
    const raw = parametersCsv.value.trim();
    if (!raw) {
      return { count: 0, error: 'Parameters are required.' };
    }

    const parts = raw
      .split(',')
      .map((part) => part.trim())
      .filter((part) => part.length > 0);
    const count = parts.length;
    if (count !== expectedCount) {
      return { count, error: `Expected ${expectedCount} values, got ${count}.` };
    }

    const numbers = parts.map((part) => Number(part));
    if (numbers.some((value) => Number.isNaN(value) || !Number.isFinite(value))) {
      return { count, error: 'All values must be valid numbers.' };
    }

    return { count, error: null };
  });

  const valueCount = computed(() => parsedState.value.count);
  const parsedValues = computed(() => {
    const raw = parametersCsv.value.trim();
    if (!raw) return null;
    const parts = raw.split(',').map((p) => p.trim()).filter((p) => p.length > 0);
    if (parts.length !== expectedCount) return null;
    const nums = parts.map(Number);
    if (nums.some((n) => Number.isNaN(n) || !Number.isFinite(n))) return null;
    return nums;
  });

  const defaultParameters = [
    0.212, 1.2931, 2.3065, 8.2956, 6.4133, 0.8334, 3.0194, 0.001, 1.8722, 0.1666, 0.796,
    1.4835, 0.0614, 0.2629, 1.6483, 0.6014, 1.8729, 0.5425, 0.0912, 0.0658, 0.1542,
  ];

  const parameterDescriptions: { label: string; description: string; unit?: string; decimals?: number }[] = [
    { label: 'Initial stability (Again)', description: 'Days of memory stability when you press Again on a new card', unit: 'd', decimals: 2 },
    { label: 'Initial stability (Hard)', description: 'Days of memory stability when you press Hard on a new card', unit: 'd', decimals: 2 },
    { label: 'Initial stability (Good)', description: 'Days of memory stability when you press Good on a new card', unit: 'd', decimals: 2 },
    { label: 'Initial stability (Easy)', description: 'Days of memory stability when you press Easy on a new card', unit: 'd', decimals: 2 },
    { label: 'Initial difficulty', description: 'Baseline difficulty for new cards (1–10 scale)', decimals: 2 },
    { label: 'Difficulty sensitivity', description: 'How much the first rating affects initial difficulty' },
    { label: 'Difficulty update rate', description: 'How quickly difficulty changes with each review' },
    { label: 'Mean reversion', description: 'How strongly difficulty pulls back toward the baseline (0 = none, 1 = full)' },
    { label: 'Recall stability gain', description: 'Controls how much stability increases on successful recall' },
    { label: 'Stability saturation', description: 'Higher stability becomes harder to increase further' },
    { label: 'Retrievability effect', description: 'Bonus for reviewing when you have almost forgotten' },
    { label: 'Forget stability scale', description: 'Base factor for new stability after forgetting' },
    { label: 'Forget difficulty effect', description: 'How much difficulty reduces post-lapse stability' },
    { label: 'Forget stability power', description: 'How previous stability affects post-lapse stability' },
    { label: 'Forget retrievability effect', description: 'How retrievability at time of lapse affects new stability' },
    { label: 'Hard penalty', description: 'Multiplier applied to stability growth for Hard ratings (0–1)' },
    { label: 'Easy bonus', description: 'Multiplier applied to stability growth for Easy ratings (1–6)' },
    { label: 'Short-term stability', description: 'Controls stability changes for same-day reviews' },
    { label: 'Short-term offset', description: 'Rating offset for short-term stability calculation' },
    { label: 'Short-term power', description: 'Decay exponent for short-term stability' },
    { label: 'Forgetting curve decay', description: 'Shape of the forgetting curve (lower = slower memory decay)' },
  ];

  const validationError = computed(() => parsedState.value.error);
  const retentionError = computed(() => {
    const value = desiredRetention.value;
    if (value === null || Number.isNaN(value) || !Number.isFinite(value)) {
      return 'Desired retention must be a valid number.';
    }

    if (value <= 0 || value >= 1) {
      return 'Desired retention must be between 0 and 1.';
    }

    return null;
  });
  const formError = computed(() => retentionError.value ?? validationError.value);

  const loadParameters = async (force = false) => {
    try {
      isLoading.value = true;
      const result = await $api<FsrsParametersResponse>('srs/settings');
      if (force || !hasUserEdited.value || !parametersCsv.value.trim()) {
        parametersCsv.value = result.parameters;
        hasUserEdited.value = false;
      }
      isDefault.value = result.isDefault;
      desiredRetention.value = result.desiredRetention ?? defaultDesiredRetention;
      reviewCount.value = result.reviewCount ?? 0;
      minimumReviews.value = result.minimumReviewsForOptimize ?? 50;
    } catch {
      toast.add({
        severity: 'error',
        summary: 'Error',
        detail: 'Failed to load FSRS parameters.',
        life: 5000,
      });
    } finally {
      isLoading.value = false;
    }
  };

  onMounted(() => {
    void loadParameters();
  });

  const saveParameters = async () => {
    if (formError.value) {
      toast.add({
        severity: 'error',
        summary: 'Invalid settings',
        detail: formError.value,
        life: 5000,
      });
      return;
    }

    try {
      isSaving.value = true;
      const result = await $api<FsrsParametersResponse>('srs/settings', {
        method: 'PUT',
        body: { parameters: parametersCsv.value, desiredRetention: desiredRetention.value },
      });
      parametersCsv.value = result.parameters;
      isDefault.value = result.isDefault;
      desiredRetention.value = result.desiredRetention ?? defaultDesiredRetention;
      hasUserEdited.value = false;
      toast.add({
        severity: 'success',
        summary: 'Saved',
        detail: 'FSRS parameters updated.',
        life: 4000,
      });
    } catch (error: unknown) {
      const message = error instanceof Error ? error.message : 'Failed to update FSRS parameters.';
      toast.add({
        severity: 'error',
        summary: 'Error',
        detail: message,
        life: 5000,
      });
    } finally {
      isSaving.value = false;
    }
  };

  const confirmResetParameters = () => {
    confirm.require({
      message: 'This will reset your FSRS parameters and desired retention to the defaults. You will need to reschedule your cards for the changes to take effect.',
      header: 'Reset to default',
      icon: 'pi pi-exclamation-triangle',
      rejectProps: {
        label: 'Cancel',
        severity: 'secondary',
        outlined: true,
      },
      acceptProps: {
        label: 'Reset',
      },
      accept: async () => {
        await resetParameters();
      },
    });
  };

  const resetParameters = async () => {
    try {
      isResetting.value = true;
      const result = await $api<FsrsParametersResponse>('srs/settings', {
        method: 'PUT',
        body: { parameters: '', desiredRetention: defaultDesiredRetention },
      });
      parametersCsv.value = result.parameters;
      isDefault.value = result.isDefault;
      desiredRetention.value = result.desiredRetention ?? defaultDesiredRetention;
      hasUserEdited.value = false;
      toast.add({
        severity: 'success',
        summary: 'Reset to default',
        detail: 'FSRS parameters reset to defaults.',
        life: 4000,
      });
    } catch (error: unknown) {
      const message = error instanceof Error ? error.message : 'Failed to reset parameters.';
      toast.add({
        severity: 'error',
        summary: 'Error',
        detail: message,
        life: 5000,
      });
    } finally {
      isResetting.value = false;
    }
  };

  const confirmRecomputeSchedule = () => {
    confirm.require({
      message:
        'This will recompute the schedule for all your cards using your current parameters. Depending on your history, this could result in a large number of immediate reviews. Are you sure you want to proceed?',
      header: 'Reschedule all cards',
      icon: 'pi pi-exclamation-triangle',
      rejectProps: {
        label: 'Cancel',
        severity: 'secondary',
        outlined: true,
      },
      acceptProps: {
        label: 'Reschedule',
      },
      accept: async () => {
        await recomputeSchedule();
      },
    });
  };

  const recomputeSchedule = async () => {
    try {
      isRecomputing.value = true;
      await $api('srs/settings/recompute', { method: 'POST' });
      toast.add({
        severity: 'success',
        summary: 'Reschedule complete',
        detail: 'Your SRS schedule has been recomputed.',
        life: 4000,
      });
    } catch (error: unknown) {
      const message = error instanceof Error ? error.message : 'Failed to queue rescheduling.';
      toast.add({
        severity: 'error',
        summary: 'Error',
        detail: message,
        life: 5000,
      });
    } finally {
      isRecomputing.value = false;
    }
  };

  const confirmOptimise = () => {
    const message = rescheduleAfterOptimise.value
      ? 'This will analyse your review history to find optimal parameters and reschedule all your cards. Due dates may change.'
      : 'This will analyse your review history to find optimal parameters. Your cards will not be rescheduled and you will be able to do that manually later.';
    confirm.require({
      message,
      header: 'Optimise parameters',
      icon: 'pi pi-sparkles',
      rejectProps: {
        label: 'Cancel',
        severity: 'secondary',
        outlined: true,
      },
      acceptProps: {
        label: 'Optimise',
      },
      accept: async () => {
        await optimiseParameters();
      },
    });
  };

  const optimiseParameters = async () => {
    try {
      isOptimising.value = true;
      optimiseError.value = null;
      const result = await $api<{ parameters: string; loss: number; reviewCount: number; desiredRetention: number; rescheduled: boolean }>(
        `srs/settings/optimize?reschedule=${rescheduleAfterOptimise.value}`,
        { method: 'POST' },
      );
      parametersCsv.value = result.parameters;
      desiredRetention.value = result.desiredRetention;
      isDefault.value = false;
      hasUserEdited.value = false;
      const detail = result.rescheduled
        ? `Parameters optimised from ${result.reviewCount} reviews. Cards have been rescheduled.`
        : `Parameters optimised from ${result.reviewCount} reviews.`;
      toast.add({
        severity: 'success',
        summary: 'Optimisation complete',
        detail,
        life: 6000,
      });
    } catch (error: unknown) {
      const message = error instanceof Error ? error.message : 'Failed to optimise parameters.';
      optimiseError.value = message;
      toast.add({
        severity: 'error',
        summary: 'Optimisation failed',
        detail: message,
        life: 5000,
      });
    } finally {
      isOptimising.value = false;
    }
  };
</script>

<template>
  <Card>
    <template #title>
      <h3 class="text-lg font-semibold">FSRS Settings</h3>
    </template>
    <template #content>
      <div class="mb-4">
        <h4 class="text-md font-semibold mb-1">Desired retention</h4>
        <p class="text-sm text-gray-600 dark:text-gray-300 mb-2">Target recall rate (0–1). For example, 0.9 = 90% retention.</p>
        <div class="flex flex-wrap items-center gap-2">
          <InputNumber v-model="desiredRetention" class="w-40" input-class="w-40" :min="0.01" :max="0.99" :step="0.01" :min-fraction-digits="2" :max-fraction-digits="4" />
          <Button label="Save" :loading="isSaving" :disabled="!!formError || isLoading || isRecomputing || isResetting" @click="saveParameters" />
        </div>
        <Message v-if="retentionError" key="retention-error" severity="error" :closable="false" class="mt-2">
          {{ retentionError }}
        </Message>
      </div>

      <div class="mb-5">
        <h4 class="text-md font-semibold mb-1">Optimise parameters</h4>
        <p class="text-sm text-gray-600 dark:text-gray-300 mb-2">
          Analyse your review history to find the optimal FSRS parameters for your memory patterns. <br /> The more reviews you have, the more accurate the optimisation will be. It is recommended to optimise every time your number of review doubles.
          <br />You currently have {{reviewCount}} reviews.
        </p>
        <div class="flex items-center gap-2 mb-2">
          <Checkbox v-model="rescheduleAfterOptimise" inputId="rescheduleAfterOptimise" :binary="true" :disabled="!canOptimise" />
          <label for="rescheduleAfterOptimise" class="text-sm cursor-pointer">Also reschedule all my cards after optimisation</label>
        </div>
        <Button
          :label="canOptimise ? 'Optimise' : `Available after ${minimumReviews} reviews`"
          icon="pi pi-sparkles"
          :loading="isOptimising"
          :disabled="!canOptimise || isLoading || isSaving || isRecomputing || isResetting"
          @click="confirmOptimise"
        />
        <p v-if="!canOptimise && !isLoading" class="text-sm text-surface-500 mt-2">
          You have {{ reviewCount }} of {{ minimumReviews }} reviews needed. Keep studying to unlock optimisation.
        </p>
        <Message v-if="optimiseError" key="optimise-error" severity="error" :closable="false" class="mt-2">
          {{ optimiseError }}
        </Message>
      </div>

      <div class="border-t border-surface-200 dark:border-surface-700 pt-4">
        <button
          class="flex items-center gap-2 text-sm font-medium text-surface-700 dark:text-surface-200 cursor-pointer"
          @click="showAdvanced = !showAdvanced"
        >
          <i :class="showAdvanced ? 'pi pi-chevron-down' : 'pi pi-chevron-right'" class="text-xs" />
          Advanced: edit raw parameters
        </button>
      </div>

      <div v-if="showAdvanced" class="mt-4">
      <h4 class="text-md font-semibold mb-1">FSRS Parameters</h4>
      <p class="text-sm text-gray-600 dark:text-gray-300 mb-3">21 comma-separated numbers that control FSRS scheduling. These are set automatically when you optimise, but you can also edit them manually.</p>
      <Textarea v-model="parametersCsv" class="w-full" rows="3" placeholder="0.2172, 1.1771, 3.2602, ..." @update:modelValue="hasUserEdited = true" />
      <div class="mt-2 text-sm text-surface-600">
        Values: <b>{{ valueCount }}</b> / {{ expectedCount }}
      </div>
      <Message v-if="formError" key="fsrs-params-error" severity="error" :closable="false" class="mt-2">
        {{ formError }}
      </Message>
      <Message v-else-if="isDefault" key="fsrs-params-default" severity="info" :closable="false" class="mt-2"> Using default FSRS settings. </Message>

      <div v-if="parsedValues" class="mt-3">
        <button class="text-sm text-primary cursor-pointer underline" @click="showBreakdown = !showBreakdown">
          {{ showBreakdown ? 'Hide' : 'Show' }} parameter breakdown
        </button>
        <div v-if="showBreakdown" class="mt-2 rounded border border-surface-200 dark:border-surface-700 overflow-hidden">
          <table class="w-full text-sm">
            <thead>
              <tr class="bg-surface-50 dark:bg-surface-800">
                <th class="text-left px-3 py-2 font-medium">#</th>
                <th class="text-left px-3 py-2 font-medium">Parameter</th>
                <th class="text-right px-3 py-2 font-medium">Value</th>
                <th class="text-right px-3 py-2 font-medium">Default</th>
                <th class="text-left px-3 py-2 font-medium hidden md:table-cell">Description</th>
              </tr>
            </thead>
            <tbody>
              <tr
                v-for="(desc, i) in parameterDescriptions"
                :key="i"
                class="border-t border-surface-100 dark:border-surface-700"
              >
                <td class="px-3 py-1.5 text-surface-500 tabular-nums">{{ i }}</td>
                <td class="px-3 py-1.5 font-medium">
                  <span class="md:hidden">
                    <Tooltip :content="desc.description">
                      <span class="cursor-help">{{ desc.label }}</span>
                    </Tooltip>
                  </span>
                  <span class="hidden md:inline">{{ desc.label }}</span>
                </td>
                <td class="px-3 py-1.5 text-right tabular-nums">
                  {{ desc.decimals != null ? parsedValues![i].toFixed(desc.decimals) : parsedValues![i].toPrecision(4) }}
                  <span v-if="desc.unit" class="text-surface-400 ml-0.5">{{ desc.unit }}</span>
                </td>
                <td class="px-3 py-1.5 text-right tabular-nums text-surface-400">
                  {{ desc.decimals != null ? defaultParameters[i].toFixed(desc.decimals) : defaultParameters[i].toPrecision(4) }}
                  <span v-if="desc.unit" class="ml-0.5">{{ desc.unit }}</span>
                </td>
                <td class="px-3 py-1.5 text-surface-500 hidden md:table-cell">{{ desc.description }}</td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>

      <div class="mt-4 flex flex-wrap gap-2">
        <Button label="Save" :loading="isSaving" :disabled="!!formError || isLoading || isRecomputing || isResetting" @click="saveParameters" />
        <Button label="Reload" severity="secondary" outlined :disabled="isLoading || isRecomputing || isResetting" @click="loadParameters(true)" />
        <Button
          label="Reset to default"
          severity="secondary"
          outlined
          :loading="isResetting"
          :disabled="isLoading || isRecomputing || isSaving"
          @click="confirmResetParameters"
        />
      </div>
      </div>
      <div class="mt-4">
        <h4 class="text-md font-semibold mb-1">Reschedule</h4>
        <p class="text-sm text-amber-600 dark:text-amber-400 mb-2">
          Warning: Be careful with this setting, as it could result in an overwhelming number of immediate reviews.
        </p>
        <Button label="Reschedule all cards" :loading="isRecomputing" :disabled="isLoading || isSaving" @click="confirmRecomputeSchedule" />
      </div>
    </template>
  </Card>
</template>

<style scoped></style>
