<script setup lang="ts">
  import OmniSearch from '~/components/OmniSearch.vue';
  import { useApiFetch } from '~/composables/useApiFetch';
  import { type GlobalStats, MediaType } from '~/types';
  import { homeDemoDecks } from '~/data/homeDemoDecks';

  useHead({
    title: 'Jiten - Vocabulary Lists and Anki Decks for Japanese Media',
    titleTemplate: null,
    meta: [
      {
        name: 'description',
        content:
          'Free vocabulary lists, Anki decks, and coverage tracking for thousands of Japanese anime, novels, visual novels, games, and manga. Track your progress and find your next immersion content.',
      },
    ],
  });

  useJitenAppSchema();

  const discordUrl = getDiscordLink();

  const { data: globalStats } = await useApiFetch<GlobalStats>('stats/get-global-stats');

  const statPool = [
    { type: MediaType.Anime, label: 'anime' },
    { type: MediaType.Drama, label: 'dramas' },
    { type: MediaType.Novel, label: 'novels' },
    { type: MediaType.VideoGame, label: 'video games' },
    { type: MediaType.VisualNovel, label: 'visual novels' },
    { type: MediaType.Manga, label: 'manga' },
  ];

  // useState so the server's random pick survives hydration
  const featuredTypes = useState('homeFeaturedMediaTypes', () => {
    const pool = [...statPool];
    const first = pool.splice(Math.floor(Math.random() * pool.length), 1)[0]!;
    const second = pool[Math.floor(Math.random() * pool.length)]!;
    return [first, second];
  });

  const typeCount = (type: MediaType): number | undefined => (globalStats.value?.mediaByType as Record<string, number> | undefined)?.[MediaType[type]!];

  const formatCharacters = (n: number) => (n >= 1e9 ? `${(n / 1e9).toFixed(1)} billion` : `${Math.round(n / 1e6)} million`);

  // useState so the server's random pick survives hydration
  const demoDeck = useState('homeDemoDeck', () => homeDemoDecks[Math.floor(Math.random() * homeDemoDecks.length)]!);

  const steps = [
    {
      title: 'Find media at your level',
      description:
        'Compare length, vocabulary, and difficulty ratings refined by community votes across thousands of anime, novels, games, visual novels, and manga.',
      linkText: 'Browse media',
      link: '/decks/media',
    },
    {
      title: "See how much you'd understand",
      description:
        'Import your vocabulary from Anki or JPDB, or quickly mark what you already know, and see your personal coverage of any title before starting it.',
      linkText: 'Create an account',
      link: '/register',
    },
    {
      title: "Learn the words you're missing",
      description:
        "Study the missing words in Jiten's modern, customisable built-in SRS, or download a filtered Anki deck with example sentences, pitch accent, and frequency data.",
      linkText: 'See a real vocabulary list',
      link: {
        path: `/decks/media/${demoDeck.value.deckId}/vocabulary`,
        query: { sortBy: 'deckFreq', excludePos: 'prt,cop,adj-f' },
      },
    },
  ];

  const companions = [
    {
      kicker: 'Browser extension',
      title: 'Read on the web with Jiten Reader',
      description:
        'A free browser extension with automatic parsing, lookups, reviews, instant coverage, all fully synced with your account. Works with Ttsu Reader, Mokuro, and Asbplayer and much more.',
      linkText: 'Get Jiten Reader',
      link: '/reader',
      external: false,
      image: '/img/jitenreader_colouring.webp',
      imageAlt: 'Jiten Reader screenshot',
      imagePosition: 'object-top',
    },
    {
      kicker: 'MPV plugin',
      title: 'Watch with JitenMPV',
      description: 'An mpv plugin that that automatically parses your subtitles, colours them, allow you to review and mine, just like Jiten Reader.',
      linkText: 'Download from GitHub',
      link: 'https://github.com/Sirush/JitenMPV/releases',
      external: true,
      image: '/img/jitenmpv.webp',
      imageAlt: 'JitenMPV screenshot',
      imagePosition: 'object-bottom',
    },
    {
      kicker: 'Userscript',
      title: 'See Jiten stats on VNDB',
      description:
        'An userscript that enhances VNDB pages with character counts, difficulty ratings, and an estimation of the time it will take you based on your reading speed. Works with any userscript manager such as Tampermonkey or Violentmonkey.\n' +
        '\n',
      linkText: 'Install the userscript',
      link: 'https://greasyfork.org/en/scripts/549246-vndb-character-count',
      external: true,
      image: '/img/vndb_userscript.jpg',
      imageAlt: 'VNDB Character Count userscript screenshot',
      imagePosition: 'object-top',
    },
  ];
</script>

