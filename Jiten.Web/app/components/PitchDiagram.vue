<script setup lang="ts">
  import { Line } from 'vue-chartjs';
  import { Chart as ChartJS, CategoryScale, LinearScale, PointElement, LineElement, Tooltip, type ChartOptions, type ChartData } from 'chart.js';
  import type { Context } from 'chartjs-plugin-datalabels';
  import ChartDataLabels from 'chartjs-plugin-datalabels'; // Import datalabels plugin and Context type
  import { hatsuon, getPitchPatternName } from 'hatsuon/dist/index.es';
  import { pitchCategory, type PitchCategory } from '~/utils/pitchAccent';

  ChartJS.register(CategoryScale, LinearScale, PointElement, LineElement, Tooltip, ChartDataLabels);

  const COLORS = {
    white: '#fff',
  };

  const props = defineProps({
    reading: {
      type: String,
      default: '',
    },
    pitchAccent: {
      type: Number,
      default: -1,
    },
  });

  // The 500 shades of the .pitch-* colours in main.css, which read on both themes.
  const colors: Record<PitchCategory, string> = {
    heiban: '#3b82f6',
    atamadaka: '#ef4444',
    nakadaka: '#f97316',
    odaka: '#22c55e',
  };
  const UNKNOWN_COLOUR = '#cccccc';

  const showMora = true;
  const showLabel = false;

  const hatsuonResult = computed(() => {
    if (props.reading && props.pitchAccent !== -1) {
      try {
        const regex = /[\u4E00-\u9FFF\u3400-\u4DBF\uFF10-\uFF5A[\]A-Za-z0-9]/g;
        const reading = props.reading.replace(regex, '');

        return hatsuon({ reading: reading, pitchNum: props.pitchAccent });
      } catch (error) {
        console.error('Error calculating hatsuon:', error);
        return null;
      }
    }
    return null;
  });

  const effectiveMorae = computed(() => hatsuonResult.value?.morae);
  const patternName = computed(() => hatsuonResult.value?.patternName as string | undefined);

  const patternNameEn = computed(() => {
    try {
      return getPitchPatternName(hatsuonResult.value.morae.length, props.pitchAccent, 'EN');
    } catch (error) {
      console.error('Error getting English pattern name:', error);
      return 'unknown';
    }
  });

  const lineChartData = computed(() => {
    return [...hatsuonResult.value.pattern];
  });

  const lineColor = computed(() => {
    const category = pitchCategory(props.pitchAccent, effectiveMorae.value?.length ?? 0);
    return category ? colors[category] : UNKNOWN_COLOUR;
  });

  // Dynamic chart width based on number of data points (morae + particle)
  const chartWidth = computed(() => lineChartData.value.length * 22);
  const chartHeight = 50;

  const chartData = computed<ChartData<'line'>>(() => {
    const labels = effectiveMorae.value.map((_, i) => `mora_${i}`);

    if (labels.length < lineChartData.value.length) {
      const diff = lineChartData.value.length - labels.length;
      for (let i = 0; i < diff; i++) {
        labels.push(`extra_${i}`);
      }
    } else if (labels.length > lineChartData.value.length) {
      labels.length = lineChartData.value.length;
    }

    const pointBgColors = lineChartData.value.map((_, index) => {
      // Last point (particle) should have a white background
      const isParticle = index === lineChartData.value.length - 1;

      if (isParticle) {
        return COLORS.white;
      }
      return lineColor.value;
    });

    const pointBorderColors = lineChartData.value.map((_, index) => {
      return lineColor.value;
    });

    return {
      labels: labels,
      datasets: [
        {
          data: lineChartData.value,
          borderColor: lineColor.value,
          borderWidth: 2,
          pointBackgroundColor: pointBgColors,
          pointBorderColor: pointBorderColors,
          pointRadius: 4,
          pointHoverRadius: 5,
          pointBorderWidth: 2,
          tension: 0.1,
          fill: false,
        },
      ],
    };
  });

  const chartOptions = computed<ChartOptions<'line'>>(() => {
    return {
      responsive: true,
      maintainAspectRatio: false,
      layout: {
        padding: {
          top: 5,
          left: 12,
        },
      },
      scales: {
        x: {
          display: false,
          grid: {
            display: false,
          },
        },
        y: {
          display: false,
          min: -0.3,
          max: 1.7,
          grid: {
            display: false,
          },
        },
      },
      plugins: {
        legend: {
          display: false,
        },
        tooltip: {
          enabled: false,
        },
        datalabels: {
          display: showMora, // Only display if showMora is true
          anchor: (context: Context) => {
            const dataValue = context.dataset.data[context.dataIndex] as number;
            return dataValue === 1 ? 'start' : 'end';
          },
          align: (context: Context) => {
            return 'end';
          },
          offset: (context: Context) => {
            const dataValue = context.dataset.data[context.dataIndex] as number;
            return dataValue === 1 ? '10' : '5';
          },
          formatter: (value: number, context: Context) => {
            return effectiveMorae.value[context.dataIndex] ?? '';
          },
          color: lineColor.value,
          font: (context: Context) => {
            const moraIndex = context.dataIndex;
            const label = effectiveMorae.value[moraIndex] ?? '';
            const size = 12;
            return {
              size: size,
              weight: 'bold',
              family: "'Noto Sans JP', sans-serif",
            };
          },
          padding: 0,
          // textStrokeColor: 'white',
          // textStrokeWidth: 0,
        },
      },
      animation: false,
      events: [],
    };
  });
</script>

<template>
  <div class="pitch-diagram-wrapper">
    <div :style="{ width: chartWidth + 'px', height: chartHeight + 'px' }">
      <Line v-if="chartData && chartOptions" :data="chartData" :options="chartOptions" />
      <div v-else>Loading chart...</div>
    </div>
    <div v-if="showLabel" class="pitch-label">
      <span lang="ja">{{ patternName }}</span>
      <small>({{ patternNameEn }})</small>
    </div>
  </div>
</template>

<style scoped>
  .pitch-diagram-wrapper {
    display: flex;
    flex-direction: column;
    align-items: center;
    padding: 0.6rem;
    min-width: 100px;
  }

  .pitch-label {
    display: flex;
    flex-direction: column;
    align-items: center;
    padding: 1rem 0.6rem 0;
    text-align: center;
    margin-top: -10px;
    font-size: 0.9em;
  }

  .pitch-label small {
    font-size: 0.8em;
    color: #666;
  }
</style>
