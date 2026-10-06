import { effectScope, getCurrentScope, onScopeDispose, ref } from 'vue';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useDeckSaveQueue } from '../app/composables/useSaveQueue';

vi.stubGlobal('ref', ref);
vi.stubGlobal('getCurrentScope', getCurrentScope);
vi.stubGlobal('onScopeDispose', onScopeDispose);

function deferred() {
  let resolve!: () => void;
  const promise = new Promise<void>((r) => (resolve = r));
  return { promise, resolve };
}

const summary = (completedCount: number) => ({
  entryId: 1,
  state: null,
  startedOn: null,
  finishedOn: null,
  charactersRead: null,
  completedCount,
  entryCount: 1,
});

describe('useDeckSaveQueue', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it("runs a title's saves in order across instances, each seeing the reply before it", async () => {
    const step = useDeckSaveQueue(7);
    const dialog = useDeckSaveQueue(7);
    const first = deferred();
    const order: string[] = [];

    const a = step.enqueue(async () => {
      order.push('step');
      await first.promise;
      return summary(1);
    });
    const b = dialog.enqueue(async () => {
      order.push(`dialog sees ${dialog.latest()?.completedCount}`);
      return summary(2);
    });

    await Promise.resolve();
    expect(order).toEqual(['step']);

    first.resolve();
    await Promise.all([a, b]);

    expect(order).toEqual(['step', 'dialog sees 1']);
    expect(step.latest()).toBeNull();
  });

  it('shows saving, then saved, and never claims saved when a save in the batch failed', async () => {
    const { state, enqueue } = useDeckSaveQueue(8);
    const save = deferred();

    const run = enqueue(async () => {
      await save.promise;
      return null;
    });
    expect(state.value).toBe('saving');
    save.resolve();
    await run;
    expect(state.value).toBe('saved');
    vi.advanceTimersByTime(2500);
    expect(state.value).toBe('idle');

    await Promise.all([enqueue(async () => false), enqueue(async () => null)]);
    expect(state.value).toBe('idle');
  });

  it('keeps going after a save throws', async () => {
    const { enqueue } = useDeckSaveQueue(9);

    const failed = enqueue(async () => {
      throw new Error('network');
    });
    const next = enqueue(async () => null);

    await expect(failed).resolves.toBe(false);
    await expect(next).resolves.toBe(true);
  });

  it('keeps titles apart and drops a queue once it is idle and unused', async () => {
    expect(useDeckSaveQueue(1).state).not.toBe(useDeckSaveQueue(2).state);

    const scoped = effectScope();
    const own = scoped.run(() => useDeckSaveQueue(4))!;
    const save = deferred();
    const run = own.enqueue(async () => {
      await save.promise;
      return null;
    });
    scoped.stop();

    const probe = effectScope();
    expect(probe.run(() => useDeckSaveQueue(4))!.state).toBe(own.state);
    probe.stop();

    save.resolve();
    await run;
    expect(effectScope().run(() => useDeckSaveQueue(4))!.state).not.toBe(own.state);
  });
});
