import { useJitenStore } from '~/stores/jitenStore';

export function useGuestPrompt() {
  const route = useRoute();
  const store = useJitenStore();

  const isDismissed = (surface: string) => store.dismissedGuestPrompts.includes(surface);

  const dismiss = (surface: string) => {
    if (!isDismissed(surface)) store.dismissedGuestPrompts = [...store.dismissedGuestPrompts, surface];
  };

  /** Defaults to the current page so the visitor lands back where they were after signing up. */
  const authLink = (path: '/register' | '/login', redirect?: string) => {
    const target = safeRedirectPath(redirect ?? route.fullPath);
    return { path, query: target ? { redirect: target } : {} };
  };

  return { isDismissed, dismiss, authLink };
}
