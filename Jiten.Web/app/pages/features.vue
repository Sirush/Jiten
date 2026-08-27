<script setup lang="ts">
  const description =
    'Everything Jiten offers: media difficulty and statistics, personal coverage tracking, a built-in SRS, Anki deck generation, frequency dictionaries, and companion tools.';

  useSeoMeta({
    title: 'Features',
    description,
    ogTitle: 'Jiten Features',
    ogDescription: description,
    twitterTitle: 'Jiten Features',
    twitterDescription: description,
  });

  defineOgImage('PageOgImage', {
    title: 'Everything Jiten offers',
    description: 'Media difficulty and statistics, coverage tracking, a built-in SRS, Anki deck generation, frequency dictionaries and companion tools.',
  });

  useJitenAppSchema();

  const groups = [
    {
      title: 'Discover',
      features: [
        {
          icon: 'material-symbols-light:travel-explore',
          title: 'Media library',
          description: 'Browse through thousands of anime, novels, visual novels, games, manga, dramas, and more.',
          linkText: 'Browse media',
          link: '/decks/media',
        },
        {
          icon: 'material-symbols-light:speed',
          title: 'Difficulty ratings',
          description: 'Algorithmic difficulty scores that are refined by community votes to easily find something at your level.',
          linkText: 'Browse by difficulty',
          link: '/decks/media?sortBy=difficulty&sortOrder=0',
        },
        {
          icon: 'material-symbols-light:bar-chart',
          title: 'Detailed statistics',
          description: 'Character counts, kanji counts, sentence length, dialogue percentage, speech speed and many more statistics.',
          linkText: 'Understand the stats',
          link: '/guides/reading-a-deck-page',
        },
      ],
    },
    {
      title: 'Track',
      features: [
        {
          icon: 'material-symbols-light:pie-chart',
          title: 'Personal coverage',
          description: 'See how much of any title you already understand before starting it.',
          linkText: 'How tracking works',
          link: '/guides/tracking-known-words',
        },
        {
          icon: 'material-symbols-light:upload',
          title: 'Import your vocabulary',
          description: 'Bring your knowledge from Anki or JPDB.',
          linkText: 'Import from Anki',
          link: '/guides/importing-known-words-from-anki',
        },
        {
          icon: 'material-symbols-light:monitoring',
          title: 'Progress over time',
          description: 'View charts of your progress such as your vocabulary growth, review history, and retention over time.',
          linkText: 'Build your vocabulary',
          link: '/guides/building-your-vocabulary',
        },
      ],
    },
    {
      title: 'Study',
      features: [
        {
          icon: 'material-symbols-light:style',
          title: 'Built-in SRS',
          description:
            'Study with the modern FSRS-6 scheduling with full customization including of the card layout, multiple review modes such as write-in, audio, examples sentences taken from what you study, and detailed statistics.',
          linkText: 'Using the SRS',
          link: '/guides/using-the-srs',
        },
        {
          icon: 'material-symbols-light:download',
          title: 'Anki deck generation',
          description: 'Download filtered vocabulary lists of any media as Anki decks, with example sentences, pitch accent, and frequency data.',
          linkText: 'Generate a deck',
          link: '/guides/generating-anki-decks',
        },
        {
          icon: 'material-symbols-light:book-2',
          title: 'Vocabulary lists',
          description: "Browse through titles' vocabulary with frequency sorting, parts of speech filter and the full detail of every word.",
          linkText: 'Learn about vocabulary lists',
          link: '/guides/reading-a-deck-page',
        },
        {
          icon: 'material-symbols-light:record-voice-over',
          title: 'Example sentences',
          description: 'Real sentences from media with TTS audio. Learn from natural sentences that you will encounter during immersion.',
          linkText: 'Reading a word page',
          link: '/guides/reading-a-word-page',
        },
      ],
    },
    {
      title: 'Tools & data',
      features: [
        {
          icon: 'material-symbols-light:dictionary',
          title: 'Yomitan frequency dictionaries',
          description: 'Frequency dictionaries built from the whole corpus with a global one and one for each media type.',
          linkText: 'Download dictionaries',
          link: '/frequency-dictionaries',
        },
        {
          icon: 'material-symbols-light:text-snippet',
          title: 'Parse your own text',
          description: 'Paste any Japanese sentence to get a breakdown of the words or search them like in a dictionary.',
          linkText: 'Parse text',
          link: '/parse',
        },
        {
          icon: 'material-symbols-light:extension',
          title: 'Companion apps',
          description: 'Official extensions including Jiten Reader to read anywhere on the web and JitenMPV for your anime or drama.',
          linkText: 'Get Jiten Reader',
          link: '/reader',
        },
        {
          icon: 'material-symbols-light:code',
          title: 'API access',
          description: 'A free and powerful API to build your own tools on top of Jiten data.',
          linkText: 'Using the API',
          link: '/guides/using-the-api',
        },
      ],
    },
  ];
</script>

<template>
  <div class="container mx-auto px-4 py-6">
    <div class="max-w-5xl mx-auto">
      <div class="text-center mb-6">
        <h1 class="text-3xl font-bold mb-2">Everything on Jiten</h1>
        <p class="text-gray-600 dark:text-gray-400">The core toolbox, free and open source.</p>
      </div>

      <Card v-for="group in groups" :key="group.title" class="shadow-lg mb-4">
        <template #content>
          <h2 class="text-2xl font-bold mb-4">{{ group.title }}</h2>
          <div class="grid grid-cols-1 sm:grid-cols-2 gap-4">
            <div v-for="feature in group.features" :key="feature.title" class="flex gap-3">
              <Icon :name="feature.icon" class="text-primary shrink-0 mt-0.5" size="1.75em" />
              <div>
                <h3 class="font-semibold mb-1">{{ feature.title }}</h3>
                <p class="text-sm text-gray-600 dark:text-gray-400 mb-1">{{ feature.description }}</p>
                <NuxtLink :to="feature.link" class="text-sm font-medium">{{ feature.linkText }} →</NuxtLink>
              </div>
            </div>
          </div>
        </template>
      </Card>

      <Card class="shadow-lg mb-4">
        <template #content>
          <div class="flex flex-col sm:flex-row items-center justify-between gap-4">
            <div class="flex-1 text-center sm:text-left">
              <div class="flex items-center justify-center sm:justify-start gap-2 mb-1">
                <h2 class="text-2xl font-bold mb-0">Want more?</h2>
                <JitenPlusBadge />
              </div>
              <p class="text-sm text-gray-600 dark:text-gray-400 mb-0">
                Jiten+ enhances your cards with your own images and audio that you can easily mine with JitenMPV, custom frequency lists, personalised immersion
                plans, your coverage journey, and higher limits. It also helps keep everything else free.
              </p>
            </div>
            <NuxtLink to="/jiten-plus" class="no-underline shrink-0">
              <Button severity="secondary" outlined>
                <Icon name="material-symbols:arrow-forward" class="mr-2" size="1.25em" />
                Discover Jiten+
              </Button>
            </NuxtLink>
          </div>
        </template>
      </Card>

      <Card class="shadow-lg">
        <template #content>
          <div class="flex flex-col sm:flex-row items-center justify-between gap-4">
            <p class="text-gray-600 dark:text-gray-300 mb-0">Ready to see your own coverage?</p>
            <NuxtLink to="/register" class="no-underline">
              <Button severity="primary">
                <Icon name="material-symbols-light:person-add" class="mr-2" size="1.25em" />
                Create an account
              </Button>
            </NuxtLink>
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
</style>
