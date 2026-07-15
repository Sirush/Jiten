<script async setup lang="ts">
  import { type UserMetadata } from '~/types';
  import { useToast } from 'primevue/usetoast';

  interface CoverageRefreshResponse {
    status: 'queued' | 'already_in_progress';
    retryAfterSeconds?: number;
  }

  const { $api } = useNuxtApp();
  const toast = useToast();
  const store = useJitenStore();

  let lastRefresh = ref<Date>();

  try {
    const result = await $api<UserMetadata>('user/metadata');
    lastRefresh.value = result.coverageRefreshedAt ? new Date(result.coverageRefreshedAt) : undefined;
  } catch {}

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
  <div>
    <Card>
      <template #title>
        <h3 class="text-lg font-semibold">Coverage</h3>
      </template>
      <template #content>
        <p class="mb-3">
          Your coverage was last refreshed: <b>{{ lastRefresh?.toLocaleString() ?? 'Never' }}</b>
        </p>
        <Button icon="pi pi-refresh" label="Refresh now" class="w-full md:w-64" @click="refreshCoverage" />
      </template>
    </Card>
    <LoadingOverlay :visible="isRefreshing" message="Refreshing your coverage, please wait a few seconds…" />
  </div>
</template>

<style scoped></style>
