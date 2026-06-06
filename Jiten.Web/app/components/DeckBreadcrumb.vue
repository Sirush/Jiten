<script setup lang="ts">
  import Breadcrumb from 'primevue/breadcrumb';
  import type { Deck } from '~/types';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';

  const props = defineProps<{
    deck: Deck;
    parentDeck?: Deck | null;
    // When set, the deck title becomes a link and this label is appended as the (unlinked) current page.
    current?: string;
  }>();

  const localiseTitle = useLocaliseTitle();

  const home = { icon: 'pi pi-home', route: '/' };

  const items = computed(() => {
    const list: { label?: string; route?: string; icon?: string }[] = [
      // Links to the searchable media browser pre-filtered by type (better for navigation than the
      // static list hub). The JSON-LD breadcrumb in useDeckSchema keeps the crawlable list-hub URL.
      { label: getMediaTypeText(props.deck.mediaType), route: `/decks/media?mediaType=${props.deck.mediaType}` },
    ];
    if (props.parentDeck) {
      list.push({ label: localiseTitle(props.parentDeck), route: `/decks/media/${props.parentDeck.deckId}/detail` });
    }
    list.push({
      label: localiseTitle(props.deck),
      route: props.current ? `/decks/media/${props.deck.deckId}/detail` : undefined,
    });
    if (props.current) {
      list.push({ label: props.current });
    }
    return list;
  });
</script>

<template>
  <Breadcrumb :home="home" :model="items" class="!bg-transparent !p-0 !text-xs !gap-1 overflow-x-auto">
    <template #item="{ item }">
      <NuxtLink v-if="item.route" :to="item.route" class="flex items-center gap-1 text-primary hover:underline">
        <span v-if="item.icon" :class="item.icon" />
        <span v-if="item.label" class="truncate max-w-[45vw] md:max-w-xs">{{ item.label }}</span>
      </NuxtLink>
      <span v-else class="flex items-center gap-1 text-muted-color">
        <span v-if="item.icon" :class="item.icon" />
        <span v-if="item.label" class="truncate max-w-[45vw] md:max-w-xs">{{ item.label }}</span>
      </span>
    </template>
  </Breadcrumb>
</template>
