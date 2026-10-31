<script setup lang="ts">
  import type { RouteLocationNormalizedLoaded } from 'vue-router';
  import type { ProfileHeader, ProfileSectionKey, ProfileViewAs } from '~/types';
  import { provideProfileContext } from '~/composables/useProfileContext';
  import { PROFILE_TABS, profileApiPath } from '~/utils/profileAppearance';
  import { useAuthStore } from '~/stores/authStore';

  definePageMeta({
    fullBleed: true,
    /** Case-insensitive, so the redirect to the canonical casing keeps the page and its loaded tab. */
    key: (route) => `profile:${String(route.params.username).toLowerCase()}`,
    validate: (route) => {
      const name = String(route.params.username);
      return name.length >= 1 && name.length <= 64 && !name.includes('/');
    },
  });

  const route = useRoute();
  const routeHandle = computed(() => String(route.params.username));

  const viewAs = ref<ProfileViewAs | null>(null);
  const viewAsQuery = computed(() => (viewAs.value ? { viewAs: viewAs.value } : {}));

  const {
    data: header,
    error,
    status,
    refresh,
    execute,
  } = useApiFetch<ProfileHeader>(() => profileApiPath(routeHandle.value), {
    key: `profile-header:${routeHandle.value}`,
    query: viewAsQuery,
    watch: false,
    immediate: false,
  });
  /** Started by hand: an immediate fetch would hold back the tab's SSR request, and only the client would count it as a useId boundary. */
  const headerReady = header.value === undefined && !error.value ? execute() : Promise.resolve();

  /** The owner's own header, kept through a preview, so the page knows when visitors can't open the profile at all. */
  const ownerHeader = ref<ProfileHeader | null>(null);
  watch(
    header,
    (value) => {
      if (value?.isOwner) ownerHeader.value = value;
    },
    { immediate: true }
  );
  const previewBlocked = computed(
    () => !!viewAs.value && !!ownerHeader.value && (ownerHeader.value.privacy?.profile !== 'public' || !ownerHeader.value.displayName)
  );

  const previewBlockedMessage = computed(() =>
    ownerHeader.value?.displayName
      ? 'Your profile is private, so anyone else who opens this link sees "Profile not found".'
      : 'Your profile stays private until you choose a display name.'
  );

  function setViewAs(next: ProfileViewAs | null) {
    if (viewAs.value === next) return;
    viewAs.value = next;
    if (!previewBlocked.value) void refresh();
  }

  const handle = computed(() => header.value?.displayName ?? routeHandle.value);
  const name = computed(() => header.value?.displayName ?? 'This member');
  const activeTab = computed(() => PROFILE_TABS.find((t) => route.name === `profile-username-${t.key}`)?.key ?? 'overview');

  const notFound = computed(() => apiStatusCode(error.value) === 404);

  watch(
    header,
    async (value) => {
      if (!value?.displayName || value.displayName.toLowerCase() === routeHandle.value.toLowerCase()) return;
      await navigateTo({ path: profilePath(value.displayName, activeTab.value), query: route.query }, { replace: true });
    },
    { immediate: true }
  );

  const isOwner = computed(() => header.value?.isOwner ?? false);
  const needsDisplayName = computed(() => isOwner.value && !header.value?.displayName);

  const auth = useAuthStore();
  async function onDisplayNameSaved() {
    await auth.fetchCurrentUser().catch(() => {});
    await refresh();
  }
  const canSee = (section: ProfileSectionKey) => header.value?.sections.includes(section) ?? false;

  provideProfileContext(
    {
      handle,
      header,
      isOwner,
      canSee,
      viewAs,
      viewAsQuery,
      apiPath: (sub) => profileApiPath(routeHandle.value, sub),
      activeTab,
      name,
    },
    headerReady
  );

  const pageKey = (r: RouteLocationNormalizedLoaded) => `${String(r.name)}|${viewAs.value ?? ''}`;

  useHead(() => ({
    title: header.value?.displayName ? `${header.value.displayName} - Profile` : 'Profile',
    meta: [{ name: 'robots', content: 'noindex' }],
    htmlAttrs: { class: 'stable-gutter' },
  }));
</script>

<template>
  <div class="flex flex-col gap-5 pb-8">
    <ProfileAwaitHeader>
      <div v-if="previewBlocked" class="container mx-auto max-w-6xl px-4">
        <ProfileLockedSection
          page
          icon="material-symbols:visibility-off-outline"
          title="Visitors can't open your profile"
          :message="previewBlockedMessage"
        >
          <div class="flex flex-wrap justify-center gap-2">
            <Button label="Back to your view" icon="pi pi-arrow-left" @click="setViewAs(null)" />
            <Button as="router-link" to="/settings/profile" label="Privacy settings" severity="secondary" outlined />
          </div>
        </ProfileLockedSection>
      </div>

      <section v-else-if="status === 'pending' && !header" aria-busy="true" aria-label="Loading profile" class="-mt-6">
        <div class="bg-surface-300 dark:bg-surface-700">
          <div class="container mx-auto flex max-w-6xl items-end gap-4 px-4 pt-6 sm:gap-5 sm:pt-10">
            <Skeleton class="relative z-10 -mb-8 !h-20 !w-20 shrink-0 sm:-mb-12 sm:!h-[120px] sm:!w-[120px]" border-radius="0.75rem" />
            <div class="flex min-w-0 flex-1 flex-col gap-2 pb-3">
              <Skeleton width="10rem" height="1.75rem" />
              <Skeleton width="14rem" height="1rem" />
            </div>
          </div>
        </div>
        <div class="border-b border-surface-200 bg-surface-0 dark:border-surface-700 dark:bg-surface-900">
          <div class="container mx-auto max-w-6xl px-4">
            <div class="flex min-h-11 items-center gap-4 max-sm:pt-9 sm:min-h-14 sm:pl-[124px]">
              <Skeleton v-for="i in 4" :key="i" width="4.5rem" height="1rem" />
            </div>
          </div>
        </div>
      </section>

      <div v-else-if="notFound" class="container mx-auto max-w-6xl px-4">
        <ProfileLockedSection page icon="material-symbols:person-off" title="Profile not found" message="This profile doesn't exist or isn't shared with you.">
          <Button as="router-link" to="/" label="Go home" />
        </ProfileLockedSection>
      </div>

      <div v-else-if="error" class="container mx-auto max-w-6xl px-4">
        <Message severity="error" :closable="false">Couldn't load this profile. Refresh the page to try again.</Message>
      </div>

      <template v-else-if="header">
        <ProfileHeaderBar :header="header" :handle="handle" @view-as="setViewAs" />
        <div v-if="needsDisplayName" class="container mx-auto max-w-6xl px-4">
          <section
            aria-labelledby="display-name-heading"
            class="flex max-w-xl flex-col gap-3 rounded-lg border border-surface-200 bg-surface-0 p-5 dark:border-surface-700 dark:bg-surface-900"
          >
            <h2 id="display-name-heading" class="m-0 text-lg font-bold">Choose a display name</h2>
            <p class="m-0 text-sm text-muted-color">
              You'll be the only one able to access your profile until you pick one. Your login username stays private.
            </p>
            <DisplayNameForm submit-label="Save" stacked @saved="onDisplayNameSaved" />
          </section>
        </div>
      </template>
    </ProfileAwaitHeader>

    <div v-if="!previewBlocked" class="container mx-auto max-w-6xl px-4 empty:hidden">
      <NuxtPage :page-key="pageKey" />
    </div>
  </div>
</template>
