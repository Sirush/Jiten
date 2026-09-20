import { computed, nextTick, ref, watch } from 'vue';
import { createPinia, setActivePinia } from 'pinia';
import { beforeEach, describe, expect, it, vi } from 'vitest';

vi.stubGlobal('ref', ref);
vi.stubGlobal('computed', computed);
vi.stubGlobal('watch', watch);
vi.stubGlobal('trackActivation', () => {});
vi.stubGlobal('useNuxtApp', () => ({ $api: vi.fn(() => Promise.resolve({})) }));

let unmount: (() => void) | null = null;
vi.stubGlobal('onMounted', (fn: () => void) => fn());
vi.stubGlobal('onUnmounted', (fn: () => void) => {
  unmount = fn;
});
vi.stubGlobal('document', { addEventListener: () => {}, removeEventListener: () => {}, visibilityState: 'visible' });
vi.stubGlobal('window', { addEventListener: () => {}, removeEventListener: () => {} });

const { useSrsStore } = await import('../app/stores/srsStore');
const { useSrsSessionCache } = await import('../app/composables/useSrsSessionCache');

describe('session cache save on unmount', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    unmount = null;
    vi.useFakeTimers();
  });

  it('flushes a pending debounced save when the page unmounts', async () => {
    const store = useSrsStore();
    const persist = vi.spyOn(store, 'persistSession');
    useSrsSessionCache();

    store.currentCardIndex = 1;
    await nextTick();
    expect(persist).not.toHaveBeenCalled();

    unmount!();
    expect(persist).toHaveBeenCalledTimes(1);

    vi.advanceTimersByTime(2000);
    expect(persist).toHaveBeenCalledTimes(1);
  });

  it('does not save on unmount when nothing was pending', () => {
    const store = useSrsStore();
    const persist = vi.spyOn(store, 'persistSession');
    useSrsSessionCache();

    unmount!();
    expect(persist).not.toHaveBeenCalled();
  });
});
