<script setup lang="ts">
  import Card from 'primevue/card';
  import Button from 'primevue/button';
  import Tag from 'primevue/tag';
  import Chart from 'primevue/chart';
  import Dialog from 'primevue/dialog';
  import ProgressSpinner from 'primevue/progressspinner';
  import { useApiFetch } from '~/composables/useApiFetch';
  import type { Issues } from '~/types';

  useHead({
    title: 'Content Issues Dashboard - Jiten Admin',
  });

  definePageMeta({
    middleware: ['auth'],
  });

  const { data: issuesData, status, error } = await useApiFetch<Issues>('admin/issues');

  const showIssuesDialog = ref(false);
  const selectedIssueType = ref<string>('');
  const selectedIssueIds = ref<number[]>([]);

  const issueTypes = computed(() => [
    {
      id: 'missing-romaji',
      name: 'Media Without Romaji Title',
      count: issuesData.value?.missingRomajiTitles?.length || 0,
      icon: 'pi pi-list',
      description: 'Media entries missing romaji titles',
      severity: 'warning',
      ids: issuesData.value?.missingRomajiTitles || [],
    },
    {
      id: 'missing-links',
      name: 'Media Without Links',
      count: issuesData.value?.missingLinks?.length || 0,
      icon: 'pi pi-link',
      description: 'Media entries without external links',
      severity: 'info',
      ids: issuesData.value?.missingLinks || [],
    },
    {
      id: 'zero-characters',
      name: 'Media With Zero Characters',
      count: issuesData.value?.zeroCharacters?.length || 0,
      icon: 'pi pi-exclamation-circle',
      description: 'Media entries with zero characters',
      severity: 'danger',
      ids: issuesData.value?.zeroCharacters || [],
    },
    {
      id: 'missing-release-date',
      name: 'Media Without Release Date',
      count: issuesData.value?.missingReleaseDate?.length || 0,
      icon: 'pi pi-exclamation-circle',
      description: 'Media entries with no release date',
      severity: 'warning',
      ids: issuesData.value?.missingReleaseDate || [],
    },
    {
      id: 'missing-description',
      name: 'Media Without Description',
      count: issuesData.value?.missingDescription?.length || 0,
      icon: 'pi pi-exclamation-circle',
      description: 'Media entries with no description',
      severity: 'warning',
      ids: issuesData.value?.missingDescription || [],
    },
    {
      id: 'missing-genres',
      name: 'Media Without Genres',
      count: issuesData.value?.missingGenres?.length || 0,
      icon: 'pi pi-tags',
      description: 'Media entries with no genre associations',
      severity: 'danger',
      ids: issuesData.value?.missingGenres || [],
    },
    {
      id: 'missing-tags',
      name: 'Media Without Tags',
      count: issuesData.value?.missingTags?.length || 0,
      icon: 'pi pi-tag',
      description: 'Media entries with no tag associations',
      severity: 'info',
      ids: issuesData.value?.missingTags || [],
    },
  ]);

  const totalIssues = computed(() => {
    return issueTypes.value.reduce((total, issue) => total + issue.count, 0);
  });

  const chartData = computed(() => ({
    labels: issueTypes.value.map((issue) => issue.name),
    datasets: [
      {
        data: issueTypes.value.map((issue) => issue.count),
        backgroundColor: ['#FF6384', '#36A2EB', '#FFCE56', '#00AA00', '#AAAA00', '#9966FF', '#FF9933'],
      },
    ],
  }));

  const chartOptions = ref({
    plugins: {
      legend: {
        position: 'right',
      },
    },
    responsive: true,
    maintainAspectRatio: false,
  });

  const getSeverityClass = (severity) => {
    switch (severity) {
      case 'danger':
        return 'bg-red-100 text-red-800 dark:bg-red-900 dark:text-red-200';
      case 'warning':
        return 'bg-yellow-100 text-yellow-800 dark:bg-yellow-900 dark:text-yellow-200';
      case 'info':
        return 'bg-blue-100 text-blue-800 dark:bg-blue-900 dark:text-blue-200';
      default:
        return 'bg-gray-100 text-gray-800 dark:bg-gray-900 dark:text-gray-200';
    }
  };

  const openIssuesDialog = (issue) => {
    selectedIssueType.value = issue.name;
    selectedIssueIds.value = issue.ids;
    showIssuesDialog.value = true;
  };
