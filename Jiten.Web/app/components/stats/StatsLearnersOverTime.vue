<script setup lang="ts">
  import type { SiteStatsMonthPoint } from '~/types';

  defineProps<{
    points: SiteStatsMonthPoint[];
  }>();
</script>

<template>
  <StatsSection v-if="points.length > 1" title="Learners over time">
    <StatsRunningTotalChart :points="points" label="Learners" />
  </StatsSection>
</template>
