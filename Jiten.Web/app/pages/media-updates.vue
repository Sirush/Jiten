<script setup lang="ts">
  import { useApiFetchPaginated } from '~/composables/useApiFetch';
  import { type Deck, MediaType } from '~/types';
  import Card from 'primevue/card';
  import Skeleton from 'primevue/skeleton';
  import Button from 'primevue/button';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { useToast } from 'primevue/usetoast';

  const route = useRoute();
  const localiseTitle = useLocaliseTitle();

  const offset = computed(() => (route.query.offset ? Number(route.query.offset) : 0));

  const url = computed(() => `media-deck/media-update-log`);

  const {
    data: response,
    status,
    error,
  } = await useApiFetchPaginated<Deck[]>(url, {
    query: {
      offset: offset,
    },
    watch: [offset],
  });

  const groupedByDay = computed(() => {
    if (!response.value?.data) return [];

    const groups: Record<string, Deck[]> = {};

    response.value.data.forEach((deck) => {
      // Format date as YYYY-MM-DD
      const date = new Date(deck.creationDate);
      const dateString = date.toISOString().split('T')[0];

      if (!groups[dateString]) {
        groups[dateString] = [];
      }

      groups[dateString].push(deck);
    });

    // Convert to array and sort by date (most recent first)
    return Object.entries(groups)
      .map(([date, decks]) => ({ date, decks }))
      .sort((a, b) => new Date(b.date).getTime() - new Date(a.date).getTime());
  });

  // Format for display
  const formatDate = (dateString: string) => {
    const date = new Date(dateString);
    return date.toLocaleDateString(undefined, {
      year: 'numeric',
      month: 'long',
      day: 'numeric',
    });
  };

  // Function to group decks by media type
  const groupDecksByMediaType = (decks: Deck[]) => {
    const decksByType: Record<MediaType, Deck[]> = {} as Record<MediaType, Deck[]>;

    decks.forEach((deck) => {
      if (!decksByType[deck.mediaType]) {
        decksByType[deck.mediaType] = [];
      }
      decksByType[deck.mediaType].push(deck);
    });

    return decksByType;
  };

  // Scroll to top when navigating between pages
  const scrollToTop = () => {
    nextTick(() => {
      window.scrollTo({ top: 0, behavior: 'instant' });
    });
  };

  const toast = useToast();

  const generateDiscordMarkdown = (date: string, decks: Deck[]) => {
    let markdown = `**${formatDate(date)}**\n\n`;

    // Group decks by media type
    const decksByType: Record<MediaType, Deck[]> = {} as Record<MediaType, Deck[]>;

    decks.forEach((deck) => {
      if (!decksByType[deck.mediaType]) {
        decksByType[deck.mediaType] = [];
      }
      decksByType[deck.mediaType].push(deck);
    });

    for (const [mediaType, typeDecks] of Object.entries(decksByType)) {
      markdown += `Added **${typeDecks.length}** ${getMediaTypeText(Number(mediaType))}${typeDecks.length > 1 ? 's' : ''}:\n`;

      for (const deck of typeDecks) {
        markdown += `- ${localiseTitle(deck)}\n`;
      }

      markdown += '\n';
    }

    return markdown;
  };

  const copyToClipboard = (text: string) => {
    navigator.clipboard
      .writeText(text)
      .then(() => {
        toast.add({ severity: 'success', summary: 'Copied', detail: 'Text copied to clipboard', life: 3000 });
      })
      .catch((err) => {
        console.error('Failed to copy text: ', err);
        toast.add({ severity: 'error', summary: 'Error', detail: 'Failed to copy text', life: 3000 });
      });
  };

  const authStore = useAuthStore();

  useHead({
    title: 'Media Updates',
    meta: [
      {
        name: 'description',
        content: "Here's the latest media added to Jiten",
      },
    ],
  });

  const previousLink = computed(() => {
    return offset.value > 0 ? { query: { ...route.query, offset: offset.value - 1 } } : null;
  });

  const nextLink = computed(() => {
    return response.value?.data.length > 0 ? { query: { ...route.query, offset: offset.value + 1 } } : null;
  });
</script>

<template>
  <div class="flex flex-col gap-4">
    <div class="flex items-center gap-3">
      <h1 class="text-2xl font-bold">Media Updates</h1>
      <NuxtLink v-if="authStore.isAuthenticated" to="/requests" class="text-sm hover:underline">Request Media</NuxtLink>
    </div>

    <div v-if="status === 'pending'" class="flex flex-col gap-4">
      <Card v-for="i in 5" :key="i" class="p-2">
        <template #content>
          <Skeleton width="100%" height="100px" />
        </template>
      </Card>
    </div>

    <div v-else-if="error">Error: {{ error }}</div>

    <div v-else>
      <div class="flex gap-8 pl-2 mb-4">
        <NuxtLink :to="previousLink" :class="previousLink == null ? '!text-gray-500 dark:text-gray-400 pointer-events-none' : ''" no-rel @click="scrollToTop">
          Previous
        </NuxtLink>
        <NuxtLink :to="nextLink" :class="nextLink == null ? '!text-gray-500 dark:text-gray-400 pointer-events-none' : ''" no-rel @click="scrollToTop">
          Next
        </NuxtLink>
      </div>

      <!-- Media updates grouped by day -->
      <div v-if="groupedByDay.length > 0" class="flex flex-col gap-6">
        <Card v-for="group in groupedByDay" :key="group.date" class="p-2">
          <template #content>
            <div class="flex flex-col gap-2">
              <!-- Date header with copy button -->
              <div class="flex items-center gap-2">
                <h2 class="text-xl font-bold">{{ formatDate(group.date) }}</h2>
                <Button
                  icon="pi pi-copy"
                  size="small"
                  text
                  aria-label="Copy day content"
                  tooltip="Copy in Discord format"
                  @click="copyToClipboard(generateDiscordMarkdown(group.date, group.decks))"
                />
              </div>

              <!-- Media types with their respective items -->
              <div v-for="(typeDecks, mediaType) in groupDecksByMediaType(group.decks)" :key="mediaType" class="mb-4">
                <div class="ml-4 mb-1">
                  Added
                  <strong>{{ typeDecks.length }}</strong>
                  {{ getMediaTypeText(Number(mediaType)) }}{{ typeDecks.length > 1 ? 's' : '' }}:
                </div>

                <!-- Media list for this type -->
                <ul class="list-disc ml-8">
                  <li v-for="deck in typeDecks" :key="deck.deckId" class="mb-1">
                    <NuxtLink :to="`/decks/media/${deck.deckId}/detail`" target="_blank" class="hover:underline">
                      {{ localiseTitle(deck) }}
                    </NuxtLink>
                  </li>
                </ul>
              </div>
            </div>
          </template>
        </Card>
      </div>

      <div v-else class="text-center py-8">No media updates found for this time period.</div>

      <div class="flex gap-8 pl-2 mt-4">
        <NuxtLink :to="previousLink" :class="previousLink == null ? '!text-gray-500 dark:text-gray-400 pointer-events-none' : ''" no-rel @click="scrollToTop">
          Previous
        </NuxtLink>
        <NuxtLink :to="nextLink" :class="nextLink == null ? '!text-gray-500 dark:text-gray-400 pointer-events-none' : ''" no-rel @click="scrollToTop">
          Next
        </NuxtLink>
      </div>
    </div>
  </div>
</template>

<style scoped></style>
