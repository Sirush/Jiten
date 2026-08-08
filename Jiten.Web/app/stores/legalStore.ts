import { defineStore } from 'pinia';
import { useAuthStore } from '~/stores/authStore';

export interface LegalCguStatus {
  version: string;
  accepted: boolean;
  dismissed: boolean;
  noticeShownAt: string | null;
  effectiveDate: string | null;
  phase: 'notice' | 'elapsed' | null;
}

export interface LegalCgvStatus {
  version: string;
  accepted: boolean;
}

export interface LegalStatus {
  cgu: LegalCguStatus;
  cgv: LegalCgvStatus;
}

/**
 * Per-user legal document state (notice / acceptance / dismissal). Fetched once per session; the server
 * rows are the evidence, so every transition goes through the API rather than local storage.
 */
export const useLegalStore = defineStore('legal', () => {
  const { $api } = useNuxtApp();
  const auth = useAuthStore();

  const status = ref<LegalStatus | null>(null);
  const fetched = ref(false);
  // "Remind me later": session-only by design, so the banner returns next visit until accepted.
  const sessionDismissed = ref(false);
  let inflight: Promise<void> | null = null;

  async function fetchStatus() {
    if (!import.meta.client || !auth.isAuthenticated) return;
    try {
      status.value = await $api<LegalStatus>('/legal/status');
      fetched.value = true;
    } catch {
      // Fail closed (no banner) rather than nagging on a transient error.
    }
  }

  function ensure(): Promise<void> {
    if (!import.meta.client || !auth.isAuthenticated) return Promise.resolve();
    if (fetched.value) return Promise.resolve();
    if (!inflight) {
      inflight = fetchStatus().finally(() => {
        inflight = null;
      });
    }
    return inflight;
  }

  /** Idempotent server-side; safe to call on every banner render. */
  async function recordNoticeShown() {
    const cgu = status.value?.cgu;
    if (!cgu || cgu.noticeShownAt) return;
    try {
      await $api('/legal/notice-shown', { method: 'POST', body: { document: 'cgu' } });
      const now = new Date();
      cgu.noticeShownAt = now.toISOString();
      cgu.effectiveDate = new Date(now.getTime() + 30 * 24 * 60 * 60 * 1000).toISOString();
    } catch {
      // The clock simply starts on a later render.
    }
  }

  async function acceptCgu() {
    const cgu = status.value?.cgu;
    if (!cgu) return;
    await $api('/legal/accept', { method: 'POST', body: { document: 'cgu', version: cgu.version } });
    cgu.accepted = true;
    cgu.phase = null;
  }

  async function acceptCgv() {
    const cgv = status.value?.cgv;
    if (!cgv) return;
    await $api('/legal/accept', { method: 'POST', body: { document: 'cgv', version: cgv.version } });
    cgv.accepted = true;
  }

  async function dismissCgu() {
    const cgu = status.value?.cgu;
    if (!cgu) return;
    await $api('/legal/dismiss', { method: 'POST', body: { document: 'cgu' } });
    cgu.dismissed = true;
    cgu.phase = null;
  }

  function remindLater() {
    sessionDismissed.value = true;
  }

  const cguPending = computed(() => {
    const cgu = status.value?.cgu;
    return !!cgu && !cgu.accepted && !cgu.dismissed && !sessionDismissed.value;
  });

  const cgvAccepted = computed(() => status.value?.cgv.accepted ?? false);
  const cgvVersion = computed(() => status.value?.cgv.version ?? '');

  function reset() {
    status.value = null;
    fetched.value = false;
    sessionDismissed.value = false;
    inflight = null;
  }

  return {
    status,
    fetched,
    cguPending,
    cgvAccepted,
    cgvVersion,
    ensure,
    recordNoticeShown,
    acceptCgu,
    acceptCgv,
    dismissCgu,
    remindLater,
    reset,
  };
});
