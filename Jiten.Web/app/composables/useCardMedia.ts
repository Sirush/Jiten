import type { CardMediaBatchResponse, CardMediaDto, CardMediaEntry, CardMediaKind, CardMediaQuotaDto, CardMediaUploadResponse } from '~/types';

// Signed media URLs expire ~30 min; the cache is a per-tab singleton keyed by (wordId, readingIndex).
// A load failure triggers a single fresh batch fetch for that card (see refreshOne). Media is
// user-global, not session-scoped, so the cache survives across study sessions.
const mediaCache = ref(new Map<string, CardMediaEntry>());
const quota = ref<CardMediaQuotaDto | null>(null);
const inFlightKeys = new Set<string>();

// URLs already handed to the browser for byte download. Keyed by URL, not card, so a token-rotated
// refresh re-warms naturally while the same signed URL is never requested twice.
const warmedUrls = new Set<string>();
// Warm elements are pinned briefly so the GC can't collect them mid-download and abort the request.
const warmPins: (HTMLImageElement | HTMLAudioElement)[] = [];
const WARM_PIN_LIMIT = 16;

function pinWarm(el: HTMLImageElement | HTMLAudioElement) {
  warmPins.push(el);
  if (warmPins.length > WARM_PIN_LIMIT) warmPins.shift();
}

const BATCH_LIMIT = 500;

function keyOf(wordId: number, readingIndex: number) {
  return `${wordId}-${readingIndex}`;
}

function normaliseQuota(res: unknown): CardMediaQuotaDto | null {
  const q = (res as { quota?: CardMediaQuotaDto })?.quota ?? (res as CardMediaQuotaDto);
  if (q && typeof q.usedBytes === 'number' && typeof q.maxBytes === 'number') return q;
  return null;
}

export function useCardMedia() {
  const { $api } = useNuxtApp();

  function get(wordId: number, readingIndex: number): CardMediaEntry | undefined {
    return mediaCache.value.get(keyOf(wordId, readingIndex));
  }

  function setEntry(entry: CardMediaEntry) {
    const next = new Map(mediaCache.value);
    next.set(keyOf(entry.wordId, entry.readingIndex), entry);
    mediaCache.value = next;
  }

  // Batch-prefetch media for a set of cards, skipping anything already cached or in flight.
  async function prefetch(pairs: { wordId: number; readingIndex: number }[]) {
    if (!import.meta.client) return;
    const todo = pairs.filter((p) => {
      const k = keyOf(p.wordId, p.readingIndex);
      return !mediaCache.value.has(k) && !inFlightKeys.has(k);
    });
    if (todo.length === 0) return;

    const keys = todo.map((p) => keyOf(p.wordId, p.readingIndex));
    keys.forEach((k) => inFlightKeys.add(k));
    try {
      for (let i = 0; i < todo.length; i += BATCH_LIMIT) {
        const chunk = todo.slice(i, i + BATCH_LIMIT);
        const res = await $api<CardMediaBatchResponse>('srs/card-media/batch', {
          method: 'POST',
          body: { items: chunk },
        });
        const next = new Map(mediaCache.value);
        for (const item of res.items) next.set(keyOf(item.wordId, item.readingIndex), item);
        // Cache an empty entry for any card the server omitted so it isn't refetched every trigger.
        for (const p of chunk) {
          const k = keyOf(p.wordId, p.readingIndex);
          if (!next.has(k)) next.set(k, { wordId: p.wordId, readingIndex: p.readingIndex, image: null, audio: null });
        }
        mediaCache.value = next;
      }
    } catch {
      // Leave uncached; a later trigger (or an on-error refresh) retries.
    } finally {
      keys.forEach((k) => inFlightKeys.delete(k));
    }
  }

  // Start browser downloads of a card's media bytes so display/playback hits the HTTP cache.
  // No-op for cards whose URLs aren't in the metadata cache yet; a later trigger catches them.
  function warm(wordId: number, readingIndex: number) {
    if (!import.meta.client) return;
    const entry = get(wordId, readingIndex);
    if (!entry) return;
    const imageUrl = entry.image?.url;
    if (imageUrl && !warmedUrls.has(imageUrl)) {
      warmedUrls.add(imageUrl);
      const img = new Image();
      img.src = imageUrl;
      pinWarm(img);
    }
    const audioUrl = entry.audio?.url;
    if (audioUrl && !warmedUrls.has(audioUrl)) {
      warmedUrls.add(audioUrl);
      const a = new Audio();
      a.preload = 'auto';
      a.src = audioUrl;
      a.load();
      pinWarm(a);
    }
  }

  // Re-fetch a single card, used to refresh an expired signed URL after a load failure.
  async function refreshOne(wordId: number, readingIndex: number): Promise<CardMediaEntry | null> {
    if (!import.meta.client) return null;
    try {
      const res = await $api<CardMediaBatchResponse>('srs/card-media/batch', {
        method: 'POST',
        body: { items: [{ wordId, readingIndex }] },
      });
      const entry = res.items[0] ?? { wordId, readingIndex, image: null, audio: null };
      setEntry(entry);
      return entry;
    } catch {
      return null;
    }
  }

  async function upload(wordId: number, readingIndex: number, file: File): Promise<CardMediaUploadResponse> {
    const formData = new FormData();
    formData.append('file', file);
    const res = await $api<CardMediaUploadResponse>(`srs/card-media/${wordId}/${readingIndex}`, {
      method: 'POST',
      body: formData,
    });

    // Uploaded media lives on this exact form, so it wins over any previously inherited media.
    const existing = get(wordId, readingIndex) ?? { wordId, readingIndex, image: null, audio: null };
    const updated: CardMediaEntry = { ...existing };
    if (res.media.kind === 'image') updated.image = res.media;
    else updated.audio = res.media;
    setEntry(updated);
    quota.value = res.quota;
    return res;
  }

  // Delete media of the given kind. Inherited media is owned by a sibling form, so the delete is
  // routed to sourceReadingIndex; both the current and owning form are then refreshed since the
  // current form's fallback may change.
  async function remove(wordId: number, readingIndex: number, kind: CardMediaKind, sourceReadingIndex?: number): Promise<CardMediaQuotaDto | null> {
    const owner = sourceReadingIndex ?? readingIndex;
    const res = await $api(`srs/card-media/${wordId}/${owner}/${kind}`, { method: 'DELETE' });
    const q = normaliseQuota(res);
    if (q) quota.value = q;
    await refreshOne(wordId, readingIndex);
    if (owner !== readingIndex) await refreshOne(wordId, owner);
    return quota.value;
  }

  function mediaFor(wordId: number, readingIndex: number, kind: CardMediaKind): CardMediaDto | null {
    const entry = get(wordId, readingIndex);
    if (!entry) return null;
    return kind === 'image' ? entry.image : entry.audio;
  }

  // Drop every cached entry for a word. Sibling forms inherit media across a word, so a change to any one
  // form can alter another form's fallback; evicting the whole word forces a fresh fetch on next use.
  function invalidateWord(wordId: number) {
    const next = new Map(mediaCache.value);
    let changed = false;
    for (const k of next.keys()) {
      if (k.startsWith(`${wordId}-`)) {
        next.delete(k);
        changed = true;
      }
    }
    if (changed) mediaCache.value = next;
  }

  function clearCache() {
    if (mediaCache.value.size > 0) mediaCache.value = new Map();
  }

  return { get, mediaFor, prefetch, warm, refreshOne, upload, remove, quota, mediaCache, invalidateWord, clearCache };
}
