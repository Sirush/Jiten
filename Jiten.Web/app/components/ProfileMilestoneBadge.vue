<script setup lang="ts">
  import type { MilestoneTier, ProfileMilestone } from '~/types';
  import { formatCompact } from '~/utils/siteStats';
  import {
    ASCEND_PARTICLES,
    ICHIBU_BOX,
    ICHIBU_FLOWERS,
    LACQUER_FLECKS,
    MAGATAMA_CLIP,
    MAGATAMA_CLIP_BACK,
    ROPE_STRANDS,
    SAKURA_PETALS,
  } from '~/utils/medalArt';

  const props = defineProps<{ milestone: ProfileMilestone }>();

  /** Public names are the material, so the order reads without knowing the objects; the medal carries the Japanese identity. */
  const TIER_NAMES: Record<MilestoneTier, string> = {
    ema: '木 Wood',
    zeni: '銅 Copper',
    ichibu: '銀 Silver',
    koban: '金 Gold',
    jade: '玉 Jade',
    crystal: '晶 Crystal',
    lacquer: '漆 Lacquer',
    shimenawa: '神 Kami',
  };

  const amount = (n: number) => (props.milestone.kind === 'streak' || props.milestone.kind === 'studyDays' ? n.toLocaleString('en-US') : formatCompact(n));

  const KINDS: Record<ProfileMilestone['kind'], { glyph?: string; icon?: string; label: (n: number) => string }> = {
    characters: { glyph: '字', label: (n) => `${amount(n)} characters` },
    titles: { glyph: '本', label: (n) => `${amount(n)} ${n === 1 ? 'title finished' : 'titles finished'}` },
    words: { glyph: '語', label: (n) => `${amount(n)} words known` },
    kanji: { glyph: '漢', label: (n) => `${amount(n)} kanji known` },
    reviews: { glyph: '習', label: (n) => `${amount(n)} ${n === 1 ? 'review' : 'reviews'}` },
    streak: { icon: 'material-symbols:local-fire-department', label: (n) => `${n}-day longest streak` },
    studyDays: { glyph: '日', label: (n) => `${amount(n)} ${n === 1 ? 'day' : 'days'} studied` },
    votes: { glyph: '比', label: (n) => `${amount(n)} difficulty ${n === 1 ? 'vote' : 'votes'}` },
  };

  const kind = computed(() => KINDS[props.milestone.kind]);
  const tier = computed(() => props.milestone.tier);
  const tierName = computed(() => (tier.value ? TIER_NAMES[tier.value] : null));
  const nextName = computed(() => (props.milestone.nextTier ? TIER_NAMES[props.milestone.nextTier] : null));
  const isLacquer = computed(() => tier.value === 'lacquer' || tier.value === 'shimenawa');
  const uid = useId();

  const progress = computed(() => {
    const { value, threshold, nextThreshold } = props.milestone;
    if (nextThreshold == null) return 100;
    const from = threshold ?? 0;
    return Math.max(0, Math.min(100, ((value - from) / (nextThreshold - from)) * 100));
  });

  const valueLabel = computed(() => kind.value.label(props.milestone.value));

  const SPIN_CHANCE = 1 / 8;
  const COIN_THICKNESS = 8;
  const EDGE_LAYERS = Array.from({ length: COIN_THICKNESS * 2 - 1 }, (_, i) => i / 2 - COIN_THICKNESS / 2 + 0.5);
  const ASCEND_MS = 2000;
  const medalStyle = computed(() => ({
    '--coin-half': `${COIN_THICKNESS / 2}px`,
    '--ascend-ms': `${ASCEND_MS}ms`,
    ...(tier.value === 'jade' ? { '--coin-clip': MAGATAMA_CLIP } : {}),
  }));
  const spinning = ref(false);
  const ascending = ref(false);

  /** The top rank never flips; its rare roll plays the ascension instead. */
  function playRare() {
    if (tier.value !== 'shimenawa') {
      spinning.value = true;
      return;
    }
    ascending.value = true;
    setTimeout(() => (ascending.value = false), ASCEND_MS);
  }

  function maybeSpin() {
    if (spinning.value || ascending.value || !tier.value) return;
    if (prefersReducedMotion()) return;
    if (Math.random() < SPIN_CHANCE) playRare();
  }

  const tooltip = computed(() => {
    const { nextThreshold, value } = props.milestone;
    const reached = tierName.value ? `${tierName.value} tier. ` : 'Not reached yet. ';
    if (!nextName.value || nextThreshold == null) return `${reached}The highest tier, ${valueLabel.value}.`;
    return `${reached}${nextName.value} at ${amount(nextThreshold)}, ${amount(nextThreshold - value)} to go.`;
  });
