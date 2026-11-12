import type { OnboardingPath, OnboardingState, WordCheckLevel } from '~/types/wordCheck';

type OnboardingUpdate = Partial<{ level: WordCheckLevel; path: OnboardingPath; dismissed: boolean; opened: boolean }>;

let pending: Promise<OnboardingState> | null = null;

export function useOnboarding() {
  const { $api } = useNuxtApp();
  const state = useState<OnboardingState | null>('onboarding-state', () => null);

  function refresh() {
    pending ??= $api<OnboardingState>('user/onboarding')
      .then((loaded) => (state.value = loaded))
      .finally(() => (pending = null));
    return pending;
  }

  async function update(patch: OnboardingUpdate) {
    state.value = await $api<OnboardingState>('user/onboarding', { method: 'PUT', body: patch });
    return state.value;
  }

  return { state, refresh, update };
}
