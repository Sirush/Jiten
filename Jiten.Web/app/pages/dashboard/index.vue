<script setup lang="ts">
  useHead({
    title: 'Dashboard',
  });

  definePageMeta({
    middleware: ['auth-admin'],
  });

  interface DashboardLink {
    title: string;
    description: string;
    icon: string;
    to: string;
  }

  const uploadMedia: DashboardLink = {
    title: 'Upload Media',
    description: 'Add a deck from local files',
    icon: 'pi pi-upload',
    to: '/dashboard/add-media',
  };

  const dataManagement: DashboardLink = {
    title: 'Data Management',
    description: 'Reparse media, recompute frequencies and difficulty',
    icon: 'pi pi-database',
    to: '/dashboard/manage',
  };

  const animeDramaTracking: DashboardLink = {
    title: 'Anime & Drama Tracking',
    description: 'Follow airing shows on Jimaku and ingest new episodes',
    icon: 'pi pi-video',
    to: '/dashboard/anime-drama',
  };

  const addYoutubeSource: DashboardLink = {
    title: 'Add YouTube Source',
    description: 'Track a channel or playlist with manual Japanese subtitles',
    icon: 'pi pi-youtube',
    to: '/dashboard/add-youtube',
  };

  const metadataEdits: DashboardLink = {
    title: 'Metadata Edits',
    description: 'Trust thresholds, auto-apply rules, reviewers and contributors',
    icon: 'pi pi-file-edit',
    to: '/dashboard/metadata-edits/settings',
  };

  const pinnedLinks = [uploadMedia, dataManagement, animeDramaTracking, addYoutubeSource];

  const groups: { id: string; title: string; links: DashboardLink[] }[] = [
    {
      id: 'add-media',
      title: 'Add Media',
      links: [
        uploadMedia,
        { title: 'Add from Jimaku', description: 'Add new media from Jimaku', icon: 'pi pi-comment', to: '/dashboard/add-jimaku' },
        animeDramaTracking,
        { title: 'Add Webnovel', description: 'Import a Syosetu or Kakuyomu novel and keep it in sync', icon: 'pi pi-book', to: '/dashboard/add-webnovel' },
        addYoutubeSource,
      ],
    },
    {
      id: 'catalogue',
      title: 'Catalogue & Metadata',
      links: [
        dataManagement,
        { title: 'Issues', description: 'Missing titles, missing links', icon: 'pi pi-exclamation-triangle', to: '/dashboard/issues' },
        { title: 'Furigana Issues', description: 'Find and fix kanji words missing furigana', icon: 'pi pi-language', to: '/dashboard/furigana-issues' },
        { title: 'Series & Franchises', description: 'Series settings and the franchise builder', icon: 'pi pi-sitemap', to: '/dashboard/series' },
        { title: 'Tags', description: 'Create and edit tags', icon: 'pi pi-tag', to: '/dashboard/tags' },
        { title: 'Genre Mappings', description: 'Map external genres to site genres', icon: 'pi pi-map', to: '/dashboard/genre-mappings' },
        { title: 'Tag Mappings', description: 'Map external tags to site tags', icon: 'pi pi-arrow-right-arrow-left', to: '/dashboard/tag-mappings' },
        { title: 'Word Sets', description: 'Create and edit word sets', icon: 'pi pi-bookmark', to: '/dashboard/word-sets' },
      ],
    },
    {
      id: 'community',
      title: 'Community',
      links: [
        metadataEdits,
        { title: 'Request Activity', description: 'Request activity and user contributions', icon: 'pi pi-inbox', to: '/dashboard/request-activity' },
        { title: 'Community Lists', description: 'Review reports, unlist or restore lists, see views', icon: 'pi pi-list', to: '/dashboard/lists' },
        { title: 'Polls', description: 'Create polls, publish them and read the results', icon: 'pi pi-chart-bar', to: '/dashboard/polls' },
      ],
    },
    {
      id: 'site',
      title: 'Site & Users',
      links: [
        { title: 'Send Notification', description: 'Notify a specific user or everyone', icon: 'pi pi-bell', to: '/dashboard/send-notification' },
        { title: 'Site Updates', description: 'Write, preview and publish changelog entries', icon: 'pi pi-megaphone', to: '/dashboard/updates' },
        { title: 'Maintenance Banner', description: 'Display a site-wide maintenance notice', icon: 'pi pi-wrench', to: '/dashboard/maintenance' },
        { title: 'Jiten+', description: 'Grant Jiten+ rewards and manage promo codes', icon: 'pi pi-gift', to: '/dashboard/jiten-plus' },
        { title: 'Legal Notices', description: 'Send the terms-change notice to paid subscribers', icon: 'pi pi-envelope', to: '/dashboard/legal-notices' },
      ],
    },
  ];
</script>

