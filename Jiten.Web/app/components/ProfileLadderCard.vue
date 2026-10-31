<script setup lang="ts">
  import type Popover from 'primevue/popover';
  import type { ProfileLadder } from '~/types';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { useJitenStore } from '~/stores/jitenStore';

  const { isOwner, viewAsQuery, apiPath } = useProfileContext();
  const jitenStore = useJitenStore();
  const uid = useId();

  const split = computed(() => [...jitenStore.profileLadderSplitTypes].sort((a, b) => a - b));
  const { data: ladder } = useApiFetch<ProfileLadder>(() => apiPath('stats/ladder'), {
    query: { viewAs: computed(() => viewAsQuery.value.viewAs), split },
  });

  const popover = ref<InstanceType<typeof Popover> | null>(null);
  const open = ref(false);

  const points = computed(() => ladder.value?.points ?? []);
  const typeOptions = computed(() => (ladder.value?.volumeTypes ?? []).map((type) => ({ type, label: getMediaTypeText(type) })));
  const hasVolumes = computed(() => points.value.some((p) => p.isVolume));
  const undatedTitles = computed(() => ladder.value?.undatedTitles ?? 0);
  const undatedVolumes = computed(() => ladder.value?.undatedVolumes ?? 0);
  const undatedTotal = computed(() => undatedTitles.value + undatedVolumes.value);
  const visible = computed(() => points.value.length >= 2 || undatedTotal.value > 0 || (typeOptions.value.length > 0 && points.value.length > 0));

  const undatedNote = computed(() => {
    const parts = [
      undatedTitles.value > 0 ? `${undatedTitles.value} finished ${undatedTitles.value === 1 ? 'title' : 'titles'}` : null,
      undatedVolumes.value > 0 ? `${undatedVolumes.value} ${undatedVolumes.value === 1 ? 'volume' : 'volumes'}` : null,
    ].filter(Boolean);
    return `${parts.join(' and ')} ${undatedTotal.value === 1 ? "has no finish date, so it isn't" : "have no finish date, so they aren't"} shown.`;
  });
</script>

<template>
  <Card v-if="visible">
    <template #title>
      <div class="flex items-center justify-between gap-2">
        <h2 class="text-base font-semibold">Difficulty of finished titles over time</h2>
        <Button
          v-if="typeOptions.length"
          severity="secondary"
          text
          rounded
          size="small"
          class="-my-2 min-h-11 min-w-11 shrink-0 justify-center sm:pointer-fine:min-h-9 sm:pointer-fine:min-w-9"
          aria-label="Chart settings"
          aria-haspopup="dialog"
          :aria-expanded="open"
          :aria-controls="open ? `${uid}-settings` : undefined"
          @click="popover?.toggle($event)"
        >
          <i class="pi pi-cog" aria-hidden="true" />
        </Button>
      </div>
    </template>
    <template #subtitle>
      <span class="text-xs">
        Each dot is a {{ hasVolumes ? 'title or volume' : 'title' }}, placed on the day it was finished.{{
          points.some((p) => p.deckId != null) ? ' Select one to open it.' : ''
        }}
      </span>
    </template>
    <template #content>
      <ProfileLadderChart v-if="points.length >= 2" :points="points" />
      <p v-if="undatedTotal > 0" class="mt-2 text-sm text-muted-color">
        {{ undatedNote }}
        <NuxtLink v-if="isOwner" to="/settings/media-list" class="!text-primary-600 dark:!text-primary-400">Import dates from AniList or VNDB</NuxtLink>
      </p>
    </template>
  </Card>

  <Popover ref="popover" @show="open = true" @hide="open = false">
    <div :id="`${uid}-settings`" role="dialog" aria-label="Chart settings" class="flex w-max max-w-[calc(100vw_-_2rem)] flex-col gap-1 p-1">
      <span :id="`${uid}-split`" class="text-sm font-semibold">One dot per volume or episode</span>
      <div role="group" :aria-labelledby="`${uid}-split`" class="flex flex-col">
        <label
          v-for="option in typeOptions"
          :key="option.type"
          :for="`${uid}-type-${option.type}`"
          class="flex min-h-11 cursor-pointer items-center gap-2 text-sm sm:pointer-fine:min-h-8"
        >
          <Checkbox v-model="jitenStore.profileLadderSplitTypes" :value="option.type" :input-id="`${uid}-type-${option.type}`" />
          {{ option.label }}
        </label>
      </div>
    </div>
  </Popover>
</template>
