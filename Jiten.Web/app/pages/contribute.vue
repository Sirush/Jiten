<script setup lang="ts">
  import { type GlobalStats, MediaType } from '~/types';

  const discordUrl = getDiscordLink();

  const globalStatsUrl = 'stats/get-global-stats';
  const { data: response, status, error } = await useApiFetch<GlobalStats>(globalStatsUrl);
  const mediaByTypeSorted = computed(() => Object.entries(response.value?.mediaByType ?? {}).sort(([, a], [, b]) => b - a));
</script>

<template>
  <div class="container mx-auto px-4 py-10">
    <Card class="mx-auto shadow-lg">
      <template #title>Contribute to Jiten</template>
      <template #subtitle>Help us grow our library of Japanese content</template>
      <template #content>
        <p class="text-gray-700 dark:text-gray-300 leading-relaxed mb-4">Want to contribute to Jiten? Please join our community Discord and say hi!</p>

        <Message severity="info" class="mb-4">
          We're especially looking for new Visual Novels and Video Games.
          <br />
          If you are able to extract new scripts, please help us by contributing!
        </Message>

        <div class="mb-6">
          <a :href="discordUrl" target="_blank" rel="noopener noreferrer">
            <Button label="Join our Discord" icon="pi pi-discord" severity="primary" class="w-full sm:w-auto" />
          </a>
        </div>

        <Divider />

        <h2 class="text-xl font-semibold mb-3">We accept contributions in the following formats:</h2>
        <ul class="list-disc pl-6 space-y-2 text-gray-700 dark:text-gray-300">
          <li>
            <span class="font-medium">Anime, dramas, movies:</span>
            <span class="ml-1 font-semibold">.srt, .ass</span>
          </li>
          <li>
            <span class="font-medium">Novels, non-fiction:</span>
            <span class="ml-1 font-semibold">.epub, .txt, .html</span>
          </li>
          <li>
            <span class="font-medium">Visual novels, video games:</span>
            <span class="ml-1 font-semibold">anything that has readable characters in SHIFT-JIS or UTF-8</span>
          </li>
          <li>
            <span class="font-medium">Manga:</span>
            <span class="ml-1 font-semibold">.mokuro</span>
          </li>
        </ul>

        <Divider />

        You can also contribute in other ways like reporting issues with some decks and making suggestions for new features.
      </template>
    </Card>

    <Card class="shadow-lg mb-8 !border-1 !border-purple-500 mt-4">
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

    <Card v-if="status === 'success'" class="mt-4">
      <template #title>
        <div class="flex items-center">
          <Icon name="material-symbols-light:bar-chart" class="mr-2 text-primary" size="1.5em" />
          Global Stats
        </div>
      </template>
      <template #content>
        <div class="mb-3">
          <b>{{ response.totalMojis?.toLocaleString() }}</b>
          characters in
          <b>{{ response.totalMedia?.toLocaleString() }}</b>
          media
        </div>

        <div class="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
          <div v-for="[mediaType, amount] in mediaByTypeSorted" :key="mediaType" class="p-2 border rounded-md">
            <div class="font-medium">
              {{ getMediaTypeText(MediaType[mediaType]) }}
            </div>
            <div class="text-lg font-bold text-primary-600">
              {{ amount?.toLocaleString() }}
            </div>
          </div>
        </div>
      </template>
    </Card>
  </div>
</template>

<style scoped></style>
