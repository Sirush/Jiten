<script setup lang="ts">
  import { useAuthStore } from '~/stores/authStore';
  import { useJitenStore } from '~/stores/jitenStore';

  const props = defineProps<{
    deckId: number;
  }>();

  const auth = useAuthStore();
  const jitenStore = useJitenStore();
  const { stats, loading, failed, rateLimited, granted, statusReady, retry } = useSentenceStats(() => props.deckId, true);

  const mounted = ref(false);
  onMounted(() => (mounted.value = true));
  const showSkeleton = computed(() => !mounted.value || loading.value || !statusReady.value);

  const dismissed = computed(() => !granted.value && jitenStore.hideSentenceStats);
  const showSection = computed(() => auth.isAuthenticated && !dismissed.value && (!failed.value || rateLimited.value));
  const hasData = computed(() => granted.value && !!stats.value?.hasData);
</script>

<template>
  <Card v-if="showSection" class="mt-4">
    <template #content>
      <div class="flex items-center justify-between gap-2 mb-2">
        <h2 class="font-bold">Sentences you can read</h2>
        <NuxtLink
          v-if="hasData"
          :to="`/decks/media/${deckId}/sentences`"
          class="text-sm text-primary-600 dark:text-primary-400 hover:underline whitespace-nowrap"
        >
          See sentence stats
        </NuxtLink>
      </div>

      <div v-if="showSkeleton" class="h-[140px] rounded bg-surface-100 dark:bg-surface-800 animate-pulse" />

      <div v-else-if="rateLimited" class="py-2 flex flex-wrap items-center justify-between gap-2">
        <span class="text-sm text-gray-500 dark:text-gray-400">Couldn't load your sentence stats just now.</span>
        <Button label="Try again" icon="pi pi-refresh" severity="secondary" outlined size="small" @click="retry()" />
      </div>

      <div v-else-if="hasData" class="flex flex-col gap-4">
        <SentenceReadableHeadline :readable="stats!.readable" :one-unknown="stats!.oneUnknown" :total="stats!.total" />
        <SentenceDistributionBar
          :readable="stats!.readable"
          :one-unknown="stats!.oneUnknown"
          :two-unknown="stats!.twoUnknown"
          :three-or-more-unknown="stats!.threeOrMoreUnknown"
        />
        <p v-if="stats!.profiledParts < stats!.totalParts" class="text-xs text-gray-500 dark:text-gray-400">
          Based on {{ stats!.profiledParts }} of {{ stats!.totalParts }} parts; the rest are still being processed.
        </p>
      </div>

      <div v-else-if="granted" class="py-2 text-sm text-gray-500 dark:text-gray-400">Sentence stats for this title aren't ready yet.</div>

      <template v-else>
        <JitenPlusGate feature="sentence-stats" feature-label="Sentence stats">
          <div class="flex flex-col gap-2">
            <p class="text-sm text-gray-500 dark:text-gray-400">See how many of this title's sentences you can already understand. (example)</p>
            <SentenceDistributionBar :readable="38" :one-unknown="21" :two-unknown="17" :three-or-more-unknown="24" />
          </div>
        </JitenPlusGate>
        <div class="flex justify-end pt-2">
          <Button label="Hide this" icon="pi pi-eye-slash" severity="secondary" text size="small" @click="jitenStore.hideSentenceStats = true" />
        </div>
      </template>
    </template>
  </Card>
</template>
