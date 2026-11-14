<script setup lang="ts">
  import { formatNumber } from '~/utils/formatNumber';
  import { createWaniKaniClient, WANIKANI_STAGE_OPTIONS, WaniKaniApiError, type WaniKaniProgressPhase } from '~/utils/waniKaniApi';

  const emit = defineEmits<{ changed: [] }>();

  const { $api } = useNuxtApp();
  const toast = useToast();

  type Mode = 'level' | 'account';
  const modeOptions: { label: string; value: Mode }[] = [
    { label: 'By Level', value: 'level' },
    { label: 'From Account', value: 'account' },
  ];
  const mode = ref<Mode>('level');

  const LEVEL_MIN = 1;
  const LEVEL_MAX = 60;

  const level = ref(10);
  const isLoading = ref(false);
  const cumulativeWordCounts = ref<number[]>([]);

  const levelRange = computed(() => (level.value === 1 ? 'Level 1' : `Levels 1 to ${level.value}`));
  const wordCount = computed(() => cumulativeWordCounts.value[level.value - 1]);

  const updateLevel = (value: number | null) => {
    level.value = Math.min(LEVEL_MAX, Math.max(LEVEL_MIN, Math.round(value || LEVEL_MIN)));
  };

  onMounted(async () => {
    try {
      cumulativeWordCounts.value = await $api<number[]>('user/vocabulary/wanikani-levels');
    } catch {
      cumulativeWordCounts.value = [];
    }
  });

  async function addLevelWords() {
    isLoading.value = true;
    const range = levelRange.value.toLowerCase();
    try {
      const data = await $api<{ words: number; forms: number }>(`user/vocabulary/import-from-wanikani-level/${level.value}`, { method: 'POST' });
      toast.add({
        severity: 'success',
        detail:
          data.words > 0
            ? `Added ${formatNumber(data.words)} words, ${formatNumber(data.forms)} forms from WaniKani ${range}.`
            : `All words from WaniKani ${range} are already in the vocabulary.`,
        life: 5000,
      });
      await nextTick();
      emit('changed');
    } catch (error) {
      toast.add({
        severity: 'error',
        summary: 'Error',
        detail: extractApiError(error, 'Failed to add WaniKani words.'),
        life: 5000,
      });
    } finally {
      isLoading.value = false;
    }
  }

  interface AccountImportResult {
    words: number;
    forms: number;
    matched: number;
    unmatched: number;
    unmatchedWords: string[];
  }

  const token = ref('');
  const minStage = ref<number>(1);
  const progress = ref('');
  const accountError = ref('');
  const accountResult = ref<(AccountImportResult & { received: number }) | null>(null);

  const PHASE_LABELS: Record<WaniKaniProgressPhase, string> = {
    assignments: 'Reading progress',
    statistics: 'Reading answer totals',
    subjects: 'Reading vocabulary',
  };

  async function importFromAccount() {
    const key = token.value.trim();
    if (!key || isLoading.value) return;

    isLoading.value = true;
    accountError.value = '';
    accountResult.value = null;
    try {
      const client = createWaniKaniClient(key);
      progress.value = 'Checking the token…';
      const user = await client.getUser();

      const words = await client.getStudiedWords(minStage.value, (phase, loaded, total) => {
        progress.value = `${PHASE_LABELS[phase]} for ${user.username}: ${formatNumber(loaded)} of ${formatNumber(total)}`;
      });
      if (words.length === 0) {
        accountResult.value = { received: 0, words: 0, forms: 0, matched: 0, unmatched: 0, unmatchedWords: [] };
        return;
      }

      progress.value = `Adding ${formatNumber(words.length)} words…`;
      const data = await $api<AccountImportResult>('user/vocabulary/import-from-wanikani', { method: 'POST', body: { words } });
      accountResult.value = { ...data, received: words.length };
      await nextTick();
      emit('changed');
    } catch (error) {
      if (error instanceof WaniKaniApiError)
        accountError.value =
          error.status === 401
            ? 'WaniKani rejected this token. Check that it was copied in full.'
            : error.status === 429
              ? 'WaniKani is limiting requests. Try again in a minute.'
              : 'WaniKani could not be reached. Try again in a few minutes.';
      else accountError.value = extractApiError(error, 'Failed to add WaniKani words.');
    } finally {
      isLoading.value = false;
      progress.value = '';
    }
  }
</script>

