import type { MediaGroupRef, MediaGroupStats, MediaType } from '~/types';

/** Live totals for a group under the media filter. Client only. */
export function useMediaGroupStats(group: Ref<MediaGroupRef>, mediaTypes: Ref<MediaType[]>) {
  const { $api } = useNuxtApp();
  const cache = new Map<string, Promise<MediaGroupStats>>();
  const stats = ref<MediaGroupStats | null>(null);
  const status = ref<'loading' | 'ready' | 'error'>('loading');
  let latest = 0;

  function fetchStats(target: MediaGroupRef, types: MediaType[]): Promise<MediaGroupStats> {
    const query = { kind: target.kind, id: target.id, mediaTypes: types.length > 0 ? [...types].sort((a, b) => a - b).join(',') : undefined };
    const key = JSON.stringify(query);
    const cached = cache.get(key);
    if (cached) return cached;
    const pending = $api<MediaGroupStats>('media-group/stats', { query }) as Promise<MediaGroupStats>;
    pending.catch(() => cache.delete(key));
    cache.set(key, pending);
    return pending;
  }

  async function reload() {
    const request = ++latest;
    status.value = 'loading';
    try {
      const result = await fetchStats(group.value, mediaTypes.value);
      if (request !== latest) return;
      stats.value = result;
      status.value = 'ready';
    } catch {
      if (request === latest) status.value = 'error';
    }
  }

  onMounted(() => {
    watch(() => [group.value.kind, group.value.id, mediaTypes.value.join(',')], reload, { immediate: true });
  });

  return { stats, status, reload };
}
