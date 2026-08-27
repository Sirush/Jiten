<script setup lang="ts">
  const highlights = [
    { icon: 'material-symbols-light:palette', label: 'Subtitle colouring' },
    { icon: 'material-symbols-light:blur-on', label: 'Blur known words' },
    { icon: 'material-symbols-light:add-card', label: 'One-click mining' },
    { icon: 'material-symbols-light:school', label: 'Review in the player' },
  ];
</script>

<template>
  <Card class="shadow-lg">
    <template #title>
      <div class="flex items-center">
        <Icon name="material-symbols-light:subtitles" class="mr-2 text-primary" size="1.5em" />
        JitenMPV
        <span class="ml-2 px-2 py-0.5 text-xs font-bold rounded-full bg-emerald-500 text-white">NEW</span>
      </div>
    </template>
    <template #content>
      <div class="flex flex-col md:flex-row items-start gap-6">
        <div class="flex-1">
          <p class="text-gray-700 dark:text-gray-300 mb-4">
            A free plugin for
            <strong>mpv</strong>
            that brings most of Jiten Reader to your video player. Subtitles are coloured by what you already know, and you can blur the words you have learnt
            to test yourself.
          </p>
          <p class="text-gray-700 dark:text-gray-300 mb-4">
            Mine a word and its sentence in one click, and review your cards without leaving the episode. With
            <NuxtLink to="/jiten-plus">Jiten+</NuxtLink>
            , mined cards can have a screenshot or an animated screenshot as well as the audio of the current line.
          </p>
          <div class="flex flex-wrap gap-2 mb-4">
            <span
              v-for="highlight in highlights"
              :key="highlight.label"
              class="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-full text-xs font-medium bg-primary-50 dark:bg-primary-950/40 text-primary-700 dark:text-primary-300"
            >
              <Icon :name="highlight.icon" size="1.1em" />
              {{ highlight.label }}
            </span>
          </div>
          <a href="https://github.com/Sirush/JitenMPV/releases" target="_blank" rel="noopener noreferrer" class="no-underline">
            <Button severity="primary" size="large">
              <Icon name="material-symbols-light:download" class="mr-2" size="1.25em" />
              Get It Now
            </Button>
          </a>
        </div>
        <div class="w-full md:w-80 shrink-0 rounded-lg overflow-hidden">
          <Image
            src="/img/jitenmpv.webp"
            alt="JitenMPV screenshot"
            class="block w-full"
            image-class="w-full h-auto rounded-lg"
            preview
            width="320"
            height="180"
          />
        </div>
      </div>
    </template>
  </Card>
</template>

<style scoped>
  .no-underline {
    text-decoration: none !important;
  }
</style>
