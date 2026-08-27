<script setup lang="ts">
  import type { CardMediaDto, CardMediaKind } from '~/types';
  import { stripRubyMarkup } from '~/utils/stripRubyMarkup';
  import { useToast } from 'primevue/usetoast';
  import { useConfirm } from 'primevue/useconfirm';

  const props = withDefaults(
    defineProps<{
      wordId: number;
      readingIndex: number;
      // Readings of the word, used to label inherited media ("from 会う").
      readings?: { text: string; readingIndex: number }[];
      // A file dropped elsewhere (e.g. onto the study card) to stage for the confirm dialog.
      droppedFile?: File | null;
      // Single-row layout for the vocabulary detail page: chips instead of labelled rows, and the
      // dropzone only appears while a file is being dragged over the window.
      compact?: boolean;
    }>(),
    {
      readings: () => [],
      droppedFile: null,
      compact: false,
    }
  );

  const emit = defineEmits<{
    changed: [];
  }>();

  const toast = useToast();
  const confirm = useConfirm();
  const cardMedia = useCardMedia();
  const { refresh: refreshPlus, tierSatisfies } = useJitenPlus();

  // Uploading (and replacing) needs an active tier; viewing and deleting existing media do not, so
  // a lapsed user can always remove media they own. Gating lives on the upload surfaces below.
  const canUpload = computed(() => tierSatisfies('trial'));

  const MAX_BYTES = 5 * 1024 * 1024;
  // Only the formats the backend accepts. .wav/.aac and other audio/image subtypes are rejected
  // client-side (before the confirm dialog) rather than confirmed and then failed server-side.
  const IMAGE_EXT = ['jpg', 'jpeg', 'png', 'webp', 'gif', 'heic', 'heif', 'avif'];
  const AUDIO_EXT = ['mp3', 'm4a', 'ogg', 'opus', 'webm', 'wav', 'flac'];
  const IMAGE_MIME = ['image/jpeg', 'image/png', 'image/webp', 'image/gif', 'image/heic', 'image/heif', 'image/avif'];
  const AUDIO_MIME = [
    'audio/mpeg',
    'audio/mp3',
    'audio/mp4',
    'audio/m4a',
    'audio/x-m4a',
    'audio/ogg',
    'audio/opus',
    'audio/webm',
    'video/webm',
    'audio/wav',
    'audio/x-wav',
    'audio/flac',
    'audio/x-flac',
  ];

  const entry = computed(() => cardMedia.get(props.wordId, props.readingIndex));
  const image = computed<CardMediaDto | null>(() => entry.value?.image ?? null);
  const audio = computed<CardMediaDto | null>(() => entry.value?.audio ?? null);

  const loading = ref(false);
  const uploading = ref(false);
  const deletingKind = ref<CardMediaKind | null>(null);
  const dragOver = ref(false);

  // Staged file awaiting the "Upload as image/audio?" confirmation.
  const pending = ref<{ file: File; kind: CardMediaKind; url: string } | null>(null);

  // HEIC/HEIF can't be rendered by browsers (Safari aside), so the staged blob preview would show broken.
  // The server converts them to WebP on upload; here we just show a placeholder instead of a dead <img>.
  const pendingPreviewable = computed(() => {
    const p = pending.value;
    if (!p || p.kind !== 'image') return false;
    const ext = p.file.name.split('.').pop()?.toLowerCase() ?? '';
    const type = (p.file.type || '').toLowerCase();
    return !(['heic', 'heif'].includes(ext) || type === 'image/heic' || type === 'image/heif');
  });
  const fileInput = ref<HTMLInputElement | null>(null);
  const rootEl = ref<HTMLElement | null>(null);

  async function ensureLoaded() {
    if (!import.meta.client || entry.value) return;
    loading.value = true;
    try {
      await cardMedia.refreshOne(props.wordId, props.readingIndex);
    } finally {
      loading.value = false;
    }
  }

  // Compact mode has no persistent <audio> element, so the clip plays through a throwaway element.
  const previewPlaying = ref(false);
  let previewAudio: HTMLAudioElement | null = null;

  function stopPreview() {
    if (previewAudio) {
      previewAudio.onended = null;
      previewAudio.onerror = null;
      previewAudio.pause();
      previewAudio = null;
    }
    previewPlaying.value = false;
  }

  function togglePreview() {
    if (previewPlaying.value) {
      stopPreview();
      return;
    }
    const url = audio.value?.url;
    if (!url) return;
    const a = new Audio(url);
    previewAudio = a;
    const done = () => {
      if (previewAudio === a) stopPreview();
    };
    a.onended = done;
    a.onerror = done;
    previewPlaying.value = true;
    a.play().catch(done);
  }

  watch(
    () => `${props.wordId}-${props.readingIndex}`,
    () => {
      clearPending();
      stopPreview();
      ensureLoaded();
    },
    { immediate: true }
  );

  function formText(idx: number): string {
    const r = props.readings.find((x) => x.readingIndex === idx);
    return r ? stripRubyMarkup(r.text) : '';
  }

  function inheritedLabel(m: CardMediaDto): string {
    const t = formText(m.sourceReadingIndex);
    return t ? `from ${t}` : 'from another form of this word';
  }

  function detectKind(file: File): CardMediaKind | null {
    const type = (file.type || '').toLowerCase();
    const ext = file.name.split('.').pop()?.toLowerCase() ?? '';
    // Extension is the most reliable signal; MIME is a fallback for files without one. Both are
    // matched against the supported set only, so unsupported audio/image subtypes fail here.
    if (IMAGE_EXT.includes(ext)) return 'image';
    if (AUDIO_EXT.includes(ext)) return 'audio';
    if (IMAGE_MIME.includes(type)) return 'image';
    if (AUDIO_MIME.includes(type)) return 'audio';
    return null;
  }

  function stageFile(file: File) {
    if (!canUpload.value) return;
    const kind = detectKind(file);
    if (!kind) {
      toast.add({
        severity: 'error',
        summary: 'Unsupported file',
        detail: 'Choose an image (jpg, png, webp, gif, heic, avif) or audio (mp3, m4a, ogg, opus, webm, wav, flac) file.',
        life: 4000,
      });
      return;
    }
    if (file.size > MAX_BYTES) {
      toast.add({ severity: 'error', summary: 'File too large', detail: 'Card media must be 5 MB or smaller.', life: 4000 });
      return;
    }
    clearPending();
    pending.value = { file, kind, url: URL.createObjectURL(file) };
  }

  function clearPending() {
    if (pending.value) URL.revokeObjectURL(pending.value.url);
    pending.value = null;
  }

  function openPicker() {
    fileInput.value?.click();
  }

  function onFilePicked(e: Event) {
    const input = e.target as HTMLInputElement;
    const file = input.files?.[0];
    if (file) stageFile(file);
    input.value = '';
  }

  function onDrop(e: DragEvent) {
    e.preventDefault();
    dragOver.value = false;
    windowDragDepth = 0;
    const file = e.dataTransfer?.files?.[0];
    if (file) stageFile(file);
  }

  function onDragOver(e: DragEvent) {
    e.preventDefault();
    dragOver.value = true;
  }

  function onDragLeave(e: DragEvent) {
    if (e.currentTarget === e.target) dragOver.value = false;
  }

  function onPaste(e: ClipboardEvent) {
    const items = e.clipboardData?.items;
    if (!items) return;
    for (const item of items) {
      if (item.kind === 'file') {
        const file = item.getAsFile();
        if (file) {
          stageFile(file);
          e.preventDefault();
          return;
        }
      }
    }
  }

  watch(
    () => props.droppedFile,
    (file) => {
      if (file) stageFile(file);
    },
    { immediate: true }
  );

  // In compact mode the visible drop target only exists while a file drag is in flight, so drag
  // state is tracked at the window level. Enter/leave fire per element and bubble, hence the depth
  // counter; it only reaches zero when the drag leaves the window entirely.
  let windowDragDepth = 0;

  function isFileDrag(e: DragEvent): boolean {
    return !!e.dataTransfer && Array.from(e.dataTransfer.types).includes('Files');
  }

  function onWindowDragEnter(e: DragEvent) {
    if (!canUpload.value || !isFileDrag(e)) return;
    windowDragDepth++;
    dragOver.value = true;
  }

  function onWindowDragOver(e: DragEvent) {
    if (dragOver.value && isFileDrag(e)) e.preventDefault();
  }

  function onWindowDragLeave() {
    if (!dragOver.value) return;
    windowDragDepth = Math.max(0, windowDragDepth - 1);
    if (windowDragDepth === 0) dragOver.value = false;
  }

  function onWindowDrop(e: DragEvent) {
    windowDragDepth = 0;
    if (!dragOver.value) return;
    dragOver.value = false;
    if (!isFileDrag(e)) return;
    e.preventDefault();
    const file = e.dataTransfer?.files?.[0];
    if (file) stageFile(file);
  }

  onMounted(() => {
    window.addEventListener('paste', onPaste);
    if (props.compact) {
      window.addEventListener('dragenter', onWindowDragEnter);
      window.addEventListener('dragover', onWindowDragOver);
      window.addEventListener('dragleave', onWindowDragLeave);
      window.addEventListener('drop', onWindowDrop);
    }
  });
  onUnmounted(() => {
    window.removeEventListener('paste', onPaste);
    if (props.compact) {
      window.removeEventListener('dragenter', onWindowDragEnter);
      window.removeEventListener('dragover', onWindowDragOver);
      window.removeEventListener('dragleave', onWindowDragLeave);
      window.removeEventListener('drop', onWindowDrop);
    }
    clearPending();
    stopPreview();
  });

  function handleError(e: unknown, action: 'upload' | 'delete' = 'upload') {
    const err = e as { status?: number; statusCode?: number; data?: { message?: string; error?: string } | string };
    const status = err?.status ?? err?.statusCode;
    if (status === 403 && action === 'upload') {
      toast.add({ severity: 'warn', summary: 'Jiten+ required', detail: 'Card media uploads are part of Jiten+.', life: 5000 });
      return;
    }
    if (status === 429) {
      toast.add({ severity: 'warn', summary: 'Slow down', detail: 'Too many uploads at once. Wait a moment and try again.', life: 5000 });
      return;
    }
    const summary = action === 'delete' ? 'Could not remove media' : 'Upload failed';
    // Quota and validation rejections carry `error`; other endpoints use `message`.
    const detail = (typeof err?.data === 'object' ? (err?.data?.error ?? err?.data?.message) : err?.data) || 'Please try again.';
    toast.add({ severity: 'error', summary, detail, life: 5000 });
  }

  async function confirmUpload() {
    if (!pending.value || uploading.value) return;
    uploading.value = true;
    try {
      await cardMedia.upload(props.wordId, props.readingIndex, pending.value.file);
      toast.add({ severity: 'success', summary: 'Saved', detail: 'Card media updated.', life: 2500 });
      refreshPlus();
      emit('changed');
      clearPending();
    } catch (e) {
      handleError(e);
    } finally {
      uploading.value = false;
    }
  }

  function removeKind(kind: CardMediaKind) {
    const m = kind === 'image' ? image.value : audio.value;
    if (!m || deletingKind.value) return;

    const noun = kind === 'image' ? 'image' : 'audio clip';
    const message = m.inherited
      ? `This ${noun} is inherited ${inheritedLabel(m)}. Removing it deletes it from that form too. This can't be undone.`
      : `Remove the ${noun} from this card? This can't be undone.`;

    confirm.require({
      message,
      header: kind === 'image' ? 'Remove image?' : 'Remove audio?',
      icon: 'pi pi-exclamation-triangle',
      rejectProps: { label: 'Cancel', severity: 'secondary', outlined: true },
      acceptProps: { label: 'Remove', severity: 'danger' },
      accept: () => doRemoveKind(kind),
    });
  }

  async function doRemoveKind(kind: CardMediaKind) {
    const m = kind === 'image' ? image.value : audio.value;
    if (!m || deletingKind.value) return;
    if (kind === 'audio') stopPreview();
    deletingKind.value = kind;
    try {
      await cardMedia.remove(props.wordId, props.readingIndex, kind, m.inherited ? m.sourceReadingIndex : undefined);
      toast.add({ severity: 'success', summary: 'Removed', life: 2000 });
      refreshPlus();
      emit('changed');
    } catch (e) {
      handleError(e, 'delete');
    } finally {
      deletingKind.value = null;
    }
  }
