<script setup lang="ts">
  const route = useRoute();

  const { data: page } = await useAsyncData(`guide-${route.path}`, () => queryCollection('guides').path(route.path).first());

  if (!page.value || (page.value.draft && !import.meta.dev)) {
    throw createError({ statusCode: 404, statusMessage: 'Guide not found', fatal: true });
  }

  const { data: surround } = await useAsyncData(`guide-surround-${route.path}`, () =>
    queryCollectionItemSurroundings('guides', route.path).order('order', 'ASC')
  );
  const prev = computed(() => surround.value?.[0]);
  const next = computed(() => surround.value?.[1]);

  // Sidebar TOC only for longer guides (more than a couple of sections).
  const tocLinks = computed(() => page.value?.body?.toc?.links ?? []);
  const showToc = computed(() => tocLinks.value.length > 2);

  const discordLink = getDiscordLink();

  // ISO timestamp for article freshness signals (og/twitter meta + Article dateModified).
  const updatedIso = computed(() => (page.value?.updated ? new Date(page.value.updated).toISOString() : undefined));

  useSeoMeta({
    title: () => page.value?.title,
    description: () => page.value?.summary,
    ogTitle: () => page.value?.title,
    ogDescription: () => page.value?.summary,
    ogType: 'article',
    twitterCard: 'summary_large_image',
    twitterTitle: () => page.value?.title,
    twitterDescription: () => page.value?.summary,
    articlePublishedTime: () => updatedIso.value,
    articleModifiedTime: () => updatedIso.value,
  });

  // Per-guide structured data: Article (publisher/author auto-linked to the Organization identity)
  // + a Home › Guides › <title> breadcrumb. Relative items are resolved against site.url by the module.
  useSchemaOrg(
    computed(() => {
      const p = page.value;
      if (!p) return [];
      return [
        defineArticle({
          headline: p.title,
          description: p.summary,
          inLanguage: 'en',
          ...(updatedIso.value ? { datePublished: updatedIso.value, dateModified: updatedIso.value } : {}),
        }),
        defineBreadcrumb({
          itemListElement: [
            { name: 'Home', item: '/' },
            { name: 'Guides', item: '/guides' },
            { name: p.title, item: route.path },
          ],
        }),
        defineWebPage(),
      ];
    })
  );
</script>

<template>
  <div v-if="page" class="py-2 lg:flex lg:gap-10">
    <div class="min-w-0 flex-1">
      <div class="mb-4 flex items-center justify-between gap-3">
        <NuxtLink to="/guides" class="inline-flex shrink-0 items-center gap-1 text-sm text-surface-500 dark:text-surface-400 !no-underline hover:!text-primary-500">
          <Icon name="material-symbols-light:arrow-back" /> All guides
        </NuxtLink>
        <GuidesSearchBar class="max-w-xs flex-1" />
      </div>

      <article class="rounded-xl border border-surface-200 bg-surface-0 p-6 sm:p-8 dark:border-surface-700 dark:bg-surface-900">
        <header class="mb-6 border-b border-surface-200 pb-4 dark:border-surface-700">
          <h1 class="text-3xl font-bold mb-2">{{ page.title }}</h1>
          <p class="text-surface-500 dark:text-surface-400">{{ page.summary }}</p>
          <p v-if="page.updated" class="mt-2 text-xs text-surface-400">
            Updated
            <time :datetime="updatedIso">{{ new Date(page.updated).toLocaleDateString(undefined, { year: 'numeric', month: 'long', day: 'numeric' }) }}</time>
          </p>
        </header>

        <ContentRenderer :value="page" class="prose" />

        <footer class="mt-10 border-t border-surface-200 pt-6 dark:border-surface-700">
          <nav v-if="prev || next" class="flex flex-col gap-3 sm:flex-row sm:justify-between mb-8">
            <NuxtLink
              v-if="prev"
              :to="prev.path"
              class="flex-1 rounded-lg border border-surface-200 p-3 !no-underline !text-inherit hover:border-primary-400 dark:border-surface-700"
            >
              <span class="text-xs text-surface-400">← Previous</span>
              <span class="block font-medium group-hover:!text-primary-500">{{ prev.title }}</span>
            </NuxtLink>
            <NuxtLink
              v-if="next"
              :to="next.path"
              class="flex-1 rounded-lg border border-surface-200 p-3 text-right !no-underline !text-inherit hover:border-primary-400 dark:border-surface-700"
            >
              <span class="text-xs text-surface-400">Next →</span>
              <span class="block font-medium">{{ next.title }}</span>
            </NuxtLink>
          </nav>

          <div class="rounded-lg bg-surface-100 p-4 text-sm dark:bg-surface-800">
            Was this helpful, or is something unclear? Ask on
            <a :href="discordLink" target="_blank" rel="noopener noreferrer">Discord</a>, we're happy to help!
          </div>
        </footer>
      </article>
    </div>

    <!-- Rail is always reserved (even without a TOC) so every guide's card is the same width. -->
    <aside class="hidden lg:block w-56 shrink-0">
      <div v-if="showToc" class="sticky top-6">
        <p class="text-xs font-semibold uppercase tracking-wide text-surface-400 mb-2">On this page</p>
        <ul class="space-y-1 text-sm">
          <li v-for="link in tocLinks" :key="link.id">
            <a :href="`#${link.id}`" class="block text-surface-500 dark:text-surface-400 hover:!text-primary-500 !no-underline">{{ link.text }}</a>
            <ul v-if="link.children?.length" class="ml-3 mt-1 space-y-1">
              <li v-for="child in link.children" :key="child.id">
                <a :href="`#${child.id}`" class="block text-surface-400 hover:!text-primary-500 !no-underline">{{ child.text }}</a>
              </li>
            </ul>
          </li>
        </ul>
      </div>
    </aside>
  </div>
</template>
