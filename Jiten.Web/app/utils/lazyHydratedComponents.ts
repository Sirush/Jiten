import { defineAsyncComponent, hydrateOnVisible, type Component } from 'vue';
import MediaDeckCard from '~/components/MediaDeckCard.vue';
import MediaDeckCompactView from '~/components/MediaDeckCompactView.vue';
import MediaDeckTableView from '~/components/MediaDeckTableView.vue';
import VocabularyEntry from '~/components/VocabularyEntry.vue';

/**
 * Hydrate-on-visible wrappers for list items, defined once at module level.
 *
 * Nuxt's `<LazyX hydrate-on-visible>` creates its async wrapper inside setup(),
 * so every item mounted during client-side navigation starts unresolved and
 * renders an empty placeholder for a frame — a 100-item list collapses to
 * nothing on each page turn. A module-level defineAsyncComponent resolves once
 * and mounts synchronously ever after, while the hydrate strategy still defers
 * per-item hydration of the server-rendered HTML until scrolled into view.
 *
 * These must be imported explicitly (template tags only auto-resolve from
 * components/, not utils/).
 */
function lazyHydrate<T extends Component>(component: T) {
  return defineAsyncComponent({
    loader: () => Promise.resolve(component),
    hydrate: hydrateOnVisible(),
  });
}

export const LazyHydrateMediaDeckCard = lazyHydrate(MediaDeckCard);
export const LazyHydrateMediaDeckCompactView = lazyHydrate(MediaDeckCompactView);
export const LazyHydrateMediaDeckTableView = lazyHydrate(MediaDeckTableView);
export const LazyHydrateVocabularyEntry = lazyHydrate(VocabularyEntry);
