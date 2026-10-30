<script setup lang="ts">
  const props = defineProps<{ note: string }>();

  const noteRef = ref<HTMLElement | null>(null);
  const expanded = ref(false);
  const clipped = ref(false);
  let resizeObserver: ResizeObserver | undefined;

  const measureClip = () => {
    const el = noteRef.value;
    if (el && !expanded.value) clipped.value = el.scrollHeight > el.clientHeight + 1;
  };

  watch(
    noteRef,
    (el) => {
      resizeObserver?.disconnect();
      if (!el || typeof ResizeObserver === 'undefined') return;
      resizeObserver = new ResizeObserver(measureClip);
      resizeObserver.observe(el);
    },
    { flush: 'post' }
  );

  watch(
    () => props.note,
    () => nextTick(measureClip)
  );

  onBeforeUnmount(() => resizeObserver?.disconnect());
</script>

<template>
  <div class="flex items-start gap-2 rounded-md bg-surface-100 px-2 py-1 text-xs text-surface-700 dark:bg-surface-800 dark:text-surface-200">
    <i class="pi pi-file-edit mt-0.5 text-[11px] text-surface-500 dark:text-surface-400" aria-hidden="true" />
    <p ref="noteRef" class="min-w-0 flex-1 whitespace-pre-line break-words" :class="{ 'line-clamp-1': !expanded }">
      <span class="sr-only">Private note: </span>{{ note }}
    </p>
    <button
      v-if="clipped || expanded"
      type="button"
      :aria-expanded="expanded"
      class="inline-flex shrink-0 cursor-pointer items-center gap-1 rounded px-1 font-medium text-primary-700 hover:bg-surface-200 dark:text-primary-300 dark:hover:bg-surface-700"
      @click.stop="expanded = !expanded"
    >
      {{ expanded ? 'Less' : 'More' }}
      <i class="pi text-[9px]" :class="expanded ? 'pi-chevron-up' : 'pi-chevron-down'" aria-hidden="true" />
    </button>
  </div>
</template>
