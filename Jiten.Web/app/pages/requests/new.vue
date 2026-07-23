<script setup lang="ts">
import { MediaType } from '~/types';
import type { DuplicateCheckResultDto } from '~/types/types';
import { getMediaTypeText } from '~/utils/mediaTypeMapper';
import { getRequestStatusText } from '~/utils/requestStatusMapper';
import { getLinkTypeText } from '~/utils/linkTypeMapper';

definePageMeta({
  middleware: ['auth'],
});

useHead({ title: 'New Request - Jiten' });

const { createRequest, checkDuplicates, fetchMyQuota, error: requestError } = useMediaRequests();
const toast = useToast();
const router = useRouter();
const route = useRoute();

const title = ref('');
const mediaType = ref<MediaType | null>(null);
const externalUrl = ref('');
const description = ref('');
const isSubmitting = ref(false);
const duplicates = ref<DuplicateCheckResultDto | null>(null);
const quota = ref<MediaRequestQuota | null>(null);

const isAtQuotaLimit = computed(() => quota.value !== null && quota.value.activeCount >= quota.value.limit);
const showPlusUpsell = computed(() => quota.value !== null && !quota.value.isPlus && quota.value.plusLimit > quota.value.limit);

onMounted(async () => {
  quota.value = await fetchMyQuota();
});

const mediaTypeOptions = Object.values(MediaType)
  .filter(v => typeof v === 'number')
  .map(v => ({ label: getMediaTypeText(v as MediaType), value: v as MediaType }))
  .sort((a, b) => a.label.localeCompare(b.label));

// Prefill media type from query param
const queryMediaType = route.query.mediaType;
if (queryMediaType) {
  const parsed = Number(queryMediaType);
  if (!isNaN(parsed) && Object.values(MediaType).includes(parsed)) {
    mediaType.value = parsed as MediaType;
  }
}

let duplicateTimeout: ReturnType<typeof setTimeout> | null = null;
watch(title, (newTitle) => {
  if (duplicateTimeout) clearTimeout(duplicateTimeout);
  if (newTitle.trim().length < 2) {
    duplicates.value = null;
    return;
  }
  duplicateTimeout = setTimeout(async () => {
    duplicates.value = await checkDuplicates(newTitle.trim());
  }, 500);
});

const canSubmit = computed(() =>
  title.value.trim().length > 0 && mediaType.value !== null && !isSubmitting.value && !isAtQuotaLimit.value
);

async function handleSubmit() {
  if (!canSubmit.value || mediaType.value === null) return;

  isSubmitting.value = true;
  const result = await createRequest({
    title: title.value.trim(),
    mediaType: mediaType.value,
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
          <!-- Media Type -->
          <div class="flex flex-col gap-2">
            <label class="font-semibold">Media Type *</label>
            <Select
              v-model="mediaType"
              :options="mediaTypeOptions"
              optionLabel="label"
              optionValue="value"
              placeholder="Select media type"
              class="w-full"
            />
          </div>

          <!-- Title -->
          <div class="flex flex-col gap-2">
            <label class="font-semibold">Title *</label>
            <InputText
              v-model="title"
              placeholder="Enter the title of the media"
              maxlength="300"
              class="w-full"
            />

            <!-- Duplicate detection results -->
            <div v-if="duplicates && (duplicates.existingDecks.length > 0 || duplicates.existingRequests.length > 0)" class="mt-2">
              <div v-if="duplicates.existingDecks.length > 0" class="mb-3">
                <p class="text-sm font-semibold text-orange-600 dark:text-orange-400 mb-1">This media may already exist:</p>
                <div v-for="deck in duplicates.existingDecks" :key="deck.deckId" class="flex items-center gap-2 text-sm py-1">
                  <Tag :value="getMediaTypeText(deck.mediaType)" severity="secondary" class="text-xs" />
                  <NuxtLink :to="`/decks/media/${deck.deckId}/detail`" class="text-primary hover:underline" @click.stop>
                    {{ deck.title }}
                  </NuxtLink>
                </div>
              </div>

              <div v-if="duplicates.existingRequests.length > 0">
                <p class="text-sm font-semibold text-orange-600 dark:text-orange-400 mb-1">Similar requests already exist:</p>
                <div v-for="req in duplicates.existingRequests" :key="req.id" class="flex items-center gap-2 text-sm py-1">
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
            <InputText
              v-model="externalUrl"
              placeholder="Link to a database"
              maxlength="500"
              class="w-full"
            />
            <small class="text-muted-color">
              Providing a link helps us find and add the correct media faster. Requests without a link may take longer or result in the wrong version being added.
            </small>
            <Message severity="warn" :closable="false" class="text-sm">
              <i class="pi pi-exclamation-triangle mr-1" />
              Do not link to piracy websites. Only link to official sources such as Anilist, VNDB, TMDB, MyAnimeList, IGDB, Bookmeter or similar databases.
            </Message>
          </div>

          <!-- Description -->
          <div class="flex flex-col gap-2">
            <label class="font-semibold">Description</label>
            <Textarea
              v-model="description"
              placeholder="Any additional details (edition, volume, version...)"
              :maxlength="1000"
              rows="3"
              class="w-full"
            />
            <small class="text-muted-color text-right">{{ description.length }}/1000</small>
          </div>

          <small class="text-muted-color">
            <i class="pi pi-info-circle mr-1" />
            You'll be able to attach files (scripts, subtitles, etc.) in the comments after submitting your request.
          </small>

          <Message severity="secondary" :closable="false" class="text-sm">
            Your username is not visible to other users, but is visible to administrators to avoid abuse.
          </Message>

          <template v-if="quota">
            <Message v-if="isAtQuotaLimit" severity="error" :closable="false" class="text-sm">
              You have reached the limit of {{ quota.limit }} active requests. Wait for existing requests to be fulfilled or rejected before submitting a new one.
              <template v-if="showPlusUpsell">
                <NuxtLink to="/jiten-plus" class="underline">Jiten+</NuxtLink> raises this to {{ quota.plusLimit }} slots.
              </template>
            </Message>
            <small v-else class="text-muted-color">
              <i class="pi pi-list mr-1" />
              {{ quota.limit - quota.activeCount }} of {{ quota.limit }} active request slots remaining.
            </small>
          </template>

          <Button
            label="Submit Request"
            icon="pi pi-send"
            :loading="isSubmitting"
            :disabled="!canSubmit"
            @click="handleSubmit"
            class="w-full"
          />
        </div>
      </template>
    </Card>
  </div>
</template>
