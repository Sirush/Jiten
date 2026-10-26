<script setup lang="ts">
  import { useToast } from 'primevue/usetoast';
  import type { CommunityListDetail } from '~/types/types';
  import { coverUrl } from '~/utils/coverImage';
  import { drawCommunityListShareCard } from '~/utils/communityListShareCard';
  import { getGenreText } from '~/utils/genreMapper';
  import { createBitmapLoader, currentExportPalette, saveCanvasPng } from '~/utils/imageExport';

  const props = defineProps<{ list: CommunityListDetail }>();

  const SHARE_ITEM_COUNT = 10;

  const toast = useToast();
  const localiseTitle = useLocaliseTitle();
  const isExporting = ref(false);
  const loadBitmap = createBitmapLoader();

  const topItems = computed(() => props.list.items.data.slice(0, SHARE_ITEM_COUNT));

  async function exportImage() {
    if (topItems.value.length === 0) return;
    isExporting.value = true;
    try {
      const [logo, covers] = await Promise.all([
        loadBitmap('/favicon-96x96.png'),
        Promise.all(topItems.value.map((item) => loadBitmap(coverUrl(item.deck.coverName, 'lg')))),
        document.fonts.ready,
      ]);

      const total = props.list.itemCount;
      const canvas = drawCommunityListShareCard({
        palette: currentExportPalette(),
        logo,
        name: props.list.name,
        byline: [props.list.ownerDisplayName ? `by ${props.list.ownerDisplayName}` : null, `${total} ${total === 1 ? 'title' : 'titles'}`]
          .filter(Boolean)
          .join(', '),
        genres: props.list.genres.map(getGenreText),
        items: topItems.value.map((item, index) => ({ title: localiseTitle(item.deck), cover: covers[index] ?? null })),
        url: `jiten.moe/lists/${props.list.listId}`,
        footRight: total > SHARE_ITEM_COUNT ? `Top ${SHARE_ITEM_COUNT} of ${total}` : '',
      });

      await saveCanvasPng(canvas, `jiten-list-${props.list.slug || props.list.listId}.png`, props.list.name);
    } catch {
      toast.add({ severity: 'error', summary: 'Could not create the image', life: 3000 });
    } finally {
      isExporting.value = false;
    }
  }
</script>

<template>
  <Button
    label="Save image"
    icon="pi pi-image"
    severity="secondary"
    text
    size="small"
    :loading="isExporting"
    :disabled="topItems.length === 0"
    @click="exportImage"
  />
</template>
