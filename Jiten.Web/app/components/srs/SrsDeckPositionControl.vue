<script setup lang="ts">
  const props = defineProps<{ index: number; total: number }>();
  const emit = defineEmits<{ move: [targetIndex: number] }>();

  const popover = ref();
  const target = ref<number | null>(null);

  function toggle(event: Event) {
    target.value = props.index + 1;
    popover.value.toggle(event);
  }

  function moveTo(targetIndex: number) {
    if (targetIndex !== props.index) emit('move', targetIndex);
    // The row keeps its component instance across the reorder, so close after the list has patched.
    nextTick(() => popover.value?.hide());
  }

  function applyTarget() {
    const value = target.value;
    if (value == null || Number.isNaN(value)) return;
    moveTo(Math.min(Math.max(Math.round(value), 1), props.total) - 1);
  }
</script>

<template>
  <button
    type="button"
    class="flex-shrink-0 h-8 min-w-9 px-1.5 rounded-md text-xs font-semibold tabular-nums cursor-pointer text-gray-500 dark:text-gray-400 bg-surface-100 dark:bg-surface-800 hover:bg-surface-200 hover:text-gray-700 dark:hover:bg-surface-700 dark:hover:text-gray-200 transition-colors"
    :aria-label="`Position ${index + 1} of ${total}, change position`"
    @click="toggle"
  >
    #{{ index + 1 }}
  </button>

  <Popover ref="popover">
    <div class="flex w-[min(15rem,calc(100vw_-_3rem))] flex-col gap-2 p-1">
      <div class="text-xs text-gray-500 dark:text-gray-400">Position {{ index + 1 }} of {{ total }}</div>

      <Button
        label="Move to top"
        icon="pi pi-angle-double-up"
        size="small"
        severity="secondary"
        fluid
        class="justify-start! px-3!"
        :disabled="index === 0"
        @click="moveTo(0)"
      />
      <Button
        label="Move to bottom"
        icon="pi pi-angle-double-down"
        size="small"
        severity="secondary"
        fluid
        class="justify-start! px-3!"
        :disabled="index === total - 1"
        @click="moveTo(total - 1)"
      />

      <div class="flex items-center gap-2 pt-1">
        <InputNumber
          v-model="target"
          :min="1"
          :max="total"
          :use-grouping="false"
          size="small"
          aria-label="Move to position"
          class="flex-1 min-w-0"
          fluid
          @keydown.enter="applyTarget"
        />
        <Button icon="pi pi-check" size="small" aria-label="Apply position" @click="applyTarget" />
      </div>
    </div>
  </Popover>
</template>
