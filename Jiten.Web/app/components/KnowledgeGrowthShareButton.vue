<script setup lang="ts">
  import { useToast } from 'primevue/usetoast';
  import type { KnowledgeGrowth } from '~/types';
  import { createBitmapLoader, currentExportPalette, drawSeriesCard, saveCanvasPng } from '~/utils/imageExport';

  const props = defineProps<{
    growth: KnowledgeGrowth;
  }>();

  const toast = useToast();
  const isExporting = ref(false);
  const loadExportBitmap = createBitmapLoader();

  async function exportImage() {
    const points = props.growth.points;
    if (!points.length) return;

    const palette = currentExportPalette();
    isExporting.value = true;
    try {
      const [logoBitmap] = await Promise.all([loadExportBitmap('/favicon-96x96.png'), document.fonts.ready]);

      const known = points.map((p) => p.knownWords);
      const combined = points.map((p) => p.knownWordsCombined);
      const startLabel = formatBucketDated(points[0]!.date, props.growth.granularity);

      const canvas = drawSeriesCard({
        palette,
        logo: logoBitmap,
        kicker: 'WORDS LEARNED OVER TIME',
        stat: (known[known.length - 1] ?? 0).toLocaleString(),
        statSuffix: 'words learned',
        subtitle: `since ${startLabel}`,
        line: known,
        band: combined,
        max: Math.max(...combined, 1),
        footLeft: startLabel,
        footRight: 'Today',
      });

      await saveCanvasPng(canvas, 'jiten-words-learned.png', 'My Japanese vocabulary over time');
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
      aria-label="Save your words-learned chart as an image"
      @click="exportImage"
    />
  </Tooltip>
</template>
