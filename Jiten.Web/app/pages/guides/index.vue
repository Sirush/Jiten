<script setup lang="ts">
  type GuideListItem = {
    path: string;
    title: string;
    summary: string;
    category: string;
    level: 'beginner' | 'advanced';
    order: number;
    icon?: string;
  };

  // Stable category ordering for the index nav. Keep in sync with the schema enum in content.config.ts.
  const CATEGORY_ORDER = ['Getting Started', 'Using Jiten', 'Studying', 'Coming from another app?', 'Advanced & tools', 'FAQ'];

  const { data: guides } = await useAsyncData('guides-index', () => {
    let q = queryCollection('guides').select('path', 'title', 'summary', 'category', 'level', 'order', 'icon').order('order', 'ASC');
    // Drafts are committable but hidden in production; visible in dev for previewing.
    if (!import.meta.dev) q = q.where('draft', '=', false);
    return q.all() as Promise<GuideListItem[]>;
  });

  const groupedGuides = computed(() => {
    const groups = new Map<string, GuideListItem[]>();
    for (const g of guides.value ?? []) {
      if (!groups.has(g.category)) groups.set(g.category, []);
      groups.get(g.category)!.push(g);
    }
    return CATEGORY_ORDER.filter((c) => groups.has(c)).map((c) => ({ category: c, items: groups.get(c)! }));
  });

  // Full-text search lives in the global command palette (GuidesSearch.vue); the bar below opens it.

  // FAQPage JSON-LD over the migrated FAQ guides keeps the rich result that /faq used to earn.
  const faqItems = computed(() => (guides.value ?? []).filter((g) => g.category === 'FAQ'));
  const faqSchema = computed(() =>
    JSON.stringify({
      '@context': 'https://schema.org',
      '@type': 'FAQPage',
      mainEntity: faqItems.value.map((g) => ({
        '@type': 'Question',
        name: g.title,
        acceptedAnswer: { '@type': 'Answer', text: g.summary },
      })),
    })
  );

  useSeoMeta({
    title: 'Japanese Learning Guides & Tutorials',
    description: 'Tutorials and answers for getting the most out of Jiten — building vocabulary, choosing media, studying with the SRS, and more.',
    ogTitle: 'Jiten Guides — Japanese Immersion Tutorials & FAQ',
    ogType: 'website',
    twitterCard: 'summary_large_image',
    twitterTitle: 'Jiten Guides',
    twitterDescription: 'Tutorials and answers for building vocabulary, choosing media at your level, and studying Japanese with Jiten.',
  });

  // Typed schema graph: CollectionPage + Home › Guides breadcrumb, plus an ItemList exposing the full
  // guide set as a crawlable ordered collection (relative item URLs are resolved against site.url).
  useSchemaOrg(
    computed(() => [
      defineWebPage({ '@type': ['CollectionPage'] }),
      defineBreadcrumb({
        itemListElement: [
          { name: 'Home', item: '/' },
          { name: 'Guides', item: '/guides' },
        ],
      }),
      ...(guides.value?.length
        ? [
            defineItemList({
              name: 'Jiten Guides',
              itemListElement: guides.value.map((g, i) => ({ '@type': 'ListItem', position: i + 1, name: g.title, item: g.path })),
            }),
          ]
        : []),
    ])
  );

  // FAQPage stays a standalone JSON-LD block: its rich-result type differs from the CollectionPage node
  // above, so it is emitted separately rather than folded into the typed graph.
  useHead(() => ({
    script: faqItems.value.length ? [{ type: 'application/ld+json', innerHTML: faqSchema.value }] : [],
  }));

</script>

<template>
  <div class="py-2">
    <header class="mb-6">
      <h1 class="text-3xl font-bold mb-1">Guides</h1>
      <p class="text-surface-500 dark:text-surface-400">Tutorials and answers for getting the most out of Jiten.</p>
    </header>

    <GuidesSearchBar class="mb-8 max-w-xl" />

    <section v-for="group in groupedGuides" :key="group.category" class="mb-8">
      <h2 class="text-xl font-semibold mb-3">{{ group.category }}</h2>
      <div class="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4">
        <NuxtLink
          v-for="g in group.items"
          :key="g.path"
          :to="g.path"
          class="group block rounded-lg border border-surface-200 bg-surface-0 p-4 !no-underline transition hover:border-primary-400 hover:shadow-md dark:border-surface-700 dark:bg-surface-900"
        >
          <div class="flex items-start gap-3">
            <Icon v-if="g.icon" :name="g.icon" class="mt-0.5 shrink-0 text-2xl text-primary-500" />
            <div class="min-w-0">
              <h3 class="font-semibold !text-inherit group-hover:!text-primary-500">
                {{ g.title }}
                <span v-if="g.level === 'advanced'" class="pos-badge pos-amber ml-1 align-middle">advanced</span>
              </h3>
              <p class="mt-1 text-sm text-surface-500 dark:text-surface-400 line-clamp-2">{{ g.summary }}</p>
            </div>
          </div>
        </NuxtLink>
      </div>
    </section>
  </div>
</template>
