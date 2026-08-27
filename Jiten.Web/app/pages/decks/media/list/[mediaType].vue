<script setup lang="ts">
  import { useRoute } from '#vue-router';
  import type { DeckRankingRow, PaginatedResponse } from '~/types';
  import { MediaType } from '~/types';
  import { getMediaTypeFromSlug, getMediaTypePluralText, getMediaTypeSlug } from '~/utils/mediaTypeMapper';

  const route = useRoute();
  const param = String(route.params.mediaType);

  // Legacy numeric URLs 301 to the slug via routeRules in nuxt.config; anything else unknown is a 404.
  const mediaType = getMediaTypeFromSlug(param);
  if (mediaType === null) {
    throw createError({ statusCode: 404, statusMessage: 'Unknown media type', fatal: true });
  }

  const plural = getMediaTypePluralText(mediaType);
  const pluralLower = plural.toLowerCase();
  const slug = getMediaTypeSlug(mediaType);

  const introTexts: Record<number, string> = {
    [MediaType.Anime]:
      'All of the anime were analysed from their Japanese subtitles and the difficulty reflects the actual dialogues. Start near the top for shows you can follow early on, and open any title for its full vocabulary list, per-episode breakdown and free Anki deck.',
    [MediaType.Drama]:
      'All of the dramas were analysed from their Japanese subtitles and the difficulty reflects the actual dialogues. Dramas cover a wide range, from everyday casual Japanese, to business and historical dialogues, which makes the easier ones a good first step into native media.',
    [MediaType.Movie]:
      "Each movie's rating comes from analysing its full Japanese subtitles. A film is a smaller commitment than a series, so this list is a perfect way to try native media at your level.",
    [MediaType.Novel]:
      'The difficulty of these novels was analysed from their full text. Open any title to see its vocabulary list, its length in characters, and how much of it you can already read.',
    [MediaType.NonFiction]:
      'Non-fiction reuses the same domain vocabulary again and again, so a book that looks hard at first can become comfortable a few chapters in. Every ranking here is measured from the complete text of the book.',
    [MediaType.VideoGame]:
      'Difficulty ratings for these games are measured from their extracted scripts. Each entry links to a full vocabulary list and a free Anki deck, so you can prepare before you play.',
    [MediaType.VisualNovel]:
      'Visual novel ratings come from the complete scripts, often spanning hundreds of thousands of characters of real dialogue and narration. Visual novel can be a good start as they carry audio as well as images to help you understand the context.',
    [MediaType.WebNovel]:
      'Web novels are free to read online, which makes them an easy way to start reading at length. Each one here is ranked by difficulty measured from its actual chapters.',
    [MediaType.Manga]:
      'Manga difficulty is measured from the text of each volumes. An easy manga can be a good way to start your Japanese journey, as the pictures can help you understand the context.',
    [MediaType.Audio]:
      'Audio works are ranked by the difficulty of their transcripts. There is no text on screen to lean on, so the vocabulary lists are a way to prepare before you press play.',
  };

  const page = computed(() => Math.max(1, Number(route.query.page) || 1));
  const hardestFirst = computed(() => route.query.sort === 'hardest');

  const localiseTitle = useLocaliseTitle();

  // Params go through the reactive `query` option (not the URL string) so useFetch watches them and refetches on toggle/page navigation.
  const {
    data: response,
    status,
    ready,
  } = useApiFetch<PaginatedResponse<DeckRankingRow[]>>(`media-deck/get-media-decks-by-type-ranked/${mediaType}`, { query: { page, descending: hardestFirst } });
  await ready;

  if (import.meta.client) {
    watch([page, hardestFirst], () => window.scrollTo({ top: 0 }));
  }

  const rows = computed(() => response.value?.data ?? []);
  const totalItems = computed(() => response.value?.totalItems ?? 0);
  const pageSize = computed(() => response.value?.pageSize ?? 500);
  const totalPages = computed(() => Math.max(1, Math.ceil(totalItems.value / pageSize.value)));
  const rankOffset = computed(() => response.value?.currentOffset ?? 0);

  // The main cell already shows the user's preferred title; the rest appear muted so every language stays scannable (and crawlable).
  const secondaryTitles = (deck: DeckRankingRow) => {
    const main = localiseTitle(deck);
    return [deck.originalTitle, deck.romajiTitle, deck.englishTitle].filter((t, i, arr) => !!t && t !== main && arr.indexOf(t) === i);
  };

  const pageQuery = (p: number) => {
    const query: Record<string, string> = {};
    if (hardestFirst.value) query.sort = 'hardest';
    if (p > 1) query.page = String(p);
    return query;
  };

  // Condensed page list: everything when short, otherwise first/last plus a window around the current page.
  const pageLinks = computed(() => {
    const total = totalPages.value;
    if (total <= 10) return Array.from({ length: total }, (_, i) => i + 1);
    const around = [1, 2, page.value - 1, page.value, page.value + 1, total - 1, total].filter((p) => p >= 1 && p <= total);
    const unique = [...new Set(around)].sort((a, b) => a - b);
    const withGaps: (number | null)[] = [];
    for (const [i, p] of unique.entries()) {
      if (i > 0 && p - unique[i - 1]! > 1) withGaps.push(null);
      withGaps.push(p);
    }
    return withGaps;
  });

  const pageSuffix = computed(() => (page.value > 1 ? ` (Page ${page.value})` : ''));
  const metaTitle = computed(() => `Japanese ${plural} by Difficulty: Vocabulary Lists & Anki Decks${pageSuffix.value}`);
  const metaDescription = computed(
    () =>
      `Browse ${totalItems.value.toLocaleString('en-US')} Japanese ${pluralLower} ranked from easiest to hardest. Difficulty ratings, character counts, full vocabulary lists and free Anki decks for every title.`
  );

  useSeoMeta({
    title: () => metaTitle.value,
    description: () => metaDescription.value,
    ogTitle: () => metaTitle.value,
    ogDescription: () => metaDescription.value,
    ogType: 'website',
    twitterCard: 'summary_large_image',
    twitterTitle: () => metaTitle.value,
    twitterDescription: () => metaDescription.value,
  });

  // ItemList only on the canonical first page, capped so the schema block stays a fraction of the page weight.
  useSchemaOrg(
    computed(() => [
      defineWebPage({ '@type': ['CollectionPage'] }),
      defineBreadcrumb({
        itemListElement: [
          { name: 'Home', item: '/' },
          { name: 'Media', item: '/decks/media' },
          { name: plural, item: `/decks/media/list/${slug}` },
        ],
      }),
      ...(page.value === 1 && !hardestFirst.value && rows.value.length
        ? [
            defineItemList({
              name: `Japanese ${plural} by Difficulty`,
              itemListElement: rows.value.slice(0, 100).map((d, i) => ({
                '@type': 'ListItem',
                position: i + 1,
                name: localiseTitle(d),
                item: `/decks/media/${d.deckId}/detail`,
              })),
            }),
          ]
        : []),
    ])
  );

  const otherTypes = computed(() =>
    (Object.values(MediaType).filter((v): v is MediaType => typeof v === 'number') as MediaType[])
      .filter((t) => t !== mediaType)
      .map((t) => ({ slug: getMediaTypeSlug(t), label: getMediaTypePluralText(t) }))
  );
