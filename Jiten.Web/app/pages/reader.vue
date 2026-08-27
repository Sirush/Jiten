<script setup lang="ts">
  const description =
    'A free pop-up dictionary extension for reading Japanese online. Instant lookups, per-word knowledge tracking, ' +
    'and a readability estimate for any page. Works with Ttsu, Asbplayer and Mokuro.';

  useSeoMeta({
    title: 'Jiten Reader - Browser Extension',
    description,
    ogTitle: 'Jiten Reader - Free Pop-up Japanese Dictionary Extension',
    ogDescription: description,
    twitterTitle: 'Jiten Reader - Free Pop-up Japanese Dictionary Extension',
    twitterDescription: description,
  });

  defineOgImage('PageOgImage', {
    title: 'Jiten Reader',
    category: 'Browser extension',
    description: 'A free pop-up Japanese dictionary for the web. Instant lookups, per-word knowledge tracking, and a readability estimate for any page.',
  });

  const chromeStoreUrl = 'https://chromewebstore.google.com/detail/jiten-reader/fkegmlkjkenojfiplaclhlmncfeooaeo';
  const firefoxStoreUrl = 'https://addons.mozilla.org/en-US/firefox/addon/jiten-reader/';

  // SoftwareApplication entity for the extension, tied to the store listings and the site
  // Organization so the Reader resolves as its own installable product.
  useSchemaOrg([
    {
      '@type': 'SoftwareApplication',
      '@id': 'https://jiten.moe/reader#app',
      name: 'Jiten Reader',
      url: 'https://jiten.moe/reader',
      description,
      applicationCategory: 'BrowserApplication',
      operatingSystem: 'Chrome, Firefox',
      offers: { '@type': 'Offer', price: '0', priceCurrency: 'EUR' },
      installUrl: [chromeStoreUrl, firefoxStoreUrl],
      sameAs: [chromeStoreUrl, firefoxStoreUrl],
      publisher: { '@id': 'https://jiten.moe/#identity' },
    },
  ]);

  const discordUrl = getDiscordLink();

  const features = [
    {
      icon: 'material-symbols-light:language-japanese-kana',
      title: 'Parse Japanese Anywhere',
      description: 'Works on webpages and 15+ apps including Ttsu Reader, Asbplayer, and Mokuro Reader.',
    },
    {
      icon: 'material-symbols-light:dictionary',
      title: 'Pop-up Dictionary',
      description: 'See words definitions, parts of speech and frequency.',
    },
    {
      icon: 'material-symbols-light:check-circle',
      title: 'Vocabulary Tracking',
      description: 'Review vocabulary. See their status with different colours.',
    },
    {
      icon: 'material-symbols-light:pie-chart',
      title: 'Coverage Display',
      description: 'See how much of the content you can read at a glance.',
    },
    {
      icon: 'material-symbols-light:palette',
      title: 'Smart Text Colouring',
      description: 'Words are coloured by knowledge state for easy visual scanning.',
    },
    {
      icon: 'material-symbols-light:target',
      title: 'I+1 Sentences',
      description: 'Highlight sentences with exactly one unknown word.',
    },
    {
      icon: 'material-symbols-light:trending-up',
      title: 'High Frequency Words',
      description: 'Mark and prioritise high-frequency vocabulary for faster progress.',
    },
    {
      icon: 'material-symbols-light:group',
      title: 'Multiple Profiles',
      description: 'Switch between users or presets for different workflows.',
    },
    {
      icon: 'material-symbols-light:tune',
      title: 'Full Customisation',
      description: 'Custom CSS, theme colours, and keyboard shortcuts to match your style.',
    },
  ];

  const supportedApps = [
    { name: 'Ttsu Reader', url: 'https://reader.ttsu.app' },
    { name: 'Asbplayer', url: 'https://github.com/killergerbah/asbplayer' },
    { name: 'Mokuro Reader', url: 'https://reader.mokuro.app' },
    { name: 'Satori Reader', url: 'https://www.satorireader.com' },
    { name: 'Wikipedia 日本語', url: 'https://ja.wikipedia.org' },
    { name: 'NHK News Web Easy', url: 'https://www3.nhk.or.jp/news/easy/' },
    { name: 'Bunpro', url: 'https://bunpro.jp' },
    { name: 'Readwok', url: 'https://app.readwok.com' },
    { name: 'Aozora Bunko', url: 'https://www.aozora.gr.jp' },
    { name: 'LunaTranslator', url: 'https://docs.lunatranslator.org' },
    { name: 'Texthooker Pages', url: null },
  ];

  const screenshots = [
    { src: '/img/jitenreader_popup.webp', alt: "Jiten Reader Japanese popup dictionary showing a word's reading, definition, part of speech and frequency" },
    { src: '/img/jitenreader_colouring.webp', alt: 'Japanese text with words coloured by knowledge state in Jiten Reader' },
    { src: '/img/jitenreader_customisation.webp', alt: 'Jiten Reader customisation panel for colours, popup behaviour and display options' },
  ];
</script>

