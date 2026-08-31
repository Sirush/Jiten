<script setup lang="ts">
  import { rangeSummary, sliderBounds, type MediaRangeSpec } from '~/utils/mediaFilterRanges';
  import type { RangeBounds } from '~/utils/rangeFilters';

  const props = withDefaults(
    defineProps<{
      spec: MediaRangeSpec;
      steps?: number[] | null;
      expanded?: boolean;
      mobile?: boolean;
      stackEditor?: boolean;
    }>(),
    { steps: null, expanded: false, mobile: false, stackEditor: false }
  );

  const stacked = computed(() => props.mobile || props.stackEditor);

  const emit = defineEmits<{ toggle: [] }>();

  const bounds = defineModel<RangeBounds>({ required: true });

  const min = computed({
    get: () => bounds.value.min,
    set: (value) => {
      bounds.value = { ...bounds.value, min: value };
    },
  });
  const max = computed({
    get: () => bounds.value.max,
    set: (value) => {
      bounds.value = { ...bounds.value, max: value };
    },
  });

  const isSet = computed(() => min.value != null || max.value != null);
  const summary = computed(() => rangeSummary(props.spec, { min: min.value, max: max.value }) ?? 'Any');

  const stepIndex = (steps: number[], value: number) => {
    let best = 0;
    for (let i = 1; i < steps.length; i++) {
      if (Math.abs(steps[i]! - value) < Math.abs(steps[best]! - value)) best = i;
    }
    return best;
  };

  const sliderMin = computed(() => (props.steps ? 0 : props.spec.floor));
  const sliderMax = computed(() => (props.steps ? props.steps.length - 1 : props.spec.ceil));

  const sliderRange = computed<[number, number]>({
    get: () => {
      const low = min.value ?? props.spec.floor;
      const high = max.value ?? props.spec.ceil;
      return props.steps ? [stepIndex(props.steps, low), stepIndex(props.steps, high)] : [low, high];
    },
    set: ([low, high]) => {
      bounds.value = props.steps ? sliderBounds(props.spec, props.steps[low]!, props.steps[high]!) : sliderBounds(props.spec, low, high);
    },
  });

  const clear = () => {
    bounds.value = { min: null, max: null };
  };

  const digits = computed(() => props.spec.fractionDigits ?? 0);
  const valueClass = computed(() =>
    isSet.value ? 'font-semibold text-purple-700 dark:text-purple-300' : 'text-surface-400 dark:text-surface-500'
  );
</script>

<template>
  <div
    v-if="!expanded"
    role="button"
    tabindex="0"
    :aria-expanded="false"
    :class="[
      'flex cursor-pointer items-center gap-2 rounded px-2 hover:bg-surface-50 dark:hover:bg-surface-800',
      mobile ? 'h-11' : 'h-[34px]',
    ]"
    @click="emit('toggle')"
    @keydown.enter.prevent="emit('toggle')"
    @keydown.space.prevent="emit('toggle')"
  >
    <span class="truncate text-sm text-surface-700 dark:text-surface-200">{{ spec.label }}</span>
    <span :class="['ml-auto shrink-0 text-[13px]', valueClass]">{{ summary }}</span>
    <button
      v-if="isSet"
      type="button"
      class="shrink-0 cursor-pointer rounded text-surface-400 hover:text-surface-700 dark:hover:text-surface-100"
      :aria-label="`Clear ${spec.label}`"
      @click.stop="clear"
    >
      <Icon name="material-symbols:close-rounded" size="1em" />
    </button>
    <Icon name="material-symbols:chevron-right-rounded" class="shrink-0 text-surface-400" size="1.1em" />
  </div>

  <div
    v-else
    :class="[
      'rounded border border-surface-200 bg-surface-50 px-3 dark:border-surface-700 dark:bg-surface-800',
      mobile ? 'py-2.5' : 'py-2',
    ]"
  >
    <div
      role="button"
      tabindex="0"
      :aria-expanded="true"
      class="flex cursor-pointer items-center gap-2"
      @click="emit('toggle')"
      @keydown.enter.prevent="emit('toggle')"
      @keydown.space.prevent="emit('toggle')"
    >
      <span class="text-sm font-semibold text-surface-900 dark:text-surface-0">{{ spec.label }}</span>
      <span :class="['ml-auto text-[13px]', valueClass]">{{ summary }}</span>
      <Icon name="material-symbols:expand-more-rounded" class="text-surface-500" size="1.1em" />
    </div>

    <p v-if="spec.hint" class="mt-1 text-xs text-surface-400 dark:text-surface-500">{{ spec.hint }}</p>

    <div :class="['flex items-center gap-3', stacked ? 'flex-wrap' : '', mobile ? 'pt-3' : 'pt-2']">
      <InputNumber
        v-model="min"
        :min="spec.floor"
        :max="spec.ceil"
        :use-grouping="spec.grouping !== false"
        mode="decimal"
        :min-fraction-digits="0"
        :max-fraction-digits="digits"
        :step="spec.step"
        :class="['max-w-28 shrink-0', stacked ? 'order-2' : 'order-1']"
        size="small"
        placeholder="Min"
        :aria-label="`${spec.label} minimum`"
        fluid
      />
      <Slider
        v-model="sliderRange"
        range
        :min="sliderMin"
        :max="sliderMax"
        :step="steps ? 1 : spec.step"
        :class="stacked ? 'order-1 mx-2 w-full' : 'order-2 flex-1'"
        :aria-label="spec.label"
      />
      <InputNumber
        v-model="max"
        :min="spec.floor"
        :max="spec.ceil"
        :use-grouping="spec.grouping !== false"
        mode="decimal"
        :min-fraction-digits="0"
        :max-fraction-digits="digits"
        :step="spec.step"
        :class="['max-w-28 shrink-0 order-3', stacked ? 'ml-auto' : '']"
        size="small"
        placeholder="Max"
        :aria-label="`${spec.label} maximum`"
        fluid
      />
    </div>
  </div>
</template>
