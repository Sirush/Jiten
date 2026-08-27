<script setup lang="ts">
  import type { FsrsState } from '~/types/enums';

  const emit = defineEmits<{ changed: [] }>();

  const { $api } = useNuxtApp();
  const toast = useToast();
  const confirm = useConfirm();
  const convertToRuby = useConvertToRuby();

  interface RedundantForm {
    wordId: number;
    readingIndex: number;
    reading: string;
    state: FsrsState;
    reviewCount: number;
    coveringReadingIndex: number;
    coveringReading: string;
    coveringState: FsrsState;
    mainDefinition: string | null;
    frequencyRank: number;
  }

  type RedundantFormRow = RedundantForm & { key: string };

  const scanLoading = ref(false);
  const removeLoading = ref(false);
  const showPreview = ref(false);
  const scanned = ref(false);
  const forms = ref<RedundantFormRow[]>([]);
  const selectedKeys = ref(new Set<string>());

  const selected = computed(() => forms.value.filter((f) => selectedKeys.value.has(f.key)));
  const allSelected = computed(() => forms.value.length > 0 && selectedKeys.value.size === forms.value.length);

  function toggle(key: string) {
    const next = new Set(selectedKeys.value);
    if (!next.delete(key)) next.add(key);
    selectedKeys.value = next;
  }

  function toggleAll() {
    selectedKeys.value = allSelected.value ? new Set() : new Set(forms.value.map((f) => f.key));
  }

  async function scan() {
    scanLoading.value = true;
    try {
      const data = await $api<{ items: RedundantForm[]; totalItems: number }>('user/vocabulary/redundant-forms');
      forms.value = (data?.items ?? []).map((f) => ({ ...f, key: `${f.wordId}:${f.readingIndex}` }));
      selectedKeys.value = new Set(forms.value.map((f) => f.key));
      scanned.value = true;
      showPreview.value = true;
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Scan failed', detail: extractApiError(e, 'Could not check for redundant forms.'), life: 5000 });
    } finally {
      scanLoading.value = false;
    }
  }

  function confirmRemove() {
    const count = selected.value.length;
    const withHistory = selected.value.filter((f) => f.reviewCount > 0).length;
    const historyNote =
      withHistory > 0
        ? ` ${withHistory} of them ${withHistory === 1 ? 'has' : 'have'} review history, which is kept. You can restore it any time from Recently Removed below, or delete it for good there.`
        : '';
    confirm.require({
      header: `Remove ${count} form${count === 1 ? '' : 's'}`,
      message: `The selected card${count === 1 ? '' : 's'} will be removed. Each word stays known through the mastered form that covers it.${historyNote}`,
      icon: 'pi pi-exclamation-triangle',
      acceptLabel: 'Remove',
      rejectLabel: 'Cancel',
      acceptProps: { severity: 'danger' },
      accept: () => remove(),
    });
  }

  async function remove() {
    removeLoading.value = true;
    try {
      const result = await $api<{ removed: number; skipped: number }>('user/vocabulary/redundant-forms/resolve', {
        method: 'POST',
        body: { forms: selected.value.map((f) => ({ wordId: f.wordId, readingIndex: f.readingIndex })) },
      });
      toast.add({
        severity: 'success',
        summary: 'Redundant forms removed',
        detail: `Removed ${result?.removed ?? 0} form${result?.removed === 1 ? '' : 's'}.`,
        life: 5000,
      });
      const removedKeys = new Set(selected.value.map((f) => f.key));
      forms.value = forms.value.filter((f) => !removedKeys.has(f.key));
      selectedKeys.value = new Set();
      showPreview.value = false;
      emit('changed');
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Removal failed', detail: extractApiError(e, 'Could not remove redundant forms.'), life: 5000 });
    } finally {
      removeLoading.value = false;
    }
  }
</script>

