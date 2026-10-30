<script setup lang="ts">
  import type { CommunityListProfile } from '~/types';

  const { handle, header, isOwner, viewAsQuery, name } = useProfileContext();

  const { data, status, error } = await useApiFetch<CommunityListProfile>(() => `lists/profile/${encodeURIComponent(handle.value)}`, {
    query: viewAsQuery.value,
  });

  const favouritesPrivate = computed(() => isOwner.value && header.value?.privacy?.sections.favouriteLists !== 'everyone');

  useHead(() => ({ title: `${name.value} - Lists` }));
</script>

<template>
  <ProfileTabGate>
    <div v-if="status === 'pending' && !data" class="grid grid-cols-1 gap-4 sm:grid-cols-2 md:grid-cols-3 lg:grid-cols-4">
      <CommunityListCardSkeleton v-for="n in 4" :key="n" />
    </div>

    <Message v-else-if="error" severity="error" :closable="false">Couldn't load these lists. Refresh the page to try again.</Message>

    <div v-else-if="data" class="flex flex-col gap-8">
      <section aria-labelledby="written-lists-heading" class="flex flex-col gap-3">
        <div class="flex items-baseline justify-between gap-2">
          <h2 id="written-lists-heading" class="text-lg font-semibold">{{ isOwner ? 'Your lists' : `Lists by ${name}` }}</h2>
          <NuxtLink v-if="isOwner" to="/lists?tab=mine" class="text-sm !text-primary-600 hover:underline dark:!text-primary-400">Manage</NuxtLink>
        </div>
        <div v-if="data.written.length" class="grid grid-cols-1 gap-4 sm:grid-cols-2 md:grid-cols-3 lg:grid-cols-4">
          <CommunityListCard v-for="list in data.written" :key="list.listId" :list="list" />
        </div>
        <p v-else class="text-sm text-muted-color">
          <template v-if="isOwner">Lists you publish will show up here. <NuxtLink to="/lists/new" class="!text-primary-600 hover:underline dark:!text-primary-400">Start a list</NuxtLink></template>
          <template v-else>{{ name }} hasn't published a list yet.</template>
        </p>
      </section>

      <section v-if="data.favourited" aria-labelledby="favourited-lists-heading" class="flex flex-col gap-3">
        <div>
          <h2 id="favourited-lists-heading" class="text-lg font-semibold">Favourited lists</h2>
          <p v-if="favouritesPrivate" class="mt-0.5 text-sm text-muted-color">
            Only you can see this section. You can share it in
            <NuxtLink to="/settings/profile" class="!text-primary-600 hover:underline dark:!text-primary-400">privacy settings</NuxtLink>.
          </p>
        </div>
        <div v-if="data.favourited.length" class="grid grid-cols-1 gap-4 sm:grid-cols-2 md:grid-cols-3 lg:grid-cols-4">
          <CommunityListCard v-for="list in data.favourited" :key="list.listId" :list="list" />
        </div>
        <p v-else class="text-sm text-muted-color">
          {{ isOwner ? 'Lists you favourite will show up here.' : `${name} hasn't favourited a list yet.` }}
        </p>
      </section>
    </div>
  </ProfileTabGate>
</template>
