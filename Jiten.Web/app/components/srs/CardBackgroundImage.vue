<script setup lang="ts">
  const props = defineProps<{
    url: string;
    blurred?: boolean;
  }>();

  const emit = defineEmits<{
    error: [];
    reveal: [];
  }>();

  const mediaRef = ref<{ openPreview: () => void } | null>(null);

  defineExpose({
    openPreview: () => mediaRef.value?.openPreview(),
  });
</script>

<template>
  <div class="pointer-events-none absolute inset-x-0 top-0 z-0 overflow-hidden rounded-t-2xl">
    <div class="card-background-image relative w-full">
      <SrsCardImage
        ref="mediaRef"
        class="w-full"
        :url="props.url"
        :blurred="props.blurred"
        :fill="true"
        img-class="block h-auto w-full object-contain object-top opacity-75"
        @error="emit('error')"
        @reveal="emit('reveal')"
      />
    </div>
  </div>
</template>

<style scoped>
  .card-background-image {
    mask-image: linear-gradient(
      to bottom,
      black 28%,
      rgb(0 0 0 / 0.96) 36.64%,
      rgb(0 0 0 / 0.9) 46%,
      rgb(0 0 0 / 0.78) 56.8%,
      rgb(0 0 0 / 0.6) 67.6%,
      rgb(0 0 0 / 0.38) 78.4%,
      rgb(0 0 0 / 0.2) 88.48%,
      rgb(0 0 0 / 0.06) 95.68%,
      transparent 100%
    );
  }
</style>
