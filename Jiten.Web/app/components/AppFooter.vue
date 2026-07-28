<script setup lang="ts"></script>

<template>
  <footer class="bg-indigo-900 text-white p-4 bottom-0 w-full">
    <div class="flex flex-col md:flex-row justify-evenly items-center gap-4 max-w-6xl mx-auto">
      <div class="flex flex-col sm:flex-row items-center gap-4">
        <p class="text-sm">© {{ new Date().getFullYear() }} Jiten</p>
        <NuxtLink :to="`/terms`" target="_blank" class="!text-white text-sm">Terms of Service</NuxtLink>
        <NuxtLink :to="`/privacy`" target="_blank" class="!text-white text-sm">Privacy Policy</NuxtLink>
      </div>
      <div class="flex items-center space-x-4">
        <a href="https://github.com/Sirush/Jiten" target="_blank">
          <Icon name="mdi:github" size="2em" class="text-white" />
        </a>
        <a :href="getDiscordLink()" target="_blank">
          <Icon name="ic:baseline-discord" size="2em" class="text-white" />
        </a>
      </div>
      <div class="flex space-x-4">
        <NuxtLink to="/media-updates" class="!text-white" no-rel>New media updates</NuxtLink>
        <NuxtLink to="/updates" class="!text-white" no-rel>What's new</NuxtLink>
        <NuxtLink to="/donate" class="!text-white" no-rel>Donate</NuxtLink>
      </div>
    </div>
    <p class="text-xs text-center text-gray-300 mt-3 max-w-4xl mx-auto">
      Decks, frequency lists, and derived data are licensed under
      <a href="https://creativecommons.org/licenses/by-sa/4.0/" target="_blank" class="underline hover:text-white">CC BY-SA 4.0</a>.
    </p>
    <p class="text-xs text-center text-gray-300 mt-1 max-w-4xl mx-auto">
      This site uses dictionary data from
      <a href="https://www.edrdg.org/wiki/index.php/JMdict-EDICT_Dictionary_Project" target="_blank" class="underline hover:text-white">JMdict</a>,
      <a href="https://www.edrdg.org/wiki/index.php/JMnedict" target="_blank" class="underline hover:text-white">JMnedict</a>, and
      <a href="https://www.edrdg.org/wiki/KANJIDIC_Project.html" target="_blank" class="underline hover:text-white">KANJIDIC</a>,
      which are the property of the
      <a href="https://www.edrdg.org/" target="_blank" class="underline hover:text-white">Electronic Dictionary Research and Development Group</a>,
      and are used in conformance with the Group's
      <a href="https://www.edrdg.org/edrdg/licence.html" target="_blank" class="underline hover:text-white">licence</a>.
    </p>
  </footer>
</template>

<style scoped></style>
