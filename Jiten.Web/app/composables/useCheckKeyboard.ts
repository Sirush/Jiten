import { normalizeKey } from '~/composables/useStudyKeyboard';
import { isTypingTarget } from '~/utils/keyboard';

export function useCheckKeyboard(active: () => boolean, actions: () => Record<string, () => void>) {
  function onKey(event: KeyboardEvent) {
    if (!active() || event.repeat || event.ctrlKey || event.metaKey || event.altKey || isTypingTarget(event.target)) return;
    const key = normalizeKey(event);
    if ((key === ' ' || key === 'Enter') && (event.target as Element | null)?.closest?.('button, a, [role="button"]')) return;
    const action = actions()[key];
    if (!action) return;
    event.preventDefault();
    action();
  }

  onMounted(() => window.addEventListener('keydown', onKey));
  onBeforeUnmount(() => window.removeEventListener('keydown', onKey));
}
