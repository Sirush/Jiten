<script setup lang="ts">
  import OmniSearch from '~/components/OmniSearch.vue';

  const authStore = useAuthStore();

  useHead({
    title: 'Jiten - Vocabulary Lists and Anki Decks for Japanese Media',
    meta: [
      {
        name: 'description',
        content:
          'Free vocabulary lists, Anki decks, and coverage tracking for thousands of Japanese anime, novels, visual novels, games, and manga. Track your progress and find your next immersion content.',
      },
    ],
  });

  const discordUrl = getDiscordLink();

  const heroFeatures = [
    {
      icon: 'material-symbols-light:library-books',
      title: 'Comprehensive Library',
      description: 'Thousands of anime, novels, VNs, games, manga and more.',
    },
    {
      icon: 'material-symbols-light:style',
      title: 'Anki Deck Generation',
      description: 'Generate and download Anki decks from any media in seconds.',
    },
    {
      icon: 'material-symbols-light:pie-chart',
      title: 'Coverage Tracking',
      description: 'See how much of any media you can understand at a glance.',
    },
    {
      icon: 'material-symbols-light:volunteer-activism',
      title: 'Free & Open Source',
      description: 'Free to use and supported by the community.',
    },
  ];

  const features = [
    {
      icon: 'material-symbols-light:analytics',
      title: 'Detailed Statistics',
      description: 'Character count, unique word count, difficulty ratings, and much more stats for every media.',
    },
    {
      icon: 'material-symbols-light:download',
      title: 'Custom Anki Decks',
      description: 'Download vocabulary decks with example sentences, pitch accent, and frequency data. Filter your already known words.',
    },
    {
      icon: 'material-symbols-light:dictionary',
      title: 'Rich Vocabulary Lists',
      description: 'Browse vocabulary with English definitions, frequency by media type, and example sentences in context.',
    },
    {
      icon: 'material-symbols-light:trending-up',
      title: 'Vocabulary Tracking',
      description: 'Import from Anki or JPDB, mark words as known, and see your coverage for any media.',
    },
    {
      icon: 'material-symbols-light:book-2',
      title: 'Frequency Dictionaries',
      description: 'Download frequency dictionaries for Yomitan based on our extensive media collection.',
      link: '/other',
    },
    {
      icon: 'material-symbols-light:thumbs-up-down',
      title: 'Difficulty Ratings',
      description: 'AI-powered difficulty scores refined by community votes. Find content that matches your level.',
      link: '/ratings',
      isNew: true,
    },
  ];
</script>