</script>

<template>
  <Tooltip :content="tooltip" block>
    <div
      tabindex="0"
      class="medal-card flex flex-col items-center gap-2 rounded-lg p-1 text-center focus-visible:outline-2 focus-visible:outline-primary-500"
      @mouseenter="maybeSpin"
      @focus="maybeSpin"
    >
      <div class="flex h-[4.5rem] items-center justify-center">
      <div
        v-if="tier"
        class="medal"
        :class="[`medal-${tier}`, { 'medal-lacquered': isLacquer, 'medal-ascend': ascending }]"
        :style="medalStyle"
        aria-hidden="true"
      >
        <span v-if="ascending" class="medal-rays" />
        <svg v-if="tier === 'shimenawa'" class="medal-rope" viewBox="0 0 100 100">
          <defs>
            <radialGradient :id="`${uid}-straw`" cx="35%" cy="30%" r="75%">
              <stop offset="0" stop-color="#fffbe8" />
              <stop offset="0.45" stop-color="#ecd48e" />
              <stop offset="1" stop-color="#a98336" />
            </radialGradient>
          </defs>
          <circle cx="50" cy="50" r="42" fill="none" stroke="#5c4316" stroke-width="11" />
          <ellipse
            v-for="(strand, i) in ROPE_STRANDS"
            :key="i"
            :cx="strand.cx"
            :cy="strand.cy"
            rx="8.4"
            ry="4.4"
            :transform="`rotate(${strand.rotate} ${strand.cx} ${strand.cy})`"
            :fill="`url(#${uid}-straw)`"
            stroke="#6b4f1d"
            stroke-width="0.8"
          />
        </svg>
        <div class="coin" :class="{ 'coin-spin': spinning }" @animationend.self="spinning = false">
          <span v-for="z in EDGE_LAYERS" :key="z" class="coin-edge" :style="{ transform: `translateZ(${z}px)` }" />
          <div
            v-for="side in spinning ? ['back', 'front'] : ['front']"
            :key="side"
            class="coin-side"
            :class="`coin-${side}`"
            :style="tier === 'jade' && side === 'back' ? { '--coin-clip': MAGATAMA_CLIP_BACK } : undefined"
          >
            <div class="medal-face">
              <svg v-if="isLacquer" class="medal-art" viewBox="0 0 100 100">
                <rect
                  v-for="(f, i) in LACQUER_FLECKS"
                  :key="i"
                  :x="f.x"
                  :y="f.y"
                  :width="f.size"
                  :height="f.size"
                  :opacity="f.opacity"
                  :transform="`rotate(${f.rotate} ${f.x + f.size / 2} ${f.y + f.size / 2})`"
                  style="fill: var(--m-light)"
                />
              </svg>
              <svg v-if="tier === 'ichibu'" class="medal-art" :viewBox="`0 0 ${ICHIBU_BOX.w} ${ICHIBU_BOX.h}`">
                <rect x="7.5" y="7.5" :width="ICHIBU_BOX.w - 15" :height="ICHIBU_BOX.h - 15" rx="1" fill="none" stroke-width="0.6" style="stroke: var(--m-dark)" />
                <g v-for="(f, i) in ICHIBU_FLOWERS" :key="i" :transform="`translate(${f.x} ${f.y})`">
                  <circle v-for="(p, k) in SAKURA_PETALS" :key="k" :cx="p.cx" :cy="p.cy" r="0.95" stroke-width="0.25" style="fill: var(--m-light); stroke: var(--m-dark)" />
                  <circle r="0.45" style="fill: var(--m-dark)" />
                </g>
              </svg>
              <template v-if="tier === 'zeni'">
                <span v-for="m in ['t', 'r', 'b', 'l']" :key="m" class="zeni-mark" :class="`zeni-mark-${m}`" />
              </template>
              <span v-if="tier === 'koban'" class="koban-stamp" />
              <span v-if="tier === 'jade'" class="magatama-hole" />
              <span class="medal-glyph-slot">
                <Icon v-if="kind.icon" :name="kind.icon" size="1.5rem" class="medal-glyph" />
                <span v-else lang="ja" class="medal-glyph ja-general text-2xl font-bold leading-none">{{ kind.glyph }}</span>
              </span>
              <span v-if="tier === 'koban'" class="koban-stamp" />
            </div>
            <span v-if="side === 'front'" class="medal-shine" />
          </div>
          <span v-if="tier === 'ema'" class="ema-cord" />
        </div>
        <template v-if="tier === 'shimenawa'">
          <span class="medal-halo" />
          <span class="medal-halo medal-halo-b" />
        </template>
        <template v-if="ascending">
          <span
            v-for="(p, i) in ASCEND_PARTICLES"
            :key="i"
            class="medal-particle"
            :class="p.petal ? 'medal-particle-petal' : 'medal-particle-leaf'"
            :style="{ '--dx': `${p.dx}px`, '--dy': `${p.dy}px`, '--rot': `${p.rotate}deg`, animationDelay: `${p.delay}ms` }"
          />
        </template>
        <template v-if="tier === 'crystal'">
          <span class="medal-sparkle medal-sparkle-a" />
          <span class="medal-sparkle medal-sparkle-b" />
        </template>
      </div>
      <div
        v-else
        class="flex h-16 w-16 items-center justify-center rounded-full border-[3px] border-dashed border-gray-300 text-2xl font-bold text-gray-400 dark:border-gray-600 dark:text-gray-500"
        aria-hidden="true"
      >
        <Icon v-if="kind.icon" :name="kind.icon" size="1.5rem" />
        <span v-else lang="ja" class="ja-general leading-none">{{ kind.glyph }}</span>
      </div>
      </div>
      <span class="line-clamp-2 min-h-[2lh] text-xs leading-snug">{{ valueLabel }}</span>
      <div v-if="nextName" class="h-1 w-full max-w-16 overflow-hidden rounded-full bg-gray-200 dark:bg-gray-700" aria-hidden="true">
        <div class="medal-bar h-full rounded-full" :class="tier ? `medal-${tier}` : 'bg-gray-400'" :style="{ width: `${progress}%` }" />
      </div>
      <span class="sr-only">{{ tooltip }}</span>
    </div>
  </Tooltip>