<template>
  <Card>
    <template #title>
      <h2 class="text-xl font-bold">Redundant Forms</h2>
    </template>
    <template #content>
      <p class="text-sm text-muted-color mb-4">
        Some older imports can leave you with separate cards for two spellings of the same word that are
        <b>redundant</b>
        with each other. This will list those words so you can review and clean the redundant ones you no longer need manually. Nothing is deleted until you
        confirm and cards that are deleted will be in the archive below if they have reviews.
      </p>

      <div class="flex flex-wrap items-center gap-3">
        <Button label="Check my vocabulary" icon="pi pi-search" :loading="scanLoading" @click="scan" />
        <span v-if="scanned && forms.length === 0" class="text-sm text-muted-color italic">No redundant forms found.</span>
        <Button
          v-else-if="scanned"
          :label="`Review ${forms.length} form${forms.length === 1 ? '' : 's'}`"
          icon="pi pi-list"
          severity="secondary"
          text
          @click="showPreview = true"
        />
      </div>

      <Dialog
        v-model:visible="showPreview"
        modal
        :header="`Redundant forms — ${forms.length.toLocaleString()} form${forms.length === 1 ? '' : 's'}`"
        :style="{ width: '720px', maxWidth: '95vw' }"
      >
        <div v-if="forms.length === 0" class="text-sm text-muted-color italic py-4 text-center">No redundant forms found. Nothing to clean up.</div>

        <template v-else>
          <p class="text-sm text-muted-color mb-3">
            Ticked cards will be removed. The word stays known through the mastered form, and its review history moves to
            <strong>Recently Removed</strong>
            so you can put it back.
          </p>

          <div class="flex items-center justify-between gap-3 border-b border-surface-200 dark:border-surface-700 pb-2 mb-1">
            <label class="flex items-center gap-2 cursor-pointer select-none">
              <Checkbox :model-value="allSelected" binary :indeterminate="!allSelected && selected.length > 0" @update:model-value="toggleAll" />
              <span class="text-sm">Select all</span>
            </label>
            <span class="text-xs text-muted-color">{{ selected.length.toLocaleString() }} of {{ forms.length.toLocaleString() }} selected</span>
          </div>

          <div class="max-h-[55vh] overflow-y-auto divide-y divide-surface-200 dark:divide-surface-700">
            <div
              v-for="form in forms"
              :key="form.key"
              class="flex items-start gap-3 px-1 py-3 cursor-pointer transition-colors hover:bg-surface-50 dark:hover:bg-surface-800/60"
              @click="toggle(form.key)"
            >
              <Checkbox
                :model-value="selectedKeys.has(form.key)"
                binary
                class="mt-0.5 shrink-0"
                :aria-label="`Remove ${form.reading}`"
                @click.stop
                @update:model-value="toggle(form.key)"
              />

              <div class="min-w-0 flex-1">
                <div class="flex flex-wrap items-baseline gap-x-2 gap-y-1">
                  <NuxtLink
                    :to="`/vocabulary/${form.wordId}/${form.readingIndex}`"
                    target="_blank"
                    class="font-noto-sans text-base hover:underline"
                    :class="selectedKeys.has(form.key) ? 'text-muted-color line-through decoration-red-400' : 'text-blue-500'"
                    @click.stop
                  >
                    <span lang="ja" v-html="convertToRuby(form.reading)" />
                  </NuxtLink>
                  <span class="text-xs" :class="fsrsStateTone(form.state).tone">{{ fsrsStateTone(form.state).label }}</span>
                  <span v-if="form.reviewCount > 0" class="text-xs text-muted-color">
                    {{ form.reviewCount.toLocaleString() }} review{{ form.reviewCount === 1 ? '' : 's' }}
                  </span>

                  <i class="pi pi-arrow-right text-[0.65rem] text-muted-color mx-1" />

                  <NuxtLink
                    :to="`/vocabulary/${form.wordId}/${form.coveringReadingIndex}`"
                    target="_blank"
                    class="font-noto-sans text-base text-blue-500 hover:underline"
                    @click.stop
                  >
                    <span lang="ja" v-html="convertToRuby(form.coveringReading)" />
                  </NuxtLink>
                  <span class="text-xs" :class="fsrsStateTone(form.coveringState).tone">{{ fsrsStateTone(form.coveringState).label }}</span>
                </div>

                <div class="mt-0.5 text-xs text-muted-color truncate">{{ form.mainDefinition || '—' }}</div>
              </div>

              <span v-if="form.frequencyRank > 0" class="shrink-0 text-xs text-muted-color tabular-nums pt-0.5">
                #{{ form.frequencyRank.toLocaleString() }}
              </span>
            </div>
          </div>
        </template>

        <template #footer>
          <div class="flex justify-end gap-2">
            <Button label="Cancel" severity="secondary" text @click="showPreview = false" />
            <Button
              :label="selected.length === 0 ? 'Remove selected' : selected.length === 1 ? 'Remove 1 form' : `Remove ${selected.length.toLocaleString()} forms`"
              icon="pi pi-trash"
              severity="danger"
              :disabled="selected.length === 0 || removeLoading"
              :loading="removeLoading"
              @click="confirmRemove"
            />
          </div>
        </template>
      </Dialog>
    </template>
  </Card>
</template>
