<template>
  <div class="rounded-xl border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 shadow-sm p-5">
    <div class="flex flex-col md:flex-row items-start gap-5">
      <div class="flex-1">
        <div class="flex items-center gap-2 mb-2">
          <Icon name="material-symbols-light:extension" class="text-primary" size="1.5em" />
          <h2 class="text-lg font-bold">Jiten Reader</h2>
        </div>
        <p class="text-sm text-gray-600 dark:text-gray-300 mb-2">
          A free browser extension that helps you read Japanese anywhere on the web. Compatible with apps such as
          <strong>Ttsu Reader, Mokuro Reader, and Asbplayer</strong>.
        </p>
        <p class="text-sm text-gray-600 dark:text-gray-300 mb-4">
          Look up words instantly, sync vocabulary to Jiten, see the coverage of what you're immersing in, add furigana, and more.
        </p>
        <NuxtLink to="/reader" class="no-underline">
          <Button severity="primary">
            <Icon name="material-symbols-light:download" class="mr-2" size="1.25em" />
            Get It Now
          </Button>
        </NuxtLink>
      </div>

      <div class="w-full md:w-72 shrink-0 h-44 md:h-52 overflow-hidden rounded-lg">
        <Image
          src="/img/jitenreader_screenshot.webp"
          alt="Jiten Reader screenshot"
          class="block w-full h-full"
          image-class="w-full h-full object-cover object-top rounded-lg"
          preview
        />
      </div>
    </div>
  </div>
</template>

<style scoped>
  .no-underline {
    text-decoration: none !important;
  }
</style>
