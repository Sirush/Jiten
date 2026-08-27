<script setup lang="ts">
  const props = defineProps<{
    title: string;
    description?: string;
    category?: string;
  }>();

  const shownTitle = computed(() => (props.title.length > 70 ? `${props.title.slice(0, 70)}…` : props.title));
  const shownDescription = computed(() => (props.description && props.description.length > 170 ? `${props.description.slice(0, 170)}…` : props.description));
</script>

<template>
  <div class="bg-white text-black flex flex-row overflow-hidden" style="width: 1200px; height: 630px; font-family: 'Noto Sans JP', sans-serif">
    <div style="width: 18px; height: 100%; background-color: #a855f7" />

    <div class="flex flex-col flex-grow" style="padding: 56px 64px">
      <div class="flex flex-row items-center" style="gap: 20px">
        <img src="/web-app-manifest-192x192.png" alt="" style="width: 84px; height: 84px; border-radius: 18px" />
        <span class="font-bold" style="font-size: 3rem; font-weight: 700">Jiten</span>
      </div>

      <div class="flex flex-grow" />

      <span v-if="category" class="font-bold" style="font-size: 1.5rem; color: #7e22ce; margin-bottom: 12px">
        {{ category }}
      </span>
      <h1 class="font-bold" style="font-size: 3.4rem; line-height: 1.15; margin: 0 0 24px 0">
        {{ shownTitle }}
      </h1>
      <span v-if="shownDescription" style="font-size: 1.7rem; line-height: 1.45; color: #4b5563">
        {{ shownDescription }}
      </span>

      <div class="flex flex-grow" />

      <span style="font-size: 1.5rem; color: #6b7280">jiten.moe</span>
    </div>
  </div>
</template>
