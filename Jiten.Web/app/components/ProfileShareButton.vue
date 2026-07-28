<script setup lang="ts">
  import { useToast } from 'primevue/usetoast';
  import type { KnowledgeGrowth, ProfileVocabularyStats, StudyHeatmapResponse, UserAccomplishment } from '~/types';
  import { createBitmapLoader, currentExportPalette, saveCanvasPng } from '~/utils/imageExport';
  import { drawProfileShareCard } from '~/utils/profileShareCard';

  const props = defineProps<{
    username: string;
    vocabulary: ProfileVocabularyStats | null;
    growth: KnowledgeGrowth | null;
    accomplishment: UserAccomplishment | null;
    heatmap: StudyHeatmapResponse | null;
  }>();

  const toast = useToast();
  const isExporting = ref(false);
  const loadExportBitmap = createBitmapLoader();

  const hasData = computed(() => Boolean(props.vocabulary || props.growth || props.accomplishment || props.heatmap));

  async function exportImage() {
    if (!hasData.value) return;

    isExporting.value = true;
    try {
      const [logo] = await Promise.all([loadExportBitmap('/favicon-96x96.png'), document.fonts.ready]);

      const canvas = drawProfileShareCard({
        username: props.username,
        vocabulary: props.vocabulary,
        growth: props.growth,
        accomplishment: props.accomplishment,
        heatmap: props.heatmap,
        logo,
        palette: currentExportPalette(),
        isDark: document.documentElement.classList.contains('dark-mode'),
      });

      await saveCanvasPng(canvas, `jiten-profile-${props.username}.png`, `${props.username} on Jiten`);
    } catch {
      toast.add({ severity: 'error', summary: 'Could not create the image', life: 3000 });
    } finally {
      isExporting.value = false;
    }
  }
</script>

<template>
  <Tooltip content="Save your profile as an image">
    <Button
      icon="pi pi-image"
      severity="secondary"
      size="small"
      outlined
      :loading="isExporting"
      :disabled="!hasData"
      aria-label="Save your profile as an image"
      @click="exportImage"
    />
  </Tooltip>
</template>