<template>
  <div class="dashboard-page container mx-auto p-2 md:p-4">
    <h1 class="mb-4 text-2xl font-bold">Dashboard</h1>

    <section class="mb-8 grid grid-cols-2 gap-3 md:grid-cols-3 xl:grid-cols-5" aria-label="Pinned">
      <NuxtLink v-for="link in pinnedLinks" :key="link.to" :to="link.to" class="dash-tile dash-link">
        <i :class="link.icon" class="mb-1 text-xl text-primary-600 dark:text-primary-300" aria-hidden="true" />
        <span class="font-semibold text-surface-900 dark:text-surface-0">{{ link.title }}</span>
        <span class="text-sm text-surface-500 dark:text-surface-400">{{ link.description }}</span>
      </NuxtLink>

      <div class="dash-tile col-span-2 xl:col-span-1">
        <i :class="metadataEdits.icon" class="mb-1 text-xl text-primary-600 dark:text-primary-300" aria-hidden="true" />
        <span class="font-semibold text-surface-900 dark:text-surface-0">{{ metadataEdits.title }}</span>
        <div class="mt-auto flex flex-wrap gap-2 pt-2">
          <NuxtLink to="/review" class="dash-chip">Review Queue</NuxtLink>
          <NuxtLink :to="metadataEdits.to" class="dash-chip">Settings</NuxtLink>
        </div>
      </div>
    </section>

    <div class="grid grid-cols-1 items-start gap-x-8 gap-y-6 sm:grid-cols-2 xl:grid-cols-4">
      <section v-for="group in groups" :key="group.id" :aria-labelledby="`dashboard-${group.id}`">
        <h2 :id="`dashboard-${group.id}`" class="mb-1 border-b border-surface-200 pb-2 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:border-surface-700 dark:text-surface-400">
          {{ group.title }}
        </h2>
        <ul class="-mx-2 flex flex-col">
          <li v-for="link in group.links" :key="link.to">
            <NuxtLink :to="link.to" class="dash-row dash-link">
              <i :class="link.icon" class="mt-0.5 w-5 shrink-0 text-center text-primary-600 dark:text-primary-300" aria-hidden="true" />
              <span class="min-w-0">
                <span class="block font-medium text-surface-900 dark:text-surface-0">{{ link.title }}</span>
                <span class="block text-sm text-surface-500 dark:text-surface-400">{{ link.description }}</span>
              </span>
            </NuxtLink>
          </li>
        </ul>
      </section>
    </div>
  </div>
</template>

<style scoped>
  .dash-tile {
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
    min-width: 0;
    height: 100%;
    padding: 1rem;
    border-radius: var(--radius-lg);
    border: 1px solid var(--p-surface-200);
    background: var(--p-surface-0);
    transition: border-color 0.15s ease;
  }

  :global(.dark-mode .dash-tile) {
    border-color: var(--p-surface-700);
    background: var(--p-surface-900);
  }

  .dash-row {
    display: flex;
    align-items: flex-start;
    gap: 0.75rem;
    padding: 0.5rem;
    border-radius: var(--radius-md);
    transition: background-color 0.15s ease;
  }

  .dashboard-page a.dash-link,
  .dashboard-page a.dash-link:hover {
    color: inherit;
    text-decoration: none;
  }

  .dashboard-page a.dash-tile:hover {
    border-color: var(--p-primary-400);
  }

  :global(.dark-mode .dashboard-page a.dash-tile:hover) {
    border-color: var(--p-primary-500);
  }

  .dashboard-page a.dash-row:hover {
    background: var(--p-surface-100);
  }

  :global(.dark-mode .dashboard-page a.dash-row:hover) {
    background: var(--p-surface-800);
  }

  .dashboard-page a.dash-chip {
    padding: 0.375rem 0.75rem;
    border-radius: var(--radius-md);
    font-size: 0.875rem;
    font-weight: 500;
    color: var(--p-primary-700);
    background: var(--p-primary-50);
    text-decoration: none;
    transition: background-color 0.15s ease;
  }

  .dashboard-page a.dash-chip:hover {
    background: var(--p-primary-100);
  }

  :global(.dark-mode .dashboard-page a.dash-chip) {
    color: var(--p-primary-200);
    background: var(--p-primary-950);
  }

  :global(.dark-mode .dashboard-page a.dash-chip:hover) {
    background: var(--p-primary-900);
  }

  .dashboard-page a.dash-link:focus-visible,
  .dashboard-page a.dash-chip:focus-visible {
    outline: 2px solid var(--p-primary-600);
    outline-offset: 2px;
  }

  :global(.dark-mode .dashboard-page a.dash-link:focus-visible),
  :global(.dark-mode .dashboard-page a.dash-chip:focus-visible) {
    outline-color: var(--p-primary-400);
  }
</style>
