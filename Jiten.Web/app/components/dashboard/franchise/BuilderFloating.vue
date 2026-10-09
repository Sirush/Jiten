<script setup lang="ts">
  import { ref, watch, onMounted, onBeforeUnmount, nextTick } from 'vue';

  const props = withDefaults(defineProps<{ anchor: { x: number; y: number }; label?: string; wide?: boolean }>(), { label: undefined, wide: false });
  const emit = defineEmits<{ close: [] }>();

  const el = ref<HTMLElement | null>(null);
  const pos = ref({ left: -9999, top: -9999 });

  function place() {
    const node = el.value;
    if (!node) return;
    const w = node.offsetWidth;
    const h = node.offsetHeight;
    let x = props.anchor.x + 12;
    let y = props.anchor.y + 12;
    if (x + w > window.innerWidth - 12) x = window.innerWidth - 12 - w;
    if (y + h > window.innerHeight - 12) y = Math.max(12, props.anchor.y - h - 12);
    pos.value = { left: Math.max(12, x), top: Math.max(12, y) };
  }

  function onDocPointerDown(ev: PointerEvent) {
    if (el.value && !el.value.contains(ev.target as Node)) emit('close');
  }

  onMounted(() => {
    nextTick(place);
    document.addEventListener('pointerdown', onDocPointerDown, true);
  });
  onBeforeUnmount(() => document.removeEventListener('pointerdown', onDocPointerDown, true));
  watch(
    () => props.anchor,
    () => nextTick(place)
  );

  defineExpose({ place, el });
</script>

<template>
  <Teleport to="body">
    <div
      ref="el"
      role="dialog"
      :aria-label="label"
      class="fixed z-[1100] flex flex-col gap-2 rounded-md border border-gray-300 bg-white p-3 text-sm text-gray-900 shadow-xl dark:border-gray-600 dark:bg-gray-900 dark:text-gray-100"
      :class="wide ? 'w-[min(430px,calc(100vw-24px))]' : 'w-[min(340px,calc(100vw-24px))]'"
      :style="{ left: `${pos.left}px`, top: `${pos.top}px` }"
      @keydown.esc.stop="emit('close')"
    >
      <slot />
    </div>
  </Teleport>
</template>
