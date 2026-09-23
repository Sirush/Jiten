<script setup lang="ts">
  import type { JpdbImportSummary, JpdbSkippedWord } from '~/composables/useJpdbApi';

  const visible = defineModel<boolean>('visible', { required: true });
  defineProps<{ summary: JpdbImportSummary | null }>();

  const reasonLabels: Record<JpdbSkippedWord['reason'], string> = {
    NotInDictionary: "not in Jiten's dictionary",
    NoReviews: 'no usable reviews',
    Redundant: 'made redundant by another form, kept in Recently Removed',
  };

  function groupByReason(words: JpdbSkippedWord[]) {
    const groups = new Map<JpdbSkippedWord['reason'], JpdbSkippedWord[]>();
    for (const w of words) {
      const list = groups.get(w.reason) ?? [];
      list.push(w);
      groups.set(w.reason, list);
    }
    return [...groups.entries()].map(([reason, list]) => ({ reason, label: reasonLabels[reason], words: list }));
  }

  function plural(n: number, one: string, many = `${one}s`) {
    return `${n.toLocaleString()} ${n === 1 ? one : many}`;
  }
</script>

<template>
  <Dialog v-model:visible="visible" modal header="JPDB import complete" class="w-[95vw] sm:w-[90vw] md:w-[40rem]">
    <div v-if="summary" class="flex flex-col gap-5">
      <section v-if="summary.reviews">
        <h4 class="font-medium mb-1">Review history</h4>
        <dl class="grid grid-cols-[auto_1fr] gap-x-4 gap-y-0.5 text-sm">
          <dt class="text-gray-600 dark:text-gray-400">Cards in file</dt>
          <dd class="tabular-nums">{{ summary.reviews.cardsInFile.toLocaleString() }}</dd>
          <dt class="text-gray-600 dark:text-gray-400">Cards imported</dt>
          <dd class="tabular-nums">{{ summary.reviews.cardsProcessed.toLocaleString() }}</dd>
          <dt class="text-gray-600 dark:text-gray-400">Reviews added</dt>
          <dd class="tabular-nums">{{ summary.reviews.reviewsImported.toLocaleString() }}</dd>
          <dt v-if="summary.reviews.reviewsUpdated > 0" class="text-gray-600 dark:text-gray-400">Reviews updated</dt>
          <dd v-if="summary.reviews.reviewsUpdated > 0" class="tabular-nums">{{ summary.reviews.reviewsUpdated.toLocaleString() }}</dd>
          <dt class="text-gray-600 dark:text-gray-400">Skipped</dt>
          <dd class="tabular-nums">{{ summary.reviews.skipped.toLocaleString() }}</dd>
        </dl>
        <details v-for="group in groupByReason(summary.reviews.skippedWords)" :key="group.reason" class="mt-2 text-sm">
          <summary class="cursor-pointer text-primary-500 hover:underline">
            {{ plural(group.words.length, 'word') }} skipped: {{ group.label }}
          </summary>
          <VocabularyImportSkippedWordList :words="group.words" class="mt-2" />
        </details>
        <p v-if="summary.reviews.skipped > summary.reviews.skippedWords.length" class="mt-1 text-sm text-gray-500 dark:text-gray-400">
          Only the first {{ summary.reviews.skippedWords.length }} skipped words are listed.
        </p>
      </section>

      <section v-if="summary.knownWords">
        <h4 class="font-medium mb-1">Known words</h4>
        <dl class="grid grid-cols-[auto_1fr] gap-x-4 gap-y-0.5 text-sm">
          <dt class="text-gray-600 dark:text-gray-400">Marked as known</dt>
          <dd class="tabular-nums">{{ summary.knownWords.added.toLocaleString() }}</dd>
          <dt class="text-gray-600 dark:text-gray-400">Already tracked</dt>
          <dd class="tabular-nums">{{ summary.knownWords.skipped.toLocaleString() }}</dd>
          <dt v-if="summary.knownWords.unmatched > 0" class="text-gray-600 dark:text-gray-400">Not in dictionary</dt>
          <dd v-if="summary.knownWords.unmatched > 0" class="tabular-nums">{{ summary.knownWords.unmatched.toLocaleString() }}</dd>
        </dl>
        <details v-if="summary.knownWords.unmatchedWords.length > 0" class="mt-2 text-sm">
          <summary class="cursor-pointer text-primary-500 hover:underline">
            {{ plural(summary.knownWords.unmatchedWords.length, 'word') }} skipped: not in Jiten's dictionary
          </summary>
          <VocabularyImportSkippedWordList :words="summary.knownWords.unmatchedWords" class="mt-2" />
        </details>
      </section>

      <section v-if="summary.wordLists">
        <h4 class="font-medium mb-1">Word lists</h4>
        <ul class="flex flex-col divide-y divide-surface-200 dark:divide-surface-700 rounded border border-surface-200 dark:border-surface-700 text-sm">
          <li v-for="deck in summary.wordLists" :key="deck.userStudyDeckId" class="px-3 py-2">
            <div class="flex flex-wrap items-baseline gap-x-3 gap-y-0.5">
              <NuxtLink :to="`/srs/decks/${deck.userStudyDeckId}/vocabulary`" target="_blank" class="text-primary-500 hover:underline min-w-0 truncate">{{ deck.name }}</NuxtLink>
              <span class="text-gray-600 dark:text-gray-400">{{ deck.replaced ? 'updated' : 'created' }}, {{ plural(deck.matched, 'word') }}</span>
            </div>
            <details v-if="deck.unmatchedWords.length > 0" class="mt-1">
              <summary class="cursor-pointer text-primary-500 hover:underline">
                {{ plural(deck.unmatched, 'word') }} skipped: not in Jiten's dictionary
              </summary>
              <VocabularyImportSkippedWordList :words="deck.unmatchedWords" class="mt-2" />
              <p v-if="deck.unmatched > deck.unmatchedWords.length" class="mt-1 text-gray-500 dark:text-gray-400">
                Only the first {{ deck.unmatchedWords.length }} are listed.
              </p>
            </details>
          </li>
        </ul>
      </section>

      <p class="text-sm text-gray-600 dark:text-gray-400">Your coverage is being recalculated, this can take a few minutes.</p>
    </div>
    <template #footer>
      <Button label="Close" @click="visible = false" />
    </template>
  </Dialog>
</template>
