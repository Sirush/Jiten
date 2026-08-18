<script setup lang="ts">
  const route = useRoute();
</script>

<template>
  <footer class="bg-indigo-900 text-white px-4 py-5 w-full">
    <div class="mx-auto flex flex-col items-center gap-3" :class="route.meta.wide ? 'max-w-7xl' : 'max-w-6xl'">
      <div class="flex flex-wrap justify-center items-center gap-x-6 gap-y-2">
        <NuxtLink to="/media-updates" class="!text-white text-sm" no-rel>New media updates</NuxtLink>
        <NuxtLink to="/updates" class="!text-white text-sm" no-rel>What's new</NuxtLink>
        <NuxtLink to="/jiten-plus" class="!text-white text-sm" no-rel>Jiten+</NuxtLink>
        <NuxtLink to="/donate" class="!text-white text-sm" no-rel>Donate</NuxtLink>
        <span class="flex items-center gap-3">
          <a href="https://github.com/Sirush/Jiten" target="_blank" aria-label="GitHub">
            <Icon name="mdi:github" size="1.5em" class="text-white block" />
          </a>
          <a :href="getDiscordLink()" target="_blank" aria-label="Discord">
            <Icon name="ic:baseline-discord" size="1.5em" class="text-white block" />
          </a>
        </span>
      </div>

      <div class="flex flex-wrap justify-center items-center gap-x-4 gap-y-1 text-xs text-gray-300">
        <span>© {{ new Date().getFullYear() }} Jiten</span>
        <NuxtLink :to="`/terms`" target="_blank" class="!text-gray-300 hover:!text-white">Terms of Use</NuxtLink>
        <NuxtLink :to="`/cgv`" target="_blank" class="!text-gray-300 hover:!text-white">Terms of Sale</NuxtLink>
        <NuxtLink :to="`/privacy`" target="_blank" class="!text-gray-300 hover:!text-white">Privacy Policy</NuxtLink>
        <NuxtLink :to="`/mentions-legales`" target="_blank" class="!text-gray-300 hover:!text-white">Legal notice</NuxtLink>
      </div>

      <div class="text-xs text-center text-gray-300 max-w-4xl">
        <p>
          Decks, frequency lists, and derived data are licensed under
          <a href="https://creativecommons.org/licenses/by-sa/4.0/" target="_blank" class="underline hover:text-white">CC BY-SA 4.0</a>.
        </p>
        <p class="mt-1">
          This site uses dictionary data from
          <a href="https://www.edrdg.org/wiki/index.php/JMdict-EDICT_Dictionary_Project" target="_blank" class="underline hover:text-white">JMdict</a>,
          <a href="https://www.edrdg.org/wiki/index.php/JMnedict" target="_blank" class="underline hover:text-white">JMnedict</a>, and
          <a href="https://www.edrdg.org/wiki/KANJIDIC_Project.html" target="_blank" class="underline hover:text-white">KANJIDIC</a>,
          which are the property of the
          <a href="https://www.edrdg.org/" target="_blank" class="underline hover:text-white">Electronic Dictionary Research and Development Group</a>,
          and are used in conformance with the Group's
          <a href="https://www.edrdg.org/edrdg/licence.html" target="_blank" class="underline hover:text-white">licence</a>.
        </p>
      </div>
    </div>
  </footer>
</template>

<style scoped></style>
