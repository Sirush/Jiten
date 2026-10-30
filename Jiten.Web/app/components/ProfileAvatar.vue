<script setup lang="ts">
  import { avatarBackground, avatarCharacter, loadAvatarFont, tintColour, type AvatarStyle } from '~/utils/profileAppearance';

  const props = withDefaults(
    defineProps<{
      name: string | null;
      kanji?: string | null;
      colour?: string | null;
      avatarStyle?: AvatarStyle | null;
      /** KanjiVG paths of the character, needed by the stroke style. */
      strokes?: string[] | null;
      size?: number;
      /** A white frame that lifts the filled tile off a coloured banner; the other styles carry their own edge. */
      framed?: boolean;
    }>(),
    { kanji: null, colour: null, avatarStyle: null, strokes: null, size: 40, framed: false }
  );

  /** At list sizes, textures, guides and animation turn to noise, so each style draws a plainer version. */
  const SMALL_SIZE = 40;
  const STROKE_STAGGER_SECONDS = 0.24;

  const character = computed(() => avatarCharacter(props.kanji, props.name));
  const base = computed(() => avatarBackground(props.colour, props.name ?? ''));
  const label = computed(() => (props.name ? `${props.name}'s avatar` : 'Avatar'));
  const small = computed(() => props.size <= SMALL_SIZE);
  const radius = computed(() => `${Math.max(6, Math.round(props.size * 0.14))}px`);

  /** Characters without KanjiVG data, such as kana or a Latin initial, draw filled. */
  const style = computed<AvatarStyle>(() => {
    const chosen = props.avatarStyle ?? 'filled';
    return chosen === 'stroke' && !props.strokes?.length ? 'filled' : chosen;
  });

  if (import.meta.client) watch(style, (s) => loadAvatarFont(s), { immediate: true });

  const filterId = `avatar-${useId().replace(/[^a-zA-Z0-9-]/g, '')}`;

  const strokeColours = computed(() => {
    const count = Math.max(1, (props.strokes?.length ?? 1) - 1);
    return (props.strokes ?? []).map((_, i) => tintColour(base.value, 1 - (i / count) * 0.42));
  });

  const ensoRadius = 37;
  const ensoPoint = (degrees: number) => {
    const rad = (degrees * Math.PI) / 180;
    return `${(50 + ensoRadius * Math.cos(rad)).toFixed(2)} ${(50 + ensoRadius * Math.sin(rad)).toFixed(2)}`;
  };
  const ensoArc = `M ${ensoPoint(-62)} A ${ensoRadius} ${ensoRadius} 0 1 1 ${ensoPoint(-96)}`;
  const ensoTail = `M ${ensoPoint(-62)} A ${ensoRadius} ${ensoRadius} 0 0 1 ${ensoPoint(30)}`;

  const playCount = ref(0);
  function replayStrokes() {
    if (style.value === 'stroke' && !small.value) playCount.value++;
  }
</script>

