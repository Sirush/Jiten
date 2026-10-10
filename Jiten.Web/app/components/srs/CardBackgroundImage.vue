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
    <div class="relative w-full">
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
      <div class="card-background-fade pointer-events-none absolute inset-x-0 bottom-0 z-20 h-[72%]" />
    </div>
  </div>
</template>

<style scoped>
  .card-background-fade {
    --fade-color: 0 0 0;
    background: linear-gradient(
      to bottom,
      transparent 0%,
      rgb(var(--fade-color) / 0.04) 12%,
      rgb(var(--fade-color) / 0.1) 25%,
      rgb(var(--fade-color) / 0.22) 40%,
      rgb(var(--fade-color) / 0.4) 55%,
      rgb(var(--fade-color) / 0.62) 70%,
      rgb(var(--fade-color) / 0.8) 84%,
      rgb(var(--fade-color) / 0.94) 94%,
      rgb(var(--fade-color)) 100%
    );
  }

  :root:not(.dark-mode) .card-background-fade {
    --fade-color: 248 250 252;
  }
</style>
