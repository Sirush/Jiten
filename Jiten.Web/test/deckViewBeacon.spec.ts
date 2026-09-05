import { ref, watch } from 'vue';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { DECK_VIEW_DWELL_MS, useDeckViewBeacon } from '../app/composables/useDeckViewBeacon';

type Hook = () => void;
let mounted: Hook[] = [];
let unmounted: Hook[] = [];

vi.stubGlobal('watch', watch);
vi.stubGlobal('onMounted', (fn: Hook) => mounted.push(fn));
vi.stubGlobal('onBeforeUnmount', (fn: Hook) => unmounted.push(fn));

const $api = vi.fn(() => Promise.resolve());
vi.stubGlobal('useNuxtApp', () => ({ $api }));

describe('useDeckViewBeacon', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    mounted = [];
    unmounted = [];
    $api.mockClear();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('fires once after the dwell delay with only the deck id', () => {
    useDeckViewBeacon(ref('42'));
    mounted.forEach((fn) => fn());

    vi.advanceTimersByTime(DECK_VIEW_DWELL_MS - 1);
    expect($api).not.toHaveBeenCalled();

    vi.advanceTimersByTime(1);
    expect($api).toHaveBeenCalledTimes(1);
    expect($api).toHaveBeenCalledWith('media-deck/42/view', { method: 'POST' });

    vi.advanceTimersByTime(DECK_VIEW_DWELL_MS * 5);
    expect($api).toHaveBeenCalledTimes(1);
  });

  it('sends nothing when the page is left before the delay', () => {
    useDeckViewBeacon(ref('42'));
    mounted.forEach((fn) => fn());

    vi.advanceTimersByTime(DECK_VIEW_DWELL_MS / 2);
    unmounted.forEach((fn) => fn());
    vi.advanceTimersByTime(DECK_VIEW_DWELL_MS);

    expect($api).not.toHaveBeenCalled();
  });

  it('restarts the clock when the route moves to another deck', async () => {
    const id = ref('1');
    useDeckViewBeacon(id);
    mounted.forEach((fn) => fn());

    vi.advanceTimersByTime(DECK_VIEW_DWELL_MS / 2);
    id.value = '2';
    await Promise.resolve();

    vi.advanceTimersByTime(DECK_VIEW_DWELL_MS / 2);
    expect($api).not.toHaveBeenCalled();

    vi.advanceTimersByTime(DECK_VIEW_DWELL_MS / 2);
    expect($api).toHaveBeenCalledTimes(1);
    expect($api).toHaveBeenCalledWith('media-deck/2/view', { method: 'POST' });
  });
});