</script>

<template>
  <div class="container mx-auto p-4">
    <div class="flex items-center mb-6">
      <Button icon="pi pi-arrow-left" class="p-button-text mr-2" @click="navigateTo('/dashboard')" />
      <h1 class="text-3xl font-bold">Content Issues Dashboard</h1>
    </div>

    <!-- Loading State -->
    <div v-if="status == 'pending'" class="flex flex-col gap-2 justify-center items-center py-12">
      <ProgressSpinner style="width: 50px; height: 50px" stroke-width="4" />
      <div class="ml-3">Loading issues data...</div>
    </div>

    <div v-else>
      <!-- Summary Statistics -->
      <div class="grid grid-cols-1 md:grid-cols-3 gap-4 mb-6">
        <Card class="shadow-md">
          <template #title>
            <div class="flex items-center">
              <i class="pi pi-exclamation-triangle mr-2 text-yellow-500" />
              Total Issues
            </div>
          </template>
          <template #content>
            <div class="text-4xl font-bold text-center">{{ totalIssues }}</div>
            <p class="text-center text-gray-500 dark:text-gray-400 mt-2">Issues requiring attention</p>
          </template>
        </Card>

        <Card class="shadow-md md:col-span-2">
          <template #title>
            <div class="flex items-center">
              <i class="pi pi-chart-pie mr-2 text-blue-500" />
              Issues Distribution
            </div>
          </template>
          <template #content>
            <div class="h-64">
              <Chart type="pie" :data="chartData" :options="chartOptions" />
            </div>
          </template>
        </Card>
      </div>

      <!-- Issues Grid -->
      <Card class="shadow-md mb-6">
        <template #title>
          <div class="flex items-center">
            <i class="pi pi-list mr-2 text-blue-500" />
            All Issues
          </div>
        </template>
        <template #content>
          <div class="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
            <div v-for="issue in issueTypes" :key="issue.id" class="border rounded-lg p-4 hover:shadow-md transition-shadow">
              <div class="flex justify-between items-center mb-2">
                <h3 class="font-bold text-lg">{{ issue.name }}</h3>
                <Tag :class="getSeverityClass(issue.severity)">{{ issue.count }}</Tag>
              </div>
              <p class="text-sm text-gray-600 dark:text-gray-300 mb-3">{{ issue.description }}</p>
              <Button
                label="View Issues"
                :icon="issue.icon"
                :class="issue.count > 0 ? 'p-button-primary w-full' : 'p-button-secondary w-full'"
                :disabled="issue.count === 0"
                @click="openIssuesDialog(issue)"
              />
            </div>
          </div>
        </template>
      </Card>
    </div>

    <!-- Issues Dialog -->
    <Dialog
      v-model:visible="showIssuesDialog"
      :header="selectedIssueType"
      :style="{ width: '90vw', maxWidth: '1200px' }"
      :modal="true"
      :closable="true"
      :close-on-escape="true"
    >
      <div class="p-4">
        <div v-if="selectedIssueIds.length === 0" class="text-center py-8">
          <p class="text-gray-500 dark:text-gray-400">No issues found.</p>
        </div>
        <div v-else class="max-h-[70vh] overflow-y-auto">
          <h3 class="text-lg font-semibold mb-4">Media Items with Issues:</h3>
          <div class="grid grid-cols-1 sm:grid-cols-2 md:grid-cols-3 lg:grid-cols-4 gap-4">
            <div v-for="id in selectedIssueIds" :key="id" class="border rounded-lg p-3 hover:shadow-md transition-shadow">
              <NuxtLink
                :to="`/dashboard/media/${id}`"
                class="flex items-center justify-center w-full p-2 hover:bg-gray-100 dark:hover:bg-gray-800 rounded transition-colors"
              >
                <i class="pi pi-external-link mr-2" />
                <span>Media ID: {{ id }}</span>
              </NuxtLink>
            </div>
          </div>
        </div>
      </div>
    </Dialog>
  </div>
</template>

<style scoped></style>
