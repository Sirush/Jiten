<script setup lang="ts">
  import Button from 'primevue/button';

  import { storeToRefs } from 'pinia';
  import { useJitenStore } from '~/stores/jitenStore';
  import { useAuthStore } from '~/stores/authStore';
  import { useSrsStore } from '~/stores/srsStore';

  import { useToast } from 'primevue/usetoast';
  import { ThemeMode } from '~/types';

  const toast = useToast();
  const store = useJitenStore();
  const { displayAdminFunctions, themeMode } = storeToRefs(store);
  const auth = useAuthStore();
  const srs = useSrsStore();
  const { isPlus } = useJitenPlus();

  const mobileMenuOpen = ref(false);
  const toggleMobileMenu = () => (mobileMenuOpen.value = !mobileMenuOpen.value);

  const route = useRoute();
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

  const userInitial = computed(() => auth.user?.userName?.trim()?.slice(0, 2)?.toUpperCase() || '');

  const userMenuItems = computed(() => [
    {
      label: 'Profile',
      icon: 'pi pi-user',
      route: '/profile',
    },
    {
      label: 'Settings',
      icon: 'pi pi-cog',
      route: '/settings',
    },
    {
      label: 'Media Requests',
      icon: 'pi pi-list',
      route: '/requests',
    },
    {
      label: 'Ratings',
      icon: 'pi pi-star',
      route: '/ratings',
    },
    { separator: true },
    {
      label: 'Logout',
      icon: 'pi pi-sign-out',
      command: () => auth.logout(),
    },
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
    <div class="bg-indigo-900">
      <div class="flex justify-between items-center mb-6 mx-auto p-4" :class="route.meta.wide ? 'max-w-7xl' : 'max-w-6xl'">
        <NuxtLink to="/" class="!no-underline" aria-label="Jiten home">
          <span class="text-2xl font-bold text-white"
            >Jiten<span v-if="isPlus" class="text-purple-400 text-sm font-black relative -top-[3px] ml-1">+</span></span
          >
        </NuxtLink>

        <!-- Desktop nav -->
        <nav class="hidden md:flex items-center space-x-4">
          <nuxt-link to="/decks/media" :class="route.path.startsWith('/decks/media') ? 'font-semibold !text-purple-200' : '!text-white'">Media</nuxt-link>
          <nuxt-link
            v-if="auth.isAuthenticated"
            to="/srs/decks"
            class="inline-flex items-center gap-1.5"
            :class="route.path.startsWith('/srs') ? 'font-semibold !text-purple-200' : '!text-white'"
          >
            Study
            <span
              v-if="totalDue > 0"
              class="inline-flex items-center justify-center min-w-[1.1rem] rounded-full bg-white/15 px-1 py-0.5 text-[10px] font-semibold leading-none tabular-nums text-purple-100"
              >{{ dueBadge }}</span
            >
          </nuxt-link>
          <nuxt-link to="/frequency-dictionaries" :class="route.path === '/frequency-dictionaries' ? 'font-semibold !text-purple-200' : '!text-white'"
            >Tools</nuxt-link
          >
          <nuxt-link to="/guides" :class="route.path.startsWith('/guides') ? 'font-semibold !text-purple-200' : '!text-white'">Guides</nuxt-link>
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
            to="/register"
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
            class="inline-flex items-center gap-0.5 p-1 rounded-full text-white cursor-pointer hover:bg-indigo-800 focus:outline-none focus:ring-2 focus:ring-white"
            aria-label="User menu"
            aria-haspopup="true"
            @click="userMenu.toggle($event)"
          >
            <span
              v-if="userInitial"
              class="flex items-center justify-center w-8 h-8 rounded-full bg-purple-200 text-indigo-900 text-xs font-bold tracking-tight"
              >{{ userInitial }}</span
            >
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
        <div class="md:hidden flex items-center gap-1">
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
      <div v-if="mobileMenuOpen" class="md:hidden mx-auto max-w-6xl px-4 pb-4">
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
              v-if="auth.isAuthenticated"
              to="/srs/decks"
              class="py-2 px-3 flex items-center gap-2"
              :class="route.path.startsWith('/srs') ? 'font-semibold !text-purple-200' : '!text-white'"
              @click="mobileMenuOpen = false"
            >
              Study
              <span
                v-if="totalDue > 0"
                class="inline-flex items-center justify-center min-w-[1.1rem] rounded-full bg-white/15 px-1 py-0.5 text-[10px] font-semibold leading-none tabular-nums text-purple-100"
                >{{ dueBadge }}</span
              >
            </nuxt-link>
            <nuxt-link
              v-if="auth.isAuthenticated"
              to="/profile"
              class="py-2 px-3"
              :class="route.path.startsWith('/profile') ? 'font-semibold !text-purple-200' : '!text-white'"
              @click="mobileMenuOpen = false"
              >Profile</nuxt-link
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
              to="/guides"
              class="py-2 px-3"
              :class="route.path.startsWith('/guides') ? 'font-semibold !text-purple-200' : '!text-white'"
              @click="mobileMenuOpen = false"
              >Guides</nuxt-link
            >
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
                to="/register"
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
  <TieredMenu v-if="auth.isAuthenticated" ref="userMenu" :model="userMenuItems" popup>
    <template #item="{ item, props }">
      <NuxtLink v-if="item.route" v-slot="{ href, navigate }" :to="item.route" custom>
        <a :href="href" v-bind="props.action" @click="navigate">
          <span :class="item.icon" />
          <span class="ml-2">{{ item.label }}</span>
        </a>
      </NuxtLink>
      <a v-else v-bind="props.action">
        <span :class="item.icon" />
        <span class="ml-2">{{ item.label }}</span>
      </a>
    </template>
  </TieredMenu>
</template>

<style scoped></style>
