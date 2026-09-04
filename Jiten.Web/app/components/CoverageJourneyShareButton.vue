<script setup lang="ts">
  import { useToast } from 'primevue/usetoast';
  import type { Deck, CoverageJourney, JourneyPoint } from '~/types';
  import { createBitmapLoader, currentExportPalette, drawSeriesCard, saveCanvasPng } from '~/utils/imageExport';
  import type { CoverageScale } from '~/utils/coverageAxis';

  const props = defineProps<{
    deck: Deck;
    title: string;
    journey: CoverageJourney;
    metric: 'total' | 'unique';
    scale?: CoverageScale;
  }>();

  const toast = useToast();
  const isExporting = ref(false);
  const loadExportBitmap = createBitmapLoader();

  async function exportImage() {
    const journey = props.journey;
    if (!journey.points.length) return;

    const palette = currentExportPalette();
    isExporting.value = true;
    try {
      const [coverBitmap, logoBitmap] = await Promise.all([
        loadExportBitmap(coverUrl(props.deck.coverName)),
        loadExportBitmap('/favicon-96x96.png'),
        document.fonts.ready,
      ]);

      const unique = props.metric === 'unique';
      // The card is cropped to the same window the on-page headline quotes, so its two numbers, its
      // subtitle and the curve it draws all describe one span instead of three.
      const trend = journeyWindow(journey.points, (p) => (unique ? (p as JourneyPoint).uniqueCoverage : (p as JourneyPoint).coverage));
      const from = trend ? journey.points.findIndex((p) => p.date === trend.fromDate) : 0;
      const windowed = journey.points.slice(Math.max(from, 0));
      const mature = windowed.map((p) => (unique ? p.uniqueCoverage : p.coverage));
      const combined = windowed.map((p) => (unique ? p.combinedUniqueCoverage : p.combinedCoverage));
      const startLabel = formatBucketDated(windowed[0]!.date, journey.granularity);
      const axis = props.scale === 'full' ? { min: 0, max: 100 } : coverageWindow([...mature, ...combined]);
      // Whole percentages hide the journey once it spans less than a few points.
      const decimals = mature[mature.length - 1]! - mature[0]! < 5 ? 1 : 0;

      const canvas = drawSeriesCard({
        palette,
        logo: logoBitmap,
        kicker: 'COVERAGE JOURNEY',
        cover: { bitmap: coverBitmap, title: props.title },
        stat: `${mature[0]!.toFixed(decimals)}% → ${mature[mature.length - 1]!.toFixed(decimals)}%`,
        statSuffix: unique ? 'unique words known' : 'readable',
        subtitle: trend ? trend.overLabel : `${startLabel} to today`,
        line: mature,
        band: combined,
        min: axis.min,
        max: axis.max,
        footLeft: startLabel,
        footRight: 'Today',
      });

      await saveCanvasPng(canvas, `jiten-journey-${props.deck.deckId}.png`, `${props.title} coverage journey`);
    } catch {
      toast.add({ severity: 'error', summary: 'Could not create the image', life: 3000 });
    } finally {
      isExporting.value = false;
    }
  }
</script>

<template>
  <Tooltip content="Save as image">
    <Button
      icon="pi pi-image"
      severity="secondary"
      size="small"
      outlined
      :loading="isExporting"
      aria-label="Save your coverage journey as an image"
      @click="exportImage"
    />
  </Tooltip>
</template>
