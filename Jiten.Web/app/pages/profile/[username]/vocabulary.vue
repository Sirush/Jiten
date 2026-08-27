<script setup lang="ts">
  import { type AccomplishmentVocabularyDto, type UserProfile, MediaType } from '~/types';
  import { useAuthStore } from '~/stores/authStore';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { debounce } from 'perfect-debounce';
  import { parseStringArray, toBooleanOrNull } from '~/utils/queryParams';

  const route = useRoute();
  const router = useRouter();
  const auth = useAuthStore();

  const targetUsername = computed(() => route.params.username as string);
  const isOwnProfile = computed(() => auth.isAuthenticated && auth.user?.userName?.toLowerCase() === targetUsername.value.toLowerCase());

  const offset = computed(() => (route.query.offset ? Number(route.query.offset) : 0));
  const pageSizeQuery = computed(() => (route.query.pageSize ? Number(route.query.pageSize) : undefined));

  const sortByOptions = ref([
    { label: 'Occurrences', value: 'occurrences' },
    { label: 'Global Frequency', value: 'globalFreq' },
  ]);

  const ALL_MEDIA_TYPES = 'all';

  const mediaTypeOptions = computed(() => {
    const options = [{ label: 'All Media Types', value: ALL_MEDIA_TYPES }];
    for (const type of Object.values(MediaType).filter((v) => typeof v === 'number')) {
      options.push({
        label: getMediaTypeText(type as MediaType),
        value: (type as MediaType).toString(),
      });
    }
    return options;
  });

  const sortDescending = ref(route.query.sortOrder !== 'false' && route.query.sortOrder !== '0');
  const sortBy = ref(route.query.sortBy?.toString() || sortByOptions.value[0].value);
  const mediaTypeFilter = ref(route.query.mediaType?.toString() || ALL_MEDIA_TYPES);
  const { tiers: displayTiers, suspended, redundant, displayFilter, suspendedParam, redundantParam } = useVocabularyDisplayFilter();
  const search = ref(route.query.search?.toString() || '');
  const debouncedSearch = ref(search.value);

  const includePos = ref<string[]>(parseStringArray(route.query.pos));
  const excludePos = ref<string[]>(parseStringArray(route.query.excludePos));
  const hideKanaOnly = ref(toBooleanOrNull(route.query.hideKanaOnly) ?? false);

  watch(sortDescending, (newValue) => {
    router.replace({
      query: { ...route.query, sortOrder: newValue ? '1' : '0', offset: 0 },
    });
  });

  watch(sortBy, (newValue) => {
    router.replace({
      query: { ...route.query, sortBy: newValue, offset: 0 },
    });
  });

  watch(mediaTypeFilter, (newValue) => {
    router.replace({
      query: { ...route.query, mediaType: newValue === ALL_MEDIA_TYPES ? undefined : newValue, offset: 0 },
    });
  });

  const updateSearch = debounce((val: string) => {
    debouncedSearch.value = val;
    router.replace({ query: { ...route.query, search: val || undefined, offset: undefined } });
  }, 300);
  watch(search, updateSearch);

  const debouncedIncludePos = ref([...includePos.value]);
  const debouncedExcludePos = ref([...excludePos.value]);
  const debouncedHideKanaOnly = ref(hideKanaOnly.value);

  const updateAdvancedFilters = debounce(() => {
    debouncedIncludePos.value = [...includePos.value];
    debouncedExcludePos.value = [...excludePos.value];
    debouncedHideKanaOnly.value = hideKanaOnly.value;
    router.replace({
      query: {
        ...route.query,
        pos: includePos.value.length > 0 ? includePos.value.join(',') : undefined,
        excludePos: excludePos.value.length > 0 ? excludePos.value.join(',') : undefined,
        hideKanaOnly: hideKanaOnly.value ? 'true' : undefined,
        offset: 0,
      },
    });
  }, 500);

  watch([includePos, excludePos, hideKanaOnly], updateAdvancedFilters, { deep: true });

  const userNotFound = ref(false);

  const { data: profileData, error: profileError } = await useApiFetch<UserProfile>(`user/profile/${targetUsername.value}`);

  if (profileError.value?.statusCode === 404) {
    userNotFound.value = true;
  }

  const profile = profileData.value;
  const canViewStats = computed(() => isOwnProfile.value || profile?.isPublic);
  const displayUsername = computed(() => profile?.username ?? targetUsername.value);

  const queryParams = computed(() => {
    const params: Record<string, any> = {
      offset: offset.value,
      sortBy: sortBy.value,
      descending: sortDescending.value,
      displayFilter: displayFilter.value,
      suspended: suspendedParam.value,
      redundant: redundantParam.value,
      search: debouncedSearch.value || undefined,
      pos: debouncedIncludePos.value.length > 0 ? debouncedIncludePos.value.join(',') : undefined,
      excludePos: debouncedExcludePos.value.length > 0 ? debouncedExcludePos.value.join(',') : undefined,
      hideKanaOnly: debouncedHideKanaOnly.value || undefined,
      pageSize: pageSizeQuery.value,
    };
    if (mediaTypeFilter.value !== ALL_MEDIA_TYPES) {
      params.mediaType = parseInt(mediaTypeFilter.value);
    }
    return params;
  });

  const {
    data: response,
    status,
    error,
  } = await useApiFetchPaginated<AccomplishmentVocabularyDto>(`user/profile/${targetUsername.value}/accomplishments/vocabulary`, {
    query: queryParams,
    watch: [
      offset,
      sortBy,
      sortDescending,
      mediaTypeFilter,
      displayFilter,
      suspendedParam,
      redundantParam,
      debouncedSearch,
      debouncedIncludePos,
      debouncedExcludePos,
      debouncedHideKanaOnly,
      pageSizeQuery,
    ],
  });

  const { start, end, totalItems, previousLink, nextLink, currentPage, totalPages, pageLinkFor, pageSize } = usePagination(response);

  const listContext = computed(() => ({
    label: `${displayUsername.value} - Vocabulary`,
    sortLabel: sortByOptions.value.find((o) => o.value === sortBy.value)?.label,
    sortDescending: sortDescending.value,
    offset: offset.value,
    totalItems: totalItems.value,
    pageSize: pageSize.value,
  }));

  useHead(() => ({
    title: `${displayUsername.value} - Vocabulary`,
    meta: [{ name: 'description', content: `Vocabulary list for ${displayUsername.value}` }],
  }));
