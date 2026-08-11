<script setup lang="ts">
  import type { MediaConflict } from '~/composables/useAnkiMediaImport';

  const props = defineProps<{
    conflicts: MediaConflict[];
    resolved: number;
    fetchMedia: (filename: string) => Promise<string | false | null>;
  }>();

  const emit = defineEmits<{
    resolve: [useAnki: boolean];
    resolveAll: [useAnki: boolean];
  }>();

  const visible = defineModel<boolean>('visible', { required: true });

  const current = computed(() => props.conflicts[0] ?? null);
  const total = computed(() => props.resolved + props.conflicts.length);

  const incomingUrl = ref<string | null>(null);
  const incomingBytes = ref<number | null>(null);
  const loadingIncoming = ref(false);
  const incomingFailed = ref(false);

  function revokeIncoming() {
    if (incomingUrl.value) URL.revokeObjectURL(incomingUrl.value);
    incomingUrl.value = null;
    incomingBytes.value = null;
  }

  // The incoming file is only pulled from Anki when its conflict reaches the top of the queue, so
  // reviewing a hundred conflicts never loads a hundred files.
  async function loadIncoming(conflict: MediaConflict) {
    revokeIncoming();
    incomingFailed.value = false;
    loadingIncoming.value = true;

    try {
      const base64 = await props.fetchMedia(conflict.filename);
      if (!base64) {
        incomingFailed.value = true;
        return;
      }
      const bytes = base64ToBytes(base64);
      incomingBytes.value = bytes.length;
      incomingUrl.value = URL.createObjectURL(new Blob([bytes as BlobPart]));
    } catch {
      incomingFailed.value = true;
    } finally {
      loadingIncoming.value = false;
    }
  }

  watch(current, conflict => {
    if (conflict) loadIncoming(conflict);
    else revokeIncoming();
  }, { immediate: true });

  onUnmounted(revokeIncoming);

  const confirmingAll = ref<'anki' | 'current' | null>(null);

  function resolveAll(useAnki: boolean) {
    confirmingAll.value = null;
    emit('resolveAll', useAnki);
  }
</script>

<template>
  <Dialog
    v-model:visible="visible"
    modal
    :closable="false"
    :close-on-escape="false"
    :header="current ? `Conflicts — ${resolved + 1}/${total}` : 'Conflicts'"
    class="w-[95vw] sm:w-[90vw] md:w-[44rem]"
  >
    <div v-if="current" class="flex flex-col gap-4">
      <div>
        <span class="font-noto-sans text-lg">{{ current.word }}</span>
        <span v-if="current.reading" class="text-sm text-surface-500 ml-2 font-noto-sans">{{ current.reading }}</span>
        <span class="text-sm text-surface-500 ml-2">({{ current.kind }})</span>
      </div>

      <div class="grid grid-cols-1 sm:grid-cols-2 gap-4">
        <div class="flex flex-col gap-2">
          <p class="text-sm font-semibold">In Jiten</p>
          <img
            v-if="current.kind === 'image'"
            :src="current.current.url"
            alt="Current card image"
            class="max-h-48 w-full rounded border border-surface-200 dark:border-surface-700 object-contain bg-surface-50 dark:bg-surface-800"
          />
          <audio v-else :src="current.current.url" controls class="w-full" />
          <p class="text-xs text-surface-500">{{ formatBytes(current.current.fileSizeBytes) }}</p>
        </div>

        <div class="flex flex-col gap-2">
          <p class="text-sm font-semibold">From Anki</p>
          <div
            v-if="loadingIncoming"
            class="h-48 w-full animate-pulse rounded border border-surface-200 dark:border-surface-700 bg-surface-100 dark:bg-surface-800"
          />
          <Message v-else-if="incomingFailed" severity="warn" :closable="false">
            This file is missing from your Anki media folder.
          </Message>
          <template v-else-if="incomingUrl">
            <img
              v-if="current.kind === 'image'"
              :src="incomingUrl"
              alt="Incoming Anki image"
              class="max-h-48 w-full rounded border border-surface-200 dark:border-surface-700 object-contain bg-surface-50 dark:bg-surface-800"
            />
            <audio v-else :src="incomingUrl" controls class="w-full" />
          </template>
          <p v-if="incomingBytes !== null" class="text-xs text-surface-500">
            {{ formatBytes(incomingBytes) }}<span v-if="current.kind === 'image'"> before Jiten re-compresses it</span>
          </p>
          <p class="text-xs text-surface-500 font-noto-sans break-all">{{ current.filename }}</p>
        </div>
      </div>

      <div class="flex flex-wrap gap-2">
        <Button label="Keep current" severity="secondary" @click="emit('resolve', false)" />
        <Button label="Use Anki's" :disabled="incomingFailed" @click="emit('resolve', true)" />
      </div>

      <div class="flex flex-wrap gap-2 border-t border-surface-200 dark:border-surface-700 pt-3">
        <template v-if="confirmingAll === null">
          <Button label="Keep current for all remaining" severity="secondary" text size="small" @click="confirmingAll = 'current'" />
          <Button label="Use Anki's for all remaining" severity="secondary" text size="small" @click="confirmingAll = 'anki'" />
        </template>
        <template v-else>
          <span class="text-sm self-center">
            Apply to all {{ conflicts.length }} remaining conflict{{ conflicts.length === 1 ? '' : 's' }}?
          </span>
          <Button label="Confirm" size="small" @click="resolveAll(confirmingAll === 'anki')" />
          <Button label="Cancel" severity="secondary" text size="small" @click="confirmingAll = null" />
        </template>
      </div>
    </div>

    <p v-else class="text-sm text-surface-500">All conflicts reviewed.</p>
  </Dialog>
</template>
