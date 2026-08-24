<script setup lang="ts">
  import { useJitenStore } from '~/stores/jitenStore';
  import { applyTtsVolume } from '~/composables/useTts';
  import { DEFAULT_TTS_VOLUME, resolveTtsVolume } from '~/utils/ttsVolume';

  const emit = defineEmits<{ interactStart: []; interactEnd: [] }>();

  const store = useJitenStore();
  const inputId = useId();

  let lastAudible = DEFAULT_TTS_VOLUME;

  const percent = computed({
    get: () => Math.round(resolveTtsVolume(store.ttsVolume) * 100),
    set: (value: number) => setVolume(resolveTtsVolume(value / 100)),
  });

  const muted = computed(() => percent.value === 0);

  function setVolume(volume: number) {
    if (volume > 0) lastAudible = volume;
    store.ttsVolume = volume;
    applyTtsVolume(volume);
  }

  function toggleMute() {
    setVolume(muted.value ? lastAudible : 0);
  }
</script>

<template>
  <div class="flex flex-col gap-1" @focusin="emit('interactStart')" @focusout="emit('interactEnd')">
    <div class="flex items-center justify-between gap-2">
      <label :for="inputId" class="text-sm">TTS Volume</label>
      <span class="text-sm tabular-nums text-muted-color">{{ muted ? 'Muted' : `${percent}%` }}</span>
    </div>
    <div class="flex items-center gap-3">
      <button
        type="button"
        class="shrink-0 flex items-center justify-center w-7 h-7 rounded text-surface-500 dark:text-surface-400 hover:bg-surface-100 dark:hover:bg-surface-800 hover:text-surface-700 dark:hover:text-surface-200"
        :aria-label="muted ? 'Unmute text-to-speech' : 'Mute text-to-speech'"
        :aria-pressed="muted"
        @click="toggleMute"
      >
        <i class="pi text-base" :class="muted ? 'pi-volume-off' : 'pi-volume-up'" />
      </button>
      <Slider
        v-model="percent"
        :min="0"
        :max="100"
        :step="5"
        :input-id="inputId"
        aria-label="TTS volume"
        class="flex-1 !min-w-0"
        @pointerdown="emit('interactStart')"
        @slideend="emit('interactEnd')"
      />
    </div>
  </div>
</template>