</script>

<template>
  <div class="flex flex-col gap-2">
    <div v-if="userNotFound" class="text-center py-16">
      <Card>
        <template #content>
          <div class="flex flex-col items-center gap-4">
            <Icon name="material-symbols:person-off" size="4rem" class="text-gray-400" />
            <h2 class="text-xl font-semibold">User not found</h2>
            <p class="text-gray-500 dark:text-gray-400">This user does not exist or has been removed.</p>
            <NuxtLink to="/">
              <Button label="Go Home" icon="pi pi-home" />
            </NuxtLink>
          </div>
        </template>
      </Card>
    </div>

    <div v-else-if="!canViewStats" class="text-center py-16">
      <Card>
        <template #content>
          <div class="flex flex-col items-center gap-4">
            <Icon name="material-symbols:lock" size="4rem" class="text-gray-400" />
            <h2 class="text-xl font-semibold">This profile is private</h2>
            <p class="text-gray-500 dark:text-gray-400">This user has chosen to keep their profile private.</p>
            <NuxtLink :to="`/profile/${targetUsername}`">
              <Button label="Back to Profile" icon="pi pi-arrow-left" />
            </NuxtLink>
          </div>
        </template>
      </Card>
    </div>

    <div v-else class="flex flex-col gap-2">
      <div class="flex items-center gap-2">
        <NuxtLink :to="`/profile/${displayUsername}`" class="flex items-center gap-1 text-purple-600 hover:text-purple-800">
          <Icon name="material-symbols:arrow-back" />
          Back to Profile
        </NuxtLink>
      </div>

      <h1 class="text-xl font-semibold">Vocabulary from completed media</h1>

      <VocabularyFilters
        v-model:sort-by="sortBy"
        v-model:sort-descending="sortDescending"
        v-model:display-tiers="displayTiers"
        v-model:suspended="suspended"
        v-model:redundant="redundant"
        v-model:search="search"
        v-model:include-pos="includePos"
        v-model:exclude-pos="excludePos"
        v-model:hide-kana-only="hideKanaOnly"
        :sort-by-options="sortByOptions"
        :show-display-filter="auth.isAuthenticated"
        sort-by-width="md:w-44"
      >
        <FloatLabel variant="on">
          <Select
            v-model="mediaTypeFilter"
            :options="mediaTypeOptions"
            option-label="label"
            option-value="value"
            placeholder="Media Type"
            input-id="mediaType"
            class="w-full md:w-44"
            scroll-height="30vh"
          />
          <label for="mediaType">Media Type</label>
        </FloatLabel>
      </VocabularyFilters>

      <PaginationControls
        v-if="response?.data?.words?.length"
        :previous-link="previousLink"
        :next-link="nextLink"
        :current-page="currentPage"
        :total-pages="totalPages"
        :page-link-for="pageLinkFor"
        :start="start"
        :end="end"
        :total-items="totalItems"
        item-label="words"
        :page-size="pageSize"
        :page-size-options="[25, 50, 100]"
        page-size-param="pageSize"
        mobile-compact
      />

      <VocabularyList
        :words="response?.data?.words ?? []"
        :status="status"
        :error="error"
        :list-context="listContext"
        empty-message="Complete some media to see your vocabulary!"
      />

      <PaginationControls
        v-if="response?.data?.words?.length"
        :previous-link="previousLink"
        :next-link="nextLink"
        :current-page="currentPage"
        :total-pages="totalPages"
        :page-link-for="pageLinkFor"
        :start="start"
        :end="end"
        :total-items="totalItems"
        :scroll-to-top-on-navigate="true"
      />
    </div>
  </div>
</template>