<template>
  <div>
    <!-- Hero Section -->
    <section class="band -mt-6 bg-primary-100/70 dark:bg-primary-950/40">
      <div class="max-w-6xl mx-auto px-4 pt-12 pb-10 md:pt-16 md:pb-12">
        <div class="text-center">
          <h1 class="mb-6">
            <span class="block text-4xl md:text-5xl font-bold mb-2">Jiten</span>
            <span class="block text-xl font-normal text-gray-600 dark:text-gray-300">Immerse yourself in Japanese media you can understand</span>
          </h1>

          <!-- OmniSearch -->
          <div class="max-w-2xl mx-auto mb-3">
            <OmniSearch autofocus />
          </div>

          <p v-if="globalStats" class="text-sm text-gray-600 dark:text-gray-400 mb-5">
            <b>{{ formatCharacters(globalStats.totalMojis) }}</b>
            characters analysed across
            <b>{{ globalStats.totalMedia.toLocaleString() }}</b>
            titles, including
            <template v-for="(featured, index) in featuredTypes" :key="featured.type">
              <template v-if="index > 0"> and </template>
              <NuxtLink :to="{ path: '/decks/media', query: { mediaType: featured.type } }">
                {{ typeCount(featured.type)?.toLocaleString() }} {{ featured.label }}
              </NuxtLink>
            </template>
          </p>

          <div class="flex flex-wrap items-center justify-center gap-x-3 gap-y-2">
            <NuxtLink to="/register" class="no-underline">
              <Button severity="primary" size="small">
                <Icon name="material-symbols:person-add" class="mr-2" size="1.25em" />
                Create an account
              </Button>
            </NuxtLink>
            <p class="text-sm text-gray-600 dark:text-gray-400">Track your vocabulary and see your coverage on every title.</p>
          </div>
        </div>
      </div>
    </section>

    <!-- Live Demo Section -->
    <section class="band bg-surface-0 dark:bg-surface-900/40 border-y border-surface-200 dark:border-surface-800">
      <div class="max-w-[90rem] mx-auto px-4 py-10 md:py-12">
        <div class="flex flex-col lg:flex-row items-center gap-6 lg:gap-10">
          <div class="flex-1 text-center lg:text-left">
            <h2 class="text-2xl font-bold mb-3">Your coverage on every title</h2>
            <p class="text-gray-600 dark:text-gray-400 mb-3">
              Import your vocabulary from Anki or JPDB, or mark what you already know, and know instantly how much you'll understand of any title present in the
              media library. Choose what you will immerse in next based on your current knowledge.
            </p>
            <NuxtLink to="/decks/media" class="font-medium">
              {{ globalStats ? `Browse ${globalStats.totalMedia.toLocaleString()} titles` : 'Browse the library' }} →
            </NuxtLink>
          </div>
          <div class="w-full max-w-xl xl:max-w-2xl shrink-0">
            <div class="relative rounded-xl ring-2 ring-primary/40">
              <span class="absolute -top-2.5 right-4 z-10 rounded-full bg-primary text-primary-contrast text-xs font-semibold px-2.5 py-0.5 shadow">
                Example
              </span>
              <MediaDeckCard :deck="demoDeck" demo-coverage />
            </div>
            <p class="text-sm text-gray-500 dark:text-gray-400 text-center mt-2 mb-0">A real title from the library with example data.</p>
          </div>
        </div>
      </div>
    </section>

    <!-- Three-step Loop Section -->
    <section class="band">
      <div class="max-w-6xl mx-auto px-4 py-10 md:py-12">
        <div class="relative mb-6">
          <h2 class="text-2xl font-bold text-center">Learn with what you love</h2>
          <NuxtLink to="/features" class="block text-center text-sm font-medium mt-1 lg:mt-0 lg:absolute lg:right-0 lg:top-1/2 lg:-translate-y-1/2">
            Explore all features →
          </NuxtLink>
        </div>
        <div class="flex flex-col md:flex-row items-stretch gap-3 md:gap-2">
          <template v-for="(step, index) in steps" :key="step.title">
            <div v-if="index > 0" class="flex items-center justify-center shrink-0" aria-hidden="true">
              <Icon name="material-symbols-light:arrow-right-alt" class="text-primary opacity-70 rotate-90 md:rotate-0" size="1.75em" />
            </div>
            <div class="flex-1 min-w-0 p-4 border border-gray-200 dark:border-gray-700 rounded-lg flex flex-col bg-surface-0 dark:bg-surface-900">
              <span class="flex items-center justify-center w-7 h-7 rounded-full bg-primary text-primary-contrast text-sm font-bold shrink-0 mb-3">
                {{ index + 1 }}
              </span>
              <h3 class="text-lg font-semibold mb-2">{{ step.title }}</h3>
              <p class="text-gray-600 dark:text-gray-400 mb-4 flex-1">{{ step.description }}</p>
              <NuxtLink :to="step.link" class="font-medium">{{ step.linkText }} →</NuxtLink>
            </div>
          </template>
        </div>
      </div>
    </section>

    <!-- Companion Tools Section -->
    <section class="band bg-surface-0 dark:bg-surface-900/40 border-y border-surface-200 dark:border-surface-800">
      <div class="max-w-6xl mx-auto px-4 py-10 md:py-12">
        <h2 class="text-2xl font-bold text-center mb-6">Bring Jiten into your immersion workflow</h2>
        <div class="grid grid-cols-1 md:grid-cols-3 gap-4">
          <div
            v-for="companion in companions"
            :key="companion.title"
            class="p-4 border border-gray-200 dark:border-gray-700 rounded-lg flex flex-col bg-surface-0 dark:bg-surface-900"
          >
            <div class="mb-3">
              <span class="block text-xs font-semibold uppercase tracking-wider text-primary mb-1">{{ companion.kicker }}</span>
              <h3 class="text-lg font-semibold">{{ companion.title }}</h3>
            </div>
            <div class="mb-3 h-36 overflow-hidden rounded-md">
              <Image
                :src="companion.image"
                :alt="companion.imageAlt"
                class="block w-full h-full"
                :image-class="`w-full h-full object-cover ${companion.imagePosition}`"
                preview
              />
            </div>
            <p class="text-gray-600 dark:text-gray-400 mb-4 flex-1">{{ companion.description }}</p>
            <a v-if="companion.external" :href="companion.link" target="_blank" rel="noopener noreferrer" class="font-medium">{{ companion.linkText }} →</a>
            <NuxtLink v-else :to="companion.link" class="font-medium">{{ companion.linkText }} →</NuxtLink>
          </div>
        </div>
      </div>
    </section>

    <!-- Community Section -->
    <section class="band">
      <div class="max-w-6xl mx-auto px-4 py-10 md:py-12">
        <div class="text-center">
          <h2 class="text-2xl font-bold mb-4">Join the community</h2>
          <p class="text-gray-600 dark:text-gray-300 mb-6">Free, open source, and built for immersion learners.</p>

          <!-- Primary CTAs -->
          <div class="flex flex-col sm:flex-row gap-4 justify-center mb-6">
            <NuxtLink to="/decks/media" class="no-underline">
              <Button severity="primary" size="large" class="w-full sm:w-auto">
                <Icon name="material-symbols:search" class="mr-2" size="1.25em" />
                Browse Media
              </Button>
            </NuxtLink>
            <NuxtLink to="/register" class="no-underline">
              <Button severity="secondary" size="large" class="w-full sm:w-auto">
                <Icon name="material-symbols:person-add" class="mr-2" size="1.25em" />
                Create an account
              </Button>
            </NuxtLink>
          </div>

          <Divider />

          <!-- Community Links -->
          <div class="flex flex-col sm:flex-row gap-4 justify-center text-sm">
            <a :href="discordUrl" target="_blank" rel="noopener noreferrer" class="flex items-center justify-center gap-2">
              <Icon name="ic:baseline-discord" size="1.25em" />
              Join our Discord
            </a>
            <a href="https://github.com/Sirush/Jiten" target="_blank" rel="noopener noreferrer" class="flex items-center justify-center gap-2">
              <Icon name="mdi:github" size="1.25em" />
              View on GitHub
            </a>
          </div>
        </div>

        <!-- Support callout -->
        <Card class="shadow-lg !border-1 !border-purple-500 mt-10 max-w-4xl mx-auto">
          <template #content>
            <div class="flex flex-col md:flex-row items-center gap-6">
              <div class="flex-1 text-center md:text-left">
                <h2 class="text-2xl font-bold mb-2">Support Jiten</h2>
                <p class="text-gray-600 dark:text-gray-300">
                  Jiten is free and open source. If you find it useful,
                  <NuxtLink to="/jiten-plus">Jiten+</NuxtLink>
                  or a donation helps cover server costs and fund new features.
                </p>
              </div>
              <NuxtLink to="/donate" class="no-underline">
                <Button severity="primary" size="large">
                  <Icon name="material-symbols:favorite" class="mr-2" size="1.25em" />
                  Support Us
                </Button>
              </NuxtLink>
            </div>
          </template>
        </Card>
      </div>
    </section>
  </div>
</template>

<style scoped>
  /* Escapes the app shell's max-w-6xl container so section backgrounds span the viewport;
     the shell's overflow-x-clip absorbs the scrollbar-width excess of 100vw. */
  .band {
    width: 100vw;
    margin-left: calc(50% - 50vw);
  }

  .no-underline {
    text-decoration: none !important;
  }

  .no-underline:hover {
    text-decoration: none !important;
  }
</style>
