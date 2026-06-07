import type { Definition } from '~/types';
import type { ResolvedDefinitionGroup } from './useYomitanDictionary';
import { JMDICT_DICTIONARY_ID } from './useYomitanDictionary';

// Ensures the (shared, singleton) dictionary metadata is only loaded once even
// when dozens of entries mount at the same time, instead of each one kicking off
// its own IndexedDB read.
let _initialLoadStarted = false;

function parseFurigana(furiganaText: string): { expression: string; reading: string } {
  if (!furiganaText) return { expression: '', reading: '' };

  const expression = furiganaText.replace(/\[.*?\]/g, '');
  const reading = furiganaText.replace(/([^\[\]]+)\[([^\]]+)\]/g, '$2').replace(/\[|\]/g, '');

  return { expression, reading };
}

export function useDictionaryDefinitions(
  furiganaText: Ref<string | undefined> | ComputedRef<string | undefined>,
  jmDictDefinitions: Ref<Definition[] | undefined> | ComputedRef<Definition[] | undefined>,
) {
  const { resolveDefinitions, hasCustomDictionaries, loadDictionaries, dictionaries } = useYomitanDictionary();

  // Holds the result of the (expensive) custom-dictionary lookup; only populated
  // when the user actually has custom dictionaries installed.
  const customResolved = ref<ResolvedDefinitionGroup[]>([]);

  // Default (no custom dictionaries) is the overwhelmingly common case. Resolve
  // it synchronously here so a list of entries doesn't pay an async watcher +
  // IndexedDB round-trip each.
  const resolvedGroups = computed<ResolvedDefinitionGroup[]>(() => {
    const text = unref(furiganaText);
    if (!text) return [];

    if (!hasCustomDictionaries.value) {
      const defs = unref(jmDictDefinitions);
      if (defs && defs.length > 0) {
        return [{
          dictionaryId: JMDICT_DICTIONARY_ID,
          dictionaryName: 'Default',
          isJmDict: true,
          jmDictDefinitions: defs,
        }];
      }
      return [];
    }

    return customResolved.value;
  });

  if (import.meta.client) {
    // Trigger the one-time metadata load so hasCustomDictionaries becomes accurate.
    if (!_initialLoadStarted && dictionaries.value.length === 0) {
      _initialLoadStarted = true;
      loadDictionaries();
    }

    const resolve = async () => {
      // Default path is handled by the computed above — nothing async to do.
      if (!hasCustomDictionaries.value) {
        customResolved.value = [];
        return;
      }

      const text = unref(furiganaText);
      const defs = unref(jmDictDefinitions);
      if (!text) {
        customResolved.value = [];
        return;
      }

      const { expression, reading } = parseFurigana(text);
      customResolved.value = await resolveDefinitions(expression, reading, defs);
    };

    watch([furiganaText, jmDictDefinitions, hasCustomDictionaries], resolve, { immediate: true });
  }

  return { resolvedGroups: resolvedGroups as Readonly<Ref<ResolvedDefinitionGroup[]>> };
}
