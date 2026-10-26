<script setup lang="ts">
  import { coverUrl } from '~/utils/coverImage';

  const props = defineProps<{
    name: string;
    description?: string | null;
    ownerDisplayName?: string | null;
    itemCount: number;
    coverNames?: string[];
  }>();

  const MAX_COVERS = 5;

  const clip = (text: string, max: number) => (text.length > max ? `${text.slice(0, max).replace(/\s+\S*$/, '')}…` : text);

  const shownName = computed(() => clip(props.name, 60));
  const shownDescription = computed(() => (props.description ? clip(props.description.replace(/\s+/g, ' ').trim(), 140) : ''));
  const byline = computed(() => {
    const titles = `${props.itemCount.toLocaleString('en-US')} ${props.itemCount === 1 ? 'title' : 'titles'}`;
    return props.ownerDisplayName ? `${titles} picked by ${props.ownerDisplayName}` : titles;
  });
  const covers = computed(() => (props.coverNames ?? []).slice(0, MAX_COVERS).map((name) => coverUrl(name, 'sm')));
  const hiddenCount = computed(() => (covers.value.length ? props.itemCount - covers.value.length : 0));
</script>

<template>
  <div class="bg-white text-black flex flex-row overflow-hidden" style="width: 1200px; height: 630px; font-family: 'Noto Sans JP', sans-serif">
    <div style="width: 18px; height: 100%; background-color: #a855f7" />

    <div class="flex flex-col flex-grow" style="padding: 48px 64px">
      <div class="flex flex-row items-center justify-between">
        <div class="flex flex-row items-center" style="gap: 16px">
          <img src="/web-app-manifest-192x192.png" alt="" style="width: 64px; height: 64px; border-radius: 14px" />
          <span class="font-bold" style="font-size: 2.4rem; font-weight: 700">Jiten</span>
        </div>
        <span class="font-bold" style="font-size: 1.5rem; color: #7e22ce">Community list</span>
      </div>

      <h1 class="font-bold" style="font-size: 3rem; line-height: 1.15; margin: 28px 0 10px 0">
        {{ shownName }}
      </h1>
      <span style="font-size: 1.6rem; color: #4b5563">{{ byline }}</span>
      <span v-if="shownDescription" style="font-size: 1.4rem; line-height: 1.45; color: #6b7280; margin-top: 8px">
        {{ shownDescription }}
      </span>

      <div class="flex flex-grow" />

      <div class="flex flex-row items-end justify-between">
        <div class="flex flex-row" style="gap: 14px">
          <img
            v-for="(src, i) in covers"
            :key="i"
            :src="src"
            alt=""
            class="object-cover"
            style="width: 112px; height: 168px; border-radius: 8px; border: 1px solid #e5e7eb"
          />
          <div
            v-if="hiddenCount > 0"
            class="flex items-center justify-center font-bold"
            style="width: 112px; height: 168px; border-radius: 8px; background-color: #f3e8ff; color: #7e22ce; font-size: 2rem"
          >
            +{{ hiddenCount.toLocaleString('en-US') }}
          </div>
        </div>
        <span style="font-size: 1.5rem; color: #6b7280">jiten.moe</span>
      </div>
    </div>
  </div>
</template>
