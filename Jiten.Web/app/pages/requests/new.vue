<script setup lang="ts">
  import { LinkType, MediaType, RequestKind } from '~/types';
  import type { DuplicateCheckResultDto, MediaSuggestion } from '~/types/types';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { getRequestStatusText } from '~/utils/requestStatusMapper';
  import { detectLinkTypeFromUrl, getLinkTypeText } from '~/utils/linkTypeMapper';

  definePageMeta({
    middleware: ['auth'],
  });

  useHead({ title: 'New Request - Jiten' });

  const { createRequest, checkDuplicates, fetchMyQuota, error: requestError } = useMediaRequests();
  const toast = useToast();
  const router = useRouter();
  const route = useRoute();
  const localiseTitle = useLocaliseTitle();

  const title = ref('');
  const mediaType = ref<MediaType | null>(null);
  const externalUrl = ref('');
  const description = ref('');
  const isUpdate = ref(false);
  const targetDeckId = ref<number | null>(null);
  const isSubmitting = ref(false);
  const duplicates = ref<DuplicateCheckResultDto | null>(null);
  const quota = ref<MediaRequestQuota | null>(null);

  // Null until the field holds a parseable absolute URL, so the AniList hint cannot flash while the user is still typing.
  const externalLinkType = computed(() => (externalUrl.value.trim() ? detectLinkTypeFromUrl(externalUrl.value) : null));
  const showAnilistHint = computed(
    () => mediaType.value === MediaType.Novel && externalLinkType.value !== null && externalLinkType.value !== LinkType.Anilist
  );

  const isYouTube = computed(() => mediaType.value === MediaType.YouTube);

  const isAtQuotaLimit = computed(() => quota.value !== null && quota.value.activeCount >= quota.value.limit);
  const showPlusUpsell = computed(() => quota.value !== null && !quota.value.isPlus && quota.value.plusLimit > quota.value.limit);

  const { load: loadTurnaround, fulfilmentRange, awaitingWait } = useRequestTurnaround();

  onMounted(async () => {
    loadTurnaround();
    quota.value = await fetchMyQuota();
  });

  const mediaTypeOptions = Object.values(MediaType)
    .filter((v) => typeof v === 'number')
    .map((v) => ({ label: getMediaTypeText(v as MediaType), value: v as MediaType }))
    .sort((a, b) => a.label.localeCompare(b.label));

  // Prefill media type from query param
  const queryMediaType = route.query.mediaType;
  if (queryMediaType) {
    const parsed = Number(queryMediaType);
    if (!isNaN(parsed) && Object.values(MediaType).includes(parsed)) {
      mediaType.value = parsed as MediaType;
    }
  }

  function onTargetDeckSelect(suggestion: MediaSuggestion | null) {
    if (!suggestion) return;
    mediaType.value = suggestion.mediaType;
    if (!title.value.trim()) title.value = localiseTitle(suggestion);
  }

  watch(isUpdate, (val) => {
    if (!val) targetDeckId.value = null;
  });

  let duplicateTimeout: ReturnType<typeof setTimeout> | null = null;
  watch([title, isUpdate, targetDeckId], () => {
    if (duplicateTimeout) clearTimeout(duplicateTimeout);
    const trimmed = title.value.trim();
    const deckId = isUpdate.value ? (targetDeckId.value ?? undefined) : undefined;
    if (trimmed.length < 2 && deckId === undefined) {
      duplicates.value = null;
      return;
    }
    duplicateTimeout = setTimeout(async () => {
      duplicates.value = await checkDuplicates(trimmed, deckId);
    }, 500);
  });

  const duplicateDecks = computed(() => (isUpdate.value ? [] : (duplicates.value?.existingDecks ?? [])));
  const duplicateUpdateRequests = computed(() => (isUpdate.value ? (duplicates.value?.existingUpdateRequests ?? []) : []));
  const duplicateRequests = computed(() => {
    const alreadyShown = new Set(duplicateUpdateRequests.value.map((r) => r.id));
    return (duplicates.value?.existingRequests ?? []).filter((r) => !alreadyShown.has(r.id));
  });
  const hasDuplicateHints = computed(() => duplicateDecks.value.length > 0 || duplicateUpdateRequests.value.length > 0 || duplicateRequests.value.length > 0);

  const canSubmit = computed(
    () =>
      title.value.trim().length > 0 &&
      mediaType.value !== null &&
      (!isUpdate.value || targetDeckId.value !== null) &&
      !isSubmitting.value &&
      !isAtQuotaLimit.value
  );

  async function handleSubmit() {
    if (!canSubmit.value || mediaType.value === null) return;

    isSubmitting.value = true;
    const result = await createRequest({
      title: title.value.trim(),
      mediaType: mediaType.value,
      kind: isUpdate.value ? RequestKind.Update : RequestKind.New,
      targetDeckId: isUpdate.value ? (targetDeckId.value ?? undefined) : undefined,
      externalUrl: externalUrl.value.trim() || undefined,
      description: description.value.trim() || undefined,
    });
    isSubmitting.value = false;

    if (result) {
      toast.add({
        severity: 'success',
        summary: 'Request submitted',
        detail: 'Your request has been created.',
        life: 3000,
      });
      router.push(`/requests/${result.id}`);
    } else {
      const err = requestError.value as any;
      const is422 = err?.response?.status === 422 || err?.status === 422;
      const hasActiveCount = err?.data?.activeCount !== undefined || err?.response?._data?.activeCount !== undefined;
      if (is422 && hasActiveCount) {
        const limit = quota.value?.limit ?? 20;
        quota.value = {
          activeCount: limit,
          limit,
          plusLimit: quota.value?.plusLimit ?? 30,
          isPlus: quota.value?.isPlus ?? false,
        };
        toast.add({
          severity: 'warn',
          summary: 'Quota reached',
          detail: `You've reached your request quota (${limit} active requests). Wait for some to be fulfilled or rejected.`,
          life: 6000,
        });
      } else {
        toast.add({
          severity: 'error',
          summary: 'Error',
          detail: extractApiError(requestError.value, 'Failed to create request. Please try again.'),
          life: 5000,
        });
      }
    }
  }