<template>
  <div class="container mx-auto px-4 py-6">
    <div class="max-w-5xl mx-auto">
      <!-- Hero Section -->
      <Card class="shadow-lg mb-4">
        <template #content>
          <div class="text-center">
            <h1 class="text-4xl font-bold mb-2">Jiten</h1>
            <p class="text-xl text-gray-600 dark:text-gray-300 mb-6">The Japanese immersion toolkit for all your favourite media</p>

            <!-- OmniSearch -->
            <div class="max-w-2xl mx-auto mb-4">
              <OmniSearch autofocus />
            </div>

            <!-- 4 Key Features Grid -->
            <div class="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
              <div
                v-for="feature in heroFeatures"
                :key="feature.title"
                class="p-4 border border-gray-200 dark:border-gray-700 rounded-lg text-center hover:border-primary transition-colors"
              >
                <Icon :name="feature.icon" class="text-primary mb-2" size="2.5em" />
                <h3 class="font-semibold mb-1">{{ feature.title }}</h3>
                <p class="text-sm text-gray-600 dark:text-gray-400">{{ feature.description }}</p>
              </div>
            </div>
          </div>
        </template>
      </Card>

      <!-- Support Section -->
      <Card class="shadow-lg mb-4 !border-1 !border-purple-500">
        <template #content>
          <div class="flex flex-col md:flex-row items-center gap-6">
            <div class="flex-1 text-center md:text-left">
              <h2 class="text-2xl font-bold mb-2">Support Jiten</h2>
              <p class="text-gray-600 dark:text-gray-300">
                Jiten is free and open source. If you find it useful, consider supporting the project to help cover server costs and fund the continued
                development of new features.
              </p>
            </div>
            <NuxtLink to="/donate" class="no-underline">
              <Button severity="primary" size="large">
                <Icon name="material-symbols-light:favorite" class="mr-2" size="1.25em" />
                Support Us
              </Button>
            </NuxtLink>
          </div>
        </template>
      </Card>

      <!-- Features Section -->
      <Card class="shadow-lg mb-4">
        <template #title>
          <div class="flex items-center">
            <Icon name="material-symbols-light:star" class="mr-2 text-primary" size="1.5em" />
            Features
          </div>
        </template>
        <template #content>
          <div class="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
            <template v-for="feature in features" :key="feature.title">
              <NuxtLink v-if="feature.link" :to="feature.link" class="no-underline !text-inherit !bg-transparent">
                <div class="p-4 border border-gray-200 dark:border-gray-700 rounded-lg hover:border-primary transition-colors h-full">
                  <div class="flex items-center mb-3">
                    <Icon :name="feature.icon" class="text-primary mr-3" size="1.75em" />
                    <h3 class="text-lg font-semibold text-gray-900 dark:text-gray-100">{{ feature.title }}</h3>
                    <span v-if="feature.isNew" class="ml-2 px-2 py-0.5 text-xs font-bold rounded-full bg-emerald-500 text-white">NEW</span>
                  </div>
                  <p class="text-gray-600 dark:text-gray-400">{{ feature.description }}</p>
                </div>
              </NuxtLink>
              <div v-else class="p-4 border border-gray-200 dark:border-gray-700 rounded-lg hover:border-primary transition-colors">
                <div class="flex items-center mb-3">
                  <Icon :name="feature.icon" class="text-primary mr-3" size="1.75em" />
                  <h3 class="text-lg font-semibold">{{ feature.title }}</h3>
                  <span v-if="feature.isNew" class="ml-2 px-2 py-0.5 text-xs font-bold rounded-full bg-emerald-500 text-white">NEW</span>
                </div>
                <p class="text-gray-600 dark:text-gray-400">{{ feature.description }}</p>
              </div>
            </template>
          </div>
        </template>
      </Card>

      <!-- Jiten Reader Section -->
      <Card class="shadow-lg mb-4">
        <template #title>
          <div class="flex items-center">
            <Icon name="material-symbols-light:extension" class="mr-2 text-primary" size="1.5em" />
            Jiten Reader
            <span class="ml-2 px-2 py-0.5 text-xs font-bold rounded-full bg-emerald-500 text-white">NEW</span>
          </div>
        </template>
        <template #content>
          <div class="flex flex-col md:flex-row items-start gap-6">
            <div class="flex-1">
              <p class="text-gray-700 dark:text-gray-300 mb-4">
                A free browser extension that helps you read Japanese anywhere on the web. Compatible with apps such as
                <strong>Ttsu Reader, Mokuro Reader, and Asbplayer</strong>.
              </p>
              <p class="text-gray-700 dark:text-gray-300 mb-4">
                Look up words instantly, sync vocabulary to Jiten, see the coverage of what you're immersing in, add furigana, and more.
              </p>
              <p class="text-gray-700 dark:text-gray-300 mb-4">Customise everything from themes to keyboard shortcuts to make it fit your workflow.</p>
              <NuxtLink to="/reader" class="no-underline">
                <Button severity="primary" size="large">
                  <Icon name="material-symbols-light:download" class="mr-2" size="1.25em" />
                  Get It Now
                </Button>
              </NuxtLink>
            </div>
            <Image src="/img/jitenreader_screenshot.webp" alt="Jiten Reader screenshot" class="rounded-lg w-full md:w-80" preview width="320" height="200" />
          </div>
        </template>
      </Card>

      <!-- VNDB Character Count Userscript Section -->
      <Card class="shadow-lg mb-4">
        <template #title>
          <div class="flex items-center">
            <Icon name="material-symbols-light:code" class="mr-2 text-primary" size="1.5em" />
            VNDB Character Count
          </div>
        </template>
        <template #content>
          <div class="flex flex-col md:flex-row items-start gap-6">
            <div class="flex-1">
              <p class="text-gray-700 dark:text-gray-300 mb-4">
                A free userscript that enhances VNDB pages by displaying character count, difficulty ratings, and other useful statistics from Jiten directly on
                visual novel entries.
              </p>
              <p class="text-gray-700 dark:text-gray-300 mb-4">
                Input your own reading speed to get an estimate on how long it will take you to read. Works with any userscript manager such as Tampermonkey or
                Violentmonkey.
              </p>
              <a href="https://greasyfork.org/en/scripts/549246-vndb-character-count" target="_blank" rel="noopener noreferrer" class="no-underline">
                <Button severity="primary" size="large">
                  <Icon name="material-symbols-light:download" class="mr-2" size="1.25em" />
                  Install Userscript
                </Button>
              </a>
            </div>
            <Image src="/img/vndb_userscript.jpg" alt="VNDB Character Count userscript screenshot" class="rounded-lg w-full md:w-80" preview width="320" height="167" />
          </div>
        </template>
      </Card>

      <!-- Community Section -->
      <Card v-if="!authStore.isAuthenticated" class="shadow-lg">
        <template #content>
          <div class="text-center">
            <h2 class="text-2xl font-bold mb-4">Join the community</h2>
            <p class="text-gray-600 dark:text-gray-300 mb-6">Free, open source, and built for immersion learners.</p>

            <!-- Primary CTAs -->
            <div class="flex flex-col sm:flex-row gap-4 justify-center mb-6">
              <NuxtLink to="/decks/media" class="no-underline">
                <Button severity="primary" size="large" class="w-full sm:w-auto">
                  <Icon name="material-symbols-light:search" class="mr-2" size="1.25em" />
                  Browse Media
                </Button>
              </NuxtLink>
              <NuxtLink to="/register" class="no-underline">
                <Button severity="secondary" size="large" class="w-full sm:w-auto">
                  <Icon name="material-symbols-light:person-add" class="mr-2" size="1.25em" />
                  Create Account
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
        </template>
      </Card>
    </div>
  </div>
</template>

<style scoped>
  .no-underline {
    text-decoration: none !important;
  }

  .no-underline:hover {
    text-decoration: none !important;
  }
</style>