<template>
  <Card>
    <template #title>
      <h3 class="text-lg font-semibold">Import from WaniKani</h3>
    </template>
    <template #content>
      <div class="flex flex-col gap-4">
        <SelectButton
          v-model="mode"
          :options="modeOptions"
          option-label="label"
          option-value="value"
          :allow-empty="false"
          :disabled="isLoading"
          aria-label="Import method"
        />

        <template v-if="mode === 'level'">
          <div class="flex flex-col gap-2">
            <label for="wanikani-level" class="text-sm font-medium">Last completed level</label>
            <div class="flex flex-row flex-wrap gap-2 items-center">
              <InputNumber
                input-id="wanikani-level"
                :model-value="level"
                :min="LEVEL_MIN"
                :max="LEVEL_MAX"
                show-buttons
                fluid
                size="small"
                class="max-w-30 flex-shrink-0"
                @update:model-value="updateLevel"
              />
              <Slider v-model="level" :min="LEVEL_MIN" :max="LEVEL_MAX" aria-label="Last completed level" class="flex-grow mx-2 flex-basis-auto" />
            </div>
          </div>
          <p class="min-h-5 text-sm text-surface-600 dark:text-surface-300" aria-live="polite">
            <template v-if="wordCount != null">
              <span class="font-semibold tabular-nums text-surface-900 dark:text-surface-0">{{ formatNumber(wordCount) }}</span>
              words will be added as mastered.
            </template>
          </p>
          <Button icon="pi pi-plus" label="Add Words" class="w-full md:w-auto md:self-start" :loading="isLoading" @click="addLevelWords" />
        </template>

        <template v-else>
          <div class="flex flex-col gap-2">
            <label for="wanikani-token" class="text-sm font-medium">Personal access token</label>
            <InputText id="wanikani-token" v-model="token" type="password" autocomplete="off" class="w-full md:max-w-md" :disabled="isLoading" />
            <p class="text-sm text-surface-600 dark:text-surface-300">
              Create a read-only token in
              <a href="https://www.wanikani.com/settings/personal_access_tokens" target="_blank" rel="nofollow noopener" class="text-primary-500 hover:underline"
                >WaniKani settings</a
              >. Your token is only used locally in your browser and is never stored.
            </p>
          </div>
          <div class="flex flex-col gap-2">
            <label for="wanikani-stage" class="text-sm font-medium">Words to add</label>
            <Select
              v-model="minStage"
              input-id="wanikani-stage"
              :options="[...WANIKANI_STAGE_OPTIONS]"
              option-label="label"
              option-value="value"
              class="w-full md:max-w-md"
              :disabled="isLoading"
            />
            <p class="text-sm text-surface-600 dark:text-surface-300">
              Your burned words will be set as mastered. Other words will be added as reviews with a reconstructed history.
            </p>
          </div>
          <div class="flex flex-col md:flex-row md:items-center gap-3">
            <Button
              icon="pi pi-download"
              label="Import"
              class="w-full md:w-auto"
              :loading="isLoading"
              :disabled="!token.trim()"
              @click="importFromAccount"
            />
            <span class="text-sm text-surface-600 dark:text-surface-300" aria-live="polite">{{ progress }}</span>
          </div>
          <Message v-if="accountError" severity="error" :closable="false">{{ accountError }}</Message>
          <div v-if="accountResult" class="flex flex-col gap-2" aria-live="polite">
            <Message v-if="accountResult.received === 0" severity="info" :closable="false">No vocabulary at this stage on the account.</Message>
            <Message v-else severity="success" :closable="false">
              <template v-if="accountResult.words > 0">
                Added {{ formatNumber(accountResult.words) }} words, {{ formatNumber(accountResult.forms) }} forms from
                {{ formatNumber(accountResult.received) }} WaniKani words.
              </template>
              <template v-else>All {{ formatNumber(accountResult.matched) }} matched WaniKani words are already in the vocabulary.</template>
            </Message>
            <div v-if="accountResult.unmatched > 0" class="text-sm">
              <p class="mb-1 text-surface-600 dark:text-surface-300">
                Not in the dictionary ({{ formatNumber(accountResult.unmatched) }}):
              </p>
              <p class="max-h-32 overflow-y-auto rounded border border-surface-200 p-2 font-noto-sans dark:border-surface-700" lang="ja">
                {{ accountResult.unmatchedWords.join('、') }}
              </p>
            </div>
          </div>
        </template>
      </div>
    </template>
  </Card>
</template>
