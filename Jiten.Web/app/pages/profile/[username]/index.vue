<script setup lang="ts">
  import { useAuthStore } from '~/stores/authStore';
  import { type UserProfile, type UserAccomplishment, MediaType } from '~/types';
  import { useToast } from 'primevue/usetoast';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';

  const route = useRoute();
  const auth = useAuthStore();
  const toast = useToast();
  const { $api } = useNuxtApp();

  const targetUsername = computed(() => route.params.username as string);
  const isOwnProfile = computed(() => auth.isAuthenticated && auth.user?.userName?.toLowerCase() === targetUsername.value.toLowerCase());

  const isUpdatingVisibility = ref(false);
  const isUpdatingMediaListVisibility = ref(false);
  const selectedTab = ref<string>('global');

  const {
    data: profile,
    error: profileError,
    status: profileStatus,
  } = await useApiFetch<UserProfile>(
    () => `user/profile/${targetUsername.value}`,
    { watch: [targetUsername], deep: true }
  );

  const { data: accomplishmentsData } = await useApiFetch<UserAccomplishment[]>(
    () => `user/profile/${targetUsername.value}/accomplishments`,
    { watch: [targetUsername] }
  );

  watch(targetUsername, () => {
    selectedTab.value = 'global';
  });

  const accomplishments = computed(() => accomplishmentsData.value ?? []);
  const isLoading = computed(() => profileStatus.value === 'pending');
  const profileNotFound = computed(() => {
    if (!profileError.value) return false;
    const err = profileError.value as any;
    return err?.statusCode === 404;
  });
  const error = computed<string | null>(() => {
    if (!profileError.value || profileNotFound.value) return null;
    return 'Failed to load profile';
  });

  const globalAccomplishment = computed(() => accomplishments.value.find((a) => a.mediaType === null));

  const mediaTypeAccomplishments = computed(() => accomplishments.value.filter((a) => a.mediaType !== null));

  const availableTabs = computed(() => {
    const mediaTabs = mediaTypeAccomplishments.value
      .filter((acc) => acc.mediaType !== null)
      .map((acc) => ({
        label: getMediaTypeText(acc.mediaType!),
        value: acc.mediaType!.toString(),
      }))
      .sort((a, b) => a.label.localeCompare(b.label));

    return [{ label: 'Global', value: 'global' }, ...mediaTabs];
  });

  const selectedAccomplishment = computed(() => {
    if (selectedTab.value === 'global') {
      return globalAccomplishment.value;
    }
    const mediaType = parseInt(selectedTab.value);
    return accomplishments.value.find((a) => a.mediaType === mediaType);
  });

  const displayUsername = computed(() => profile.value?.username ?? targetUsername.value);

  const profileIsPublic = computed({
    get: () => profile.value?.isPublic ?? false,
    set: (value: boolean) => {
      if (profile.value) {
        profile.value.isPublic = value;
      }
    },
  });

  const toggleVisibility = async () => {
    if (!isOwnProfile.value || isUpdatingVisibility.value || !profile.value) return;

    isUpdatingVisibility.value = true;
    try {
      const newIsPublic = profile.value.isPublic;
      await $api('user/profile', {
        method: 'PATCH',
        body: { isPublic: newIsPublic },
      });
      toast.add({
        severity: 'success',
        summary: newIsPublic ? 'Your profile is now public and accessible to anyone with the link' : 'Your profile is now private and visible only to you',
        life: 3000,
      });
      if (!newIsPublic && profile.value) profile.value.isMediaListPublic = false;
    } catch (err) {
      if (profile.value) {
        profile.value.isPublic = !profile.value.isPublic;
      }
      toast.add({
        severity: 'error',
        summary: 'Failed to update profile visibility',
        life: 5000,
      });
    } finally {
      isUpdatingVisibility.value = false;
    }
  };

  const profileMediaListIsPublic = computed({
    get: () => profile.value?.isMediaListPublic ?? false,
    set: (value: boolean) => {
      if (profile.value) {
        profile.value.isMediaListPublic = value;
      }
    },
  });

  const toggleMediaListVisibility = async () => {
    if (!isOwnProfile.value || isUpdatingMediaListVisibility.value || !profile.value) return;

    isUpdatingMediaListVisibility.value = true;
    try {
      const newIsPublic = profile.value.isMediaListPublic;
      await $api('user/profile', {
        method: 'PATCH',
        body: { isMediaListPublic: newIsPublic },
      });
      toast.add({
        severity: 'success',
        summary: newIsPublic ? 'Your media list is now public' : 'Your media list is now private',
        life: 3000,
      });
    } catch (err) {
      if (profile.value) {
        profile.value.isMediaListPublic = !profile.value.isMediaListPublic;
      }
      toast.add({
        severity: 'error',
        summary: 'Failed to update media list visibility',
        life: 5000,
      });
    } finally {
      isUpdatingMediaListVisibility.value = false;
    }
  };

  const formatNumber = (num: number | undefined) => {
    if (num === undefined) return '0';
    return num.toLocaleString();
  };

  useHead(() => ({
    title: `Profile - ${displayUsername.value}`,
    meta: [{ name: 'description', content: `User profile for ${displayUsername.value}` }],
  }));
</script>

