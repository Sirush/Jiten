<script setup lang="ts">
  const props = withDefaults(
    defineProps<{
      url: string;
      // Blur + click-to-reveal (Front position only). While blurred, preview is disabled and a click
      // reveals instead of enlarging.
      blurred?: boolean;
      imgClass?: string;
    }>(),
    {
      blurred: false,
      imgClass: '',
    }
  );

  const emit = defineEmits<{
    error: [];
    reveal: [];
  }>();

  function onClick() {
    if (props.blurred) emit('reveal');
  }

  // Scroll-wheel / trackpad-pinch zoom inside the opened preview, composed on top of PrimeVue's own
  // rotate/scale transform (from its toolbar buttons) rather than reaching into its internal scale.
  // Reset whenever the preview opens or closes.
  const wheelScale = ref(1);
  function resetZoom() {
    wheelScale.value = 1;
  }
  function onWheel(e: WheelEvent) {
    const factor = e.deltaY < 0 ? 1.12 : 1 / 1.12;
    wheelScale.value = Math.min(6, Math.max(0.4, wheelScale.value * factor));
  }
  function previewStyle(base: { transform?: string } | undefined) {
    const t = base?.transform ?? '';
    return { transform: `${t} scale(${wheelScale.value})`, transformOrigin: 'center center', cursor: 'zoom-in' };
  }
</script>

<template>
  <div class="inline-flex" :class="{ 'cursor-pointer select-none': blurred }" @click.stop="onClick">
    <Image :preview="!blurred" @show="resetZoom" @hide="resetZoom">
      <template #image>
        <img :src="url" alt="Card image" :class="[imgClass, { 'blur-md': blurred }]" @error="emit('error')" />
      </template>
      <template #preview="slotProps">
        <img :src="url" alt="Card image" :style="previewStyle(slotProps.style)" @click="slotProps.previewCallback" @wheel.prevent="onWheel" />
      </template>
    </Image>
  </div>
</template>
