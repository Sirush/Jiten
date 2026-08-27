import type { CardMediaBatchEntry, CardMediaBatchResponse, CardMediaImportResponse, ResolveWordsResponse } from '~/types';
import { base64ToBytes, extractAudioRef, extractImageRef } from '~/utils/ankiMediaExtract';

/** Matches the server's per-request manifest cap. */
const UPLOAD_CHUNK = 20;

/** Stays under the endpoint's 55 MB request size limit, with room for multipart overhead. */
const MAX_CHUNK_BYTES = 45 * 1024 * 1024;

/** Matches the server's import concurrency permits; requests are mostly CDN-write idle time. */
const WORKER_COUNT = 4;

/** Files per AnkiConnect multi call; ten at the 5 MB file cap is ~67 MB of base64 in one response. */
const FETCH_BATCH = 10;

/** Returns one entry per filename, in order; `false` means absent from Anki's media folder. */
type FetchMedia = (filenames: string[]) => Promise<Array<string | false | null>>;

const PRESENCE_CHUNK = 500;
const RESOLVE_CHUNK = 2000;
const RESOLVE_CHUNK_PARSED = 500;
const MAX_FILE_BYTES = 5 * 1024 * 1024;

export type MediaConflictMode = 'skip' | 'replace' | 'ask';
export type MediaKind = 'image' | 'audio';

export interface AnkiMediaImportStats {
  uploaded: number;
  replaced: number;
  skippedExisting: number;
  missingInAnki: number;
  tooLarge: number;
  invalid: number;
  notTracked: number;
  duplicateTarget: number;
  extraRefsIgnored: number;
  unresolved: number;
  quotaExceeded: number;
  uploadFailed: number;
}

export interface MediaConflict {
  key: string;
  word: string;
  reading: string;
  kind: MediaKind;
  filename: string;
  current: CardMediaBatchEntry;
}

interface UploadTarget {
  key: string;
  wordId: number;
  readingIndex: number;
  kind: MediaKind;
  filename: string;
  overwrite: boolean;
}

interface Candidate {
  word: string;
  reading: string;
  image: string | null;
  audio: string | null;
}

function emptyStats(): AnkiMediaImportStats {
  return {
    uploaded: 0,
    replaced: 0,
    skippedExisting: 0,
    missingInAnki: 0,
    tooLarge: 0,
    invalid: 0,
    notTracked: 0,
    duplicateTarget: 0,
    extraRefsIgnored: 0,
    unresolved: 0,
    quotaExceeded: 0,
    uploadFailed: 0,
  };
}

const candidateKey = (word: string, reading: string) => `${word} ${reading}`;

/**
 * Pulls a deck's images and audio into card media. Files are fetched from AnkiConnect just in time, so a
 * deck of thousands never sits in browser memory, and conflicts are reviewed while the rest keeps
 * uploading rather than after.
 */
