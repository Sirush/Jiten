export interface RequestTurnaround {
  medianDays: number | null;
  p75Days: number | null;
  sampleSize: number;
  readyToProcess: number;
  awaitingFile: number;
  medianAwaitingFileDays: number | null;
}

function formatDays(days: number): string {
  if (days < 21) return `${Math.round(days)} days`;
  const weeks = Math.round(days / 7);
  return weeks <= 8 ? `${weeks} weeks` : `${Math.round(days / 30)} months`;
}

export function useRequestTurnaround() {
  const { $api } = useNuxtApp();
  const stats = ref<RequestTurnaround | null>(null);

  const load = async () => {
    try {
      stats.value = await $api<RequestTurnaround>('requests/turnaround');
    } catch {
      stats.value = null;
    }
  };

  const fulfilmentRange = computed(() => {
    const s = stats.value;
    if (!s || s.medianDays === null || s.p75Days === null || s.sampleSize < 30) return null;
    const low = Math.round(s.medianDays);
    const high = Math.round(s.p75Days);
    return low === high ? `${low} days` : `${low} to ${high} days`;
  });

  const awaitingWait = computed(() => {
    const s = stats.value;
    if (!s || s.medianAwaitingFileDays === null || s.awaitingFile < 10) return null;
    return formatDays(s.medianAwaitingFileDays);
  });

  return { stats, load, fulfilmentRange, awaitingWait };
}
