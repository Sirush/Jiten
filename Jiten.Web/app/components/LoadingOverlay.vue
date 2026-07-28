<script setup lang="ts">
  defineProps<{
    visible: boolean;
    message?: string;
  }>();
</script>

<template>
  <div
    v-if="visible"
    role="alert"
    aria-busy="true"
    class="fixed inset-0 z-[9999] flex flex-col items-center justify-center bg-black/80 text-white"
  >
    <ProgressSpinner
      style="width: 50px; height: 50px"
      stroke-width="8"
      fill="transparent"
      animation-duration=".5s"
      class="mb-4"
      aria-hidden="true"
    />
    <p v-if="message" class="font-bold text-lg">{{ message }}</p>
    <slot v-else />
  </div>
</template>
