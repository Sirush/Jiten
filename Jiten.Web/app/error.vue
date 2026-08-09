<script setup lang="ts">
  import type { NuxtError } from '#app';

  const props = defineProps({
    error: { type: Object as PropType<NuxtError>, required: true },
  });

  // Deliberately free of PrimeVue and other app-level dependencies: this page renders precisely when
  // something else has failed, and a broken import here degrades it to a blank page.
  const isNotFound = computed(() => Number(props.error?.statusCode) === 404);

  const heading = computed(() => (isNotFound.value ? 'Page not found' : 'Something went wrong'));
  const message = computed(() => (isNotFound.value
    ? "This page doesn't exist, or the media it pointed to has been removed."
    : 'An unexpected error occurred. Trying again in a moment usually helps.'));

  // Not useRobotsRule: it throws here (nuxt-robots has no per-route context on the error render), and
  // an uncaught throw in this setup silently blanks every binding in the template.
  useSeoMeta({
    title: () => heading.value,
    robots: 'noindex, follow',
  });

  // main.css hides :root until .loaded is set, and this page replaces app.vue rather than nesting in
  // it, so without repeating the hook the whole error page renders invisible.
  onMounted(() => {
    document.documentElement.classList.add('loaded');
  });
</script>

<template>
  <div id="app" class="flex min-h-screen flex-col">
    <AppHeader />

    <div class="container mx-auto flex max-w-6xl flex-grow flex-col items-center justify-center px-4 py-16 text-center">
      <p class="text-primary-500 dark:text-primary-400 text-7xl font-bold">
        {{ error?.statusCode ?? 500 }}
      </p>
      <h1 class="mt-4 text-2xl font-semibold">{{ heading }}</h1>
      <p class="text-surface-600 dark:text-surface-400 mt-2 max-w-md">{{ message }}</p>

      <div class="mt-8 flex flex-wrap justify-center gap-3">
        <!-- Global anchor colouring overrides plain text-white, hence the ! utilities (as in AppFooter). -->
        <NuxtLink
          to="/"
          class="bg-primary-500 hover:bg-primary-600 !text-white !no-underline rounded-md px-4 py-2 text-sm font-medium transition-colors"
        >
          Back to Jiten
        </NuxtLink>
        <NuxtLink
          to="/decks/media"
          class="border-surface-300 !text-surface-700 dark:!text-surface-200 hover:bg-surface-100 dark:border-surface-600 dark:hover:bg-surface-800 !no-underline rounded-md border px-4 py-2 text-sm font-medium transition-colors"
        >
          Browse media
        </NuxtLink>
      </div>
    </div>

    <AppFooter />
  </div>
</template>
