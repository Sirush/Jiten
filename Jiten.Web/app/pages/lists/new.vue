<script setup lang="ts">
  import type { AccountInfo, CommunityListQuota } from '~/types/types';
  import { COMMUNITY_LIST_FORBIDDEN_CONTENT, COMMUNITY_LIST_MAX_DESCRIPTION, COMMUNITY_LIST_MAX_NAME } from '~/composables/useCommunityLists';
  import { apiErrorMessage } from '~/utils/apiErrorMessage';

  definePageMeta({ middleware: ['auth'] });
  useSeoMeta({ title: 'New list', robots: 'noindex, nofollow' });

  const { $api } = useNuxtApp();
  const lists = useCommunityLists();

  const loading = ref(true);
  const loadFailed = ref(false);
  const displayName = ref<string | null>(null);
  const quota = ref<CommunityListQuota | null>(null);
  const name = ref('');
  const description = ref('');
  const agreedToRules = ref(false);
  const rulesOpen = ref(false);
  const creating = ref(false);
  const createError = ref<string | null>(null);

  onMounted(async () => {
    try {
      const [account, listQuota] = await Promise.all([$api<AccountInfo>('account'), lists.quota()]);
      displayName.value = account.displayName;
      quota.value = listQuota;
    } catch {
      loadFailed.value = true;
    } finally {
      loading.value = false;
    }
  });

  function acceptRules() {
    agreedToRules.value = true;
    rulesOpen.value = false;
  }

  async function create() {
    createError.value = null;
    if (!name.value.trim()) {
      createError.value = 'You need to give the list a name.';
      return;
    }
    if (!agreedToRules.value) {
      createError.value = 'You need to agree to the list rules to create a list.';
      return;
    }
    creating.value = true;
    try {
      const result = await lists.create(name.value.trim(), description.value.trim() || null);
      await navigateTo(`/lists/${result.listId}/edit`);
    } catch (e) {
      createError.value = apiErrorMessage(e, 'Could not create the list. Try again.');
    } finally {
      creating.value = false;
    }
  }
</script>

<template>
  <div class="mx-auto flex w-full max-w-xl flex-col gap-4 px-2 py-6 md:px-4">
    <nav aria-label="Breadcrumb" class="flex items-center gap-2 text-sm text-muted-color">
      <NuxtLink to="/lists">Lists</NuxtLink>
      <span aria-hidden="true">/</span>
      <span>New list</span>
    </nav>

    <Skeleton v-if="loading" height="16rem" />

    <div v-else-if="loadFailed" class="flex flex-col items-center gap-3 py-10 text-center">
      <p class="m-0 text-muted-color">This page didn't load.</p>
      <Button label="Retry" icon="pi pi-refresh" @click="reloadNuxtApp()" />
    </div>

    <section
      v-else-if="!displayName"
      aria-labelledby="display-name-heading"
      class="flex flex-col gap-3 rounded-lg border border-surface-200 bg-surface-0 p-5 dark:border-surface-700 dark:bg-surface-900"
    >
      <h1 id="display-name-heading" class="m-0 text-xl font-bold">Choose a display name</h1>
      <p class="m-0 text-sm text-muted-color">It will be shown on your lists and in the community forum.</p>
      <DisplayNameForm submit-label="Continue" submit-icon="pi pi-arrow-right" submit-icon-pos="right" stacked @saved="displayName = $event.displayName" />
    </section>

    <section
      v-else-if="quota && !quota.canCreate"
      class="flex flex-col gap-3 rounded-lg border border-surface-200 bg-surface-0 p-5 dark:border-surface-700 dark:bg-surface-900"
    >
      <h1 class="m-0 text-xl font-bold">You've reached your list limit</h1>
      <p class="m-0 text-muted-color">
        You have {{ quota.listCount }} lists, the most your plan allows. Delete one to make room<template v-if="quota.maxLists < quota.fullMaxLists"
          >, or get <NuxtLink to="/jiten-plus" class="font-semibold">Jiten+</NuxtLink> for up to {{ quota.fullMaxLists }} lists</template
        >.
      </p>
      <NuxtLink :to="{ path: '/lists', query: { tab: 'mine' } }" class="font-semibold">Go to my lists</NuxtLink>
    </section>

    <form
      v-else
      class="flex flex-col gap-4 rounded-lg border border-surface-200 bg-surface-0 p-5 dark:border-surface-700 dark:bg-surface-900"
      @submit.prevent="create"
    >
      <h1 class="m-0 text-xl font-bold">New list</h1>
      <div class="flex flex-col gap-1.5">
        <div class="flex justify-between">
          <label for="newListName" class="text-sm font-semibold">Name</label>
          <span class="text-xs text-muted-color tabular-nums">{{ name.length }} / {{ COMMUNITY_LIST_MAX_NAME }}</span>
        </div>
        <InputText id="newListName" v-model="name" :maxlength="COMMUNITY_LIST_MAX_NAME" placeholder="Light novels for beginners" class="w-full" autofocus />
      </div>
      <div class="flex flex-col gap-1.5">
        <div class="flex justify-between">
          <label for="newListDescription" class="text-sm font-semibold">Description <span class="font-normal text-muted-color">(optional)</span></label>
          <span class="text-xs text-muted-color tabular-nums">{{ description.length }} / {{ COMMUNITY_LIST_MAX_DESCRIPTION }}</span>
        </div>
        <Textarea id="newListDescription" v-model="description" :maxlength="COMMUNITY_LIST_MAX_DESCRIPTION" rows="3" auto-resize class="w-full" />
      </div>
      <div class="flex items-center gap-2 text-sm">
        <Checkbox v-model="agreedToRules" input-id="newListRules" binary />
        <span>
          <label for="newListRules">I've read and agree to the</label> <button type="button" class="cursor-pointer font-semibold text-primary-500 hover:underline" @click="rulesOpen = true">list rules</button>
        </span>
      </div>
      <small v-if="createError" class="text-red-600 dark:text-red-400" role="alert">{{ createError }}</small>
      <div class="flex flex-wrap items-center justify-between gap-3">
        <span class="text-sm text-muted-color">Your list will be created as a draft that you can publish later.</span>
        <Button type="submit" label="Create and add titles" icon="pi pi-arrow-right" icon-pos="right" :loading="creating" />
      </div>
    </form>

    <Dialog v-model:visible="rulesOpen" modal header="List rules" :draggable="false" class="w-[30rem] max-w-[calc(100vw-2rem)]">
      <div class="flex flex-col gap-2 text-sm">
        <p class="m-0">A list's name, description and notes must not contain:</p>
        <ul class="m-0 flex list-disc flex-col gap-0.5 pl-5">
          <li v-for="rule in COMMUNITY_LIST_FORBIDDEN_CONTENT" :key="rule">{{ rule }}</li>
        </ul>
        <p class="m-0 text-muted-color">Repeatedly breaking the rules will result in a restricted access to the community features.</p>
      </div>
      <template #footer>
        <Button label="Close" severity="secondary" text @click="rulesOpen = false" />
        <Button label="I agree" @click="acceptRules" />
      </template>
    </Dialog>
  </div>
</template>
