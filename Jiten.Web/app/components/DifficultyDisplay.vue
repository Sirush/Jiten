<script setup lang="ts">
  import { computed } from 'vue';
  import { useJitenStore } from '~/stores/jitenStore';
  import { DifficultyDisplayStyle, DifficultyValueDisplayStyle } from '~/types';
  import { difficultyNames, difficultyTextClasses, formatDifficultyValue } from '~/utils/difficultyColours';

  const props = defineProps<{
    difficulty: number;
    difficultyRaw?: number;
    difficultyAlgorithmic?: number;
    userAdjustment?: number;
    voteCount?: number;
    adjustmentConfidence?: number;
    useStars?: boolean;
  }>();

  const store = useJitenStore();

  const nameValues = difficultyNames;
  const starValues = ['★☆☆☆☆', '★★☆☆☆', '★★★☆☆', '★★★★☆', '★★★★★', '★★★★★'];

  const colorClasses = difficultyTextClasses;

  const effectiveRaw = computed(() => {
    return Math.min(Math.max(props.difficultyRaw ?? props.difficulty, 0), 5);
  });

  const effectiveBucket = computed(() => {
    const raw = effectiveRaw.value;
    return Math.min(Math.max(Math.floor(raw), 0), 5);
  });

  const usePercentage = computed(() => store.difficultyValueDisplayStyle === DifficultyValueDisplayStyle.Percentage);

  const difficultyValue = computed(() => formatDifficultyValue(effectiveRaw.value, usePercentage.value, 1));

  const difficultyText = computed(() => {
    const bucket = effectiveBucket.value;
    if (props.useStars) return starValues[bucket];
    switch (store.difficultyDisplayStyle) {
      case DifficultyDisplayStyle.Name:
        return nameValues[bucket];

      case DifficultyDisplayStyle.Value:
        return difficultyValue.value;

      case DifficultyDisplayStyle.NameAndValue:
        return `${nameValues[bucket]} (${difficultyValue.value})`;

      default:
        return '';
    }
  });

  const minAdjustment = 0.1;

  /// Below this the adjustment came from the calculator's single-voter path, which is too thin to call a confirmation.
  const fullGateVoters = 3;

  const hasAdjustment = computed(() => Math.abs(props.userAdjustment ?? 0) >= minAdjustment);

  /// Votes below the calculator's confidence gate carry a zero adjustment and must not read as agreement.
  const votesApplied = computed(() => (props.adjustmentConfidence ?? 0) > 0 || hasAdjustment.value);
  const hasVotesButSmallAdjustment = computed(() => (props.voteCount ?? 0) > 0 && votesApplied.value && !hasAdjustment.value);

  const arrowIndicator = computed(() => {
    const adj = props.userAdjustment ?? 0;
    if (!hasAdjustment.value) return '';
    const isLarge = Math.abs(adj) >= 0.5;
    return adj > 0 ? (isLarge ? '▲▲' : '▲') : (isLarge ? '▼▼' : '▼');
  });

  const arrowClass = computed(() => {
    const adj = props.userAdjustment ?? 0;
    if (!hasAdjustment.value) return '';
    return adj > 0 ? 'text-amber-500 dark:text-amber-400' : 'text-sky-500 dark:text-sky-400';
  });

  const rawBucket = computed(() => {
    const raw = props.difficultyAlgorithmic ?? props.difficultyRaw ?? props.difficulty;
    return Math.min(Math.max(Math.floor(raw), 0), 5);
  });

  const rawValue = computed(() => formatDifficultyValue(props.difficultyAlgorithmic ?? props.difficultyRaw ?? props.difficulty, usePercentage.value, 1));

  const tooltip = computed(() => {
    const parts: string[] = [];

    if (props.useStars) {
      parts.push(`${nameValues[effectiveBucket.value]} (${difficultyValue.value})`);
    } else {
      switch (store.difficultyDisplayStyle) {
        case DifficultyDisplayStyle.Name:
          parts.push(difficultyValue.value);
          break;
        case DifficultyDisplayStyle.Value:
          parts.push(nameValues[effectiveBucket.value]);
          break;
      }
    }

    if (hasAdjustment.value && props.difficultyRaw != null) {
      const adj = props.userAdjustment!;
      const sign = adj > 0 ? '+' : '';
      parts.push(`**Algorithmic:** ${rawValue.value} (${nameValues[rawBucket.value]})`);
      const adjDisplay = usePercentage.value ? `${(adj * 20).toFixed(0)}%` : adj.toFixed(1);
      parts.push(`**Community:** ${difficultyValue.value} (${nameValues[effectiveBucket.value]})  ${arrowIndicator.value} ${sign}${adjDisplay}`);
      if (props.voteCount && props.voteCount > 0) {
        parts.push(`Based on ${props.voteCount} vote${props.voteCount !== 1 ? 's' : ''}`);
      }
    } else if (hasVotesButSmallAdjustment.value) {
      const votes = props.voteCount ?? 0;
      const plural = votes !== 1 ? 's' : '';
      parts.push(votes >= fullGateVoters
        ? `Algorithmic score confirmed by ${votes} community vote${plural}`
        : `${votes} community vote${plural}, consistent with the algorithmic score`);
    } else if ((props.voteCount ?? 0) > 0) {
      parts.push('Algorithmic difficulty. Not enough community votes to adjust it yet.');
    } else {
      parts.push('Algorithmic difficulty. No community votes yet.');
    }

    return parts.join('\n');
  });

  const difficultyClass = computed(() => {
    const index = Math.min(Math.max(effectiveBucket.value, 0), colorClasses.length - 1);
    return colorClasses[index];
  });

  defineExpose({ tooltip });
</script>

<template>
  <span :class="['tabular-nums font-bold whitespace-nowrap', difficultyClass]">
    <span v-if="hasVotesButSmallAdjustment && !useStars" class="text-xs mr-0.5 text-gray-400 dark:text-gray-400">&asymp;</span>
    <span v-else-if="arrowIndicator && !useStars" :class="['text-xs mr-0.5', arrowClass]">{{ arrowIndicator }}</span>
    {{ difficultyText }}
    <span v-if="voteCount && voteCount >= 3" class="text-xs font-normal text-muted-color ml-1"></span>
  </span>
</template>

<style scoped></style>
