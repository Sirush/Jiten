<script setup lang="ts">
import { useToast } from 'primevue/usetoast';
import { useConfirm } from 'primevue/useconfirm';
import type { DeckSummaryDto } from '~/types/types';
import { ComparisonOutcome, TitleLanguage } from '~/types';
import { getMediaTypeText } from '~/utils/mediaTypeMapper';

const props = defineProps<{
  deckA: DeckSummaryDto;
  deckB: DeckSummaryDto;
  voteTimestamps: number[];
}>();

const jitenStore = useJitenStore();

function deckTitle(deck: DeckSummaryDto): string {
  if (jitenStore.titleLanguage === TitleLanguage.English)
    return deck.englishTitle ?? deck.romajiTitle ?? deck.title;
  if (jitenStore.titleLanguage === TitleLanguage.Romaji)
    return deck.romajiTitle ?? deck.title;
  return deck.title;
}

const emit = defineEmits<{
  voted: [];
  skipped: [permanent: boolean];
  blocked: [deckId: number];
}>();

const toast = useToast();
const confirm = useConfirm();
const { submitVote, skipPair, blockDeck, error: voteError } = useDifficultyVotes();

function toastFailure(summary: string, fallback: string) {
  toast.add({ severity: 'error', summary, detail: extractApiError(voteError.value, fallback), life: 5000 });
}

const isSubmitting = ref(false);
const rateLimited = ref(false);
const rateLimitCountdown = ref(0);
let countdownInterval: ReturnType<typeof setInterval> | undefined;

function checkRateLimit(): boolean {
  if (rateLimited.value) return false;
  const now = Date.now();
  const oneMinuteAgo = now - 60_000;
  const recent = props.voteTimestamps.filter(t => t > oneMinuteAgo).sort((a, b) => a - b);
  if (recent.length >= 33) {
    const oldestRelevant = recent[recent.length - 33];
    const waitMs = oldestRelevant + 60_000 - now;
    const waitSeconds = Math.ceil(waitMs / 1000);

    rateLimited.value = true;
    rateLimitCountdown.value = waitSeconds;
    countdownInterval = setInterval(() => {
      rateLimitCountdown.value--;
      if (rateLimitCountdown.value <= 0) {
        clearInterval(countdownInterval);
        rateLimited.value = false;
      }
    }, 1000);
    return false;
  }
  return true;
}

onUnmounted(() => clearInterval(countdownInterval));

async function vote(outcome: ComparisonOutcome) {
  if (isSubmitting.value) return;
  if (!checkRateLimit()) return;

  isSubmitting.value = true;
  const success = await submitVote(props.deckA.id, props.deckB.id, outcome);
  isSubmitting.value = false;

  if (success) {
    props.voteTimestamps.push(Date.now());
    toast.add({
      severity: 'success',
      summary: 'Vote recorded!',
      life: 1500,
    });
    emit('voted');
  } else {
    toastFailure('Vote failed', 'Could not record your vote. Please try again.');
  }
}

async function skip(permanent: boolean) {
  if (isSubmitting.value) return;
  isSubmitting.value = true;
  const success = await skipPair(props.deckA.id, props.deckB.id, permanent);
  isSubmitting.value = false;
  if (success) {
    emit('skipped', permanent);
  } else {
    toastFailure('Skip failed', 'Could not skip this pair. Please try again.');
  }
}

function confirmBlock(event: Event, deck: DeckSummaryDto) {
  confirm.require({
    target: event.currentTarget as HTMLElement,
    group: 'blockDeck',
    message: `Stop showing "${deckTitle(deck)}" in comparisons?`,
    acceptProps: { label: 'Block', severity: 'danger', size: 'small' },
    rejectProps: { label: 'Cancel', severity: 'secondary', outlined: true, size: 'small' },
    accept: async () => {
      const success = await blockDeck(deck.id);
      if (success) {
        toast.add({ severity: 'info', summary: `${deckTitle(deck)} blocked from comparisons`, life: 3000 });
        emit('blocked', deck.id);
      } else {
        toastFailure('Block failed', 'Could not block this title. Please try again.');
      }
    },
  });
}

function getOutcomeLabel(outcome: ComparisonOutcome): string {
  switch (outcome) {
    case ComparisonOutcome.MuchHarder: return 'Much harder';
    case ComparisonOutcome.Harder: return 'Harder';
    case ComparisonOutcome.Same: return 'Same';
    case ComparisonOutcome.Easier: return 'Easier';
    case ComparisonOutcome.MuchEasier: return 'Much easier';
    default: return '';
  }
}
</script>

