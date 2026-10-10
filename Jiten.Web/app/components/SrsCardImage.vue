<script setup lang="ts">
  const props = withDefaults(
    defineProps<{
      url: string;
      contentType?: string;
      // Blur + click-to-reveal (Front position only). While blurred, preview is disabled and a click
      // reveals instead of enlarging.
      blurred?: boolean;
      imgClass?: string;
      loading?: 'eager' | 'lazy';
    }>(),
    {
      blurred: false,
      imgClass: '',
      contentType: 'image/*',
      loading: 'eager',
    }
  );

  const emit = defineEmits<{
    error: [];
    reveal: [];
  }>();

  function onClick() {
    if (props.blurred) emit('reveal');
  }

  const videoPreviewOpen = ref(false);

  function openVideoPreview() {
    if (props.blurred) {
      emit('reveal');
      return;
    }
    videoPreviewOpen.value = true;
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
  <div class="inline-flex" :class="{ 'cursor-pointer select-none': blurred || contentType === 'video/webm' }" @click.stop="onClick">
    <template v-if="contentType === 'video/webm'">
      <video
        :src="url"
        autoplay
        loop
        muted
        playsinline
        preload="metadata"
        :class="[imgClass, 'block', { 'blur-md': blurred }]"
        @click.stop="openVideoPreview"
        @error="emit('error')"
      />
      <Dialog v-model:visible="videoPreviewOpen" modal dismissable-mask header="Card animation" :style="{ width: 'min(90vw, 60rem)' }">
        <video :src="url" autoplay loop muted playsinline class="max-h-[80vh] max-w-full w-full object-contain" @error="emit('error')" />
      </Dialog>
    </template>
    <Image v-else :preview="!blurred" @show="resetZoom" @hide="resetZoom">
      <template #image>
        <img :src="url" alt="Card image" :loading="loading" :class="[imgClass, { 'blur-md': blurred }]" @error="emit('error')" />
      </template>
      <template #preview="slotProps">
        <img :src="url" alt="Card image" :style="previewStyle(slotProps.style)" @click="slotProps.previewCallback" @wheel.prevent="onWheel" />
      </template>
    </Image>
  </div>
</template>
