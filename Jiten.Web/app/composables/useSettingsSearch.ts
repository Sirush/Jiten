import type { ComputedRef, InjectionKey, Ref } from 'vue';

interface SearchScope {
  query: Ref<string>;
  /** True when the enclosing scope already matched, so everything inside it stays visible. */
  showAll: ComputedRef<boolean>;
  /** Children register whether their own text matches, independent of showAll. */
  register: (match: ComputedRef<boolean>) => () => void;
}

const SEARCH_SCOPE_KEY: InjectionKey<SearchScope> = Symbol('settingsSearchScope');

function normalise(text: string) {
  return text.normalize('NFKC').toLowerCase().replace(/[‘’“”]/g, '');
}

export function matchesSettingsSearch(query: string, texts: (string | null | undefined)[]): boolean {
  const terms = normalise(query).split(/\s+/).filter(Boolean);
  if (terms.length === 0) return true;
  const haystack = normalise(texts.filter(Boolean).join(' '));
  return terms.every((term) => haystack.includes(term));
}

function createScope(query: Ref<string>, showAll: ComputedRef<boolean>) {
  const children = shallowReactive(new Set<ComputedRef<boolean>>());
  const anyChildMatches = computed(() => [...children].some((m) => m.value));
  const scope: SearchScope = {
    query,
    showAll,
    register: (match) => {
      children.add(match);
      return () => children.delete(match);
    },
  };
  provide(SEARCH_SCOPE_KEY, scope);
  return anyChildMatches;
}

/** Root of a searchable settings page; returns whether anything on it matches. */
export function provideSettingsSearch(query: Ref<string>) {
  const anyChildMatches = createScope(query, computed(() => query.value.trim() === ''));
  return computed(() => query.value.trim() === '' || anyChildMatches.value);
}

/** A section: a title or keyword match shows all its settings, a description match only when no single setting matches. */
export function useSettingsSearchGroup(title: () => (string | null | undefined)[], description: () => string | null | undefined) {
  const parent = inject(SEARCH_SCOPE_KEY, null);
  if (!parent) return computed(() => true);
  const titleMatch = computed(() => matchesSettingsSearch(parent.query.value, title()));
  const descriptionMatch = computed(() => matchesSettingsSearch(parent.query.value, [description()]));
  const showAll: ComputedRef<boolean> = computed(
    () => parent.showAll.value || titleMatch.value || (descriptionMatch.value && !anyChildMatches.value)
  );
  const anyChildMatches = createScope(parent.query, showAll);
  const ownMatch = computed(() => titleMatch.value || descriptionMatch.value || anyChildMatches.value);
  const unregister = parent.register(ownMatch);
  onBeforeUnmount(unregister);
  return computed(() => parent.showAll.value || ownMatch.value);
}

/** A single searchable setting. */
export function useSettingsSearchItem(texts: () => (string | null | undefined)[]) {
  const parent = inject(SEARCH_SCOPE_KEY, null);
  if (!parent) return computed(() => true);
  const ownMatch = computed(() => matchesSettingsSearch(parent.query.value, texts()));
  const unregister = parent.register(ownMatch);
  onBeforeUnmount(unregister);
  return computed(() => parent.showAll.value || ownMatch.value);
}
