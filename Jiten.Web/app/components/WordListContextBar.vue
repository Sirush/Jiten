<script setup lang="ts">
  import type { WordListContext } from '~/composables/useWordListContext';

  const props = defineProps<{
    wordId: number;
    readingIndex: number;
  }>();

  const router = useRouter();
  const { readContext } = useWordListContext();

  const context = ref<WordListContext | null>(null);

  onMounted(() => {
    context.value = readContext();
  });

  // Falls back to a wordId-only match so switching readings on this page keeps the bar,
  // even though the list only ever listed one reading of the word.
  const index = computed(() => {
    const items = context.value?.items;
    if (!items) return -1;
    const exact = items.findIndex(([id, reading]) => id === props.wordId && reading === props.readingIndex);
    return exact === -1 ? items.findIndex(([id]) => id === props.wordId) : exact;
  });

  const isVisible = computed(() => index.value !== -1);
  const position = computed(() => (context.value?.offset ?? 0) + index.value + 1);

  const previousItem = computed(() => (index.value > 0 ? context.value?.items[index.value - 1] : undefined));
  const nextItem = computed(() => context.value?.items[index.value + 1]);

  const hasPreviousPage = computed(() => (context.value?.offset ?? 0) > 0);
  const hasNextPage = computed(() => {
    const stored = context.value;
    if (!stored) return false;
    return stored.offset + stored.items.length < stored.totalItems;
  });

  const canGoPrevious = computed(() => !!previousItem.value || hasPreviousPage.value);
  const canGoNext = computed(() => !!nextItem.value || hasNextPage.value);

  // Only for crossing a page boundary. The back link uses `listPath` verbatim, since re-encoding
  // it here would not always reproduce the path the scroll anchor was stored under.
  const listPathWithOffset = (offset: number) => {
    const stored = context.value!;
    const [path, search = ''] = stored.listPath.split('?');
    const query = new URLSearchParams(search);
    if (offset > 0) query.set('offset', String(offset));
    else query.delete('offset');
    const queryString = query.toString();
    return queryString ? `${path}?${queryString}` : path!;
  };

  // Keeps the list's restore target on the word actually being read, so stepping through
  // words and then going back lands where the user stopped rather than where they entered.
  const goToWord = ([wordId, readingIndex]: [number, number]) => {
    if (context.value) rememberListAnchor(`${wordId}-${readingIndex}`, context.value.listPath);
    router.push(`/vocabulary/${wordId}/${readingIndex}`);
  };

  const goPrevious = () => {
    if (previousItem.value) return goToWord(previousItem.value);
    const stored = context.value;
    if (stored && hasPreviousPage.value) router.push(listPathWithOffset(Math.max(0, stored.offset - stored.pageSize)));
  };

  const goNext = () => {
    if (nextItem.value) return goToWord(nextItem.value);
    const stored = context.value;
    if (stored && hasNextPage.value) router.push(listPathWithOffset(stored.offset + stored.pageSize));
  };

  const onKeydown = (event: KeyboardEvent) => {
    if (!isVisible.value) return;
    if (event.metaKey || event.ctrlKey || event.altKey || event.shiftKey) return;

    const active = document.activeElement as HTMLElement | null;
    if (active?.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(active?.tagName ?? '')) return;

    if (event.key === 'ArrowLeft' && canGoPrevious.value) {
      event.preventDefault();
      goPrevious();
    } else if (event.key === 'ArrowRight' && canGoNext.value) {
      event.preventDefault();
      goNext();
    }
  };

  onMounted(() => window.addEventListener('keydown', onKeydown));
  onBeforeUnmount(() => window.removeEventListener('keydown', onKeydown));
</script>

<template>
  <div
    v-if="isVisible && context"
    class="flex flex-wrap items-center justify-between gap-2 rounded-lg bg-surface-100 dark:bg-surface-800 px-3 py-2 text-sm"
  >
    <NuxtLink :to="context.listPath" class="flex items-center gap-1 min-w-0" no-rel>
      <Icon name="material-symbols:arrow-back-rounded" class="shrink-0" />
      <span class="truncate">{{ context.label }}</span>
    </NuxtLink>

    <div class="flex items-center gap-3 text-surface-500 dark:text-surface-400">
      <span v-if="context.sortLabel" class="hidden sm:inline-flex items-center gap-0.5">
        {{ context.sortLabel }}
        <Icon :name="context.sortDescending ? 'material-symbols:arrow-downward-rounded' : 'material-symbols:arrow-upward-rounded'" />
      </span>
      <span>word {{ position.toLocaleString() }} of {{ context.totalItems.toLocaleString() }}</span>
      <div class="flex items-center gap-1">
        <Button
          text
          size="small"
          :disabled="!canGoPrevious"
          :title="previousItem ? 'Previous word (←)' : 'Back to the previous page of the list'"
          aria-label="Previous word"
          @click="goPrevious"
        >
          <Icon name="material-symbols:chevron-left-rounded" />
        </Button>
        <Button
          text
          size="small"
          :disabled="!canGoNext"
          :title="nextItem ? 'Next word (→)' : 'On to the next page of the list'"
          aria-label="Next word"
          @click="goNext"
        >
          <Icon name="material-symbols:chevron-right-rounded" />
        </Button>
      </div>
    </div>
  </div>
</template>
