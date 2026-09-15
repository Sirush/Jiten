import type { SmartDeckSettings, SmartDeckSourceAction, SmartDeckStatusDto, SmartDeckUnitReportDto } from '~/types';
import { useSrsStore } from '~/stores/srsStore';
import { useToast } from 'primevue/usetoast';

export interface SmartDeckSettingsPayload extends Omit<SmartDeckSettings, 'enabled' | 'promoDismissed'> {
  excludeKana: boolean;
  minGlobalFrequency?: number | null;
  maxGlobalFrequency?: number | null;
  posFilter?: string | null;
}

let inflightStatus: Promise<void> | null = null;
let statusRequestSeq = 0;

export interface SmartDeckPreview {
  weighPlanning: boolean;
  lookaheadUnits: number;
  lookaheadByMediaType: Record<number, number>;
  targetPercentage: number;
  restTargetPercentage: number;
  sequenceOverrides: Record<number, boolean>;
  excludeKana: boolean;
  minGlobalFrequency?: number | null;
  maxGlobalFrequency?: number | null;
  posFilter?: string | null;
}

export function useSmartDeck() {
  const { $api } = useNuxtApp();
  const srsStore = useSrsStore();
  const toast = useToast();

  const status = useState<SmartDeckStatusDto | null>('smart-deck-status', () => null);
  const statusError = useState<string | null>('smart-deck-status-error', () => null);
  const loading = useState<boolean>('smart-deck-loading', () => false);
  const saving = ref(false);
  const promoDismissed = useState<boolean | null>('smart-deck-promo-dismissed', () => null);

  const exists = computed(() => !!status.value?.exists);
  const locked = computed(() => !!status.value?.locked);

  async function fetchStatus(force = false, preview?: SmartDeckPreview) {
    if (!import.meta.client) return;
    if (status.value && !force) return;
    if (inflightStatus && !force) return inflightStatus;
    const seq = ++statusRequestSeq;
    loading.value = true;
    statusError.value = null;
    inflightStatus = (async () => {
      try {
        const query = preview
          ? {
              weighPlanning: preview.weighPlanning,
              lookaheadUnits: preview.lookaheadUnits,
              lookaheadByMediaType: JSON.stringify(preview.lookaheadByMediaType),
              targetPercentage: preview.targetPercentage,
              restTargetPercentage: preview.restTargetPercentage,
              sequenceOverrides: Object.keys(preview.sequenceOverrides).length ? JSON.stringify(preview.sequenceOverrides) : undefined,
              excludeKana: preview.excludeKana,
              minGlobalFrequency: preview.minGlobalFrequency ?? undefined,
              maxGlobalFrequency: preview.maxGlobalFrequency ?? undefined,
              posFilter: preview.posFilter ?? undefined,
            }
          : undefined;
        const result = await $api<SmartDeckStatusDto>('srs/smart-deck', { query });
        if (seq === statusRequestSeq) status.value = result;
        if (result.building && !buildWatch && !watchingBuild) watchBuild(1);
      } catch {
        if (seq === statusRequestSeq) statusError.value = 'Could not load your Smart Deck.';
      } finally {
        if (seq === statusRequestSeq) {
          loading.value = false;
          inflightStatus = null;
        }
      }
    })();
    return inflightStatus;
  }

  async function fetchPromo() {
    if (!import.meta.client || promoDismissed.value !== null) return;
    try {
      const promo = await $api<{ dismissed: boolean }>('srs/smart-deck/promo');
      promoDismissed.value = promo.dismissed;
    } catch {
      promoDismissed.value = true;
    }
  }

  async function dismissPromo() {
    promoDismissed.value = true;
    try {
      await $api('srs/smart-deck/promo/dismiss', { method: 'POST' });
    } catch {
      promoDismissed.value = false;
    }
  }

  async function saveSettings(payload: SmartDeckSettingsPayload) {
    saving.value = true;
    try {
      await $api('srs/smart-deck/settings', { method: 'PUT', body: payload });
      await Promise.all([fetchStatus(true), srsStore.refreshOverview(true)]);
      watchBuild();
    } finally {
      saving.value = false;
    }
  }

  let buildWatch: ReturnType<typeof setTimeout> | null = null;
  let watchingBuild = false;
  /** Polls until the queued rebuild lands so the count and the decks row stop saying "Building". */
  function watchBuild(attempt = 0) {
    if (import.meta.server) return;
    if (buildWatch) clearTimeout(buildWatch);
    if (attempt >= 12) {
      buildWatch = null;
      watchingBuild = false;
      return;
    }
    watchingBuild = true;
    buildWatch = setTimeout(async () => {
      buildWatch = null;
      await fetchStatus(true);
      if (status.value?.building) {
        watchBuild(attempt + 1);
        return;
      }
      watchingBuild = false;
      await srsStore.refreshOverview(true);
      const count = status.value?.wordCount ?? 0;
      toast.add({
        severity: 'info',
        summary: 'Smart Deck rebuilt',
        detail: count > 0 ? `${count.toLocaleString()} words total.` : 'No words matched your settings.',
        life: 4000,
      });
    }, attempt === 0 ? 35000 : 10000);
  }

  async function applySourceAction(deckId: number, action: SmartDeckSourceAction) {
    const settings = await $api<SmartDeckSettings>(`srs/smart-deck/sources/${deckId}`, { method: 'POST', body: { action } });
    if (status.value) status.value = { ...status.value, settings };
    await fetchStatus(true);
    watchBuild();
    return settings;
  }

  async function rebuild() {
    await $api('srs/smart-deck/rebuild', { method: 'POST' });
    if (status.value) status.value = { ...status.value, building: true };
    watchBuild(1);
  }

  /** Null when the user is not Plus or the unit has no words; callers treat both as "nothing to say". */
  async function fetchUnitReport(deckId: number): Promise<SmartDeckUnitReportDto | null> {
    try {
      const report = await $api<SmartDeckUnitReportDto>(`srs/smart-deck/unit-report/${deckId}`);
      return report.totalWords > 0 ? report : null;
    } catch {
      return null;
    }
  }

  /** One sentence for a toast or a card: "You met 14 words you're studying in Episode 7, 3 learned this week." */
  function unitReportSentence(report: SmartDeckUnitReportDto, unitTitle: string): string {
    if (report.trackedWords === 0) return `None of the words you're studying appear in ${unitTitle} yet.`;
    const base = `You met ${report.trackedWords} word${report.trackedWords === 1 ? '' : 's'} you're studying in ${unitTitle}`;
    return report.learnedLast7Days > 0 ? `${base}, ${report.learnedLast7Days} of them learned this week.` : `${base}.`;
  }

  function sourceState(deckId: number): 'pinned' | 'included' | 'excluded' | 'none' {
    const s = status.value?.settings;
    if (!s) return 'none';
    if (s.excludedDeckIds.includes(deckId)) return 'excluded';
    if (s.pinnedDeckIds.includes(deckId)) return 'pinned';
    if (s.includedDeckIds.includes(deckId)) return 'included';
    return 'none';
  }

  return {
    status,
    statusError,
    loading,
    saving,
    promoDismissed,
    exists,
    locked,
    fetchStatus,
    fetchPromo,
    dismissPromo,
    saveSettings,
    applySourceAction,
    rebuild,
    fetchUnitReport,
    unitReportSentence,
    sourceState,
  };
}
