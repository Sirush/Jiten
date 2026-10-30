<script setup lang="ts">
  import Button from 'primevue/button';

  import { storeToRefs } from 'pinia';
  import { useJitenStore } from '~/stores/jitenStore';
  import { useAuthStore } from '~/stores/authStore';
  import { useSrsStore } from '~/stores/srsStore';

  import { useToast } from 'primevue/usetoast';
  import { ThemeMode } from '~/types';
  import { ownProfileHandle, profilePath } from '~/utils/profileAppearance';

  const toast = useToast();
  const store = useJitenStore();
  const { displayAdminFunctions, themeMode } = storeToRefs(store);
  const auth = useAuthStore();
  const srs = useSrsStore();
  const { isPlus } = useJitenPlus();

  const mobileMenuOpen = ref(false);
  const toggleMobileMenu = () => (mobileMenuOpen.value = !mobileMenuOpen.value);

  const communityUrl = useRuntimeConfig().public.communityUrl as string;

  const route = useRoute();
  const isAuthPage = computed(() => ['/login', '/register', '/google-registration', '/confirm-email'].includes(route.path));
  const { authLink } = useGuestPrompt();
  const registerLink = computed(() => (isAuthPage.value ? { path: '/register' } : authLink('/register')));
  watch(
    () => route.fullPath,
    () => {
      mobileMenuOpen.value = false;
    }
  );

  function applyTheme(mode: ThemeMode) {
    const shouldBeDark = mode === ThemeMode.Dark || (mode === ThemeMode.Auto && window.matchMedia('(prefers-color-scheme: dark)').matches);
    document.documentElement.classList.toggle('dark-mode', shouldBeDark);
  }

  const themeLabels: Record<ThemeMode, string> = {
    [ThemeMode.Light]: 'light',
    [ThemeMode.Dark]: 'dark',
    [ThemeMode.Auto]: 'system',
  };

  function cycleTheme() {
    const systemIsDark = window.matchMedia('(prefers-color-scheme: dark)').matches;
    const opposite = systemIsDark ? ThemeMode.Light : ThemeMode.Dark;
    const same = systemIsDark ? ThemeMode.Dark : ThemeMode.Light;
    const order = [ThemeMode.Auto, opposite, same];
    const next = order[(order.indexOf(themeMode.value) + 1) % order.length];
    themeMode.value = next;
    applyTheme(next);
    toast.add({ severity: 'info', summary: `Switched to ${themeLabels[next].toLowerCase()} theme`, life: 1500, group: 'bottom' });
  }

  const themeIcon = computed(() => {
    if (themeMode.value === ThemeMode.Light) return 'line-md:sun-rising-loop';
    if (themeMode.value === ThemeMode.Dark) return 'line-md:moon-rising-loop';
    return 'line-md:light-dark';
  });

  const themeLabel = computed(() => {
    if (themeMode.value === ThemeMode.Light) return 'Light';
    if (themeMode.value === ThemeMode.Dark) return 'Dark';
    return 'Auto';
  });

  watch(themeMode, (mode) => applyTheme(mode));

  onMounted(() => {
    applyTheme(store.themeMode);
    window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => {
      if (store.themeMode === ThemeMode.Auto) {
        applyTheme(ThemeMode.Auto);
      }
    });
  });

  const { startPolling, stopPolling } = useNotifications();
  watch(
    () => auth.isAuthenticated,
    (isAuth) => {
      if (isAuth) startPolling();
      else stopPolling();
    },
    { immediate: true }
  );
  onUnmounted(() => stopPolling());

  const { open: openSearch } = useHeaderSearch();

  const settings = ref();
  const userMenu = ref();
  const mediaMenu = ref();

  const mediaMenuItems = [
    { label: 'Browse media', hint: 'Every title on Jiten', icon: 'material-symbols:auto-stories-outline', route: '/decks/media' },
    { label: 'Community lists', hint: 'Picked by other learners', icon: 'material-symbols:format-list-bulleted-rounded', route: '/lists' },
  ];
  const isMediaSection = computed(() => route.path.startsWith('/decks/media') || route.path.startsWith('/lists'));

  /** Delay lets the pointer cross the gap between a header link and its menu without closing it. */
  const HOVER_MENU_CLOSE_DELAY_MS = 200;
  function hoverMenu(menu: Ref<{ show: (event: Event) => void; hide: () => void } | undefined>) {
    let closeTimer: ReturnType<typeof setTimeout> | undefined;
    return {
      open(event: Event) {
        clearTimeout(closeTimer);
        menu.value?.show(event);
      },
      scheduleClose() {
        clearTimeout(closeTimer);
        closeTimer = setTimeout(() => menu.value?.hide(), HOVER_MENU_CLOSE_DELAY_MS);
      },
      cancelClose: () => clearTimeout(closeTimer),
    };
  }

  const { open: openMediaMenu, scheduleClose: scheduleMediaMenuClose, cancelClose: cancelMediaMenuClose } = hoverMenu(mediaMenu);

  const toolsMenu = ref();
  const toolsMenuItems = computed(() => [
    { label: 'Frequency lists', hint: 'Yomitan and Anki frequency dictionaries', icon: 'material-symbols:bar-chart-rounded', route: '/frequency-dictionaries' },
    { label: 'Site stats', hint: 'What everyone reads and learns', icon: 'material-symbols:monitoring', route: '/stats' },
    ...(auth.isAuthenticated
      ? [{ label: 'Kanji grid', hint: 'Every kanji, coloured by how well you know it', icon: 'material-symbols:grid-view-outline', route: '/kanji-grid' }]
      : []),
  ]);
  const isToolsSection = computed(() => toolsMenuItems.value.some((item) => route.path.startsWith(item.route)));
  const { open: openToolsMenu, scheduleClose: scheduleToolsMenuClose, cancelClose: cancelToolsMenuClose } = hoverMenu(toolsMenu);
  const { open: openUserMenu, scheduleClose: scheduleUserMenuClose, cancelClose: cancelUserMenuClose } = hoverMenu(userMenu);

  watch(
    () => route.fullPath,
    () => {
      mediaMenu.value?.hide();
      toolsMenu.value?.hide();
      userMenu.value?.hide();
    }
  );

  const avatarName = computed(() => auth.user?.displayName ?? auth.user?.userName ?? null);

  const NuxtLink = resolveComponent('NuxtLink');

  const ownLibraryPath = computed(() => {
    const handle = ownProfileHandle(auth.user);
    return handle ? profilePath(handle, 'library') : null;
  });
  const isOwnLibrary = computed(() => !!ownLibraryPath.value && decodeURI(route.path) === decodeURI(ownLibraryPath.value));

  const userMenuItems = computed(() => [
    { label: 'Profile', icon: 'material-symbols:person-outline', route: '/profile', active: route.path.startsWith('/profile') && !isOwnLibrary.value },
    ...(ownLibraryPath.value
      ? [{ label: 'Library', icon: 'mdi:bookshelf', route: ownLibraryPath.value, active: isOwnLibrary.value }]
      : []),
    { label: 'Settings', icon: 'material-symbols:settings-outline', route: '/settings', active: route.path.startsWith('/settings') },
    { label: 'Media Requests', icon: 'material-symbols:post-add', route: '/requests', active: route.path.startsWith('/requests') },
    { label: 'Ratings', icon: 'material-symbols:star-outline', route: '/ratings', active: route.path.startsWith('/ratings') },
    { separator: true },
    { label: 'Logout', icon: 'material-symbols:logout', command: () => auth.logout() },
  ]);

  const { totalDue } = useStudySummary();
  const dueBadge = computed(() => (totalDue.value > 999 ? '999+' : String(totalDue.value)));

  watch(
    () => auth.isAuthenticated,
    (ok) => {
      if (ok && !srs.dueSummary) srs.fetchDueSummary();
    },
    { immediate: true }
  );

  const toggleSettings = (event: boolean) => {
    settings.value.toggle(event);
  };

  const showSettings = (event: boolean) => {
    settings.value.show(event);
  };
