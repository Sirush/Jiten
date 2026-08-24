<script setup lang="ts">
  import { stripRuby } from '~/utils/stripRuby';

  const props = defineProps<{
    text?: string;
    frequencyRank?: number;
    partsOfSpeech?: string[];
    meanings?: string[];
    usedInMediaAmount?: number;
  }>();

  function extractReading(text: string): string {
    return text.replace(/([\u4E00-\u9FFF\uFF10-\uFF5A々]+)\[([\u3040-\u309F\u30A0-\u30FF]+)]/g, (_m, _k, f) => f);
  }

  const wordText = computed(() => (props.text ? stripRuby(props.text) : ''));

  const readingText = computed(() => {
    if (!props.text) return '';
    const reading = extractReading(props.text);
    return reading !== wordText.value ? reading : '';
  });

  const rankText = computed(() => {
    if (props.frequencyRank == null) return '';
    return props.frequencyRank === 0 ? 'Unranked' : `Rank #${props.frequencyRank.toLocaleString()}`;
  });

  const posText = computed(() => {
    if (!props.partsOfSpeech?.length) return '';
    return props.partsOfSpeech.join(', ');
  });

  const definitionText = computed(() => {
    if (!props.meanings?.length) return '';
    const text = props.meanings.join('; ');
    return text.length > 120 ? text.slice(0, 117) + '...' : text;
  });

  const mediaCountText = computed(() => {
    if (props.usedInMediaAmount == null) return '';
    return `Appears in ${props.usedInMediaAmount.toLocaleString()} media`;
  });
</script>

<template>
  <div
    v-if="wordText"
    style="width: 1200px; height: 630px; display: flex; flex-direction: column; font-family: 'Noto Sans JP', sans-serif; background: white; border-left: 6px solid #9333ea; padding: 32px 48px; justify-content: center; gap: 24px;"
  >
    <div style="display: flex; justify-content: space-between; align-items: center;">
      <span style="font-size: 1.8rem; font-weight: 700; color: #9333ea;">jiten.moe</span>
      <span style="font-size: 1.6rem; color: #6b7280;">{{ rankText }}</span>
    </div>

    <div style="display: flex; flex-direction: column; align-items: center; width: 100%;">
      <div
        v-if="readingText"
        style="font-size: 2.2rem; color: #9ca3af; width: 100%; text-align: center;"
      >
        {{ readingText }}
      </div>

      <div style="font-size: 5.5rem; font-weight: 700; line-height: 1.2; width: 100%; text-align: center;">{{ wordText }}</div>

      <div
        v-if="posText"
        style="font-size: 1.6rem; color: #9ca3af; margin-top: 8px; width: 100%; text-align: center;"
      >
        {{ posText }}
      </div>

      <div
        v-if="definitionText"
        style="font-size: 1.8rem; color: #4b5563; margin-top: 8px; width: 100%; text-align: center;"
      >
        {{ definitionText }}
      </div>
    </div>

    <div style="border-top: 1px solid #e5e7eb; padding-top: 12px;">
      <span style="font-size: 1.5rem; color: #6b7280;">{{ mediaCountText }}</span>
    </div>
  </div>

  <div
    v-else
    style="width: 1200px; height: 630px; display: flex; align-items: center; justify-content: center; font-family: 'Noto Sans JP', sans-serif; background: white;"
  >
    <span style="color: #6b7280;">Loading...</span>
  </div>
</template>
