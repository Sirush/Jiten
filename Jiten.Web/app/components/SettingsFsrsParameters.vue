<script setup lang="ts">
  import { useToast } from 'primevue/usetoast';
  import { useConfirm } from 'primevue/useconfirm';
  import { Line } from 'vue-chartjs';
  import { Chart as ChartJS, CategoryScale, LinearScale, PointElement, LineElement, Tooltip, type ChartOptions, type ChartData } from 'chart.js';
  import type { FsrsParametersResponse, FsrsWorkloadCurveResponse, WorkloadCurvePoint, FsrsHealthResponse } from '~/types';

  ChartJS.register(CategoryScale, LinearScale, PointElement, LineElement, Tooltip);

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
    void loadHealth();
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
      // Re-anchor the workload curve (baseline multipliers + recommendation) to the saved settings.
      if (workloadCurve.value) void loadWorkloadCurve();
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
      if (workloadCurve.value) void loadWorkloadCurve();
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
      if (workloadCurve.value) void loadWorkloadCurve();
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

  // --- Review-history health check ------------------------------------------
  const health = ref<FsrsHealthResponse | null>(null);
  const healthLoading = ref(false);
  const isRemapping = ref(false);

  const loadHealth = async () => {
    try {
      healthLoading.value = true;
      health.value = await $api<FsrsHealthResponse>('srs/settings/health');
    } catch {
      health.value = null;
    } finally {
      healthLoading.value = false;
    }
  };

  const ratingLabels = ['Again', 'Hard', 'Good', 'Easy'];
  const ratingColors = ['#ef4444', '#f59e0b', '#22c55e', '#3b82f6'];

  const ratingBreakdown = computed(() => {
    const h = health.value;
    if (!h || h.totalReviews === 0) return [];
    return h.ratingCounts.map((count, i) => ({
      label: ratingLabels[i]!,
      color: ratingColors[i]!,
      count,
      pct: (count / h.totalReviews) * 100,
    }));
  });

  const sameDayPct = computed(() => {
    const h = health.value;
    if (!h || h.totalReviews === 0) return 0;
    return (h.sameDayReviews / h.totalReviews) * 100;
  });

  // Whether there is anything noteworthy to surface; when clean we keep the panel quiet.
  const hasHealthInsight = computed(() => {
    const h = health.value;
    if (!h) return false;
    return h.likelyHardAsFail || h.neverUsesHard || h.neverUsesEasy || sameDayPct.value >= 40;
  });

  const confirmRemapHard = () => {
    confirm.require({
      message:
        'This rewrites every historical Hard rating to Again and reschedules your cards. Use this only if you have been pressing Hard for cards you actually failed. This cannot be undone.',
      header: 'Remap Hard → Again',
      icon: 'pi pi-exclamation-triangle',
      rejectProps: { label: 'Cancel', severity: 'secondary', outlined: true },
      acceptProps: { label: 'Remap', severity: 'warn' },
      accept: async () => {
        await remapHard();
      },
    });
  };

  const remapHard = async () => {
    try {
      isRemapping.value = true;
      const result = await $api<{ remapped: number; rescheduled: boolean }>('srs/settings/remap-hard', {
        method: 'POST',
        body: { reschedule: true },
      });
      await Promise.all([loadHealth(), loadParameters(true)]);
      toast.add({
        severity: 'success',
        summary: 'Remap complete',
        detail: `${result.remapped} Hard reviews remapped to Again and cards rescheduled.`,
        life: 6000,
      });
    } catch (error: unknown) {
      const message = error instanceof Error ? error.message : 'Failed to remap reviews.';
      toast.add({ severity: 'error', summary: 'Remap failed', detail: message, life: 5000 });
    } finally {
      isRemapping.value = false;
    }
  };

  // --- Workload-vs-retention feedback ---------------------------------------
  const workloadCurve = ref<FsrsWorkloadCurveResponse | null>(null);
  const workloadLoading = ref(false);
  const includeNewCards = ref(false);
  let workloadDebounce: ReturnType<typeof setTimeout> | null = null;

  const loadWorkloadCurve = async () => {
    try {
      workloadLoading.value = true;
      workloadCurve.value = await $api<FsrsWorkloadCurveResponse>(
        `srs/workload-curve?includeNewCards=${includeNewCards.value}`,
      );
    } catch {
      workloadCurve.value = null;
    } finally {
      workloadLoading.value = false;
    }
  };

  // The simulation is CPU-heavy (multi-second for large collections), so it is NOT run on page load — the
  // user triggers it with the Simulate button. Once a curve exists, changing the new-cards toggle (or
  // saving/optimising) re-runs it; otherwise nothing fires.
  watch(includeNewCards, () => {
    if (!workloadCurve.value) return;
    if (workloadDebounce) clearTimeout(workloadDebounce);
    workloadDebounce = setTimeout(() => void loadWorkloadCurve(), 200);
  });

  // Slider bounds are intentionally generous and never clamp the user's own value: some learners run
  // much lower retention on purpose, so if the saved value sits below the floor we extend the floor.
  const sliderMin = computed(() => Math.min(0.7, Number(desiredRetention.value) || 0.9));
  const sliderMax = computed(() => Math.max(0.98, Number(desiredRetention.value) || 0.9));

  interface WorkloadAt {
    multiplier: number;
    reviewsPerDay: number;
    minutesPerDay: number;
    recallPct: number;
  }

  // Linear interpolation of the simulated curve at an arbitrary retention, so the live readout updates
  // smoothly as the slider drags without a request per tick.
  function interpolateCurve(r: number): WorkloadAt | null {
    const pts = workloadCurve.value?.points;
    if (!pts || pts.length === 0) return null;
    const at = (p: WorkloadCurvePoint): WorkloadAt => ({
      multiplier: p.multiplier,
      reviewsPerDay: p.reviewsPerDay,
      minutesPerDay: p.minutesPerDay,
      recallPct: p.recallPct,
    });
    if (r <= pts[0]!.retention) return at(pts[0]!);
    const last = pts[pts.length - 1]!;
    if (r >= last.retention) return at(last);
    for (let i = 0; i < pts.length - 1; i++) {
      const a = pts[i]!;
      const b = pts[i + 1]!;
      if (r >= a.retention && r <= b.retention) {
        const t = (r - a.retention) / (b.retention - a.retention);
        const mix = (x: number, y: number) => x + t * (y - x);
        return {
          multiplier: mix(a.multiplier, b.multiplier),
          reviewsPerDay: mix(a.reviewsPerDay, b.reviewsPerDay),
          minutesPerDay: mix(a.minutesPerDay, b.minutesPerDay),
          recallPct: mix(a.recallPct, b.recallPct),
        };
      }
    }
    return null;
  }

  const liveWorkload = computed(() => interpolateCurve(Number(desiredRetention.value)));
  const baseRetention = computed(() => workloadCurve.value?.baseRetention ?? defaultDesiredRetention);

  const multiplierLabel = computed(() => {
    const m = liveWorkload.value?.multiplier;
    if (m == null) return null;
    return m >= 9.95 ? Math.round(m).toString() : m.toFixed(1);
  });

  const multiplierColor = computed(() => {
    const m = liveWorkload.value?.multiplier ?? 1;
    if (m <= 1.05) return 'text-green-600 dark:text-green-400';
    if (m <= 1.5) return 'text-amber-600 dark:text-amber-400';
    return 'text-red-600 dark:text-red-400';
  });

  const reviewsPerDayLabel = computed(() => {
    const n = liveWorkload.value?.reviewsPerDay;
    if (n == null) return null;
    return n >= 10 ? Math.round(n).toString() : n.toFixed(1);
  });

  const minutesPerDayLabel = computed(() => {
    const n = liveWorkload.value?.minutesPerDay;
    if (n == null) return null;
    return n >= 10 ? Math.round(n).toString() : n.toFixed(1);
  });

  const recallPctLabel = computed(() => {
    const n = liveWorkload.value?.recallPct;
    return n == null ? null : Math.round(n).toString();
  });

  // Per-maturity review speed (seconds/card) measured from the user's own history — shown so the time
  // estimate is transparent about where it comes from.
  const speedBreakdown = computed(() => {
    const c = workloadCurve.value;
    if (!c) return null;
    const fmt = (s: number) => `${Math.round(s * 10) / 10}s`;
    return `mature ${fmt(c.matureSeconds)} · young ${fmt(c.youngSeconds)} · learning ${fmt(c.learningSeconds)}`;
  });

  // Recommended retention (CMRR-style, server-computed): only surfaced when present and meaningfully
  // different from the current setting. Never auto-applied — the user clicks "Use".
  const recommendedRetention = computed(() => workloadCurve.value?.recommendedRetention ?? null);
  // Once a recommendation exists the band stays mounted; we only swap its content when the slider reaches
  // the recommended value, so passing over it doesn't unmount the band and shove the layout up.
  const atRecommended = computed(() => {
    const rec = recommendedRetention.value;
    return rec != null && Math.abs(rec - Number(desiredRetention.value)) < 0.01;
  });
  const recommendedPct = computed(() => {
    const rec = recommendedRetention.value;
    return rec == null ? null : Math.round(rec * 100);
  });
  function applyRecommended() {
    const rec = recommendedRetention.value;
    if (rec != null) desiredRetention.value = Math.round(rec * 100) / 100;
  }

  const hasWorkloadData = computed(() => (workloadCurve.value?.points.length ?? 0) >= 2 && (workloadCurve.value?.total ?? 0) > 0);

  // Which curve the chart plots. The headline readout always shows reviews + time + recall together; the
  // chart shows one axis at a time so the scales stay readable.
  type WorkloadMetric = 'reviews' | 'time' | 'recall';
  const chartMetric = ref<WorkloadMetric>('reviews');
  const metricOptions: { key: WorkloadMetric; label: string }[] = [
    { key: 'reviews', label: 'Reviews' },
    { key: 'time', label: 'Time' },
    { key: 'recall', label: 'Recall' },
  ];
  interface MetricCfg {
    axis: string;
    color: string;
    bg: string;
    pick: (p: WorkloadCurvePoint) => number;
    fmt: (v: number) => string;
    caption: string;
    yMin?: number;
    yMax?: number;
  }
  const metricConfig: Record<WorkloadMetric, MetricCfg> = {
    reviews: {
      axis: 'Reviews / day',
      color: 'rgb(37, 99, 235)',
      bg: 'rgba(37, 99, 235, 0.12)',
      pick: (p) => p.reviewsPerDay,
      fmt: (v) => `≈ ${Math.round(v)} reviews/day`,
      caption: 'Cards you’d review per day on average — the raw workload.',
      yMin: 0,
    },
    time: {
      axis: 'Minutes / day',
      color: 'rgb(217, 119, 6)',
      bg: 'rgba(217, 119, 6, 0.12)',
      pick: (p) => p.minutesPerDay,
      fmt: (v) => `≈ ${Math.round(v * 10) / 10} min/day`,
      caption: 'Estimated minutes per day, costed from your own per-maturity review speed.',
      yMin: 0,
    },
    recall: {
      axis: 'Recall (%)',
      color: 'rgb(16, 185, 129)',
      bg: 'rgba(16, 185, 129, 0.12)',
      pick: (p) => p.recallPct,
      fmt: (v) => `≈ ${Math.round(v)}% recalled`,
      caption: 'Share of your whole collection you’d recall on an average day — the payoff for the workload.',
      yMax: 100,
    },
  };

  const workloadChartData = computed<ChartData<'line'>>(() => {
    const pts = workloadCurve.value?.points ?? [];
    const cfg = metricConfig[chartMetric.value];
    // Highlight the grid point closest to the current slider value.
    const current = Number(desiredRetention.value);
    let nearest = 0;
    let bestDist = Infinity;
    pts.forEach((p, i) => {
      const d = Math.abs(p.retention - current);
      if (d < bestDist) {
        bestDist = d;
        nearest = i;
      }
    });
    return {
      labels: pts.map((p) => `${Math.round(p.retention * 100)}%`),
      datasets: [
        {
          label: cfg.axis,
          data: pts.map((p) => cfg.pick(p)),
          borderColor: cfg.color,
          backgroundColor: cfg.bg,
          borderWidth: 2,
          cubicInterpolationMode: 'monotone' as const,
          fill: true,
          pointRadius: pts.map((_, i) => (i === nearest ? 6 : 3)),
          pointBackgroundColor: pts.map((_, i) => (i === nearest ? 'rgb(239, 68, 68)' : cfg.color)),
        },
      ],
    };
  });

  const workloadChartOptions = computed<ChartOptions<'line'>>(() => {
    const cfg = metricConfig[chartMetric.value];
    return {
      responsive: true,
      maintainAspectRatio: false,
      layout: { padding: { top: 8, bottom: 4 } },
      plugins: {
        datalabels: { display: false },
        legend: { display: false },
        tooltip: {
          callbacks: {
            title: (items) => `${items[0]?.label} retention`,
            label: (ctx) => cfg.fmt(ctx.raw as number),
          },
        },
      },
      scales: {
        y: {
          // Recall lives near the top of the scale, so auto-range it (forcing 0 flattens the curve);
          // absolute counts/minutes anchor at 0.
          min: cfg.yMin,
          max: cfg.yMax,
          title: { display: true, text: cfg.axis },
          grid: { color: 'rgba(107, 114, 128, 0.15)' },
        },
        x: { title: { display: true, text: 'Desired retention' }, grid: { display: false } },
      },
    };
  });
