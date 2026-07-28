<script setup lang="ts">
  import { type UserMetadata } from '~/types';
  import { useToast } from 'primevue/usetoast';

  interface CoverageRefreshResponse {
    status: 'queued' | 'already_in_progress';
    retryAfterSeconds?: number;
  }

  const { $api } = useNuxtApp();
  const toast = useToast();
  const store = useJitenStore();

  const lastRefresh = ref<Date>();

  onMounted(async () => {
    try {
      const result = await $api<UserMetadata>('user/metadata');
      lastRefresh.value = result.coverageRefreshedAt ? new Date(result.coverageRefreshedAt) : undefined;
    } catch {}
  });

  const status = computed(() =>
    lastRefresh.value ? `Refreshed ${lastRefresh.value.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })}` : 'Never refreshed',
  );

  const isRefreshing = ref(false);

  const refreshCoverage = async () => {
    if (isRefreshing.value) return;
    isRefreshing.value = true;

    try {
      const result = await $api<UserMetadata>('user/metadata');
      const currentRefreshDate = result.coverageRefreshedAt;

      let response: CoverageRefreshResponse | undefined;
      try {
        response = await $api<CoverageRefreshResponse>('user/coverage/refresh', { method: 'POST' });
      } catch (refreshError) {
        console.error('Coverage refresh API error:', refreshError);
        isRefreshing.value = false;
        showErrorToast(toast, 'Error refreshing coverage', extractApiError(refreshError, 'There was an error refreshing your coverage, please try again.'));
        return;
      }

      if (response?.status === 'already_in_progress') {
        showWarnToast(toast, 'Refresh in progress', `Please wait ${response.retryAfterSeconds ?? 90} seconds before trying again.`);
        isRefreshing.value = false;
        return;
      }

      const startTime = Date.now();
      const interval = setInterval(async () => {
        try {
          const result = await $api<UserMetadata>('user/metadata');
          if (result.coverageRefreshedAt !== currentRefreshDate) {
            lastRefresh.value = result.coverageRefreshedAt ? new Date(result.coverageRefreshedAt) : undefined;
            clearInterval(interval);
            isRefreshing.value = false;
            store.bumpCoverageVersion();
            showSuccessToast(toast, 'Coverage successfully refreshed!');
          } else if (Date.now() - startTime >= 60000) {
            clearInterval(interval);
            isRefreshing.value = false;
            showErrorToast(toast, 'Timeout', 'Refreshing your coverage is taking longer than usual, please wait a few minutes and try refreshing the page.');
          }
        } catch {
          clearInterval(interval);
          isRefreshing.value = false;
        }
      }, 6500);
    } catch (error) {
      console.error('Coverage refresh error:', error);
      isRefreshing.value = false;
      showErrorToast(toast, 'Error refreshing coverage', 'There was an error refreshing your coverage, please try again.');
    }
  };
</script>

<template>
  <SettingsTile icon="pi pi-chart-pie" title="Coverage" description="How much of each media you already know." :status="status">
    <Button icon="pi pi-refresh" label="Refresh now" size="small" outlined class="mt-3" @click="refreshCoverage" />
  </SettingsTile>
  <LoadingOverlay :visible="isRefreshing" message="Refreshing your coverage, please wait a few seconds…" />
</template>