</script>

<template>
  <header>
    <div class="app-header-bar">
      <div class="flex justify-between items-center mb-6 mx-auto p-4" :class="route.meta.wide ? 'max-w-7xl' : 'max-w-6xl'">
        <NuxtLink to="/" class="!no-underline" aria-label="Jiten home">
          <span class="text-2xl font-bold text-white"
            >Jiten<span v-if="isPlus" class="text-purple-400 text-sm font-black relative -top-[3px] ml-1">+</span></span
          >
        </NuxtLink>

        <!-- Desktop nav -->
        <nav class="hidden min-[900px]:flex items-center space-x-4">
          <div class="inline-flex items-center" @mouseenter="openMediaMenu" @mouseleave="scheduleMediaMenuClose">
            <nuxt-link to="/decks/media" :class="isMediaSection ? 'font-semibold !text-purple-200' : '!text-white'">Media</nuxt-link>
            <button
              type="button"
              class="inline-flex items-center rounded text-white hover:bg-indigo-800 focus:outline-none focus:ring-2 focus:ring-white"
              aria-label="More media pages"
              aria-haspopup="true"
              @click="mediaMenu.toggle($event)"
            >
              <Icon name="material-symbols:keyboard-arrow-down" size="16" />
            </button>
          </div>
          <nuxt-link
            v-if="auth.isAuthenticated"
            to="/srs/decks"
            class="inline-flex items-center gap-1.5"
            :class="route.path.startsWith('/srs') ? 'font-semibold !text-purple-200' : '!text-white'"
          >
            Study
            <ClientOnly>
              <span
                v-if="totalDue > 0"
                class="inline-flex items-center justify-center min-w-[1.1rem] rounded-full bg-white/15 px-1 py-0.5 text-[10px] font-semibold leading-none tabular-nums text-purple-100"
                >{{ dueBadge }}</span
              >
            </ClientOnly>
          </nuxt-link>
          <div class="inline-flex items-center" @mouseenter="openToolsMenu" @mouseleave="scheduleToolsMenuClose">
            <nuxt-link to="/frequency-dictionaries" :class="isToolsSection ? 'font-semibold !text-purple-200' : '!text-white'">Tools</nuxt-link>
            <button
              type="button"
              class="inline-flex items-center rounded text-white hover:bg-indigo-800 focus:outline-none focus:ring-2 focus:ring-white"
              aria-label="More tools"
              aria-haspopup="true"
              @click="toolsMenu.toggle($event)"
            >
              <Icon name="material-symbols:keyboard-arrow-down" size="16" />
            </button>
          </div>
          <nuxt-link to="/guides" :class="route.path.startsWith('/guides') ? 'font-semibold !text-purple-200' : '!text-white'">Guides</nuxt-link>
          <a :href="communityUrl" class="!text-white">Community</a>
          <nuxt-link
            v-if="auth.isAuthenticated"
            to="/jiten-plus"
            :class="route.path.startsWith('/jiten-plus') ? 'font-semibold !text-purple-200' : '!text-white'"
            >Jiten+</nuxt-link
          >
          <nuxt-link
            v-if="auth.isAuthenticated && auth.isAdmin && store.displayAdminFunctions"
            to="/dashboard"
            :class="route.path === '/dashboard' ? 'font-semibold !text-purple-200' : '!text-white'"
            >Dashboard</nuxt-link
          >
          <nuxt-link v-if="!auth.isAuthenticated" to="/login" :class="route.path === '/login' ? 'font-semibold !text-purple-200' : '!text-white'"
            >Log in</nuxt-link
          >
          <Button
            v-if="!auth.isAuthenticated"
            as="router-link"
            :to="registerLink"
            size="small"
            class="!bg-white !text-indigo-900 !border-white hover:!bg-purple-100 !font-semibold whitespace-nowrap"
            >Create an account</Button
          >
          <Button text title="Search" aria-label="Search" class="!text-white hover:!bg-indigo-800" @click="openSearch()">
            <Icon name="material-symbols:search" size="22" />
          </Button>
          <button
            v-if="auth.isAuthenticated"
            type="button"
            class="inline-flex items-center gap-0.5 p-1 rounded-lg text-white cursor-pointer hover:bg-indigo-800 focus:outline-none focus:ring-2 focus:ring-white"
            aria-label="User menu"
            aria-haspopup="true"
            @mouseenter="openUserMenu"
            @mouseleave="scheduleUserMenuClose"
            @click="userMenu.toggle($event)"
          >
            <ProfileAvatar
              v-if="avatarName"
              :name="avatarName"
              :kanji="auth.user?.avatarKanji"
              :colour="auth.user?.avatarColour"
              :avatar-style="auth.user?.avatarStyle"
              :size="32"
            />
            <span v-else class="flex items-center justify-center w-8 h-8 rounded-full bg-purple-200 text-indigo-900">
              <Icon name="material-symbols:person" />
            </span>
            <Icon name="material-symbols:keyboard-arrow-down" size="16" />
          </button>
          <NotificationBell v-if="auth.isAuthenticated" />
          <Button type="button" title="Display Settings" severity="secondary" @mouseover="showSettings($event)" @click="toggleSettings($event)">
            <Icon name="material-symbols-light:settings" />
          </Button>

          <Button text :title="`Theme: ${themeLabel}`" :aria-label="`Theme: ${themeLabel}`" class="!text-white hover:!bg-indigo-800" @click="cycleTheme()">
            <Icon :name="themeIcon" />
          </Button>
        </nav>

        <!-- Mobile: search + bell + hamburger -->
        <div class="min-[900px]:hidden flex items-center gap-1">
          <NuxtLink
            v-if="!auth.isAuthenticated && !isAuthPage"
            :to="registerLink"
            class="mr-1 inline-flex items-center min-h-11 px-3 rounded-md bg-white !text-indigo-900 text-sm font-semibold !no-underline whitespace-nowrap focus:outline-none focus:ring-2 focus:ring-white focus:ring-offset-2 focus:ring-offset-indigo-900"
            @click="markGuestPrompt('header_mobile')"
            >Sign up</NuxtLink
          >
          <button
            type="button"
            class="inline-flex items-center justify-center p-2 rounded text-white hover:bg-indigo-800 focus:outline-none focus:ring-2 focus:ring-white"
            aria-label="Search"
            @click="openSearch()"
          >
            <Icon name="material-symbols:search" size="24" />
          </button>
          <NotificationBell v-if="auth.isAuthenticated" />
          <button
            class="inline-flex items-center justify-center p-2 rounded text-white hover:bg-indigo-800 focus:outline-none focus:ring-2 focus:ring-white"
            @click="toggleMobileMenu"
            aria-label="Toggle navigation menu"
            :aria-expanded="mobileMenuOpen.toString()"
          >
            <Icon :name="mobileMenuOpen ? 'material-symbols:close' : 'material-symbols:menu'" size="28" />
          </button>
        </div>
      </div>

      <!-- Mobile menu panel -->
      <div v-if="mobileMenuOpen" class="min-[900px]:hidden mx-auto max-w-6xl px-4 pb-4">
        <div class="bg-indigo-800 rounded-lg shadow-lg divide-y divide-indigo-700">
          <div class="flex flex-col py-2">
            <nuxt-link
              to="/decks/media"
              class="py-2 px-3"
              :class="route.path.startsWith('/decks/media') ? 'font-semibold !text-purple-200' : '!text-white'"
              @click="mobileMenuOpen = false"
              >Media</nuxt-link
            >
            <nuxt-link
              to="/lists"
              class="py-2 pl-7 pr-3 text-sm"
              :class="route.path.startsWith('/lists') ? 'font-semibold !text-purple-200' : '!text-white'"
              @click="mobileMenuOpen = false"
              >Community lists</nuxt-link
            >
            <nuxt-link
              v-if="auth.isAuthenticated"
              to="/srs/decks"
              class="py-2 px-3 flex items-center gap-2"
              :class="route.path.startsWith('/srs') ? 'font-semibold !text-purple-200' : '!text-white'"
              @click="mobileMenuOpen = false"
            >
              Study
              <ClientOnly>
                <span
                  v-if="totalDue > 0"
                  class="inline-flex items-center justify-center min-w-[1.1rem] rounded-full bg-white/15 px-1 py-0.5 text-[10px] font-semibold leading-none tabular-nums text-purple-100"
                  >{{ dueBadge }}</span
                >
              </ClientOnly>
            </nuxt-link>
            <nuxt-link
              v-if="auth.isAuthenticated"
              to="/profile"
              class="py-2 px-3"
              :class="route.path.startsWith('/profile') && !isOwnLibrary ? 'font-semibold !text-purple-200' : '!text-white'"
              @click="mobileMenuOpen = false"
              >Profile</nuxt-link
            >
            <nuxt-link
              v-if="auth.isAuthenticated && ownLibraryPath"
              :to="ownLibraryPath"
              class="py-2 px-3"
              :class="isOwnLibrary ? 'font-semibold !text-purple-200' : '!text-white'"
              @click="mobileMenuOpen = false"
              >Library</nuxt-link
            >
            <nuxt-link
              v-if="auth.isAuthenticated"
              to="/ratings"
              class="py-2 px-3"
              :class="route.path === '/ratings' ? 'font-semibold !text-purple-200' : '!text-white'"
              @click="mobileMenuOpen = false"
              >Ratings</nuxt-link
            >
            <nuxt-link
              v-if="auth.isAuthenticated"
              to="/settings"
              class="py-2 px-3"
              :class="route.path === '/settings' ? 'font-semibold !text-purple-200' : '!text-white'"
              @click="mobileMenuOpen = false"
              >Settings</nuxt-link
            >
            <nuxt-link
              v-if="auth.isAuthenticated"
              to="/requests"
              class="py-2 px-3"
              :class="route.path.startsWith('/requests') ? 'font-semibold !text-purple-200' : '!text-white'"
              @click="mobileMenuOpen = false"
              >Media Requests</nuxt-link
            >
            <nuxt-link
              to="/frequency-dictionaries"
              class="py-2 px-3"
              :class="route.path === '/frequency-dictionaries' ? 'font-semibold !text-purple-200' : '!text-white'"
              @click="mobileMenuOpen = false"
              >Tools</nuxt-link
            >
            <nuxt-link
              v-for="item in toolsMenuItems.slice(1)"
              :key="item.route"
              :to="item.route"
              class="py-2 pl-7 pr-3 text-sm"
              :class="route.path.startsWith(item.route) ? 'font-semibold !text-purple-200' : '!text-white'"
              @click="mobileMenuOpen = false"
              >{{ item.label }}</nuxt-link
            >
            <nuxt-link
              to="/guides"
              class="py-2 px-3"
              :class="route.path.startsWith('/guides') ? 'font-semibold !text-purple-200' : '!text-white'"
              @click="mobileMenuOpen = false"
              >Guides</nuxt-link
            >
            <a :href="communityUrl" class="py-2 px-3 !text-white" @click="mobileMenuOpen = false">Community</a>
            <nuxt-link
              v-if="auth.isAuthenticated"
              to="/jiten-plus"
              class="py-2 px-3"
              :class="route.path.startsWith('/jiten-plus') ? 'font-semibold !text-purple-200' : '!text-white'"
              @click="mobileMenuOpen = false"
              >Jiten+</nuxt-link
            >
            <nuxt-link
              v-if="auth.isAuthenticated && auth.isAdmin && store.displayAdminFunctions"
              to="/dashboard"
              class="py-2 px-3"
              :class="route.path === '/dashboard' ? 'font-semibold !text-purple-200' : '!text-white'"
              @click="mobileMenuOpen = false"
              >Dashboard</nuxt-link
            >
            <a
              v-if="auth.isAuthenticated"
              href="#"
              class="py-2 px-3 !text-white cursor-pointer"
              @click.prevent="
                auth.logout();
                mobileMenuOpen = false;
              "
              >Logout</a
            >
            <template v-else>
              <nuxt-link
                to="/login"
                class="py-2 px-3"
                :class="route.path === '/login' ? 'font-semibold !text-purple-200' : '!text-white'"
                @click="mobileMenuOpen = false"
                >Log in</nuxt-link
              >
              <nuxt-link
                :to="registerLink"
                class="mx-3 my-2 py-2 px-3 rounded-md bg-white text-center !text-indigo-900 font-semibold"
                @click="mobileMenuOpen = false"
                >Create an account</nuxt-link
              >
            </template>
          </div>
          <div class="flex items-center gap-3 py-3 px-3">
            <Button type="button" label="Display" severity="secondary" class="w-full justify-center" @click="toggleSettings($event)">
              <Icon name="material-symbols-light:settings" />
            </Button>
            <Button :label="themeLabel" severity="secondary" class="w-full justify-center" @click="cycleTheme()">
              <Icon :name="themeIcon" />
            </Button>
          </div>
        </div>
      </div>
    </div>
  </header>

  <LazyAppHeaderSettings ref="settings" />
  <LazyHeaderSearchDialog />
  <TieredMenu
    ref="mediaMenu"
    :model="mediaMenuItems"
    popup
    :pt="{
      root: { class: 'min-w-60 !p-1', onMouseenter: cancelMediaMenuClose, onMouseleave: scheduleMediaMenuClose },
      itemContent: { class: '!bg-transparent' },
    }"
  >
    <template #item="{ item }">
      <AppHeaderMenuItem :item="item" />
    </template>
  </TieredMenu>
  <TieredMenu
    ref="toolsMenu"
    :model="toolsMenuItems"
    popup
    :pt="{
      root: { class: 'min-w-60 !p-1', onMouseenter: cancelToolsMenuClose, onMouseleave: scheduleToolsMenuClose },
      itemContent: { class: '!bg-transparent' },
    }"
  >
    <template #item="{ item }">
      <AppHeaderMenuItem :item="item" />
    </template>
  </TieredMenu>
  <TieredMenu
    v-if="auth.isAuthenticated"
    ref="userMenu"
    :model="userMenuItems"
    popup
    :pt="{
      root: { class: 'min-w-48 !p-1', onMouseenter: cancelUserMenuClose, onMouseleave: scheduleUserMenuClose },
      itemContent: { class: '!bg-transparent' },
    }"
  >
    <template #item="{ item }">
      <component
        :is="item.route ? NuxtLink : 'div'"
        :to="item.route"
        class="flex cursor-pointer items-center gap-3 rounded-md px-3 py-2 !no-underline transition-colors"
        :class="item.active ? 'bg-purple-50 dark:bg-purple-900/30' : 'hover:bg-purple-50 dark:hover:bg-purple-900/20'"
      >
        <Icon :name="item.icon" size="20" class="shrink-0 text-purple-600 dark:text-purple-300" />
        <span
          class="text-sm"
          :class="item.active ? 'text-purple-700 dark:text-purple-200' : 'text-surface-900 dark:text-surface-0'"
          >{{ item.label }}</span
        >
      </component>
    </template>
  </TieredMenu>
</template>

<style scoped></style>
