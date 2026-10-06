<script setup lang="ts">
  import { ENTRY_MIN_DATE, dateToIso, entryMaxDate, formatReadDate, isoToDate, isoToday, releaseDateChoice } from '~/utils/mediaListEntry';

  const props = defineProps<{
    label: string;
    releaseDate?: string | Date | null;
    startDate?: string | null;
    hideRelease?: boolean;
    backLabel?: string;
    closes?: boolean;
    disabled?: boolean;
  }>();

  const emit = defineEmits<{
    choose: [date: string | null];
    back: [];
  }>();

  const today = isoToday();
  const release = computed(() => {
    const iso = props.hideRelease ? null : releaseDateChoice(props.releaseDate, today);
    return iso && (!props.startDate || iso >= props.startDate) ? iso : null;
  });
  const minDate = computed(() => (props.startDate ? isoToDate(props.startDate) : ENTRY_MIN_DATE));
  const maxDate = entryMaxDate();
  const picking = ref(false);
  const picked = ref<Date | null>(null);

  watch(picked, (date) => {
    if (date && !props.disabled) emit('choose', dateToIso(date));
  });

  const root = ref<HTMLElement>();
  onMounted(() => root.value?.focus({ preventScroll: true }));

  const optionClass =
    'flex items-center gap-1 px-3 py-1.5 rounded text-left text-sm hover:bg-gray-100 dark:hover:bg-gray-700 focus-visible:bg-gray-100 dark:focus-visible:bg-gray-700 transition-colors cursor-pointer disabled:opacity-50 disabled:cursor-default';
</script>

<template>
  <div ref="root" class="flex flex-col min-w-56 outline-none" role="group" :aria-label="label" tabindex="-1">
    <div class="flex items-center gap-1 px-1 pb-1">
      <button
        type="button"
        class="p-1.5 rounded hover:bg-gray-100 dark:hover:bg-gray-700 transition-colors cursor-pointer"
        :aria-label="backLabel ?? 'Back to statuses'"
        @click="emit('back')"
      >
        <i :class="['pi text-xs', closes ? 'pi-times' : 'pi-arrow-left']" aria-hidden="true" />
      </button>
      <span class="text-sm font-semibold">{{ label }}</span>
    </div>

    <template v-if="!picking">
      <button type="button" :class="[optionClass, 'font-bold']" :disabled="disabled" @click="emit('choose', today)">Today</button>
      <button v-if="release" type="button" :class="optionClass" :disabled="disabled" @click="emit('choose', release)">
        On release <span class="text-gray-500 dark:text-gray-400">({{ formatReadDate(release) }})</span>
      </button>
      <button type="button" :class="optionClass" :disabled="disabled" @click="picking = true">Pick a date…</button>
      <button type="button" :class="optionClass" :disabled="disabled" @click="emit('choose', null)">I'm not sure</button>
    </template>

    <DatePicker v-else v-model="picked" inline :max-date="maxDate" :min-date="minDate" class="w-full" />
  </div>
</template>
