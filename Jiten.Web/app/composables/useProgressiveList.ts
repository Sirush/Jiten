import type { Ref, ComputedRef } from 'vue';

interface ProgressiveListOptions<T> {
  // Items rendered in the first frame after the list changes.
  initial?: number;
  // Items added per subsequent animation frame until the whole list is rendered.
  batch?: number;
  // Stable key for an item, used to tell a real list change (navigation, filter)
  // apart from a refetch that returns the same content (e.g. revalidateOnClient).
  // Without it, navigating between equal-length pages can't be detected.
  keyOf?: (item: T) => string | number;
}

/**
 * Spreads the mounting of a large list across several animation frames instead
 * of one blocking flush. The whole list still ends up in the DOM (so Ctrl+F and
 * crawlers keep working) — only the *timing* of the mount is sliced.
 *
 * SSR and the initial client hydration render the full list (count starts at
 * Infinity), so there is no hydration mismatch and the first paint is unchanged.
 * Chunking only kicks in for client-side list changes after mount.
 */
export function useProgressiveList<T>(
  source: Ref<T[] | null | undefined> | ComputedRef<T[] | null | undefined>,
  options: ProgressiveListOptions<T> = {},
) {
  const initial = options.initial ?? 12;
  const batch = options.batch ?? 8;
  const keyOf = options.keyOf;

  const items = computed(() => source.value ?? []);
  // Infinity => render everything (SSR + hydrated first render).
  const count = ref(Number.POSITIVE_INFINITY);

  const visibleItems = computed(() => {
    const arr = items.value;
    return count.value >= arr.length ? arr : arr.slice(0, count.value);
  });

  const signature = (arr: T[]): string => {
    if (arr.length === 0) return '0';
    if (!keyOf) return String(arr.length);
    return `${arr.length}:${keyOf(arr[0]!)}:${keyOf(arr[arr.length - 1]!)}`;
  };

  if (import.meta.client) {
    let raf: number | null = null;
    let lastSignature = '';

    const cancel = () => {
      if (raf != null) {
        cancelAnimationFrame(raf);
        raf = null;
      }
    };

    const pump = () => {
      raf = null;
      if (count.value >= items.value.length) return;
      count.value += batch;
      schedule();
    };

    const schedule = () => {
      if (count.value >= items.value.length) return;
      raf = requestAnimationFrame(pump);
    };

    onMounted(() => {
      // Seed from the hydrated/initial list so an immediate refetch returning the
      // same content is treated as a no-op rather than re-chunking on screen.
      lastSignature = signature(items.value);

      watch(items, (arr) => {
        const sig = signature(arr);
        if (sig === lastSignature) return;
        lastSignature = sig;

        cancel();
        if (arr.length <= initial) {
          count.value = arr.length;
          return;
        }
        count.value = initial;
        schedule();
      });
    });

    onBeforeUnmount(cancel);
  }

  return { visibleItems };
}