<template>
  <Card class="relative overflow-hidden">
    <template #content>
      <div
        v-if="rateLimited"
        class="absolute inset-0 z-10 flex flex-col items-center justify-center bg-surface-0/80 dark:bg-surface-900/80 backdrop-blur-sm"
      >
        <i class="pi pi-exclamation-triangle text-red-500 text-4xl mb-3" />
        <p class="text-red-500 font-semibold text-lg">Slow down!</p>
        <p class="text-muted-color text-sm mt-1">Please take a moment to consider each pair.</p>
        <p class="text-muted-color text-sm mt-2">Resuming in {{ rateLimitCountdown }}s</p>
      </div>

      <p class="text-center text-muted-color mb-4">Which did you find harder to understand?</p>

      <div class="flex flex-col md:flex-row items-stretch gap-4 md:gap-6">
        <!-- Deck A -->
        <div class="relative flex-1 flex flex-col items-center border border-surface-200 dark:border-surface-700 rounded-lg px-2 py-3">
          <button
            class="absolute top-1.5 right-1.5 text-surface-400 hover:text-red-500 transition-colors cursor-pointer bg-transparent border-none p-1"
            v-tooltip.top="'Block from comparisons'"
            @click="confirmBlock($event, deckA)"
          >
            <i class="pi pi-ban text-xs" />
          </button>
          <NuxtLink :to="`/decks/media/${deckA.id}/detail`" target="_blank" class="flex-1 no-underline text-inherit">
            <div class="flex flex-col items-center text-center gap-2 cursor-pointer hover:opacity-80 transition-opacity">
              <img
                :src="deckA.coverUrl || '/img/nocover.jpg'"
                :alt="deckTitle(deckA)"
                class="h-40 w-28 object-cover rounded"
              />
              <div class="font-semibold text-sm leading-tight">{{ deckTitle(deckA) }}</div>
              <Tag :value="getMediaTypeText(deckA.mediaType)" severity="secondary" />
            </div>
          </NuxtLink>
          <div class="flex flex-col gap-2 mt-3 w-full max-w-72">
            <Button
              label="Harder"
              icon="pi pi-angle-up"
              severity="warn"
              class="w-full"
              :disabled="isSubmitting"
              @click="vote(ComparisonOutcome.Harder)"
            />
            <Button
              label="Much harder"
              icon="pi pi-angle-double-up"
              severity="danger"
              class="w-full"
              :disabled="isSubmitting"
              @click="vote(ComparisonOutcome.MuchHarder)"
            />
          </div>
        </div>

        <!-- Same (center) -->
        <div class="flex items-center justify-center">
          <Button
            label="About the same"
            severity="info"
            :disabled="isSubmitting"
            @click="vote(ComparisonOutcome.Same)"
          />
        </div>

        <!-- Deck B -->
        <div class="relative flex-1 flex flex-col items-center border border-surface-200 dark:border-surface-700 rounded-lg px-2 py-3">
          <button
            class="absolute top-1.5 right-1.5 text-surface-400 hover:text-red-500 transition-colors cursor-pointer bg-transparent border-none p-1"
            v-tooltip.top="'Block from comparisons'"
            @click="confirmBlock($event, deckB)"
          >
            <i class="pi pi-ban text-xs" />
          </button>
          <NuxtLink :to="`/decks/media/${deckB.id}/detail`" target="_blank" class="flex-1 no-underline text-inherit">
            <div class="flex flex-col items-center text-center gap-2 cursor-pointer hover:opacity-80 transition-opacity">
              <img
                :src="deckB.coverUrl || '/img/nocover.jpg'"
                :alt="deckTitle(deckB)"
                class="h-40 w-28 object-cover rounded"
              />
              <div class="font-semibold text-sm leading-tight">{{ deckTitle(deckB) }}</div>
              <Tag :value="getMediaTypeText(deckB.mediaType)" severity="secondary" />
            </div>
          </NuxtLink>
          <div class="flex flex-col gap-2 mt-3 w-full max-w-72">
            <Button
              label="Harder"
              icon="pi pi-angle-up"
              severity="warn"
              class="w-full"
              :disabled="isSubmitting"
              @click="vote(ComparisonOutcome.Easier)"
            />
            <Button
              label="Much harder"
              icon="pi pi-angle-double-up"
              severity="danger"
              class="w-full"
              :disabled="isSubmitting"
              @click="vote(ComparisonOutcome.MuchEasier)"
            />
          </div>
        </div>
      </div>

      <div class="flex justify-center gap-4 mt-6 text-sm">
        <a href="#" class="text-muted-color hover:text-primary-500" @click.prevent="skip(true)">
          Can't compare
        </a>
        <span class="text-muted-color">|</span>
        <a href="#" class="text-muted-color hover:text-primary-500" @click.prevent="skip(false)">
          Skip for now
        </a>
      </div>
    </template>
  </Card>
</template>