<template>
  <div
    v-if="style === 'filled'"
    role="img"
    :aria-label="label"
    class="flex shrink-0 select-none items-center justify-center font-bold text-white"
    :style="{
      width: `${size}px`,
      height: `${size}px`,
      fontSize: `${Math.round(size * 0.52)}px`,
      borderRadius: radius,
      background: base,
      border: framed ? `${Math.max(3, Math.round(size / 30))}px solid #fff` : undefined,
    }"
    v-bind="japaneseTextAttrs(character)"
  >
    {{ character }}
  </div>
  <div
    v-else
    role="img"
    :aria-label="label"
    class="shrink-0 select-none overflow-hidden"
    :style="{ width: `${size}px`, height: `${size}px`, borderRadius: radius }"
    @mouseenter="replayStrokes"
  >
    <svg viewBox="0 0 100 100" class="block h-full w-full" aria-hidden="true" lang="ja">
      <defs v-if="!small && (style === 'seal' || style === 'brush')">
        <filter v-if="style === 'seal'" :id="filterId" x="-8%" y="-8%" width="116%" height="116%">
          <feTurbulence type="fractalNoise" baseFrequency="0.6" numOctaves="2" seed="4" result="warp" />
          <feDisplacementMap in="SourceGraphic" in2="warp" scale="2.4" xChannelSelector="R" yChannelSelector="G" result="rough" />
          <feTurbulence type="fractalNoise" baseFrequency="1.4" numOctaves="1" seed="11" result="grain" />
          <feColorMatrix in="grain" type="matrix" values="0 0 0 0 0  0 0 0 0 0  0 0 0 0 0  -4.2 0 0 0 3.6" result="speckle" />
          <feComposite in="rough" in2="speckle" operator="in" />
        </filter>
        <template v-else>
          <filter :id="filterId" x="-10%" y="-10%" width="120%" height="120%">
            <feTurbulence type="fractalNoise" baseFrequency="0.9" numOctaves="2" seed="2" result="warp" />
            <feDisplacementMap in="SourceGraphic" in2="warp" scale="3.2" xChannelSelector="R" yChannelSelector="G" />
          </filter>
          <filter :id="`${filterId}-fibres`" x="0" y="0" width="100%" height="100%">
            <feTurbulence type="fractalNoise" baseFrequency="0.035 0.6" numOctaves="2" seed="7" />
            <feColorMatrix type="matrix" values="0 0 0 0 0.45  0 0 0 0 0.38  0 0 0 0 0.28  0 0 0 0.9 -0.42" />
          </filter>
        </template>
      </defs>

      <template v-if="style === 'seal'">
        <rect width="100" height="100" rx="14" :fill="tintColour(base, 0.05)" />
        <g :filter="small ? undefined : `url(#${filterId})`" :transform="small ? undefined : 'rotate(-4 50 50)'">
          <rect x="11" y="11" width="78" height="78" rx="9" fill="none" :stroke="base" :stroke-width="small ? 8 : 6.5" />
          <text x="50" y="52" text-anchor="middle" dominant-baseline="central" class="avatar-mincho" font-size="52" :fill="base">{{ character }}</text>
        </g>
      </template>

      <template v-else-if="style === 'paper'">
        <rect width="100" height="100" rx="14" :fill="tintColour(base, 0.04)" :stroke="base" :stroke-width="small ? 7 : 4.5" />
        <g v-if="!small" :stroke="tintColour(base, 0.35)" stroke-width="1.4" stroke-dasharray="4 3.5">
          <line x1="50" y1="6" x2="50" y2="94" />
          <line x1="6" y1="50" x2="94" y2="50" />
        </g>
        <text x="50" y="52" text-anchor="middle" dominant-baseline="central" class="avatar-textbook" font-size="60" :fill="base">{{ character }}</text>
      </template>

      <template v-else-if="style === 'crest'">
        <rect width="100" height="100" rx="14" :fill="base" />
        <circle cx="50" cy="50" r="36" fill="none" stroke="#fff" stroke-width="4" />
        <text x="50" y="51" text-anchor="middle" dominant-baseline="central" class="avatar-mincho" font-size="40" fill="#fff">{{ character }}</text>
      </template>

      <template v-else-if="style === 'stroke'">
        <rect x="1" y="1" width="98" height="98" rx="13" :fill="tintColour(base, 0.07)" :stroke="tintColour(base, 0.3)" stroke-width="2" />
        <g :key="playCount" transform="translate(13 13) scale(0.6789)">
          <path
            v-for="(d, i) in strokes"
            :key="i"
            :d="d"
            pathLength="1"
            fill="none"
            stroke-linecap="round"
            stroke-linejoin="round"
            :stroke="strokeColours[i]"
            :stroke-width="small ? 7.5 : 5.6"
            :class="{ 'avatar-stroke-draw': playCount > 0 }"
            :style="playCount > 0 ? { animationDelay: `${(i * STROKE_STAGGER_SECONDS).toFixed(2)}s` } : undefined"
          />
        </g>
      </template>

      <template v-else-if="style === 'brush'">
        <rect width="100" height="100" rx="14" fill="#f7f3ea" />
        <rect v-if="!small" width="100" height="100" rx="14" :filter="`url(#${filterId}-fibres)`" opacity="0.55" />
        <path v-if="small" :d="ensoArc" fill="none" :stroke="base" stroke-width="8" stroke-linecap="round" />
        <g v-else :filter="`url(#${filterId})`">
          <path :d="ensoArc" fill="none" :stroke="base" stroke-width="7" stroke-linecap="round" />
          <path :d="ensoTail" fill="none" :stroke="base" stroke-width="3" stroke-linecap="round" transform="translate(1.6 0.6)" />
        </g>
        <text x="50" y="50" text-anchor="middle" dominant-baseline="central" class="avatar-brush" :font-size="small ? 54 : 50" fill="#1f1c1a">{{ character }}</text>
      </template>
    </svg>
  </div>
</template>

<style scoped>
  .avatar-mincho {
    font-family: 'Shippori Mincho B1', 'Noto Serif JP', serif;
    font-weight: 800;
  }

  .avatar-textbook {
    font-family: 'Klee One', 'Noto Sans JP Variable', 'Noto Sans JP', sans-serif;
    font-weight: 600;
  }

  .avatar-brush {
    font-family: 'Yuji Syuku', 'Noto Serif JP', serif;
  }

  .avatar-stroke-draw {
    stroke-dasharray: 1 2;
    stroke-dashoffset: 1;
    animation: avatar-stroke 0.32s ease-out forwards;
  }

  @keyframes avatar-stroke {
    to {
      stroke-dashoffset: 0;
    }
  }

  @media (prefers-reduced-motion: reduce) {
    .avatar-stroke-draw {
      animation: none;
      stroke-dashoffset: 0;
    }
  }
</style>
