<script setup lang="ts">
  import type { WordSetDto, UserWordSetSubscriptionDto } from '~/types/types';
  import { WordSetStateType } from '~/types';
  import { autoLinkUrls } from '~/utils/autoLinkUrls';

  definePageMeta({
    middleware: ['auth'],
  });

  useHead({ title: 'Word Sets - Settings - Jiten' });

  const { wordSets, subscriptions, isLoading, fetchWordSets, fetchSubscriptions, subscribe, unsubscribe } = useWordSets();
  const toast = useToast();

  const subscribingSetId = ref<number | null>(null);
  const selectedStates = ref<Record<number, WordSetStateType>>({});

  const stateOptions = [
    { label: 'Blacklist', value: WordSetStateType.Blacklisted },
    { label: 'Mark as Mastered', value: WordSetStateType.Mastered },
  ];

  function getSelectedState(setId: number): WordSetStateType {
    return selectedStates.value[setId] ?? WordSetStateType.Blacklisted;
  }

  const subscribedSets = computed(() => {
    return subscriptions.value.map((sub) => ({
      ...sub,
      wordSet: wordSets.value.find((ws) => ws.setId === sub.setId),
    }));
  });

  const availableSets = computed(() => {
    const subscribedIds = new Set(subscriptions.value.map((s) => s.setId));
    return wordSets.value.filter((ws) => !subscribedIds.has(ws.setId));
  });

  async function loadData() {
    await Promise.all([fetchWordSets(), fetchSubscriptions()]);
  }

  async function handleSubscribe(setId: number) {
    const state = getSelectedState(setId);
    subscribingSetId.value = setId;
    const success = await subscribe(setId, state);
    subscribingSetId.value = null;

    if (success) {
      await fetchSubscriptions();
      const set = wordSets.value.find((ws) => ws.setId === setId);
      const stateLabel = state === WordSetStateType.Blacklisted ? 'blacklisted' : 'mastered';
      toast.add({
        severity: 'success',
        summary: 'Subscribed',
        detail: `${set?.name || 'Word set'} words marked as ${stateLabel}`,
        life: 3000,
      });
    } else {
      toast.add({
        severity: 'error',
        summary: 'Error',
        detail: 'Failed to subscribe to word set',
        life: 5000,
      });
    }
  }

  async function handleUnsubscribe(setId: number) {
    subscribingSetId.value = setId;
    const success = await unsubscribe(setId);
    subscribingSetId.value = null;

    if (success) {
      await fetchSubscriptions();
      toast.add({
        severity: 'success',
        summary: 'Unsubscribed',
        detail: 'Subscription removed',
        life: 3000,
      });
    } else {
      toast.add({
        severity: 'error',
        summary: 'Error',
        detail: 'Failed to unsubscribe',
        life: 5000,
      });
    }
  }

  function getStateLabel(state: WordSetStateType): string {
    return state === WordSetStateType.Blacklisted ? 'Blacklisted' : 'Mastered';
  }

  function getStateSeverity(state: WordSetStateType): 'secondary' | 'success' {
    return state === WordSetStateType.Blacklisted ? 'secondary' : 'success';
  }

  function formatDate(dateString: string): string {
    return new Date(dateString).toLocaleDateString();
  }

  onMounted(() => {
    loadData();
  });
</script>

<template>
  <div class="container mx-auto p-2 md:p-4">
    <div class="flex items-center mb-6">
      <NuxtLink to="/settings">
        <Button icon="pi pi-arrow-left" severity="secondary" text />
      </NuxtLink>
      <h1 class="text-2xl font-bold ml-2">Word Sets</h1>
    </div>

    <div v-if="isLoading" class="flex justify-center items-center py-12">
      <ProgressSpinner style="width: 50px; height: 50px" />
    </div>

    <template v-else>
      <!-- Active Subscriptions -->
      <div class="mb-8">
        <h2 class="text-xl font-semibold mb-2">Your Subscriptions</h2>
        <p class="text-muted-color mb-4">Manage your word set subscriptions. Words in these sets affect your coverage calculations.</p>

        <div v-if="subscribedSets.length === 0" class="text-center py-8 text-muted-color">You haven't subscribed to any word sets yet.</div>

        <div v-else class="grid gap-6 md:grid-cols-2">
          <Card
            v-for="sub in subscribedSets"
            :key="sub.setId"
            class="shadow-sm h-full"
            pt:root:class="h-full flex flex-col"
            pt:body:class="flex-1 flex flex-col"
            pt:content:class="flex-1"
          >
            <template #header>
              <div class="p-5 pb-0">
                <div class="flex items-start justify-between gap-2">
                  <span class="text-xl font-semibold">{{ sub.name }}</span>
                  <Tag :value="getStateLabel(sub.state)" :severity="getStateSeverity(sub.state)" />
                </div>
                <p class="text-muted-color mt-1">{{ sub.wordCount.toLocaleString() }} words &middot; {{ sub.formCount.toLocaleString() }} forms</p>
              </div>
            </template>
            <template #content>
              <p class="text-muted-color">Subscribed {{ formatDate(sub.subscribedAt) }}</p>
            </template>
            <template #footer>
              <div class="flex gap-2">
                <Button as="router-link" :to="`/settings/word-sets/${sub.slug}`" label="Vocabulary" icon="pi pi-book" />
                <Button
                  label="Unsubscribe"
                  icon="pi pi-times"
                  severity="danger"
                  outlined
                  :loading="subscribingSetId === sub.setId"
                  @click="handleUnsubscribe(sub.setId)"
                />
              </div>
            </template>
          </Card>
        </div>
      </div>

      <!-- Available Sets -->
      <div>
        <h2 class="text-xl font-semibold mb-2">Available Word Sets</h2>
        <p class="text-muted-color mb-4">
          Word sets are an easy way to jumpstart your coverage. Blacklist words you don't want to learn, or mass mark as mastered words you've already learned.
        </p>

        <div v-if="availableSets.length === 0" class="text-center py-8 text-muted-color">You're subscribed to all available word sets.</div>

        <div v-else class="grid gap-6 md:grid-cols-2">
          <Card
            v-for="set in availableSets"
            :key="set.setId"
            class="shadow-sm h-full"
            pt:root:class="h-full flex flex-col"
            pt:body:class="flex-1 flex flex-col"
            pt:content:class="flex-1"
          >
            <template #header>
              <div class="p-5 pb-0">
                <span class="text-xl font-semibold">{{ set.name }}</span>
                <p class="text-muted-color mt-1">{{ set.wordCount.toLocaleString() }} words &middot; {{ set.formCount.toLocaleString() }} forms</p>
              </div>
            </template>
            <template #content>
              <p v-if="set.description" v-html="sanitiseHtml(autoLinkUrls(set.description))" />
              <p v-else class="text-muted-color italic">No description available.</p>
            </template>
            <template #footer>
              <div class="flex flex-col gap-2">
                <div class="flex gap-2">
                  <Select
                    v-model="selectedStates[set.setId]"
                    :options="stateOptions"
                    option-label="label"
                    option-value="value"
                    :placeholder="stateOptions[0].label"
                    class="flex-1"
                  />
                  <Button label="Subscribe" icon="pi pi-plus" :loading="subscribingSetId === set.setId" @click="handleSubscribe(set.setId)" />
                </div>
                <Button as="router-link" :to="`/settings/word-sets/${set.slug}`" label="Vocabulary" icon="pi pi-book" outlined />
              </div>
            </template>
          </Card>
        </div>
      </div>
    </template>
  </div>
</template>
