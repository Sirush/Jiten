<script setup lang="ts">
  import type { ProfileOverview } from '~/types';
  import { useAuthStore } from '~/stores/authStore';
  import { formatCompact } from '~/utils/siteStats';

  const { isOwner, canSee, viewAs, viewAsQuery, apiPath, name } = useProfileContext();
  const auth = useAuthStore();

  const {
    data: overview,
    status,
    error,
    refresh: refreshOverview,
  } = await useApiFetch<ProfileOverview>(() => apiPath('overview'), { query: viewAsQuery.value });

  const pinPickerOpen = ref(false);

  const snapshot = computed(() => {
    const s = overview.value?.snapshot;
    if (!s) return [];
    return [
      { label: 'Words known', value: s.wordsKnown },
      { label: 'Kanji known', value: s.kanjiKnown },
      { label: 'Characters', value: s.characters },
      { label: 'Titles finished', value: s.titlesFinished },
    ]
      .filter((x): x is { label: string; value: number } => x.value != null)
      .map((x) => ({ label: x.label, value: formatCompact(x.value) }));
  });
</script>

<template>
  <ProfileTabGate>
    <div v-if="status === 'pending' && !overview" class="flex flex-col gap-5 lg:flex-row lg:items-start" aria-busy="true">
      <div class="flex flex-col gap-5 lg:min-w-0 lg:flex-[1_1_20rem]">
        <Skeleton height="8.5rem" border-radius="0.75rem" />
        <Skeleton height="9.5rem" border-radius="0.75rem" />
        <Skeleton height="11rem" border-radius="0.75rem" />
      </div>
      <div class="flex flex-col gap-5 lg:min-w-0 lg:flex-[999_1_34rem]">
        <Skeleton height="10.5rem" border-radius="0.75rem" />
        <Skeleton height="15rem" border-radius="0.75rem" />
        <Skeleton height="11rem" border-radius="0.75rem" />
      </div>
    </div>

    <Message v-else-if="error" severity="error" :closable="false">Couldn't load this profile's overview. Refresh the page to try again.</Message>

    <div v-else-if="overview" class="flex flex-col gap-5 lg:flex-row lg:items-start">
      <div class="max-lg:contents lg:flex lg:min-w-0 lg:flex-[1_1_20rem] lg:flex-col lg:gap-5">
        <Card v-if="snapshot.length" class="max-lg:order-1">
          <template #title>
            <h2 class="text-base font-semibold">Japanese</h2>
          </template>
          <template #content>
            <dl class="grid grid-cols-2 gap-4">
              <ProfileStatTile v-for="item in snapshot" :key="item.label" :label="item.label" :value="item.value" />
            </dl>
          </template>
        </Card>

        <ProfileGenresCard v-if="overview.genres?.length" :genres="overview.genres" class="max-lg:order-5" />

        <Card v-if="overview.favouriteWords?.length || (isOwner && overview.favouriteWords)" class="max-lg:order-6">
          <template #title>
            <div class="flex items-baseline justify-between gap-2">
              <h2 class="text-base font-semibold">Favourite words</h2>
              <NuxtLink v-if="isOwner" to="/settings/profile#favourite-words" class="text-sm !text-primary-600 hover:underline dark:!text-primary-400"
                >Edit</NuxtLink
              >
            </div>
          </template>
          <template #content>
            <p v-if="!overview.favouriteWords?.length" class="text-sm text-muted-color">Pick up to five words to show here.</p>
            <ul v-else class="flex flex-col">
              <li
                v-for="word in overview.favouriteWords"
                :key="`${word.wordId}-${word.readingIndex}`"
                class="border-t border-surface-200 first:border-t-0 dark:border-surface-700"
              >
                <NuxtLink :to="`/vocabulary/${word.wordId}/${word.readingIndex}`" class="flex items-baseline gap-2.5 py-2 !text-surface-900 !no-underline hover:!text-primary-600 dark:!text-surface-0 dark:hover:!text-primary-300">
                  <ProfileFavouriteWord :word="word" meaning-end />
                </NuxtLink>
              </li>
            </ul>
          </template>
        </Card>

        <Card v-if="overview.milestones?.length" class="max-lg:order-7">
          <template #title>
            <h2 class="text-base font-semibold">Milestones</h2>
          </template>
          <template #content>
            <ul class="grid grid-cols-3 gap-x-2 gap-y-4 sm:grid-cols-5 lg:grid-cols-3">
              <li v-for="m in overview.milestones" :key="m.kind">
                <ProfileMilestoneBadge :milestone="m" />
              </li>
            </ul>
          </template>
        </Card>
      </div>

      <div class="max-lg:contents lg:flex lg:min-w-0 lg:flex-[999_1_34rem] lg:flex-col lg:gap-5">
        <Card v-if="overview.currentlyReading" class="max-lg:order-2">
          <template #title>
            <h2 class="text-base font-semibold">In progress</h2>
          </template>
          <template #content>
            <p v-if="overview.currentlyReading.length === 0" class="text-sm text-muted-color">
              {{ isOwner ? 'Set a title to Ongoing and it will show up here.' : `${name} isn't reading anything right now.` }}
            </p>
            <div v-else class="grid gap-4 sm:grid-cols-2 xl:grid-cols-3">
              <ProfileReadingRow v-for="item in overview.currentlyReading" :key="item.deck.deckId" :item="item" />
            </div>
          </template>
        </Card>

        <Card v-if="overview.pinned && (overview.pinned.length || isOwner)" class="max-lg:order-3">
          <template #title>
            <div class="flex items-baseline justify-between gap-2">
              <h2 class="text-base font-semibold">Pinned</h2>
              <div v-if="isOwner" class="flex items-baseline gap-3 text-sm">
                <button v-if="overview.pinned.length" type="button" class="!text-primary-600 hover:underline dark:!text-primary-400" @click="pinPickerOpen = true">Add</button>
                <NuxtLink to="/settings/profile#pinned" class="!text-primary-600 hover:underline dark:!text-primary-400">Edit</NuxtLink>
              </div>
            </div>
          </template>
          <template #content>
            <div v-if="!overview.pinned.length" class="flex flex-col items-start gap-3">
              <p class="text-sm text-muted-color">Pin up to {{ MAX_PROFILE_PINS }} titles from your library to show them here.</p>
              <Button label="Add a title" icon="pi pi-plus" size="small" severity="secondary" @click="pinPickerOpen = true" />
            </div>
            <div v-else class="scrollbar-styled -mx-1 flex gap-3 overflow-x-auto px-1 pb-1 sm:mx-0 sm:grid sm:grid-cols-6 sm:gap-4 sm:overflow-visible sm:px-0 sm:pb-0">
              <ProfileDeckTile
                v-for="item in overview.pinned"
                :key="item.deck.deckId"
                :deck="item.deck"
                :score="item.score"
                class="w-32 shrink-0 sm:w-auto"
              />
              <template v-if="isOwner">
                <button
                  v-for="slot in MAX_PROFILE_PINS - overview.pinned.length"
                  :key="`empty-${slot}`"
                  type="button"
                  class="flex aspect-[2/3] cursor-pointer flex-col items-center justify-center gap-1.5 self-start rounded-md border-2 border-dashed border-surface-300 text-xs text-muted-color transition-colors hover:border-primary-400 hover:text-primary-600 focus-visible:outline-2 focus-visible:outline-primary-500 max-sm:hidden dark:border-surface-600 dark:hover:border-primary-400 dark:hover:text-primary-300"
                  @click="pinPickerOpen = true"
                >
                  <i class="pi pi-plus" aria-hidden="true" />
                  Pin a title
                </button>
              </template>
            </div>
            <ProfilePinPicker v-if="isOwner" v-model:visible="pinPickerOpen" @changed="refreshOverview()" />
          </template>
        </Card>

        <Card v-if="canSee('studyActivity')" class="max-lg:order-4">
          <template #title>
            <h2 class="text-base font-semibold">Study activity</h2>
          </template>
          <template #content>
            <StudyHeatmap :data="overview.heatmap" rolling />
          </template>
        </Card>

        <ProfileSharedShelf v-if="auth.isAuthenticated && !isOwner && !viewAs && canSee('library')" class="max-lg:order-8" />
      </div>
    </div>
  </ProfileTabGate>
</template>