</script>

<template>
  <div class="container mx-auto p-2 md:p-4 max-w-2xl">
    <div class="flex items-center mb-6">
      <NuxtLink to="/requests">
        <Button icon="pi pi-arrow-left" severity="secondary" text />
      </NuxtLink>
      <h1 class="text-2xl font-bold ml-2">New Request</h1>
    </div>

    <Card class="shadow-md">
      <template #content>
        <div class="flex flex-col gap-5">
          <!-- Update to existing media -->
          <div class="flex flex-col gap-2">
            <div class="flex items-center gap-2">
              <Checkbox v-model="isUpdate" input-id="isUpdate" binary />
              <label for="isUpdate" class="font-semibold cursor-pointer">This is an update to an existing media</label>
            </div>
            <small class="text-muted-color pl-7">Use this to add new or missing volumes to an existing media.</small>

            <div v-if="isUpdate" class="flex flex-col gap-2 mt-1">
              <label class="font-semibold">Media to update *</label>
              <MediaDeckPicker
                v-model="targetDeckId"
                input-id="targetDeck"
                placeholder="Search the media on Jiten..."
                :allow-raw-id="false"
                @select="onTargetDeckSelect"
              />
            </div>
          </div>

          <!-- Media Type -->
          <div class="flex flex-col gap-2">
            <label class="font-semibold">Media Type *</label>
            <Select
              v-model="mediaType"
              :options="mediaTypeOptions"
              option-label="label"
              option-value="value"
              placeholder="Select media type"
              class="w-full"
              :disabled="isUpdate"
            />
            <small v-if="isUpdate" class="text-muted-color">Taken from the selected media.</small>
            <div v-else-if="isYouTube" class="flex flex-col gap-1">
              <small class="text-muted-color flex items-start gap-1.5">
                <i class="pi pi-info-circle mt-0.5 w-[14px] shrink-0 text-center text-[13px] text-blue-500 dark:text-blue-300" />
                <span>You can request either a full YouTube channels or some specific playlists.</span>
              </small>
              <small class="text-muted-color flex items-start gap-1.5">
                <i class="pi pi-exclamation-triangle mt-0.5 w-[14px] shrink-0 text-center text-[13px] text-amber-600 dark:text-amber-500" />
                <span>Videos must have Japanese subtitles made by a person (softsubs). Auto-generated subtitles are not accurate enough.</span>
              </small>
            </div>
          </div>

          <!-- Title -->
          <div class="flex flex-col gap-2">
            <label class="font-semibold">Title *</label>
            <InputText v-model="title" :placeholder="isYouTube ? 'Channel or playlist name' : 'Enter the title of the media'" maxlength="300" class="w-full" />

            <!-- Duplicate detection results -->
            <div v-if="hasDuplicateHints" class="mt-2">
              <div v-if="duplicateUpdateRequests.length > 0" class="mb-3">
                <p class="text-sm font-semibold text-orange-600 dark:text-orange-400 mb-1">There are already open update requests for this media:</p>
                <div v-for="req in duplicateUpdateRequests" :key="req.id" class="flex items-center gap-2 text-sm py-1">
                  <Tag :value="getRequestStatusText(req.status)" severity="secondary" class="text-xs" />
                  <NuxtLink :to="`/requests/${req.id}`" class="text-primary hover:underline" @click.stop>
                    {{ req.title }}
                  </NuxtLink>
                  <span class="text-muted-color">({{ req.upvoteCount }} votes)</span>
                </div>
                <small class="text-muted-color">Voting on an existing request helps more than filing a second one.</small>
              </div>

              <div v-if="duplicateDecks.length > 0" class="mb-3">
                <p class="text-sm font-semibold text-orange-600 dark:text-orange-400 mb-1">This media may already exist:</p>
                <div v-for="deck in duplicateDecks" :key="deck.deckId" class="flex items-center gap-2 text-sm py-1">
                  <Tag :value="getMediaTypeText(deck.mediaType)" severity="secondary" class="text-xs" />
                  <NuxtLink :to="`/decks/media/${deck.deckId}/detail`" class="text-primary hover:underline" @click.stop>
                    {{ deck.title }}
                  </NuxtLink>
                </div>
              </div>

              <div v-if="duplicateRequests.length > 0">
                <p class="text-sm font-semibold text-orange-600 dark:text-orange-400 mb-1">Similar requests already exist:</p>
                <div v-for="req in duplicateRequests" :key="req.id" class="flex items-center gap-2 text-sm py-1">
                  <Tag :value="getRequestStatusText(req.status)" severity="secondary" class="text-xs" />
                  <NuxtLink :to="`/requests/${req.id}`" class="text-primary hover:underline" @click.stop>
                    {{ req.title }}
                  </NuxtLink>
                  <span class="text-muted-color">({{ req.upvoteCount }} votes)</span>
                </div>
              </div>
            </div>
          </div>

          <!-- External URL -->
          <div class="flex flex-col gap-2">
            <label class="font-semibold">External URL</label>
            <InputText v-model="externalUrl" :placeholder="isYouTube ? 'Link to the channel or playlist' : 'Link to a database'" maxlength="500" class="w-full" />
            <small v-if="isYouTube" class="text-muted-color">Paste a link to the channel or playlist.</small>
            <div v-else class="flex flex-col gap-1">
              <small class="text-muted-color flex items-start gap-1.5">
                <span class="w-[14px] shrink-0" />
                <span>A link helps us find and add the correct media faster.</span>
              </small>
              <small class="text-muted-color flex items-start gap-1.5">
                <i class="pi pi-exclamation-triangle mt-0.5 w-[14px] shrink-0 text-center text-[13px] text-amber-600 dark:text-amber-500" />
                <span>Do not link to piracy websites. Only link to official sources such as AniList, VNDB, TMDB, MyAnimeList, IGDB or Bookmeter.</span>
              </small>
              <small v-if="showAnilistHint" class="text-muted-color flex items-start gap-1.5">
                <i class="pi pi-info-circle mt-0.5 w-[14px] shrink-0 text-center text-[13px] text-blue-500 dark:text-blue-300" />
                <span>AniList is the preferred source for novels. Requests linked to it are fulfilled faster.</span>
              </small>
            </div>
          </div>

          <!-- Description -->
          <div class="flex flex-col gap-2">
            <label class="font-semibold">Description</label>
            <Textarea v-model="description" placeholder="Any additional details (romaji name, edition, volume, version...)" :maxlength="1000" rows="3" class="w-full" />
            <small class="text-muted-color text-right">{{ description.length }}/1000</small>
          </div>

          <div class="border-t border-surface-200 dark:border-surface-700" />

          <div class="flex flex-col gap-2">
            <small class="text-muted-color flex items-start gap-1.5">
              <i class="pi pi-paperclip mt-0.5 w-[14px] shrink-0 text-center text-[13px]" />
              <span>
                You can attach files (scripts, subtitles, ebooks...) in the comments once your request is submitted.
                <template v-if="fulfilmentRange">Requests with a file are usually fulfilled within {{ fulfilmentRange }}.</template>
                <template v-else>Requests with a file are fulfilled far faster.</template>
                <template v-if="awaitingWait">Requests without a file have been waiting for about {{ awaitingWait }}.</template>
              </span>
            </small>
            <small class="text-muted-color flex items-start gap-1.5">
              <i class="pi pi-eye mt-0.5 w-[14px] shrink-0 text-center text-[13px]" />
              <span>Your username is only visible to administrators, not to other users.</span>
            </small>
          </div>

          <div class="flex items-center justify-between gap-3 flex-wrap">
            <small v-if="quota && isAtQuotaLimit" class="text-red-600 dark:text-red-400">
              You have reached the limit of {{ quota.limit }} active requests. Wait for existing requests to be fulfilled or rejected.
              <template v-if="showPlusUpsell">
                <NuxtLink to="/jiten-plus" class="underline">Jiten+</NuxtLink>
                raises this to {{ quota.plusLimit }} slots.
              </template>
            </small>
            <small v-else-if="quota" class="text-muted-color">{{ quota.limit - quota.activeCount }} of {{ quota.limit }} active request slots remaining.</small>
            <Button label="Submit Request" icon="pi pi-send" :loading="isSubmitting" :disabled="!canSubmit" class="ml-auto shrink-0" @click="handleSubmit" />
          </div>
        </div>
      </template>
    </Card>
  </div>
</template>