</script>

<template>
  <div class="py-2">
    <header class="mb-6">
      <h1 class="text-3xl font-bold mb-2">Japanese {{ plural }} by Difficulty</h1>
      <p class="max-w-3xl text-surface-600 dark:text-surface-300">{{ introTexts[mediaType] }}</p>
      <p v-if="totalItems" class="mt-2 text-sm text-surface-500 dark:text-surface-400">
        {{ totalItems.toLocaleString('en-US') }} {{ pluralLower }}, sorted from {{ hardestFirst ? 'hardest to easiest' : 'easiest to hardest' }}.
      </p>
    </header>

    <nav class="mb-4 flex gap-2" aria-label="Sort order">
      <NuxtLink
        :to="{ query: {} }"
        class="rounded-lg border px-3 py-1.5 text-sm !no-underline"
        :class="
          !hardestFirst
            ? 'border-primary-500 bg-primary-50 !text-primary-700 dark:bg-primary-950 dark:!text-primary-300'
            : 'border-surface-300 !text-surface-600 dark:border-surface-600 dark:!text-surface-300'
        "
      >
        Easiest first
      </NuxtLink>
      <NuxtLink
        :to="{ query: { sort: 'hardest' } }"
        class="rounded-lg border px-3 py-1.5 text-sm !no-underline"
        :class="
          hardestFirst
            ? 'border-primary-500 bg-primary-50 !text-primary-700 dark:bg-primary-950 dark:!text-primary-300'
            : 'border-surface-300 !text-surface-600 dark:border-surface-600 dark:!text-surface-300'
        "
      >
        Hardest first
      </NuxtLink>
    </nav>

    <div v-if="status === 'pending' && !rows.length" class="py-16 text-center text-surface-500">Loading...</div>

    <div v-else-if="!rows.length" class="flex flex-col items-center justify-center py-16">
      <i class="pi pi-search text-4xl text-primary-500 mb-4" />
      <p class="text-lg font-medium text-primary-700 dark:text-primary-300">No decks found</p>
      <p class="text-sm text-surface-400">No decks available for this media type</p>
    </div>

    <div v-else class="overflow-x-auto rounded-xl border border-surface-200 dark:border-surface-700">
      <table class="w-full text-sm">
        <thead>
          <tr class="border-b border-surface-200 bg-surface-50 text-left dark:border-surface-700 dark:bg-surface-800">
            <th class="px-3 py-2 font-semibold text-surface-500 dark:text-surface-400">#</th>
            <th class="px-3 py-2 font-semibold">Title</th>
            <th class="px-3 py-2 font-semibold">Difficulty</th>
            <th class="px-3 py-2 font-semibold text-right">Characters</th>
            <th class="px-3 py-2 font-semibold text-right">Year</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="(deck, i) in rows" :key="deck.deckId" class="even:bg-surface-100 hover:bg-primary-50 dark:even:bg-surface-800 dark:hover:bg-surface-700">
            <td class="px-3 py-2 tabular-nums text-surface-400">{{ rankOffset + i + 1 }}</td>
            <td class="px-3 py-2">
              <NuxtLink :to="`/decks/media/${deck.deckId}/detail`" class="font-medium">{{ localiseTitle(deck) }}</NuxtLink>
              <span v-for="title in secondaryTitles(deck)" :key="title" class="ml-2 text-xs text-surface-400">{{ title }}</span>
            </td>
            <td class="px-3 py-2"><DifficultyDisplay :difficulty="deck.difficulty" :difficulty-raw="deck.difficulty" /></td>
            <td class="px-3 py-2 text-right tabular-nums">{{ deck.characterCount.toLocaleString('en-US') }}</td>
            <td class="px-3 py-2 text-right tabular-nums">{{ deck.releaseYear ?? '' }}</td>
          </tr>
        </tbody>
      </table>
    </div>

    <nav v-if="totalPages > 1" class="mt-6 flex flex-wrap items-center gap-1" aria-label="Pagination">
      <NuxtLink
        v-if="page > 1"
        :to="{ query: pageQuery(page - 1) }"
        class="rounded-lg border border-surface-300 px-3 py-1.5 text-sm !no-underline !text-surface-600 dark:border-surface-600 dark:!text-surface-300"
      >
        Previous
      </NuxtLink>
      <template v-for="(p, i) in pageLinks" :key="i">
        <span v-if="p === null" class="px-1 text-surface-400">&hellip;</span>
        <NuxtLink
          v-else
          :to="{ query: pageQuery(p) }"
          class="rounded-lg border px-3 py-1.5 text-sm tabular-nums !no-underline"
          :class="
            p === page
              ? 'border-primary-500 bg-primary-50 !text-primary-700 dark:bg-primary-950 dark:!text-primary-300'
              : 'border-surface-300 !text-surface-600 dark:border-surface-600 dark:!text-surface-300'
          "
        >
          {{ p }}
        </NuxtLink>
      </template>
      <NuxtLink
        v-if="page < totalPages"
        :to="{ query: pageQuery(page + 1) }"
        class="rounded-lg border border-surface-300 px-3 py-1.5 text-sm !no-underline !text-surface-600 dark:border-surface-600 dark:!text-surface-300"
      >
        Next
      </NuxtLink>
    </nav>

    <footer class="mt-8 border-t border-surface-200 pt-4 dark:border-surface-700">
      <p class="text-sm text-surface-500 dark:text-surface-400">
        Browse other media types:
        <template v-for="(t, i) in otherTypes" :key="t.slug">
          <NuxtLink :to="`/decks/media/list/${t.slug}`">{{ t.label }}</NuxtLink>
          <span v-if="i < otherTypes.length - 1">,</span>
        </template>
      </p>
    </footer>
  </div>
</template>

<style scoped></style>