</template>

<style scoped>
  /** Tier classes carry only custom properties, so the progress bar can share them without picking up medal layout. */
  .medal-ema {
    --m-light: #e2c393;
    --m-base: #b58550;
    --m-dark: #6b4523;
    --medal-h: 3.75rem;
    --coin-radius: 0;
    --coin-clip: polygon(50% 0, 100% 30%, 100% 100%, 0 100%, 0 30%);
  }

  .medal-zeni {
    --m-light: #f3bc8e;
    --m-base: #c0743a;
    --m-dark: #73401c;
  }

  .medal-ichibu {
    --m-light: #f8fafc;
    --m-base: #aab5c3;
    --m-dark: #5f6b7c;
    --medal-w: 2.9rem;
    --medal-h: 4.3rem;
    --coin-radius: 5px;
  }

  .medal-koban {
    --m-light: #fff3b0;
    --m-base: #e0ac12;
    --m-dark: #8a6200;
    --medal-w: 3.25rem;
    --medal-h: 4.5rem;
  }

  .medal-jade {
    --m-light: #cdf0dd;
    --m-base: #56ad86;
    --m-dark: #1f6a4b;
    --coin-radius: 0;
  }

  .medal-crystal {
    --m-light: #ffffff;
    --m-base: #9fb6d6;
    --m-dark: #7d8fa8;
    --coin-radius: 0;
    --coin-clip: polygon(50% 0, 93% 25%, 93% 75%, 50% 100%, 7% 75%, 7% 25%);
  }

  .medal-lacquer,
  .medal-shimenawa {
    --m-light: #f3d77e;
    --m-base: #c9a13b;
    --m-dark: #6e5212;
  }

  .medal-shimenawa {
    --rope-overflow: 9px;
  }

  .medal-bar {
    background: var(--m-base);
  }

  /** Rests tilted back so the coin's real edge shows below the face; the tilt is what gives it depth when it spins. */
  .medal {
    --coin-rest: rotateX(16deg);
    --coin-rim: conic-gradient(from 210deg, var(--m-light), var(--m-base), var(--m-dark), var(--m-base), var(--m-light), var(--m-base), var(--m-dark), var(--m-base), var(--m-light));
    position: relative;
    width: var(--medal-w, 4rem);
    height: var(--medal-h, 4rem);
    perspective: 260px;
  }

  .medal::after {
    content: '';
    position: absolute;
    left: 14%;
    right: 14%;
    bottom: -7px;
    height: 8px;
    border-radius: 50%;
    background: rgb(0 0 0 / 0.28);
    filter: blur(3px);
    transition: transform 200ms ease;
  }

  .medal-card:is(:hover, :focus-visible) .medal::after {
    transform: scaleX(0.9);
  }

  .coin {
    position: relative;
    height: 100%;
    width: 100%;
    transform-style: preserve-3d;
    transform: var(--coin-rest);
    transition: transform 200ms ease;
  }

  .medal-card:is(:hover, :focus-visible) .coin {
    transform: translateY(-3px) var(--coin-rest);
  }

  .coin-edge {
    position: absolute;
    inset: 0;
    border-radius: var(--coin-radius, 50%);
    clip-path: var(--coin-clip, none);
    background: color-mix(in srgb, var(--m-base), var(--m-dark) 45%);
  }

  .coin-side {
    position: absolute;
    inset: 0;
    border-radius: var(--coin-radius, 50%);
    clip-path: var(--coin-clip, none);
    padding: 5px;
    backface-visibility: hidden;
    background: var(--coin-rim);
  }

  .coin-front {
    transform: translateZ(var(--coin-half));
  }

  .coin-back {
    transform: rotateY(180deg) translateZ(var(--coin-half));
  }

  .medal-face {
    position: relative;
    display: flex;
    height: 100%;
    width: 100%;
    align-items: center;
    justify-content: center;
    border-radius: var(--coin-radius, 50%);
    color: var(--m-dark);
    background: radial-gradient(circle at 35% 30%, var(--m-light), var(--m-base) 70%, color-mix(in srgb, var(--m-base), var(--m-dark) 40%));
    box-shadow:
      inset 0 2px 3px rgb(0 0 0 / 0.35),
      inset 0 -1px 2px rgb(255 255 255 / 0.45);
  }

  .medal-glyph-slot {
    position: relative;
    display: flex;
  }

  /** Engraved glyph: a light lip under the stroke; drop-shadow rather than text-shadow so the streak SVG matches. */
  .medal-glyph {
    position: relative;
    filter: drop-shadow(0 1px 0 rgb(255 255 255 / 0.55));
  }

  .medal-art {
    position: absolute;
    inset: 0;
    width: 100%;
    height: 100%;
  }

  .medal-shine {
    position: absolute;
    inset: 0;
    border-radius: var(--coin-radius, 50%);
    overflow: hidden;
    pointer-events: none;
  }

  .medal-shine::after {
    content: '';
    position: absolute;
    top: -50%;
    left: -60%;
    width: 40%;
    height: 200%;
    background: linear-gradient(90deg, transparent, rgb(255 255 255 / 0.55), transparent);
    transform: rotate(25deg);
    opacity: 0;
  }

  .medal-ema .medal-shine,
  .medal-zeni .medal-shine {
    display: none;
  }

  /** The sweep plays once per hover or focus, never on a loop. */
  .medal-card:is(:hover, :focus-visible) .medal-shine::after {
    animation: medal-sweep 900ms ease-out;
  }

  .medal-ema .coin-side {
    background: repeating-linear-gradient(100deg, rgb(70 40 15 / 0.22) 0 1.5px, transparent 1.5px 5px), var(--m-base);
  }

  .medal-ema .coin-side::before {
    content: '';
    position: absolute;
    inset: 0;
    z-index: 1;
    clip-path: polygon(50% 0, 100% 30%, 100% 41%, 50% 11%, 0 41%, 0 30%);
    background: var(--m-dark);
  }

  .medal-ema .medal-face {
    clip-path: var(--coin-clip);
    border-radius: 0;
    padding-top: 22%;
    color: #2b1a0e;
    background: repeating-linear-gradient(95deg, rgb(90 55 20 / 0.12) 0 1px, transparent 1px 4px), color-mix(in srgb, var(--m-light), white 30%);
    box-shadow: none;
  }

  .medal-ema .medal-glyph {
    filter: none;
  }

  .ema-cord {
    position: absolute;
    top: -10px;
    left: 50%;
    width: 16px;
    height: 14px;
    margin-left: -8px;
    border: 2px solid #c0392b;
    border-bottom: 0;
    border-radius: 8px 8px 0 0;
    transform: translateZ(calc(var(--coin-half) + 0.5px));
  }

  .medal-zeni .medal-face {
    background:
      radial-gradient(circle at 70% 75%, rgb(70 120 95 / 0.25), transparent 30%),
      radial-gradient(circle at 25% 65%, rgb(70 120 95 / 0.18), transparent 22%),
      radial-gradient(circle at 35% 30%, var(--m-light), var(--m-base) 65%, color-mix(in srgb, var(--m-base), var(--m-dark) 45%));
  }

  .medal-zeni .medal-glyph-slot {
    width: 50%;
    aspect-ratio: 1;
    align-items: center;
    justify-content: center;
    border: 2px solid color-mix(in srgb, var(--m-base), var(--m-dark) 55%);
    background: color-mix(in srgb, var(--m-base), var(--m-dark) 30%);
    box-shadow:
      inset 0 1px 2px rgb(0 0 0 / 0.45),
      0 1px 0 rgb(255 255 255 / 0.35);
  }

  .medal-zeni .medal-glyph {
    color: var(--m-light);
    font-size: 1.05rem;
    filter: drop-shadow(0 1px 0 rgb(0 0 0 / 0.4));
  }

  .zeni-mark {
    position: absolute;
    width: 7px;
    height: 3px;
    border-radius: 1px;
    background: color-mix(in srgb, var(--m-base), var(--m-light) 35%);
    box-shadow:
      0 1px 0 color-mix(in srgb, var(--m-dark) 70%, transparent),
      0 -1px 0 rgb(255 255 255 / 0.3);
  }

  .zeni-mark-t {
    top: 11%;
    left: calc(50% - 3.5px);
  }

  .zeni-mark-b {
    bottom: 11%;
    left: calc(50% - 3.5px);
  }

  .zeni-mark-l,
  .zeni-mark-r {
    top: calc(50% - 1.5px);
  }

  .zeni-mark-l {
    left: 11%;
  }

  .zeni-mark-r {
    right: 11%;
  }

  .medal-ichibu .coin-side {
    padding: 3px;
  }

  .medal-ichibu .medal-face {
    border-radius: 3px;
  }

  .medal-ichibu .medal-glyph {
    font-size: 1.3rem;
  }

  .medal-koban .coin-side {
    padding: 3px;
    background:
      repeating-conic-gradient(rgb(255 255 255 / 0.22) 0deg 4deg, rgb(0 0 0 / 0.1) 4deg 8deg),
      var(--coin-rim);
  }

  .medal-koban .medal-face {
    flex-direction: column;
    gap: 4px;
    background:
      repeating-linear-gradient(0deg, rgb(120 80 0 / 0.18) 0 1px, transparent 1px 3.5px),
      radial-gradient(ellipse at 35% 30%, var(--m-light), var(--m-base) 70%, color-mix(in srgb, var(--m-base), var(--m-dark) 40%));
  }

  .medal-koban .medal-glyph-slot {
    padding: 3px 2px;
    border: 1.5px solid color-mix(in srgb, var(--m-dark) 75%, transparent);
    border-radius: 3px;
    background: color-mix(in srgb, var(--m-base), var(--m-light) 35%);
    box-shadow: inset 0 1px 2px rgb(0 0 0 / 0.3);
  }

  .medal-koban .medal-glyph {
    font-size: 1.25rem;
  }

  .koban-stamp {
    width: 9px;
    height: 9px;
    border-radius: 50%;
    border: 1.5px solid color-mix(in srgb, var(--m-dark) 75%, transparent);
    box-shadow:
      inset 0 1px 1px rgb(0 0 0 / 0.3),
      0 1px 0 rgb(255 255 255 / 0.4);
  }

  .medal-jade .coin-side {
    padding: 0;
  }

  .medal-jade .medal-face {
    border-radius: 0;
    padding: 8px 0 0;
    background:
      radial-gradient(ellipse 18% 10% at 21% 9%, rgb(255 255 255 / 0.55), transparent),
      radial-gradient(circle at 62% 58%, rgb(255 255 255 / 0.3), transparent 16%),
      radial-gradient(circle at 22% 20%, rgb(255 255 255 / 0.25), transparent 10%),
      radial-gradient(circle at 50% 66%, rgb(255 255 255 / 0.2), transparent 9%),
      radial-gradient(circle at 44% 80%, rgb(10 60 35 / 0.3), transparent 14%),
      radial-gradient(circle at 70% 30%, rgb(10 60 35 / 0.22), transparent 12%),
      radial-gradient(circle at 80% 72%, rgb(0 0 0 / 0.25), transparent 30%),
      radial-gradient(circle at 38% 30%, var(--m-light), var(--m-base) 50%, var(--m-dark) 105%);
  }

  .magatama-hole {
    position: absolute;
    top: 23%;
    left: 33%;
    width: 7px;
    height: 7px;
    margin: -3.5px 0 0 -3.5px;
    border-radius: 50%;
    background: #0f2a1d;
    box-shadow:
      inset 0 1px 2px rgb(0 0 0 / 0.8),
      0 1px 0 rgb(255 255 255 / 0.35);
  }

  .medal-jade .coin-back .magatama-hole {
    left: 67%;
  }

  /** Carved in pale inlay: a dark glyph disappears into mid-green jade. */
  .medal-jade .medal-glyph {
    color: #f1fbf5;
    font-size: 1.4rem;
    filter: drop-shadow(0 1px 1px rgb(10 50 30 / 0.75));
  }

  .medal.medal-crystal::before {
    content: '';
    position: absolute;
    inset: 6px;
    border-radius: 50%;
    box-shadow: 0 0 18px rgb(185 210 255 / 0.85);
  }

  .medal-crystal .coin-edge {
    background: #aebfd6;
  }

  .medal-crystal .coin-side {
    padding: 3px;
    background: conic-gradient(from 210deg, #ffffff, #c4d3e6, #8a9cb6, #c4d3e6, #ffffff, #c4d3e6, #8a9cb6, #c4d3e6, #ffffff);
  }

  .medal-crystal .medal-face {
    clip-path: var(--coin-clip);
    border-radius: 0;
    color: #46597a;
    background: conic-gradient(#ffffff 0 16.67%, #c9d7f0 16.67% 33.33%, #eef2fc 33.33% 50%, #adc0e0 50% 66.67%, #e9e4f7 66.67% 83.33%, #bfd0ec 83.33% 100%);
    box-shadow: none;
  }

  .medal-crystal .medal-face::before {
    content: '';
    position: absolute;
    inset: 24%;
    clip-path: var(--coin-clip);
    background: rgb(255 255 255 / 0.7);
  }

  .medal-crystal .medal-glyph {
    filter: drop-shadow(0 0 2px #fff) drop-shadow(0 1px 0 #fff);
  }

  .medal-sparkle {
    position: absolute;
    background: #fff;
    clip-path: polygon(50% 0, 62% 38%, 100% 50%, 62% 62%, 50% 100%, 38% 62%, 0 50%, 38% 38%);
    filter: drop-shadow(0 0 2px var(--m-base));
    pointer-events: none;
  }

  .medal-sparkle-a {
    top: -3px;
    right: 1px;
    width: 14px;
    height: 14px;
  }

  .medal-sparkle-b {
    bottom: 4px;
    left: -2px;
    width: 9px;
    height: 9px;
  }

  .medal-card:is(:hover, :focus-visible) .medal-sparkle {
    animation: medal-twinkle 900ms ease-out;
  }

  .medal-card:is(:hover, :focus-visible) .medal-sparkle-b {
    animation-delay: 150ms;
  }

  .medal-lacquered .coin-edge {
    background: #7d1a12;
  }

  .medal-lacquered .coin-side {
    padding: 3px;
  }

  .medal-lacquered .medal-face {
    color: var(--m-light);
    background: radial-gradient(circle at 32% 28%, #3d2c2a, #140d0c 55%, #050303);
    box-shadow:
      inset 0 0 0 1px rgb(0 0 0 / 0.6),
      inset 0 2px 4px rgb(0 0 0 / 0.6);
  }

  .medal-lacquered .medal-face::after {
    content: '';
    position: absolute;
    top: 7%;
    left: 14%;
    width: 50%;
    height: 30%;
    border-radius: 50%;
    background: radial-gradient(closest-side, rgb(255 255 255 / 0.24), transparent);
    transform: rotate(-20deg);
    pointer-events: none;
  }

  .medal-lacquered .medal-glyph {
    filter: drop-shadow(0 0 1px rgb(0 0 0 / 0.9)) drop-shadow(0 0 3px color-mix(in srgb, var(--m-base) 45%, transparent));
  }

  /** The rope overflows the slot into the surrounding padding, so the top rank is the largest medal without growing its row. */
  .medal.medal-shimenawa {
    margin-bottom: 2px;
  }

  .medal.medal-shimenawa::before {
    content: '';
    position: absolute;
    inset: -20px;
    border-radius: 50%;
    background: radial-gradient(closest-side, rgb(255 214 120 / 0.5), rgb(255 200 90 / 0.2) 62%, transparent);
    pointer-events: none;
  }

  .medal-rope {
    position: absolute;
    inset: calc(var(--rope-overflow) * -1);
    width: calc(100% + var(--rope-overflow) * 2);
    height: calc(100% + var(--rope-overflow) * 2);
    filter: drop-shadow(0 2px 2px rgb(0 0 0 / 0.35)) drop-shadow(0 0 6px rgb(255 205 95 / 0.7));
  }

  .medal-card:is(:hover, :focus-visible) .medal-rope {
    animation: rope-sway 1400ms ease-out;
  }

  /** The top rank stays grounded: no lift or flip, only its own blessing of light. */
  .medal-card:is(:hover, :focus-visible) .medal-shimenawa .coin {
    transform: var(--coin-rest);
  }

  .medal-card:is(:hover, :focus-visible) .medal-shimenawa::after {
    transform: none;
  }

  .medal-halo {
    position: absolute;
    inset: 0;
    border-radius: 50%;
    opacity: 0;
    pointer-events: none;
    box-shadow:
      0 0 0 2px rgb(255 221 130 / 0.9),
      0 0 14px 2px rgb(255 200 90 / 0.7);
  }

  .medal-card:is(:hover, :focus-visible) .medal-halo {
    animation: medal-halo 1200ms ease-out;
  }

  .medal-card:is(:hover, :focus-visible) .medal-halo-b {
    animation-delay: 280ms;
  }

  .medal-ascend .medal-glyph {
    animation: glyph-flare var(--ascend-ms) ease-in-out;
  }

  .medal.medal-ascend::before {
    animation: aura-swell var(--ascend-ms) ease-in-out;
  }

  .medal-ascend .medal-halo {
    animation: medal-halo 1200ms ease-out 450ms;
  }

  .medal-ascend .medal-halo-b {
    animation-delay: 750ms;
  }

  .medal-ascend .medal-shine::after {
    animation: medal-sweep 900ms ease-out 600ms;
  }

  .medal-rays {
    position: absolute;
    inset: -30px;
    border-radius: 50%;
    pointer-events: none;
    opacity: 0;
    background: repeating-conic-gradient(rgb(255 220 130 / 0.6) 0deg 5deg, transparent 5deg 18deg);
    mask: radial-gradient(closest-side, #000 30%, transparent 100%);
    animation: medal-rays 1700ms ease-out 150ms;
  }

  .medal-particle {
    position: absolute;
    top: 50%;
    left: 50%;
    opacity: 0;
    pointer-events: none;
    animation: medal-particle 1500ms ease-out both;
  }

  .medal-particle-petal {
    width: 7px;
    height: 9px;
    border-radius: 50% 50% 50% 0 / 60% 60% 40% 40%;
    background: #ffc4d6;
    box-shadow: inset 0 -1px 1px rgb(220 120 150 / 0.5);
  }

  .medal-particle-leaf {
    width: 5px;
    height: 5px;
    background: #f6d36b;
    box-shadow: 0 0 4px rgb(255 210 100 / 0.9);
  }

  .coin.coin-spin {
    animation: coin-spin 1000ms cubic-bezier(0.3, 0, 0.2, 1);
  }

  @keyframes coin-spin {
    from {
      transform: translateY(-3px) var(--coin-rest) rotateY(0deg);
    }
    to {
      transform: translateY(-3px) var(--coin-rest) rotateY(360deg);
    }
  }

  @keyframes medal-sweep {
    0% {
      left: -60%;
      opacity: 1;
    }
    100% {
      left: 130%;
      opacity: 1;
    }
  }

  @keyframes medal-twinkle {
    0%,
    100% {
      transform: scale(1) rotate(0deg);
    }
    50% {
      transform: scale(1.4) rotate(45deg);
    }
  }

  @keyframes glyph-flare {
    0%,
    100% {
      filter: drop-shadow(0 0 1px rgb(0 0 0 / 0.9)) drop-shadow(0 0 3px rgb(201 161 59 / 0.45));
    }
    40%,
    60% {
      filter: drop-shadow(0 0 2px rgb(255 240 190)) drop-shadow(0 0 8px rgb(255 205 90));
    }
  }

  @keyframes aura-swell {
    0%,
    100% {
      transform: scale(1);
    }
    40% {
      transform: scale(1.4);
    }
  }

  @keyframes medal-rays {
    0% {
      opacity: 0;
      transform: scale(0.5) rotate(0deg);
    }
    30% {
      opacity: 1;
    }
    100% {
      opacity: 0;
      transform: scale(1.25) rotate(50deg);
    }
  }

  @keyframes medal-particle {
    0% {
      opacity: 0;
      transform: translate(-50%, -50%) scale(0.2) rotate(0deg);
    }
    15% {
      opacity: 1;
    }
    70% {
      opacity: 1;
      transform: translate(calc(-50% + var(--dx)), calc(-50% + var(--dy))) scale(1) rotate(var(--rot));
    }
    100% {
      opacity: 0;
      transform: translate(calc(-50% + var(--dx)), calc(-50% + var(--dy) + 12px)) scale(0.9) rotate(calc(var(--rot) * 1.4));
    }
  }

  @keyframes medal-halo {
    0% {
      opacity: 0.95;
      transform: scale(0.95);
    }
    100% {
      opacity: 0;
      transform: scale(1.75);
    }
  }

  @keyframes rope-sway {
    0%,
    100% {
      transform: rotate(0deg);
    }
    20% {
      transform: rotate(-5deg);
    }
    45% {
      transform: rotate(3deg);
    }
    70% {
      transform: rotate(-1deg);
    }
  }

  @media (prefers-reduced-motion: reduce) {
    .coin,
    .medal::after {
      transition: none;
    }

    .medal-card:is(:hover, :focus-visible) .coin {
      transform: var(--coin-rest);
    }

    .medal-card:is(:hover, :focus-visible) .medal-shine::after,
    .medal-card:is(:hover, :focus-visible) .medal-sparkle,
    .medal-card:is(:hover, :focus-visible) .medal-rope,
    .medal-card:is(:hover, :focus-visible) .medal-halo {
      animation: none;
    }
  }
</style>
