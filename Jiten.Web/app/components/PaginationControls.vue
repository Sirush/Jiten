<script setup lang="ts">
  const props = withDefaults(
    defineProps<{
      previousLink: object | null;
      nextLink: object | null;
      start: number;
      end: number;
      totalItems: number;
      itemLabel?: string;
      scrollToTopOnNavigate?: boolean;
      showSummary?: boolean;
      currentPage?: number;
      totalPages?: number;
      pageLinkFor?: (page: number) => object;
      pageSize?: number;
      pageSizeOptions?: number[];
      pageSizeParam?: string;
      mobileCompact?: boolean;
    }>(),
    {
      showSummary: true,
      currentPage: 0,
      totalPages: 0,
      pageLinkFor: undefined,
      pageSize: 0,
      pageSizeOptions: undefined,
      pageSizeParam: 'limit',
      mobileCompact: false,
    }
  );

  const router = useRouter();
  const route = useRoute();
  const label = computed(() => props.itemLabel ?? 'items');

  const onNavigate = () => {
    if (!props.scrollToTopOnNavigate) return;
    nextTick(() => {
      window.scrollTo({ top: 0, behavior: 'instant' });
    });
  };

  const hasPageNumbers = computed(() => !!props.pageLinkFor && props.totalPages > 1);

  const SLOTS = 7;

  /**
   * Always emits exactly SLOTS entries (null = ellipsis) once there are more pages than slots.
   * A window that grew or shrank with the current page would shift the surrounding buttons
   * under the cursor as the user pages through.
   */
  const pageItems = computed<(number | null)[]>(() => {
    if (!hasPageNumbers.value) return [];
    const total = props.totalPages;
    const current = props.currentPage;

    if (total <= SLOTS) return Array.from({ length: total }, (_, index) => index + 1);
    if (current <= 4) return [1, 2, 3, 4, 5, null, total];
    if (current >= total - 3) return [1, null, total - 4, total - 3, total - 2, total - 1, total];
    return [1, null, current - 1, current, current + 1, null, total];
  });

  const pageDigits = computed(() => String(props.totalPages).length);

  /**
   * Fixed (not minimum) width, shared by number buttons and ellipsis slots: any variation
   * between them would resize the strip as the window shifts from page to page.
   */
  const slotWidthClass = computed(() => {
    if (pageDigits.value <= 2) return 'w-9! px-0!';
    if (pageDigits.value === 3) return 'w-11! px-0!';
    return 'w-14! px-0!';
  });

  /**
   * The strip only appears once the row can hold it whole; seven four-digit slots plus the
   * jump box and page-size select are ~740px, so revealing it earlier would wrap to a second
   * line. Below these widths the compact readout navigates instead.
   */
  const numbersClass = computed(() => (pageDigits.value <= 2 ? 'hidden md:inline-flex' : 'hidden lg:inline-flex'));
  const compactClass = computed(() => (pageDigits.value <= 2 ? 'md:hidden' : 'lg:hidden'));

  // Doubles as the compact position readout, so narrow screens need no separate indicator.
  const showJump = computed(() => hasPageNumbers.value && props.totalPages > 10);
  const jumpPage = ref('');

  const jump = () => {
    const requested = Number.parseInt(jumpPage.value, 10);
    if (!props.pageLinkFor || !Number.isFinite(requested)) return;
    const target = Math.min(Math.max(requested, 1), props.totalPages);
    jumpPage.value = '';
    router.push(props.pageLinkFor(target));
    onNavigate();
  };

  const showPageSize = computed(() => (props.pageSizeOptions?.length ?? 0) > 1);

  const selectedPageSize = computed(() => {
    const fromUrl = Number(route.query[props.pageSizeParam]);
    return props.pageSizeOptions?.includes(fromUrl) ? fromUrl : props.pageSize;
  });

  const onPageSizeChange = (size: number) => {
    router.push({ query: { ...route.query, [props.pageSizeParam]: size, offset: undefined } });
    onNavigate();
  };
</script>