</script>

<template>
  <!-- Compact mode uses display:contents so the chip row and the drag panel participate directly in
       the parent's flex-wrap: row inline with the section heading, panel wrapping to a full line. -->
  <div ref="rootEl" :class="compact ? 'contents' : 'flex flex-col gap-3'" @dragover="onDragOver" @dragleave="onDragLeave" @drop="onDrop">
    <input
      ref="fileInput"
      type="file"
      accept=".jpg,.jpeg,.png,.webp,.gif,.heic,.heif,.avif,.mp3,.m4a,.ogg,.opus,.webm,.wav,.flac,image/jpeg,image/png,image/webp,image/gif,image/heic,image/heif,image/avif,audio/mpeg,audio/mp4,audio/ogg,audio/webm,audio/wav,audio/flac"
      class="hidden"
      @change="onFilePicked"
    />

    <div v-if="loading" class="flex items-center gap-2 text-sm text-surface-500 dark:text-surface-400">
      <i class="pi pi-spin pi-spinner text-sm" />
      Loading card media…
    </div>

    <template v-else-if="compact">
      <div class="flex flex-wrap items-center gap-2">
        <div v-if="image" class="flex items-center gap-0.5 rounded-md border border-surface-200 dark:border-surface-700 p-1">
          <SrsCardImage :url="image.url" img-class="h-10 min-w-10 max-w-20 w-auto rounded object-contain" />
          <Tooltip
            v-if="image.inherited"
            :content="`Inherited ${inheritedLabel(image)}. Deleting removes it from that form; uploading sets an image just for this form.`"
          >
            <i class="pi pi-link !text-[0.65rem] px-1 text-surface-400 dark:text-surface-400" />
          </Tooltip>
          <Button
            v-tooltip.top="'Remove image'"
            size="small"
            text
            rounded
            severity="danger"
            icon="pi pi-times"
            aria-label="Remove image"
            :loading="deletingKind === 'image'"
            @click="removeKind('image')"
          />
        </div>

        <div v-if="audio" class="flex items-center self-stretch gap-0.5 rounded-md border border-surface-200 dark:border-surface-700 p-1 pl-2">
          <button
            type="button"
            class="inline-flex items-center gap-1.5 text-sm text-surface-600 dark:text-surface-300 cursor-pointer"
            :aria-label="previewPlaying ? 'Stop audio' : 'Play audio'"
            @click="togglePreview"
          >
            <i :class="previewPlaying ? 'pi pi-stop' : 'pi pi-play'" class="!text-xs" />
            Audio
          </button>
          <Tooltip
            v-if="audio.inherited"
            :content="`Inherited ${inheritedLabel(audio)}. Deleting removes it from that form; uploading sets audio just for this form.`"
          >
            <i class="pi pi-link !text-[0.65rem] px-1 text-surface-400 dark:text-surface-400" />
          </Tooltip>
          <Button
            v-tooltip.top="'Remove audio'"
            size="small"
            text
            rounded
            severity="danger"
            icon="pi pi-times"
            aria-label="Remove audio"
            :loading="deletingKind === 'audio'"
            @click="removeKind('audio')"
          />
        </div>

        <Button
          v-if="canUpload && (image || audio)"
          v-tooltip.top="'Add or replace an image or audio clip. You can also drop or paste a file anywhere on the page.'"
          size="small"
          text
          icon="pi pi-upload"
          aria-label="Add or replace image or audio"
          @click="openPicker"
        />

        <JitenPlusGate v-if="!image && !audio" feature="card-media" feature-label="Card media" compact>
          <Button
            v-tooltip.top="'You can also drop or paste a file anywhere on the page.'"
            size="small"
            text
            icon="pi pi-plus"
            label="Add image or audio"
            @click="openPicker"
          />
        </JitenPlusGate>
      </div>

      <div
        v-if="dragOver"
        class="flex w-full items-center justify-center gap-2 rounded-lg border-2 border-dashed border-primary-500 bg-primary-50 dark:bg-primary-950/40 px-4 py-3 text-sm text-primary-700 dark:text-primary-300"
      >
        <i class="pi pi-cloud-upload" />
        Drop anywhere to add it to this card
      </div>
    </template>

    <template v-else>
      <!-- Image -->
      <div class="flex items-start gap-3">
        <div class="w-14 shrink-0 text-xs font-medium text-surface-500 dark:text-surface-400 pt-1">Image</div>
        <div class="flex-1 min-w-0">
          <div v-if="image" class="flex items-start gap-3">
            <SrsCardImage
              :url="image.url"
              img-class="max-h-20 min-w-20 max-w-full w-auto rounded-md object-contain border border-surface-200 dark:border-surface-700"
            />
            <div class="flex flex-col gap-1.5 min-w-0">
              <span
                v-if="image.inherited"
                class="inline-flex items-center gap-1 text-[0.7rem] px-1.5 py-0.5 rounded bg-surface-100 dark:bg-surface-800 text-surface-500 dark:text-surface-400 w-fit"
              >
                <i class="pi pi-link !text-[0.6rem]" />
                Inherited {{ inheritedLabel(image) }}
              </span>
              <p v-if="image.inherited" class="text-[0.7rem] text-surface-500 dark:text-surface-400">
                Delete removes it from {{ formText(image.sourceReadingIndex) || 'that form' }}. Upload a new one to set an image just for this form.
              </p>
              <div class="flex items-center gap-1">
                <Button
                  v-if="canUpload"
                  size="small"
                  text
                  :label="image.inherited ? 'Replace for this form' : 'Replace'"
                  icon="pi pi-upload"
                  @click="openPicker"
                />
                <Button
                  size="small"
                  text
                  severity="danger"
                  label="Delete"
                  icon="pi pi-trash"
                  :loading="deletingKind === 'image'"
                  @click="removeKind('image')"
                />
              </div>
            </div>
          </div>
          <span v-else class="text-sm text-surface-400 dark:text-surface-400">No image</span>
        </div>
      </div>

      <!-- Audio -->
      <div class="flex items-start gap-3">
        <div class="w-14 shrink-0 text-xs font-medium text-surface-500 dark:text-surface-400 pt-1">Audio</div>
        <div class="flex-1 min-w-0">
          <div v-if="audio" class="flex flex-col gap-1.5">
            <audio :src="audio.url" controls class="w-full max-w-xs h-9" />
            <span
              v-if="audio.inherited"
              class="inline-flex items-center gap-1 text-[0.7rem] px-1.5 py-0.5 rounded bg-surface-100 dark:bg-surface-800 text-surface-500 dark:text-surface-400 w-fit"
            >
              <i class="pi pi-link !text-[0.6rem]" />
              Inherited {{ inheritedLabel(audio) }}
            </span>
            <p v-if="audio.inherited" class="text-[0.7rem] text-surface-500 dark:text-surface-400">
              Delete removes it from {{ formText(audio.sourceReadingIndex) || 'that form' }}. Upload a new one to set audio just for this form.
            </p>
            <div class="flex items-center gap-1">
              <Button
                v-if="canUpload"
                size="small"
                text
                :label="audio.inherited ? 'Replace for this form' : 'Replace'"
                icon="pi pi-upload"
                @click="openPicker"
              />
              <Button size="small" text severity="danger" label="Delete" icon="pi pi-trash" :loading="deletingKind === 'audio'" @click="removeKind('audio')" />
            </div>
          </div>
          <span v-else class="text-sm text-surface-400 dark:text-surface-400">No audio</span>
        </div>
      </div>

      <!-- Dropzone -->
      <JitenPlusGate feature="card-media" feature-label="Card media">
        <button
          type="button"
          class="flex w-full flex-col items-center justify-center gap-1 rounded-lg border-2 border-dashed px-4 py-4 text-center transition-colors cursor-pointer"
          :class="
            dragOver
              ? 'border-primary-500 bg-primary-50 dark:bg-primary-950/40'
              : 'border-surface-300 dark:border-surface-600 hover:border-primary-400 hover:bg-surface-50 dark:hover:bg-surface-800/60'
          "
          @click="openPicker"
        >
          <i class="pi pi-cloud-upload text-lg text-surface-400 dark:text-surface-400" />
          <span class="text-sm text-surface-600 dark:text-surface-300">Drop, paste, or click to add an image or audio clip</span>
          <span class="text-[0.7rem] text-surface-400 dark:text-surface-400">Max 5 MB. Replaces the existing image or audio.</span>
        </button>
      </JitenPlusGate>
    </template>

    <!-- Confirm upload -->
    <Dialog
      :visible="!!pending"
      modal
      :header="pending?.kind === 'image' ? 'Upload as card image?' : 'Upload as card audio?'"
      :style="{ width: '26rem' }"
      :breakpoints="{ '480px': '92vw' }"
      @update:visible="
        (v: boolean) => {
          if (!v) clearPending();
        }
      "
    >
      <div v-if="pending" class="flex flex-col items-center gap-3">
        <SrsCardImage
          v-if="pending.kind === 'image' && pendingPreviewable"
          :url="pending.url"
          img-class="max-h-52 min-w-52 max-w-full w-auto rounded-md object-contain"
        />
        <div
          v-else-if="pending.kind === 'image'"
          class="flex flex-col items-center justify-center gap-2 w-full rounded-md border border-surface-200 dark:border-surface-700 bg-surface-50 dark:bg-surface-800 py-8 text-surface-500 dark:text-surface-400"
        >
          <i class="pi pi-image text-2xl" />
          <span class="text-xs text-center px-4">Preview isn't available for this format. It will be converted when you upload.</span>
        </div>
        <audio v-else :src="pending.url" controls class="w-full" />
        <p class="text-xs text-surface-500 dark:text-surface-400 self-start truncate w-full">{{ pending.file.name }}</p>
      </div>
      <template #footer>
        <Button label="Cancel" severity="secondary" text :disabled="uploading" @click="clearPending" />
        <Button :label="pending?.kind === 'image' ? 'Upload image' : 'Upload audio'" icon="pi pi-check" :loading="uploading" @click="confirmUpload" />
      </template>
    </Dialog>
  </div>
</template>