<template>
  <div class="container mx-auto p-4">
    <div v-if="isLoading" class="flex justify-center items-center min-h-[50vh]">
      <ProgressSpinner />
    </div>

    <div v-else-if="profileNotFound" class="text-center py-16">
      <Card>
        <template #content>
          <div class="flex flex-col items-center gap-4">
            <Icon name="material-symbols:person-off" size="4rem" class="text-gray-400" />
            <h2 class="text-xl font-semibold">Profile not found</h2>
            <p class="text-gray-500">This profile does not exist or is set to private.</p>
            <NuxtLink to="/">
              <Button label="Go Home" icon="pi pi-home" />
            </NuxtLink>
          </div>
        </template>
      </Card>
    </div>

    <div v-else-if="error" class="text-center py-8">
      <Message severity="error">{{ error }}</Message>
    </div>

    <div v-else-if="profile" class="flex flex-col gap-6">
      <Card>
        <template #content>
          <div class="flex justify-between items-center flex-wrap gap-4">
            <div>
              <h1 class="text-2xl font-bold">{{ displayUsername }}</h1>
              <p class="text-gray-500 text-sm">
                <span v-if="profile.isPublic">Public profile</span>
                <span v-else>Private profile</span>
              </p>
            </div>
            <div v-if="isOwnProfile" class="flex flex-col gap-2">
              <div class="flex items-center justify-end gap-3">
                <label for="visibility-toggle" class="text-sm">Public profile</label>
                <ToggleSwitch v-model="profileIsPublic" input-id="visibility-toggle" :disabled="isUpdatingVisibility" @change="toggleVisibility" />
              </div>
              <div v-if="profileIsPublic" class="flex items-center justify-end gap-3">
                <label for="media-list-visibility-toggle" class="text-sm">Public media list</label>
                <ToggleSwitch v-model="profileMediaListIsPublic" input-id="media-list-visibility-toggle" :disabled="isUpdatingMediaListVisibility" @change="toggleMediaListVisibility" />
              </div>
            </div>
          </div>
        </template>
      </Card>

      <div v-if="accomplishments.length === 0" class="text-center py-8">
        <Message severity="info">No accomplishments yet. Complete some media to see your stats!</Message>
      </div>

      <template v-else>
        <Tabs v-model:value="selectedTab">
          <TabList class="flex-wrap">
            <Tab v-for="tab in availableTabs" :key="tab.value" :value="tab.value">
              {{ tab.label }}
            </Tab>
          </TabList>
        </Tabs>

        <!-- Accomplishment Stats -->
        <div v-if="selectedAccomplishment" class="grid grid-cols-2 md:grid-cols-3 gap-4">
          <Card class="text-center">
            <template #content>
              <div class="text-3xl font-bold tabular-nums text-purple-600 dark:text-purple-400">
                {{ formatNumber(selectedAccomplishment.completedDeckCount) }}
              </div>
              <div class="text-sm text-gray-500 mt-1">Completed</div>
            </template>
          </Card>

          <Card class="text-center">
            <template #content>
              <div class="text-3xl font-bold tabular-nums text-purple-600 dark:text-purple-400">
                {{ formatNumber(selectedAccomplishment.totalCharacterCount) }}
              </div>
              <div class="text-sm text-gray-500 mt-1">Characters</div>
            </template>
          </Card>

          <Card class="text-center">
            <template #content>
              <div class="text-3xl font-bold tabular-nums text-purple-600 dark:text-purple-400">
                {{ formatNumber(selectedAccomplishment.totalWordCount) }}
              </div>
              <div class="text-sm text-gray-500 mt-1">Words</div>
            </template>
          </Card>

          <Card class="text-center">
            <template #content>
              <div class="text-3xl font-bold tabular-nums text-purple-600 dark:text-purple-400">
                {{ formatNumber(selectedAccomplishment.uniqueWordCount) }}
              </div>
              <div class="text-sm text-gray-500 mt-1">Unique Words</div>
            </template>
          </Card>

          <Card class="text-center">
            <template #content>
              <div class="text-3xl font-bold tabular-nums text-purple-600 dark:text-purple-400">
                {{ formatNumber(selectedAccomplishment.uniqueWordUsedOnceCount) }}
              </div>
              <div class="text-sm text-gray-500 mt-1">1-Occurrence Words</div>
            </template>
          </Card>

          <Card class="text-center">
            <template #content>
              <div class="text-3xl font-bold tabular-nums text-purple-600 dark:text-purple-400">
                {{ formatNumber(selectedAccomplishment.uniqueKanjiCount) }}
              </div>
              <div class="text-sm text-gray-500 mt-1">Unique Kanji</div>
            </template>
          </Card>
        </div>

        <div v-else class="text-center py-4">
          <Message severity="info">No data for this category</Message>
        </div>

        <div v-if="isOwnProfile" class="flex justify-center gap-3 flex-wrap">
          <NuxtLink :to="{ path: `/profile/${displayUsername}/vocabulary`, query: selectedTab !== 'global' ? { mediaType: selectedTab } : {} }">
            <Button label="View Vocabulary" icon="pi pi-list" />
          </NuxtLink>
          <NuxtLink :to="`/profile/${displayUsername}/media`">
            <Button label="View Media List" icon="pi pi-folder-open" />
          </NuxtLink>
        </div>
        <div v-else-if="profile.isMediaListPublic" class="flex justify-center">
          <NuxtLink :to="`/profile/${displayUsername}/media`">
            <Button label="View Media List" icon="pi pi-folder-open" />
          </NuxtLink>
        </div>
      </template>

      <!-- Study Heatmap -->
      <Card>
        <template #title>
          <div class="flex items-center gap-2">
            <Icon name="material-symbols:calendar-month" />
            Study Activity
          </div>
        </template>
        <template #content>
          <StudyHeatmap :username="displayUsername" />
        </template>
      </Card>

      <!-- Kanji Grid -->
      <Card>
        <template #title>
          <div class="flex items-center gap-2">
            <Icon name="material-symbols:grid-view" />
            Kanji Grid
          </div>
        </template>
        <template #subtitle>
          <span class="text-xs text-surface-400">Kanji are ordered according to their frequency</span>
        </template>
        <template #content>
          <KanjiGrid :username="displayUsername" />
        </template>
      </Card>
    </div>
  </div>
</template>
