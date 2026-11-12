<script setup lang="ts">
  import { coverUrl } from '~/utils/coverImage';
  import { avatarBackground, avatarCharacter, headerBackground, tintColour } from '~/utils/profileAppearance';

  const props = defineProps<{
    name: string;
    avatarKanji?: string | null;
    avatarColour?: string | null;
    avatarStrokes?: string[] | null;
    headerColour?: string | null;
    memberSince?: string | null;
    stats?: { label: string; value: string }[];
    coverNames?: string[];
  }>();

  const AVATAR_SIZE = 128;
  const MAX_COVERS = 6;

  const accent = computed(() => headerBackground(props.headerColour, props.avatarColour, props.name));
  const avatarBase = computed(() => avatarBackground(props.avatarColour, props.name));
  const character = computed(() => avatarCharacter(props.avatarKanji, props.name));
  const strokes = computed(() => props.avatarStrokes ?? []);
  const strokeColours = computed(() => {
    const count = Math.max(1, strokes.value.length - 1);
    return strokes.value.map((_, i) => tintColour(avatarBase.value, 1 - (i / count) * 0.42));
  });
  const covers = computed(() => (props.coverNames ?? []).slice(0, MAX_COVERS).map((name) => coverUrl(name, 'sm')));
  const shownStats = computed(() => props.stats ?? []);
</script>

<template>
  <div class="bg-white text-black flex flex-row overflow-hidden" style="width: 1200px; height: 600px; font-family: 'Noto Sans JP', sans-serif">
    <div :style="{ width: '18px', height: '100%', backgroundColor: accent }" />

    <div class="flex flex-col flex-grow" style="padding: 44px 64px">
      <div class="flex flex-row items-center justify-between">
        <span class="font-bold" :style="{ fontSize: '1.5rem', color: accent }">Profile</span>
        <div class="flex flex-row items-center" style="gap: 12px">
          <img src="/favicon-96x96.png" alt="" style="width: 40px; height: 40px; border-radius: 8px" />
          <span class="font-bold" style="font-size: 1.8rem; font-weight: 800; color: #9333ea">jiten.moe</span>
        </div>
      </div>

      <div class="flex flex-row items-center" style="gap: 28px; margin-top: 28px">
        <svg v-if="strokes.length" viewBox="0 0 100 100" :width="AVATAR_SIZE" :height="AVATAR_SIZE">
          <rect x="1" y="1" width="98" height="98" rx="13" :fill="tintColour(avatarBase, 0.07)" :stroke="tintColour(avatarBase, 0.3)" stroke-width="2" />
          <g transform="translate(13 13) scale(0.6789)">
            <path
              v-for="(d, i) in strokes"
              :key="i"
              :d="d"
              fill="none"
              stroke-linecap="round"
              stroke-linejoin="round"
              :stroke="strokeColours[i]"
              stroke-width="5.6"
            />
          </g>
        </svg>
        <div
          v-else
          class="flex items-center justify-center font-bold"
          :style="{
            width: `${AVATAR_SIZE}px`,
            height: `${AVATAR_SIZE}px`,
            flexShrink: 0,
            borderRadius: '18px',
            backgroundColor: avatarBase,
            color: '#fff',
            fontSize: '66px',
            fontWeight: 700,
          }"
        >
          {{ character }}
        </div>

        <div class="flex flex-col" style="max-width: 880px">
          <span
            class="font-bold"
            style="font-size: 3.2rem; font-weight: 700; line-height: 1.2; padding-bottom: 10px; max-width: 880px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap"
          >
            {{ name }}
          </span>
          <span v-if="memberSince" style="font-size: 1.5rem; color: #4b5563; margin-top: 4px">Member since {{ memberSince }}</span>
        </div>
      </div>

      <div v-if="!covers.length" class="flex flex-grow" />

      <div v-if="shownStats.length" class="flex flex-row" :style="{ gap: '64px', marginTop: covers.length ? '28px' : '0' }">
        <div v-for="stat in shownStats" :key="stat.label" class="flex flex-col">
          <span class="font-bold" :style="{ fontSize: covers.length ? '2.4rem' : '3.4rem', fontWeight: 700, lineHeight: 1.15 }">{{ stat.value }}</span>
          <span :style="{ fontSize: covers.length ? '1.3rem' : '1.5rem', color: '#6b7280' }">{{ stat.label }}</span>
        </div>
      </div>

      <div class="flex flex-grow" />

      <div v-if="covers.length" class="flex flex-row" style="gap: 14px">
        <img
          v-for="(src, i) in covers"
          :key="i"
          :src="src"
          alt=""
          class="object-cover"
          style="width: 112px; height: 168px; border-radius: 8px; border: 1px solid #e5e7eb"
        />
      </div>
    </div>
  </div>
</template>