export function useAnkiMediaImport() {
  const { $api } = useNuxtApp();

  const stats = ref<AnkiMediaImportStats>(emptyStats());
  const total = ref(0);
  const done = ref(0);
  const usedBytes = ref(0);
  const maxBytes = ref(0);
  const running = ref(false);
  const conflicts = ref<MediaConflict[]>([]);
  const startedAt = ref(0);

  // Recomputes when `done` advances (every upload chunk), which is fresh enough for a time estimate.
  const etaSeconds = computed(() => {
    if (!running.value || startedAt.value === 0 || done.value === 0) return null;
    const remaining = total.value - done.value;
    if (remaining <= 0) return null;
    return (((Date.now() - startedAt.value) / done.value) * remaining) / 1000;
  });

  let candidates = new Map<string, Candidate>();
  let pending: UploadTarget[] = [];
  let cancelled = false;
  let quotaHit = false;
  const wakers: Array<() => void> = [];

  function wakeWorkers() {
    wakers.splice(0).forEach((resolve) => resolve());
  }

  function reset() {
    // A worker still parked from an aborted run wakes into the fresh empty state and exits.
    wakeWorkers();
    candidates = new Map();
    pending = [];
    cancelled = false;
    quotaHit = false;
    stats.value = emptyStats();
    conflicts.value = [];
    total.value = 0;
    done.value = 0;
    usedBytes.value = 0;
    maxBytes.value = 0;
    startedAt.value = 0;
  }

  /** One card's contribution: at most one image and one audio reference per form, first card wins. */
  function collect(word: string, reading: string, imageHtml: string, audioHtml: string) {
    if (!word) return;

    const image = imageHtml ? extractImageRef(imageHtml) : null;
    const audio = audioHtml ? extractAudioRef(audioHtml) : null;
    if (!image && !audio) return;

    stats.value.extraRefsIgnored += (image?.extraRefs ?? 0) + (audio?.extraRefs ?? 0);

    const key = candidateKey(word, reading);
    const existing = candidates.get(key);
    if (!existing) {
      candidates.set(key, { word, reading, image: image?.filename ?? null, audio: audio?.filename ?? null });
      return;
    }

    // A second card for the same form only fills a gap; a competing file is counted and dropped.
    if (image) {
      if (existing.image) stats.value.duplicateTarget++;
      else existing.image = image.filename;
    }
    if (audio) {
      if (existing.audio) stats.value.duplicateTarget++;
      else existing.audio = audio.filename;
    }
  }

  function collectedCount() {
    let count = 0;
    for (const candidate of candidates.values()) {
      if (candidate.image) count++;
      if (candidate.audio) count++;
    }
    return count;
  }

  function cancel() {
    cancelled = true;
    conflicts.value = [];
    wakeWorkers();
  }

  async function resolveAll(parseWords: boolean) {
    const pairs = [...candidates.values()].map((c) => ({ word: c.word, reading: c.reading }));
    const chunkSize = parseWords ? RESOLVE_CHUNK_PARSED : RESOLVE_CHUNK;
    const resolved = new Map<string, { wordId: number; readingIndex: number }>();

    for (let i = 0; i < pairs.length && !cancelled; i += chunkSize) {
      const response = await $api<ResolveWordsResponse>('user/vocabulary/resolve-words', {
        method: 'POST',
        body: { pairs: pairs.slice(i, i + chunkSize), parseWords },
      });
      for (const entry of response.resolved ?? []) {
        resolved.set(candidateKey(entry.word, entry.reading), { wordId: entry.wordId, readingIndex: entry.readingIndex });
      }
    }

    return resolved;
  }

  async function scanPresence(forms: Array<{ wordId: number; readingIndex: number }>) {
    const present = new Map<string, { image: CardMediaBatchEntry | null; audio: CardMediaBatchEntry | null }>();

    for (let i = 0; i < forms.length && !cancelled; i += PRESENCE_CHUNK) {
      const response = await $api<CardMediaBatchResponse>('srs/card-media/batch', {
        method: 'POST',
        body: { items: forms.slice(i, i + PRESENCE_CHUNK) },
      });
      for (const item of response.items ?? []) {
        // Inherited media belongs to a sibling form, so this form still has a gap worth filling.
        present.set(`${item.wordId}-${item.readingIndex}`, {
          image: item.image && !item.image.inherited ? item.image : null,
          audio: item.audio && !item.audio.inherited ? item.audio : null,
        });
      }
    }

    return present;
  }

  function push(targets: UploadTarget[]) {
    pending.push(...targets);
    total.value += targets.length;
    wakeWorkers();
  }

  /** Answers one conflict. "Use Anki's" joins the queue that is already uploading. */
  function resolveConflict(useAnki: boolean) {
    const conflict = conflicts.value.shift();
    if (!conflict) return;

    if (useAnki) {
      const [wordId, readingIndex] = conflict.key.split('-').map(Number);
      push([
        {
          key: conflict.key,
          wordId: wordId!,
          readingIndex: readingIndex!,
          kind: conflict.kind,
          filename: conflict.filename,
          overwrite: true,
        },
      ]);
    } else {
      stats.value.skippedExisting++;
    }

    conflicts.value = [...conflicts.value];
    wakeWorkers();
  }

  function resolveAllConflicts(useAnki: boolean) {
    while (conflicts.value.length > 0) resolveConflict(useAnki);
  }

  /**
   * A throw from the fetcher means Anki is confirmed gone (the caller probes liveness before giving up),
   * which is not the same as a file being absent — so it propagates and aborts the run instead of
   * counting the rest as missing.
   */
  async function fetchFiles(targets: UploadTarget[], fetchMedia: FetchMedia) {
    const files: Array<{ target: UploadTarget; bytes: Uint8Array }> = [];

    for (let i = 0; i < targets.length && !cancelled; i += FETCH_BATCH) {
      const batch = targets.slice(i, i + FETCH_BATCH);
      const results = await fetchMedia(batch.map((target) => target.filename));
      // A mismatched batch cannot say which file is which, so it must not be mapped to per-file outcomes.
      if (results.length !== batch.length) throw new Error('Media fetch returned a mismatched batch.');

      batch.forEach((target, index) => {
        const base64 = results[index];
        if (!base64) {
          stats.value.missingInAnki++;
          return;
        }

        const bytes = base64ToBytes(base64);
        if (bytes.length > MAX_FILE_BYTES) {
          stats.value.tooLarge++;
          return;
        }

        files.push({ target, bytes });
      });
    }

    return { attempted: targets.length, files };
  }

  async function uploadChunk(files: Array<{ target: UploadTarget; bytes: Uint8Array }>) {
    const form = new FormData();
    form.append(
      'manifest',
      JSON.stringify(
        files.map((f, index) => ({
          index,
          wordId: f.target.wordId,
          readingIndex: f.target.readingIndex,
          overwrite: f.target.overwrite,
        }))
      )
    );

    files.forEach((f, index) => {
      form.append(`file${index}`, new Blob([f.bytes as BlobPart]), f.target.filename);
    });

    for (let attempt = 0; attempt < 5; attempt++) {
      try {
        return await $api<CardMediaImportResponse>('srs/card-media/import-batch', { method: 'POST', body: form });
      } catch (error) {
        const err = error as { status?: number; response?: { status?: number; headers?: Headers } };
        const status = err.status ?? err.response?.status;
        // ofetch does not retry POSTs, so the import's own rate policy has to be honoured here.
        if (status !== 429 || attempt === 4) throw error;
        const retryAfter = Number(err.response?.headers?.get?.('Retry-After'));
        const delay = Number.isFinite(retryAfter) && retryAfter > 0 ? retryAfter * 1000 : 2000 * (attempt + 1);
        await new Promise((resolve) => setTimeout(resolve, delay));
      }
    }
    return null;
  }

  /** Splits a fetched chunk so no single request can exceed the endpoint's request size limit. */
  function packByBytes(files: Array<{ target: UploadTarget; bytes: Uint8Array }>) {
    const batches: Array<Array<{ target: UploadTarget; bytes: Uint8Array }>> = [];
    let batch: Array<{ target: UploadTarget; bytes: Uint8Array }> = [];
    let batchBytes = 0;

    for (const file of files) {
      if (batch.length > 0 && batchBytes + file.bytes.length > MAX_CHUNK_BYTES) {
        batches.push(batch);
        batch = [];
        batchBytes = 0;
      }
      batch.push(file);
      batchBytes += file.bytes.length;
    }

    if (batch.length > 0) batches.push(batch);
    return batches;
  }

  async function uploadBatch(files: Array<{ target: UploadTarget; bytes: Uint8Array }>) {
    const response = await uploadChunk(files);
    if (!response) return;

    usedBytes.value = response.usedBytes;
    maxBytes.value = response.maxBytes;

    for (const result of response.results ?? []) {
      const target = files[result.index]?.target;
      switch (result.status) {
        case 'ok':
          if (target?.overwrite) stats.value.replaced++;
          else stats.value.uploaded++;
          break;
        case 'conflict':
          stats.value.skippedExisting++;
          break;
        case 'not_tracked':
          stats.value.notTracked++;
          break;
        case 'too_large':
          stats.value.tooLarge++;
          break;
        case 'quota_exceeded':
          stats.value.quotaExceeded++;
          quotaHit = true;
          break;
        case 'upload_failed':
          stats.value.uploadFailed++;
          break;
        default:
          stats.value.invalid++;
      }
    }
  }

  async function drain(fetchMedia: FetchMedia) {
    const worker = async () => {
      try {
        while (!cancelled && !quotaHit) {
          if (pending.length === 0) {
            // The clear queue can run dry while conflicts are still being reviewed; wait for the next answer.
            if (conflicts.value.length === 0) break;
            await new Promise<void>((resolve) => {
              wakers.push(resolve);
            });
            continue;
          }

          const { attempted, files } = await fetchFiles(pending.splice(0, UPLOAD_CHUNK), fetchMedia);
          // Files that never reach the server (missing in Anki, too large) complete here; uploaded ones
          // count once their batch's response lands, so progress never runs ahead of the outcome counters.
          done.value += attempted - files.length;

          for (const batch of packByBytes(files)) {
            if (cancelled || quotaHit) break;
            await uploadBatch(batch);
            done.value += batch.length;
          }
        }
      } catch (error) {
        // A worker failing (Anki gone, non-429 API error) must stop its sibling, not leave it running.
        cancelled = true;
        wakeWorkers();
        throw error;
      }
    };

    // One worker per server permit keeps them all busy; a worker's AnkiConnect fetch overlaps the
    // others' uploads, replacing the old single-worker prefetch. Staggered starts keep the workers
    // from hitting the rate window in lockstep, where they would 429 and sleep as a convoy.
    await Promise.all(
      Array.from({ length: WORKER_COUNT }, (_, index) =>
        (async () => {
          if (index > 0) await new Promise((resolve) => setTimeout(resolve, index * 400));
          return worker();
        })()
      )
    );

    if (quotaHit) {
      conflicts.value = [];
      pending = [];
    }
  }

  async function run(options: { parseWords: boolean; mode: MediaConflictMode; fetchMedia: FetchMedia }) {
    if (candidates.size === 0) return stats.value;

    running.value = true;
    try {
      const resolved = await resolveAll(options.parseWords);

      const targets: Array<UploadTarget & { word: string; reading: string }> = [];
      for (const [key, candidate] of candidates) {
        const form = resolved.get(key);
        if (!form) {
          stats.value.unresolved += (candidate.image ? 1 : 0) + (candidate.audio ? 1 : 0);
          continue;
        }
        const formKey = `${form.wordId}-${form.readingIndex}`;
        for (const kind of ['image', 'audio'] as MediaKind[]) {
          const filename = kind === 'image' ? candidate.image : candidate.audio;
          if (!filename) continue;
          targets.push({
            key: formKey,
            wordId: form.wordId,
            readingIndex: form.readingIndex,
            kind,
            filename,
            overwrite: options.mode === 'replace',
            word: candidate.word,
            reading: candidate.reading,
          });
        }
      }

      if (targets.length === 0) return stats.value;

      const forms = [...new Map(targets.map((t) => [t.key, { wordId: t.wordId, readingIndex: t.readingIndex }])).values()];
      const present = await scanPresence(forms);

      const clear: UploadTarget[] = [];
      for (const target of targets) {
        const existing = present.get(target.key);
        const current = target.kind === 'image' ? existing?.image : existing?.audio;

        if (!current) {
          clear.push(target);
          continue;
        }

        if (options.mode === 'skip') {
          stats.value.skippedExisting++;
        } else if (options.mode === 'replace') {
          clear.push({ ...target, overwrite: true });
        } else {
          conflicts.value.push({
            key: target.key,
            word: target.word,
            reading: target.reading,
            kind: target.kind,
            filename: target.filename,
            current,
          });
        }
      }

      push(clear);
      startedAt.value = Date.now();
      await drain(options.fetchMedia);
    } finally {
      running.value = false;
      conflicts.value = [];
    }

    return stats.value;
  }

  return {
    stats,
    total,
    done,
    usedBytes,
    maxBytes,
    running,
    conflicts,
    etaSeconds,
    reset,
    collect,
    collectedCount,
    cancel,
    resolveConflict,
    resolveAllConflicts,
    run,
  };
}
