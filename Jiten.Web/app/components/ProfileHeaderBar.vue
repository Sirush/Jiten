<script setup lang="ts">
  import type { ProfileHeader, ProfileViewAs } from '~/types';
  import { headerBackground, profilePath, PROFILE_TABS } from '~/utils/profileAppearance';
  import { formatMonthYear } from '~/utils/dateFormat';
  import { describeProfileLink } from '~/utils/profileLinks';

  const props = defineProps<{
    header: ProfileHeader;
    handle: string;
  }>();

  const emit = defineEmits<{ viewAs: [audience: ProfileViewAs | null] }>();

  const { activeTab, name: ownerName } = useProfileContext();

  const name = computed(() => props.header.displayName ?? 'Your profile');
  const banner = computed(() => headerBackground(props.header.headerColour, props.header.avatarColour, props.header.displayName ?? ''));

  useHead(() => ({ htmlAttrs: { style: `--page-header-tint: color-mix(in srgb, ${banner.value} 72%, black)` } }));

  const tabs = computed(() =>
    PROFILE_TABS.filter((t) => (t.key === 'lists' ? props.header.hasLists : t.section === null || props.header.sections.includes(t.section))).map((t) => ({
      ...t,
      to: profilePath(props.handle, t.key),
    }))
  );

  const links = computed(() => props.header.links.map((url) => ({ url, ...describeProfileLink(url) })));

  const pendingLink = ref<string | null>(null);

  function confirmLink(event: MouseEvent, url: string) {
    event.preventDefault();
    pendingLink.value = url;
  }
</script>

<template>
  <section aria-label="Profile header" class="-mt-6">
    <div :style="{ background: banner }">
      <div class="container mx-auto flex max-w-7xl items-end gap-4 px-4 pt-6 sm:gap-5 sm:pt-10">
        <ProfileAvatar
          :name="header.displayName"
          :kanji="header.avatarKanji"
          :colour="header.avatarColour"
          :avatar-style="header.avatarStyle"
          :strokes="header.avatarStrokes"
          :size="120"
          framed
          class="relative z-10 -mb-8 shadow-md max-sm:!h-20 max-sm:!w-20 max-sm:!text-[42px] sm:-mb-12"
        />
        <div class="min-w-0 flex-1 pb-3">
          <div class="flex flex-wrap items-baseline gap-x-2.5 gap-y-1">
            <h1 class="text-xl font-bold leading-none text-white sm:text-3xl" v-bind="japaneseTextAttrs(name)">{{ name }}</h1>
            <JitenPlusSupporterMark v-if="header.isJitenPlus" class="text-[0.95rem] sm:text-lg" />
          </div>
          <p v-if="header.bio" class="mt-1.5 line-clamp-3 max-w-2xl whitespace-pre-line text-sm text-white/90" v-bind="japaneseTextAttrs(header.bio)">
            {{ header.bio }}
          </p>
          <div v-if="header.memberSince || header.links.length" class="mt-1.5 flex flex-wrap items-center gap-x-4 gap-y-1 text-sm text-white/80">
            <span v-if="header.memberSince" class="inline-flex items-center gap-1.5">
              <Icon name="material-symbols:calendar-month-outline" size="1rem" aria-hidden="true" />
              Member since {{ formatMonthYear(header.memberSince, { utc: true }) }}
            </span>
            <a
              v-for="link in links"
              :key="link.url"
              :href="link.url"
              :title="link.url"
              target="_blank"
              rel="nofollow ugc noopener noreferrer"
              aria-haspopup="dialog"
              class="inline-flex max-w-full items-center gap-1.5 !text-white/80 hover:!text-white hover:underline"
              @click="confirmLink($event, link.url)"
              @auxclick="$event.button === 1 && confirmLink($event, link.url)"
            >
              <Icon :name="link.icon" size="1rem" aria-hidden="true" />
              <span class="truncate">
                <template v-if="link.platform">
                  <span class="font-medium">{{ link.platform }}</span>
                  {{ link.label }}
                </template>
                <template v-else>{{ link.label }}</template>
              </span>
            </a>
          </div>
        </div>
      </div>
    </div>

    <div class="border-b border-surface-200 bg-surface-0 dark:border-surface-700 dark:bg-surface-900">
      <div class="container mx-auto max-w-7xl px-4">
        <div class="flex min-h-11 flex-wrap items-center gap-x-2 max-sm:pt-1 sm:min-h-14 sm:pl-[124px]">
          <div v-if="header.isOwner" class="flex w-full items-center justify-end gap-2 py-1.5 sm:order-2 sm:w-auto">
            <Button
              label="View as visitor"
              aria-label="View as visitor"
              icon="pi pi-eye"
              severity="secondary"
              text
              size="small"
              :pt="{ label: { class: 'max-sm:hidden' } }"
              @click="emit('viewAs', 'everyone')"
            />
            <Button as="router-link" to="/settings/profile" label="Edit profile" icon="pi pi-pencil" severity="secondary" outlined size="small" />
            <ProfileShareButton v-if="header.displayName" :username="header.displayName" />
          </div>
          <div
            v-else-if="header.previewAs"
            role="status"
            class="flex w-full items-center justify-end gap-3 py-1.5 text-sm sm:order-2 sm:w-auto"
          >
            <span class="inline-flex items-center gap-1.5 font-medium text-primary-700 dark:text-primary-300">
              <i class="pi pi-eye" aria-hidden="true" />
              <span class="max-sm:hidden">Viewing as a visitor</span>
              <span class="sm:hidden">Visitor view</span>
            </span>
            <Button label="Exit preview" severity="secondary" outlined size="small" @click="emit('viewAs', null)" />
          </div>
          <div v-else class="h-8 w-full sm:hidden" aria-hidden="true" />
          <nav aria-label="Profile sections" class="no-scrollbar flex min-w-0 flex-1 overflow-x-auto max-sm:-mx-1 max-sm:basis-full sm:order-1">
            <NuxtLink
              v-for="tab in tabs"
              :key="tab.key"
              :to="tab.to"
              :aria-current="activeTab === tab.key ? 'page' : undefined"
              class="whitespace-nowrap border-b-2 px-2.5 py-3 text-sm font-medium !no-underline transition-colors sm:px-4"
              :class="
                activeTab === tab.key
                  ? 'border-primary-600 !text-primary-700 dark:border-primary-400 dark:!text-primary-300'
                  : 'border-transparent !text-surface-500 hover:!text-surface-900 dark:!text-surface-400 dark:hover:!text-surface-0'
              "
            >
              {{ tab.label }}
            </NuxtLink>
          </nav>
        </div>
      </div>
    </div>
    <ProfileExternalLinkDialog :url="pendingLink" :owner-name="ownerName" @close="pendingLink = null" />
  </section>
</template>