<template>
  <div class="flex-col gap-2 sm:flex-row sm:items-center sm:justify-between" :class="mobileCompact ? 'max-md:hidden md:flex' : 'flex'">
    <div class="flex flex-wrap items-center gap-2">
      <nav class="flex flex-wrap items-center gap-1" aria-label="Pagination">
        <Button
          v-if="previousLink"
          as="router-link"
          :to="previousLink"
          severity="secondary"
          text
          size="small"
          aria-label="Previous page"
          class="min-w-9!"
          @click="onNavigate"
        >
          <Icon name="material-symbols:chevron-left-rounded" size="1.25em" />
        </Button>
        <Button v-else severity="secondary" text size="small" disabled aria-label="Previous page" class="min-w-9!">
          <Icon name="material-symbols:chevron-left-rounded" size="1.25em" />
        </Button>

        <template v-if="hasPageNumbers">
          <span v-for="(page, index) in pageItems" :key="`${page ?? 'gap'}-${index}`" :class="numbersClass">
            <span v-if="page === null" :class="slotWidthClass" class="inline-flex justify-center text-surface-400 select-none">…</span>
            <Button v-else-if="page === currentPage" size="small" :class="slotWidthClass" aria-current="page" :aria-label="`Page ${page}`">
              {{ page }}
            </Button>
            <Button
              v-else
              as="router-link"
              :to="pageLinkFor!(page)"
              severity="secondary"
              text
              size="small"
              :class="slotWidthClass"
              :aria-label="`Go to page ${page}`"
              @click="onNavigate"
            >
              {{ page }}
            </Button>
          </span>
        </template>

        <Button
          v-if="nextLink"
          as="router-link"
          :to="nextLink"
          severity="secondary"
          text
          size="small"
          aria-label="Next page"
          class="min-w-9!"
          @click="onNavigate"
        >
          <Icon name="material-symbols:chevron-right-rounded" size="1.25em" />
        </Button>
        <Button v-else severity="secondary" text size="small" disabled aria-label="Next page" class="min-w-9!">
          <Icon name="material-symbols:chevron-right-rounded" size="1.25em" />
        </Button>
      </nav>

      <div v-if="hasPageNumbers" class="flex items-center gap-1.5 text-sm text-surface-500 dark:text-surface-400" :class="showJump ? '' : compactClass">
        <template v-if="showJump">
          <span>Page</span>
          <InputText
            v-model="jumpPage"
            size="small"
            inputmode="numeric"
            :placeholder="String(currentPage)"
            class="w-12! text-center"
            aria-label="Go to page"
            @keyup.enter="jump"
          />
          <span :class="compactClass">/ {{ totalPages.toLocaleString() }}</span>
        </template>
        <span v-else>Page {{ currentPage }} of {{ totalPages }}</span>
      </div>

      <Select
        v-if="showPageSize"
        :model-value="selectedPageSize"
        :options="pageSizeOptions"
        size="small"
        aria-label="Items per page"
        @update:model-value="onPageSizeChange"
      >
        <template #value="{ value }">
          {{ value }}
          <span class="hidden sm:inline">/ page</span>
        </template>
        <template #option="{ option }">{{ option }} / page</template>
      </Select>
    </div>

    <p v-if="showSummary" class="text-sm text-surface-500 dark:text-surface-400">
      Showing
      <span class="font-medium text-surface-700 dark:text-surface-200">{{ start.toLocaleString() }}-{{ end.toLocaleString() }}</span>
      of
      <span class="font-medium text-surface-700 dark:text-surface-200">{{ totalItems.toLocaleString() }}</span>
      {{ label }}
    </p>
  </div>

  <div v-if="mobileCompact" class="flex items-center justify-between gap-2 text-sm text-surface-500 dark:text-surface-400 md:hidden">
    <span>
      <span class="font-medium text-surface-700 dark:text-surface-200">{{ totalItems.toLocaleString() }}</span>
      {{ label }}
    </span>
    <div v-if="totalPages > 1" class="flex items-center gap-0.5">
      <Button
        v-if="previousLink"
        as="router-link"
        :to="previousLink"
        severity="secondary"
        text
        size="small"
        class="min-w-8!"
        aria-label="Previous page"
        @click="onNavigate"
      >
        <Icon name="material-symbols:chevron-left-rounded" size="1.25em" />
      </Button>
      <Button v-else severity="secondary" text size="small" disabled class="min-w-8!" aria-label="Previous page">
        <Icon name="material-symbols:chevron-left-rounded" size="1.25em" />
      </Button>
      <span class="tabular-nums">{{ currentPage }} / {{ totalPages.toLocaleString() }}</span>
      <Button
        v-if="nextLink"
        as="router-link"
        :to="nextLink"
        severity="secondary"
        text
        size="small"
        class="min-w-8!"
        aria-label="Next page"
        @click="onNavigate"
      >
        <Icon name="material-symbols:chevron-right-rounded" size="1.25em" />
      </Button>
      <Button v-else severity="secondary" text size="small" disabled class="min-w-8!" aria-label="Next page">
        <Icon name="material-symbols:chevron-right-rounded" size="1.25em" />
      </Button>
    </div>
  </div>
</template>