</script>

<template>
  <Card>
    <template #title>
      <h3 class="text-lg font-semibold">FSRS Settings</h3>
    </template>
    <template #content>
      <div class="mb-4">
        <h4 class="text-md font-semibold mb-1">Desired retention</h4>
        <p class="text-sm text-gray-600 dark:text-gray-300 mb-2">Target recall rate. Higher means you remember more, but review more often.</p>
        <div class="flex flex-wrap items-center gap-3">
          <Slider v-model="desiredRetention" :min="sliderMin" :max="sliderMax" :step="0.01" class="w-full sm:w-64" />
          <InputNumber v-model="desiredRetention" class="w-28" input-class="w-28" :min="0.01" :max="0.99" :step="0.01" :min-fraction-digits="2" :max-fraction-digits="4" />
          <Button label="Save" :loading="isSaving" :disabled="!!formError || isLoading || isRecomputing || isResetting" @click="saveParameters" />
        </div>
        <Message v-if="retentionError" key="retention-error" severity="error" :closable="false" class="mt-2">
          {{ retentionError }}
        </Message>

        <!-- Workload-vs-retention simulation: CPU-heavy, so run on demand via the Simulate button -->
        <div class="mt-3 rounded-lg border border-surface-200 dark:border-surface-700 bg-surface-50 dark:bg-surface-800/40 p-3">
          <div>
            <div class="text-sm font-medium text-gray-700 dark:text-gray-200">Workload simulation</div>
            <div class="text-[11px] text-gray-400">See how reviews, time and recall change with your desired retention.</div>
          </div>

          <div class="flex items-center gap-2 mt-3">
            <ToggleSwitch v-model="includeNewCards" inputId="includeNewCards" :disabled="workloadLoading" />
            <label for="includeNewCards" class="text-sm cursor-pointer text-gray-600 dark:text-gray-300">Include future new cards in the estimate</label>
            <span v-if="workloadLoading" class="flex items-center gap-1 text-xs text-gray-400">
              <i class="pi pi-spin pi-spinner text-xs" /> Updating…
            </span>
          </div>

          <Button
            label="Simulate"
            icon="pi pi-chart-line"
            :loading="workloadLoading"
            class="mt-3"
            @click="loadWorkloadCurve"
          />

          <template v-if="hasWorkloadData && liveWorkload">
            <div class="flex flex-wrap items-baseline gap-x-4 gap-y-1 mt-3">
              <div class="text-sm">
                <span class="font-semibold text-lg tabular-nums" :class="multiplierColor">{{ multiplierLabel }}×</span>
                <span class="text-gray-500 dark:text-gray-400"> your current reviews</span>
              </div>
              <div class="text-sm text-gray-600 dark:text-gray-300">
                ≈ <span class="font-semibold tabular-nums">{{ reviewsPerDayLabel }}</span> reviews/day,
                <span class="font-semibold tabular-nums">{{ minutesPerDayLabel }}</span> min/day,
                <span class="font-semibold tabular-nums">{{ recallPctLabel }}%</span> recall
              </div>
            </div>
            <p class="text-[11px] text-gray-400 mt-1">
              Relative to your current setting ({{ Math.round(baseRetention * 100) }}%). Projected demand from your current cards{{ includeNewCards ? ' plus future new cards' : '' }} — not capped by your daily limit.
            </p>
            <p v-if="speedBreakdown" class="text-[11px] text-gray-400 mt-0.5">
              Time uses your measured review speed: {{ speedBreakdown }}.
            </p>

            <div v-if="recommendedPct != null" class="mt-2 flex flex-wrap items-center gap-2 rounded-md bg-emerald-50 dark:bg-emerald-900/20 border border-emerald-200 dark:border-emerald-800/50 px-3 py-2">
              <i :class="atRecommended ? 'pi pi-check-circle' : 'pi pi-sparkles'" class="text-emerald-600 dark:text-emerald-400 text-sm" />
              <span class="text-sm text-emerald-800 dark:text-emerald-200">
                Recommended ≈ <span class="font-semibold tabular-nums">{{ recommendedPct }}%</span>
                <span class="text-emerald-700/70 dark:text-emerald-300/70"> — least review time per word remembered</span>
              </span>
              <Button :label="atRecommended ? 'In use' : 'Use'" size="small" severity="success" outlined class="ml-auto" :disabled="atRecommended" @click="applyRecommended" />
            </div>

            <div class="flex rounded-lg bg-surface-100 dark:bg-surface-800 p-0.5 text-xs mt-3 w-fit">
              <button
                v-for="opt in metricOptions"
                :key="opt.key"
                class="px-2.5 py-1 rounded-md transition-colors"
                :class="
                  chartMetric === opt.key
                    ? 'bg-surface-0 dark:bg-surface-700 shadow-sm font-medium text-gray-800 dark:text-gray-100'
                    : 'text-gray-500 hover:text-gray-700 dark:hover:text-gray-300'
                "
                @click="chartMetric = opt.key"
              >
                {{ opt.label }}
              </button>
            </div>
            <div class="mt-2 transition-opacity" :class="{ 'opacity-40': workloadLoading }" style="height: 180px">
              <Line :data="workloadChartData" :options="workloadChartOptions" />
            </div>
            <p class="text-[11px] text-gray-400 mt-1">{{ metricConfig[chartMetric].caption }}</p>
          </template>

          <p v-else-if="workloadCurve" class="text-sm text-gray-500 dark:text-gray-400 mt-3">Start reviewing to see a workload estimate here.</p>
        </div>
      </div>

      <div class="mb-5">
        <h4 class="text-md font-semibold mb-1">Optimise parameters</h4>
        <p class="text-sm text-gray-600 dark:text-gray-300 mb-2">
          Analyse your review history to find the optimal FSRS parameters for your memory patterns. <br /> The more reviews you have, the more accurate the optimisation will be. It is recommended to optimise every time your number of review doubles.
          <br />You currently have {{reviewCount}} reviews.
        </p>

        <!-- Review-history health check: a quick read on whether the history is fit to train on -->
        <div v-if="health && health.totalReviews > 0" class="mb-3 rounded-lg border border-surface-200 dark:border-surface-700 bg-surface-50 dark:bg-surface-800/40 p-3">
          <div class="flex items-center gap-2">
            <i class="pi pi-heart text-sm text-gray-500 dark:text-gray-400" />
            <span class="text-sm font-medium text-gray-700 dark:text-gray-200">Review history health</span>
          </div>

          <!-- Button distribution bar -->
          <div class="mt-3">
            <div class="flex items-center justify-between text-[11px] text-gray-500 dark:text-gray-400 mb-1">
              <span>Button usage</span>
              <span class="tabular-nums">{{ health.totalReviews }} reviews · {{ Math.round(sameDayPct) }}% same-day</span>
            </div>
            <div class="flex h-3 w-full overflow-hidden rounded-full bg-surface-200 dark:bg-surface-700">
              <div
                v-for="seg in ratingBreakdown"
                :key="seg.label"
                class="h-full"
                :style="{ width: seg.pct + '%', backgroundColor: seg.color }"
                :title="`${seg.label}: ${seg.count} (${Math.round(seg.pct)}%)`"
              />
            </div>
            <div class="flex flex-wrap gap-x-4 gap-y-1 mt-2">
              <div v-for="seg in ratingBreakdown" :key="seg.label" class="flex items-center gap-1.5 text-[11px] text-gray-600 dark:text-gray-300">
                <span class="inline-block h-2.5 w-2.5 rounded-sm" :style="{ backgroundColor: seg.color }" />
                <span>{{ seg.label }}</span>
                <span class="tabular-nums text-gray-400">{{ Math.round(seg.pct) }}%</span>
              </div>
            </div>
          </div>

          <!-- Insights -->
          <div v-if="hasHealthInsight" class="mt-3 flex flex-col gap-2">
            <div v-if="health.likelyHardAsFail" class="flex flex-wrap items-center gap-2 rounded-md bg-amber-50 dark:bg-amber-900/20 border border-amber-200 dark:border-amber-800/50 px-3 py-2">
              <i class="pi pi-exclamation-triangle text-amber-600 dark:text-amber-400 text-sm" />
              <span class="text-sm text-amber-800 dark:text-amber-200">
                You press <b>Hard</b> often but almost never <b>Again</b>. If Hard is your "I failed" button, it skews optimisation. Hard should mean "recalled, but with difficulty".
              </span>
              <Button label="Remap Hard → Again" size="small" severity="warn" outlined class="ml-auto" :loading="isRemapping" @click="confirmRemapHard" />
            </div>
            <div v-if="health.neverUsesHard" class="flex items-center gap-2 text-sm text-gray-600 dark:text-gray-300">
              <i class="pi pi-info-circle text-sky-500 text-sm" />
              You have never pressed <b>Hard</b>- Using it for cards you barely recalled gives the optimiser more to work with.
            </div>
            <div v-if="health.neverUsesEasy" class="flex items-center gap-2 text-sm text-gray-600 dark:text-gray-300">
              <i class="pi pi-info-circle text-sky-500 text-sm" />
              You have never pressed <b>Easy</b>- Reserve it for cards that felt effortless.
            </div>
            <div v-if="sameDayPct >= 40 && !health.likelyHardAsFail" class="flex items-center gap-2 text-sm text-gray-600 dark:text-gray-300">
              <i class="pi pi-info-circle text-sky-500 text-sm" />
              {{ Math.round(sameDayPct) }}% of your reviews repeat a card the same day. Heavy same-day cramming can make optimisation less representative of long-term memory.
            </div>
          </div>
        </div>

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
