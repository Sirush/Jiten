<script setup lang="ts">
  defineOptions({ inheritAttrs: false });
</script>

<template>
  <Tooltip content="Jiten+ supporter">
    <span class="plus-mark" tabindex="0" v-bind="$attrs">
      <span class="plus-word">Jiten+</span>
      <span class="plus-sparkle plus-sparkle-a" aria-hidden="true" />
      <span class="plus-sparkle plus-sparkle-b" aria-hidden="true" />
    </span>
  </Tooltip>
</template>

<style scoped>
  .plus-mark {
    position: relative;
    display: inline-flex;
    align-items: center;
    line-height: 1;
    outline: none;
  }

  .plus-mark:focus-visible {
    outline: 2px solid rgb(255 255 255 / 0.8);
    outline-offset: 3px;
    border-radius: 4px;
  }

  .plus-word {
    font-weight: 800;
    letter-spacing: 0.02em;
    color: transparent;
    background-image:
      linear-gradient(110deg, transparent 38%, rgb(255 255 255 / 0.95) 46%, transparent 54%),
      linear-gradient(180deg, var(--p-primary-200), var(--p-primary-300) 55%, var(--p-primary-400));
    background-size:
      220% 100%,
      100% 100%;
    background-repeat: no-repeat;
    background-position:
      100% 0,
      0 0;
    background-clip: text;
    filter: drop-shadow(0 1px 0 rgb(0 0 0 / 0.55)) drop-shadow(0 0 6px color-mix(in srgb, var(--p-primary-300) 55%, transparent));
  }

  .plus-sparkle {
    position: absolute;
    width: 7px;
    height: 7px;
    background: #ffffff;
    clip-path: polygon(50% 0, 62% 38%, 100% 50%, 62% 62%, 50% 100%, 38% 62%, 0 50%, 38% 38%);
    filter: drop-shadow(0 0 2px var(--p-primary-300));
    opacity: 0;
    pointer-events: none;
  }

  .plus-sparkle-a {
    top: -5px;
    left: -5px;
  }

  .plus-sparkle-b {
    bottom: -4px;
    right: -6px;
    width: 5px;
    height: 5px;
  }

  /** One glint when the profile opens and again on hover, never on a loop. */
  .plus-word {
    animation: plus-glint 1400ms ease-in-out 500ms both;
  }

  .plus-sparkle {
    animation: plus-twinkle 900ms ease-out 900ms;
  }

  .plus-sparkle-b {
    animation-delay: 1100ms;
  }

  .plus-mark:hover .plus-word,
  .plus-mark:focus-visible .plus-word {
    animation: plus-glint-again 1200ms ease-in-out both;
  }

  .plus-mark:hover .plus-sparkle,
  .plus-mark:focus-visible .plus-sparkle {
    animation: plus-twinkle-again 900ms ease-out 200ms;
  }

  .plus-mark:hover .plus-sparkle-b,
  .plus-mark:focus-visible .plus-sparkle-b {
    animation-delay: 400ms;
  }

  @keyframes plus-glint {
    from {
      background-position:
        100% 0,
        0 0;
    }
    to {
      background-position:
        -100% 0,
        0 0;
    }
  }

  @keyframes plus-glint-again {
    from {
      background-position:
        100% 0,
        0 0;
    }
    to {
      background-position:
        -100% 0,
        0 0;
    }
  }

  @keyframes plus-twinkle {
    0%,
    100% {
      opacity: 0;
      transform: scale(0.4) rotate(0deg);
    }
    50% {
      opacity: 1;
      transform: scale(1.2) rotate(45deg);
    }
  }

  @keyframes plus-twinkle-again {
    0%,
    100% {
      opacity: 0;
      transform: scale(0.4) rotate(0deg);
    }
    50% {
      opacity: 1;
      transform: scale(1.2) rotate(45deg);
    }
  }

  @media (prefers-reduced-motion: reduce) {
    .plus-word,
    .plus-sparkle,
    .plus-mark:hover .plus-word,
    .plus-mark:focus-visible .plus-word,
    .plus-mark:hover .plus-sparkle,
    .plus-mark:focus-visible .plus-sparkle {
      animation: none;
    }
  }
</style>