<template>
  <div class="container mx-auto px-4 py-10">
    <div class="max-w-5xl mx-auto">
      <!-- Hero Section -->
      <Card class="shadow-lg mb-4">
        <template #content>
          <div class="text-center">
            <div class="flex items-center justify-center mb-4 gap-2">
              <img src="~assets/img/jitenreader_logo.png" alt="Jiten Reader Logo" class="h-12" />
              <h1 class="text-4xl font-bold">Jiten Reader</h1>
            </div>

            <p class="text-xl text-gray-600 dark:text-gray-300 mb-6">Parse Japanese text anywhere on the web. Learn by immersion.</p>

            <p class="text-gray-700 dark:text-gray-300 leading-relaxed max-w-2xl mx-auto">
              Jiten Reader is a free browser extension that brings Jiten's vocabulary tracking and dictionary lookup to any Japanese content you encounter
              online.
            </p>
            <p class="text-gray-700 dark:text-gray-300 leading-relaxed mb-4 max-w-2xl mx-auto">
              Track your known words, see your coverage, and review vocabulary while immersing in content you like.
            </p>

            <!-- Download Buttons -->
            <div class="flex flex-col sm:flex-row gap-4 justify-center mb-4">
              <a
                href="https://chromewebstore.google.com/detail/jiten-reader/fkegmlkjkenojfiplaclhlmncfeooaeo"
                target="_blank"
                rel="noopener noreferrer"
                class="no-underline"
              >
                <Button severity="primary" size="large" class="w-full sm:w-auto">
                  <Icon name="logos:chrome" class="mr-2" size="1.5em" />
                  Get for Chrome
                </Button>
              </a>
              <a href="https://addons.mozilla.org/en-US/firefox/addon/jiten-reader/" target="_blank" rel="noopener noreferrer" class="no-underline">
                <Button severity="secondary" size="large" class="w-full sm:w-auto">
                  <Icon name="logos:firefox" class="mr-2" size="1.5em" />
                  Get for Firefox
                </Button>
              </a>
            </div>

            <!-- Hero Screenshot -->
            <div class="mx-auto" style="max-width: 800px; max-height: 450px; border-radius: var(--radius-md); overflow: hidden">
              <Image src="/img/jitenreader_screenshot.webp" alt="Jiten Reader main interface" class="rounded-lg" preview />
            </div>
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
          <div class="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-6">
            <div
              v-for="feature in features"
              :key="feature.title"
              class="p-4 border border-gray-200 dark:border-gray-700 rounded-lg hover:border-primary transition-colors"
            >
              <div class="flex items-center mb-3">
                <Icon :name="feature.icon" class="text-primary mr-3" size="1.75em" />
                <h3 class="text-lg font-semibold">{{ feature.title }}</h3>
              </div>
              <p class="text-gray-600 dark:text-gray-400">{{ feature.description }}</p>
            </div>
          </div>
        </template>
      </Card>

      <!-- Supported Apps Section -->
      <Card class="shadow-lg mb-4">
        <template #title>
          <div class="flex items-center">
            <Icon name="material-symbols-light:apps" class="mr-2 text-primary" size="1.5em" />
            Works with your favourite apps
          </div>
        </template>
        <template #subtitle>Automatic parsing on 15+ websites and apps</template>
        <template #content>
          <div class="flex flex-wrap gap-3">
            <template v-for="app in supportedApps" :key="app.name">
              <a v-if="app.url" :href="app.url" target="_blank" rel="noopener noreferrer" class="no-underline">
                <Button severity="secondary" size="small" outlined>
                  {{ app.name }}
                </Button>
              </a>
              <Button v-else severity="secondary" size="small" outlined disabled>
                {{ app.name }}
              </Button>
            </template>
            <Button severity="info" size="small" outlined disabled>And more...</Button>
          </div>

          <Message severity="info" class="mt-4">
            <p>Can't find your favourite app? You can add custom URLs for automatic parsing, or parse any page manually in a single click.</p>
          </Message>
        </template>
      </Card>

      <!-- Screenshots Section -->
      <Card class="shadow-lg mb-4">
        <template #title>
          <div class="flex items-center">
            <Icon name="material-symbols-light:screenshot-monitor" class="mr-2 text-primary" size="1.5em" />
            See it in action
          </div>
        </template>
        <template #content>
          <div class="grid grid-cols-1 md:grid-cols-3 gap-4">
            <div v-for="screenshot in screenshots" :key="screenshot.src" class="aspect-[4/3] overflow-hidden rounded-lg">
              <Image :src="screenshot.src" :alt="screenshot.alt" image-class="w-full h-full object-cover" preview />
            </div>
          </div>
        </template>
      </Card>

      <!-- Bottom CTA Section -->
      <Card class="shadow-lg">
        <template #content>
          <div class="text-center">
            <h2 class="text-2xl font-bold mb-4">Start reading Japanese today</h2>
            <p class="text-gray-600 dark:text-gray-300 mb-6">Free, open source, and designed for immersion learners.</p>

            <!-- Download Buttons -->
            <div class="flex flex-col sm:flex-row gap-4 justify-center mb-6">
              <a
                href="https://chromewebstore.google.com/detail/jiten-reader/fkegmlkjkenojfiplaclhlmncfeooaeo"
                target="_blank"
                rel="noopener noreferrer"
                class="no-underline"
              >
                <Button severity="primary" size="large" class="w-full sm:w-auto">
                  <Icon name="logos:chrome" class="mr-2" size="1.5em" />
                  Get for Chrome
                </Button>
              </a>
              <a href="https://addons.mozilla.org/en-US/firefox/addon/jiten-reader/" target="_blank" rel="noopener noreferrer" class="no-underline">
                <Button severity="secondary" size="large" class="w-full sm:w-auto">
                  <Icon name="logos:firefox" class="mr-2" size="1.5em" />
                  Get for Firefox
                </Button>
              </a>
            </div>

            <Divider />

            <!-- Links -->
            <div class="flex flex-col sm:flex-row gap-4 justify-center text-sm">
              <a :href="discordUrl" target="_blank" rel="noopener noreferrer" class="flex items-center justify-center gap-2">
                <Icon name="ic:baseline-discord" size="1.25em" />
                Join our Discord
              </a>
              <a href="https://github.com/Sirush/JitenReader" target="_blank" rel="noopener noreferrer" class="flex items-center justify-center gap-2">
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
