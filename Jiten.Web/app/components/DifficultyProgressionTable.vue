<script setup lang="ts">
  import { computed } from 'vue';
  import type { ProgressionSegmentDto } from '~/types';
  import { MediaType } from '~/types';
  import { getDifficultyTextClass, peakColour, formatDifficultyValue } from '~/utils/difficultyColours';

  const props = defineProps<{
    progression: ProgressionSegmentDto[];
    overallDifficulty: number;
    overallPeak: number;
    mediaType?: MediaType;
    isParentDeck?: boolean;
    usePercentage?: boolean;
  }>();

  const getMediaTypePrefix = (mediaType?: MediaType): string => {
    switch (mediaType) {
      case MediaType.Anime:
      case MediaType.Drama:
      case MediaType.Movie:
      case MediaType.Audio:
        return 'Ep';
      case MediaType.Novel:
      case MediaType.NonFiction:
      case MediaType.WebNovel:
      case MediaType.Manga:
        return 'Vol';
      case MediaType.VisualNovel:
      case MediaType.VideoGame:
        return 'Part';
      default:
        return '#';
    }
  };

  const tableRows = computed(() => {
    const sorted = [...props.progression].sort((a, b) => a.segment - b.segment);

    return sorted.map((s) => {
      let label: string;
      if (!props.isParentDeck || s.childStartOrder == null || s.childEndOrder == null) {
        label = `${s.segment * 10}%`;
      } else {
        const prefix = getMediaTypePrefix(props.mediaType);
        if (s.childStartOrder === s.childEndOrder) {
          label = `${prefix} ${s.childStartOrder}`;
        } else {
          label = `${prefix} ${s.childStartOrder}-${s.childEndOrder}`;
        }
      }

      return {
        label,
        difficulty: s.difficulty,
        peak: s.peak,
        difficultyClass: getDifficultyTextClass(s.difficulty),
      };
    });
  });
</script>

<template>
  <div class="overflow-x-auto">
    <table class="w-full text-sm">
      <thead>
        <tr class="border-b border-gray-200 dark:border-gray-700">
          <th class="py-2 px-3 text-left font-semibold text-gray-600 dark:text-gray-400">Progress</th>
          <th class="py-2 px-3 text-right font-semibold text-gray-600 dark:text-gray-400">Difficulty</th>
          <th class="py-2 px-3 text-right font-semibold" :style="{ color: peakColour }">Peak</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="row in tableRows" :key="row.label" class="border-b border-gray-100 dark:border-gray-800">
          <td class="py-2 px-3 text-gray-700 dark:text-gray-300">{{ row.label }}</td>
          <td class="py-2 px-3 text-right font-bold tabular-nums" :class="row.difficultyClass">
            {{ formatDifficultyValue(row.difficulty, usePercentage ?? false) }}
          </td>
          <td class="py-2 px-3 text-right font-bold tabular-nums" :style="{ color: peakColour }">
            {{ formatDifficultyValue(row.peak, usePercentage ?? false) }}
          </td>
        </tr>
        <tr class="bg-gray-50 dark:bg-gray-800 font-semibold">
          <td class="py-2 px-3 text-gray-700 dark:text-gray-300">Overall</td>
          <td class="py-2 px-3 text-right font-bold tabular-nums" :class="getDifficultyTextClass(overallDifficulty)">
            {{ formatDifficultyValue(overallDifficulty, usePercentage ?? false) }}
          </td>
          <td class="py-2 px-3 text-right font-bold tabular-nums" :style="{ color: peakColour }">
            {{ formatDifficultyValue(overallPeak, usePercentage ?? false) }}
          </td>
        </tr>
      </tbody>
    </table>
  </div>
</template>
