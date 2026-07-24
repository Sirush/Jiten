<script setup lang="ts">
  import { useApiFetchPaginated } from '~/composables/useApiFetch';
  import type { SiteUpdate } from '~/types';
  import Card from 'primevue/card';
  import Skeleton from 'primevue/skeleton';
  import Button from 'primevue/button';
  import { useToast } from 'primevue/usetoast';

  const route = useRoute();
  const toast = useToast();

  const offset = computed(() => (route.query.offset ? Number(route.query.offset) : 0));

  const {
    data: response,
    status,
    error,
  } = await useApiFetchPaginated<SiteUpdate[]>('updates', {
    query: { offset: offset, limit: 10 },
    watch: [offset],
  });

  const { start, end, totalItems, previousLink, nextLink } = usePagination(response);

  const hasMultiplePages = computed(() => totalItems.value > (response.value?.pageSize ?? 10));

  const formatDate = (date: string) => new Date(date).toLocaleDateString(undefined, { year: 'numeric', month: 'long', day: 'numeric' });

  const wasEdited = (update: SiteUpdate) => !!update.updatedAt && new Date(update.updatedAt).getTime() > new Date(update.publishedAt).getTime();

  async function shareUpdate(update: SiteUpdate) {
    const url = `${window.location.origin}/updates#update-${update.id}`;

    // Only offer the native share sheet on touch devices; on desktop it opens an OS panel where
    // people expect a copied link.
    if (navigator.share && window.matchMedia('(pointer: coarse)').matches) {
      try {
        await navigator.share({ title: update.title, url });
        return;
      } catch {
        // Sheet dismissed or refused: fall through to copying.
      }
    }

    try {
      await navigator.clipboard.writeText(url);
      toast.add({ severity: 'success', summary: 'Copied', detail: 'Link copied to clipboard', life: 3000 });
    } catch {
      toast.add({ severity: 'error', summary: 'Error', detail: 'Could not copy the link', life: 3000 });
    }
  }

  useHead({
    title: "What's New",
    meta: [{ name: 'description', content: 'New features, improvements and fixes on Jiten.' }],
  });

  // Notification links point at /updates#update-{id}; the anchor only exists once the list has rendered.
  async function scrollToHash() {
    if (!route.hash) return;
    await nextTick();
    document.querySelector(route.hash)?.scrollIntoView({ behavior: 'smooth', block: 'start' });
  }

  onMounted(scrollToHash);
  watch(() => route.hash, scrollToHash);
</script>

<template>
  <div class="flex flex-col gap-4">
    <div>
      <h1 class="text-2xl font-bold">What's New</h1>
      <p class="text-surface-500 dark:text-surface-400">New features, improvements and fixes on Jiten.</p>
    </div>

    <div v-if="status === 'pending'" class="flex flex-col gap-4">
      <Card v-for="i in 3" :key="i" class="p-2">
        <template #content>
          <Skeleton width="100%" height="120px" />
        </template>
      </Card>
    </div>

    <div v-else-if="error">Error: {{ error }}</div>

    <div v-else class="flex flex-col gap-4">
      <div v-if="(response?.data?.length ?? 0) > 0" class="flex flex-col gap-4">
        <Card v-for="update in response!.data" :id="`update-${update.id}`" :key="update.id" class="p-2 scroll-mt-20">
          <template #content>
            <article class="grid gap-3 md:grid-cols-[11rem_1fr]">
              <div class="md:border-e md:border-surface-200 md:pe-4 md:dark:border-surface-700">
                <div class="md:sticky md:top-4">
                  <time :datetime="update.publishedAt" class="block text-sm font-medium text-surface-600 dark:text-surface-300">
                    {{ formatDate(update.publishedAt) }}
                  </time>
                  <span v-if="wasEdited(update)" class="text-xs text-surface-400 dark:text-surface-500"> edited {{ formatDate(update.updatedAt!) }} </span>
                </div>
              </div>

              <div class="min-w-0">
                <div class="mb-2 flex items-start gap-1">
                  <h2 class="text-xl font-bold">
                    <a :href="`#update-${update.id}`" class="text-inherit! no-underline! hover:text-primary-500!">{{ update.title }}</a>
                  </h2>
                  <Button
                    v-tooltip.top="'Copy link to this update'"
                    icon="pi pi-link"
                    text
                    rounded
                    size="small"
                    :aria-label="`Copy link to ${update.title}`"
                    @click="shareUpdate(update)"
                  />
                </div>
                <MarkdownBody :source="update.bodyMarkdown" />
              </div>
            </article>
          </template>
        </Card>
      </div>

      <div v-else class="text-center py-8 text-surface-500 dark:text-surface-400">No updates published yet.</div>

      <PaginationControls
        v-if="hasMultiplePages"
        :previous-link="previousLink"
        :next-link="nextLink"
        :start="start"
        :end="end"
        :total-items="totalItems"
        item-label="updates"
        :scroll-to-top-on-next="true"
      />
    </div>
  </div>
</template>
