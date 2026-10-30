<script setup lang="ts">
  import { useToast } from 'primevue/usetoast';
  import type { KnowledgeGrowth, ProfileVocabularyStats, StudyHeatmapResponse, UserAccomplishment } from '~/types';
  import { createBitmapLoader, currentExportPalette, saveCanvasPng } from '~/utils/imageExport';
  import { drawProfileShareCard } from '~/utils/profileShareCard';

  const props = defineProps<{
    username: string;
  }>();

  const { $api } = useNuxtApp();
  const toast = useToast();
  const isExporting = ref(false);
  const loadExportBitmap = createBitmapLoader();

  /** Each part is optional on the card, so a section the profile hides or a failed request just leaves it out. */
  async function loadCardData() {
    const settle = <T,>(p: Promise<T>) => p.catch(() => null);
    const [vocabulary, growth, accomplishments, heatmap] = await Promise.all([
      settle($api<ProfileVocabularyStats>(profileApiPath(props.username, 'vocabulary-stats'))),
      settle($api<KnowledgeGrowth>('srs/knowledge-growth')),
      settle($api<UserAccomplishment[]>(profileApiPath(props.username, 'accomplishments'))),
      settle($api<StudyHeatmapResponse>(profileApiPath(props.username, 'study-heatmap'))),
    ]);
    return { vocabulary, growth, accomplishment: accomplishments?.find((a) => a.mediaType === null) ?? null, heatmap };
  }

  async function exportImage() {
    isExporting.value = true;
    try {
      const [logo, data] = await Promise.all([loadExportBitmap('/favicon-96x96.png'), loadCardData(), document.fonts.ready]);
      if (!data.vocabulary && !data.growth && !data.accomplishment && !data.heatmap) {
        toast.add({ severity: 'warn', summary: 'Nothing to put on the image yet', life: 3000 });
        return;
      }

      const canvas = drawProfileShareCard({
        username: props.username,
        ...data,
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
      aria-label="Save your profile as an image"
      @click="exportImage"
    />
  </Tooltip>
</template>
