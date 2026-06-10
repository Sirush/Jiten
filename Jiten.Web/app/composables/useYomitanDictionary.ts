import JSZip from 'jszip';
import type { Definition } from '~/types';

export const JMDICT_DICTIONARY_ID = '__jmdict__';
const JMDICT_SETTINGS_KEY = 'jiten-jmdict-settings';
const META_DB_NAME = 'jiten-dictionaries';
const META_DB_VERSION = 2;

export interface DictionaryMeta {
  id: string;
  name: string;
  revision: string;
  entryCount: number;
  addedAt: number;
  priority: number;
  mode: 'always' | 'fallback' | 'never';
}

export interface DictionaryEntry {
  dictionaryId: string;
  word: string;
  reading: string;
  definitions: any[];
  partsOfSpeech: string;
  score: number;
}

export interface ImportProgress {
  phase: 'reading' | 'parsing' | 'storing';
  current: number;
  total: number;
}

export interface ResolvedDefinitionGroup {
  dictionaryId: string;
  dictionaryName: string;
  isJmDict: boolean;
  jmDictDefinitions?: Definition[];
  customDefinitions?: any[];
}

interface JmDictSettings {
  priority: number;
  mode: 'always' | 'fallback' | 'never';
}

// --- IndexedDB helpers ---

function idbRequest<T>(request: IDBRequest<T>): Promise<T> {
  return new Promise((resolve, reject) => {
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}

function idbTransaction(tx: IDBTransaction): Promise<void> {
  return new Promise((resolve, reject) => {
    tx.oncomplete = () => resolve();
    tx.onerror = () => reject(tx.error);
    tx.onabort = () => reject(tx.error);
  });
}

function dictDbName(id: string): string {
  return `jiten-dict-${id}`;
}

function openMetaDb(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open(META_DB_NAME, META_DB_VERSION);
    request.onupgradeneeded = (event) => {
      const db = (event.target as IDBOpenDBRequest).result;
      if (!db.objectStoreNames.contains('dictionaries')) {
        db.createObjectStore('dictionaries', { keyPath: 'id' });
      }
      if (db.objectStoreNames.contains('entries')) {
        db.deleteObjectStore('entries');
      }
    };
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}

function openDictDb(id: string): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open(dictDbName(id), 1);
    request.onupgradeneeded = (event) => {
      const db = (event.target as IDBOpenDBRequest).result;
      if (!db.objectStoreNames.contains('entries')) {
        const store = db.createObjectStore('entries', { autoIncrement: true });
        store.createIndex('word', 'word', { unique: false });
        store.createIndex('word-reading', ['word', 'reading'], { unique: false });
      }
    };
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}

// --- Import sanitisation ---

function rewriteQueryHref(href: string): string {
  const match = href.match(/^\?query=([^&]+)(?:&wildcards=\w+)?$/);
  if (match) return `/parse?text=${match[1]}`;
  return href;
}

function sanitiseDefinition(content: any): any {
  if (content === null || content === undefined) return content;
  if (typeof content === 'string') {
    return content
      .replace(/<img[^>]*\/?>/gi, '')
      .replace(/href="(\?query=[^"]+)"/gi, (_, href) => `href="${rewriteQueryHref(href)}"`);
  }
  if (typeof content === 'number') return content;
  if (Array.isArray(content)) return content.map(sanitiseDefinition).filter((c) => c !== null);
  if (typeof content !== 'object') return content;

  if (content.tag === 'img') return null;

  const result = { ...content };
  if (result.href) result.href = rewriteQueryHref(result.href);
  if (result.content !== undefined) {
    result.content = sanitiseDefinition(result.content);
  }
  return result;
}

// --- HTML rendering ---

function camelToKebab(str: string): string {
  return str.replace(/[A-Z]/g, (m) => '-' + m.toLowerCase());
}

function escapeHtml(str: string): string {
  return str
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

function normaliseHref(href: string): string {
  const raw = href.match(/^\?query=([^&]+)/);
  if (raw) return `/parse?text=${raw[1]}`;
  const rewritten = href.match(/^\/parse\?query=([^&]+)/);
  if (rewritten) return `/parse?text=${rewritten[1]}`;
  return href;
}

export function structuredContentToHtml(content: any): string {
  if (content === null || content === undefined) return '';
  if (typeof content === 'string') return escapeHtml(content);
  if (typeof content === 'number') return String(content);
  if (Array.isArray(content)) return content.map(structuredContentToHtml).join('');
  if (typeof content !== 'object') return '';

  if (content.type === 'structured-content') {
    return structuredContentToHtml(content.content);
  }

  if (content.tag) {
    const tag = content.tag as string;
    let attrs = '';

    if (content.style && typeof content.style === 'object') {
      const css = Object.entries(content.style)
        .map(([k, v]) => `${camelToKebab(k)}:${v}`)
        .join(';');
      if (css) attrs += ` style="${escapeHtml(css)}"`;
    }

    if (content.lang) attrs += ` lang="${escapeHtml(content.lang)}"`;
    if (content.href) attrs += ` href="${escapeHtml(normaliseHref(content.href))}"`;
    if (content.title) attrs += ` title="${escapeHtml(content.title)}"`;

    if (content.data && typeof content.data === 'object') {
      for (const [key, value] of Object.entries(content.data)) {
        if (value !== null && value !== undefined && value !== '') {
          attrs += ` data-${escapeHtml(key)}="${escapeHtml(String(value))}"`;
        }
      }
    }

    const selfClosing = ['br', 'hr', 'img'];
    if (selfClosing.includes(tag)) {
      if (tag === 'img' && content.path) {
        attrs += ` src="${escapeHtml(content.path)}"`;
        if (content.width) attrs += ` width="${content.width}"`;
        if (content.height) attrs += ` height="${content.height}"`;
      }
      return `<${tag}${attrs} />`;
    }

    const inner = content.content ? structuredContentToHtml(content.content) : '';
    return `<${tag}${attrs}>${inner}</${tag}>`;
  }

  return '';
}

export function definitionsToHtml(definitions: any[]): string {
  if (!definitions || definitions.length === 0) return '';

  const parts: string[] = [];
  for (const def of definitions) {
    if (typeof def === 'string') {
      parts.push(escapeHtml(def));
    } else if (typeof def === 'object') {
      parts.push(structuredContentToHtml(def));
    }
  }

  if (parts.length === 1) return parts[0];
  return '<ol>' + parts.map((p) => `<li>${p}</li>`).join('') + '</ol>';
}

export function structuredContentToText(content: any): string {
  if (content === null || content === undefined) return '';
  if (typeof content === 'string') return content;
  if (typeof content === 'number') return String(content);
  if (Array.isArray(content)) return content.map(structuredContentToText).join('');
  if (typeof content !== 'object') return '';

  if (content.type === 'structured-content') {
    return structuredContentToText(content.content);
  }

  if (content.tag) {
    const tag = content.tag as string;
    if (tag === 'br') return '\n';
    const inner = content.content ? structuredContentToText(content.content) : '';
    if (tag === 'li') return inner + '\n';
    if (tag === 'div' || tag === 'p') return inner + '\n';
    return inner;
  }

  return '';
}

export function definitionsToText(definitions: any[]): string {
  if (!definitions || definitions.length === 0) return '';

  const parts: string[] = [];
  for (const def of definitions) {
    if (typeof def === 'string') {
      parts.push(def);
    } else if (typeof def === 'object') {
      const text = structuredContentToText(def).trim();
      if (text) parts.push(text);
    }
  }

  return parts.join('; ');
}

// --- JMDict settings ---

function getJmDictSettings(): JmDictSettings {
  try {
    const raw = localStorage.getItem(JMDICT_SETTINGS_KEY);
    if (raw) {
      const parsed = JSON.parse(raw);
      return { priority: parsed.priority ?? 999, mode: parsed.mode ?? 'fallback' };
    }
  } catch {}
  return { priority: 999, mode: 'fallback' };
}

function saveJmDictSettings(settings: JmDictSettings): void {
  localStorage.setItem(JMDICT_SETTINGS_KEY, JSON.stringify(settings));
}

function buildJmDictMeta(settings: JmDictSettings): DictionaryMeta {
  return {
    id: JMDICT_DICTIONARY_ID,
    name: 'Default',
    revision: '',
    entryCount: 0,
    addedAt: 0,
    priority: settings.priority,
    mode: settings.mode,
  };
}

// --- Shared state ---

const dictionaries = ref<DictionaryMeta[]>([]);
let _metaDbPromise: Promise<IDBDatabase> | null = null;
// Shared across all component instances so a page mounting many entries only
// triggers a single IndexedDB read. Mutations refresh it via refreshDictionaries().
let _dictionariesLoadPromise: Promise<DictionaryMeta[]> | null = null;
const _dictDbCache = new Map<string, Promise<IDBDatabase>>();

function getMetaDb(): Promise<IDBDatabase> {
  if (!_metaDbPromise) _metaDbPromise = openMetaDb();
  return _metaDbPromise;
}

function getDictDb(id: string): Promise<IDBDatabase> {
  let cached = _dictDbCache.get(id);
  if (!cached) {
    cached = openDictDb(id);
    _dictDbCache.set(id, cached);
  }
  return cached;
}

// --- Composable ---

export function useYomitanDictionary() {
  // Re-reads dictionary metadata from IndexedDB, replacing the shared cache.
  function refreshDictionaries(): Promise<DictionaryMeta[]> {
    _dictionariesLoadPromise = (async () => {
      const db = await getMetaDb();
      const tx = db.transaction('dictionaries', 'readonly');
      const store = tx.objectStore('dictionaries');
      const customDicts: DictionaryMeta[] = await idbRequest(store.getAll());

      const jmDictSettings = getJmDictSettings();
      const jmDictMeta = buildJmDictMeta(jmDictSettings);

      const all = [...customDicts, jmDictMeta];
      all.sort((a, b) => a.priority - b.priority);
      dictionaries.value = all;
      return all;
    })();
    return _dictionariesLoadPromise;
  }

  // Ensure-loaded: concurrent and repeat callers share one IndexedDB read.
  function loadDictionaries(): Promise<DictionaryMeta[]> {
    return _dictionariesLoadPromise ?? refreshDictionaries();
  }

  async function importDictionary(
    file: File,
    onProgress?: (progress: ImportProgress) => void,
  ): Promise<DictionaryMeta> {
    onProgress?.({ phase: 'reading', current: 0, total: 1 });

    const zip = await JSZip.loadAsync(file);
    const indexFile = zip.file('index.json');
    if (!indexFile) throw new Error('Invalid Yomitan dictionary: missing index.json');

    const indexJson = JSON.parse(await indexFile.async('string'));

    const termBankNames = Object.keys(zip.files)
      .filter((n) => n.match(/^term_bank_\d+\.json$/))
      .sort();

    if (termBankNames.length === 0) throw new Error('No term_bank files found in dictionary');

    const dictId = crypto.randomUUID();
    const metaDb = await getMetaDb();

    const existingDicts = await idbRequest(
      metaDb.transaction('dictionaries', 'readonly').objectStore('dictionaries').getAll(),
    );

    const meta: DictionaryMeta = {
      id: dictId,
      name: indexJson.title || file.name,
      revision: indexJson.revision || '',
      entryCount: 0,
      addedAt: Date.now(),
      priority: existingDicts.length,
      mode: 'always',
    };

    const dictDb = await getDictDb(dictId);
    let totalEntries = 0;

    for (let i = 0; i < termBankNames.length; i++) {
      onProgress?.({ phase: 'parsing', current: i, total: termBankNames.length });

      const bankFile = zip.file(termBankNames[i]);
      if (!bankFile) continue;

      const terms: any[][] = JSON.parse(await bankFile.async('string'));
      const entries: DictionaryEntry[] = [];

      for (const term of terms) {
        const rawDefs = Array.isArray(term[5]) ? term[5] : [term[5]];
        const defs = rawDefs.map(sanitiseDefinition).filter((d: any) => d !== null);

        entries.push({
          dictionaryId: dictId,
          word: term[0],
          reading: term[1],
          definitions: defs,
          partsOfSpeech: term[2] || '',
          score: typeof term[4] === 'number' ? term[4] : 0,
        });
      }

      onProgress?.({ phase: 'storing', current: i, total: termBankNames.length });

      const tx = dictDb.transaction('entries', 'readwrite');
      const store = tx.objectStore('entries');
      for (const entry of entries) {
        store.put(entry);
      }
      await idbTransaction(tx);

      totalEntries += entries.length;
    }

    meta.entryCount = totalEntries;
    const metaTx = metaDb.transaction('dictionaries', 'readwrite');
    metaTx.objectStore('dictionaries').put(meta);
    await idbTransaction(metaTx);

    await refreshDictionaries();
    return meta;
  }

  async function removeDictionary(id: string): Promise<void> {
    if (id === JMDICT_DICTIONARY_ID) return;

    const metaDb = await getMetaDb();
    const metaTx = metaDb.transaction('dictionaries', 'readwrite');
    metaTx.objectStore('dictionaries').delete(id);
    await idbTransaction(metaTx);

    const cachedDb = await _dictDbCache.get(id);
    if (cachedDb) {
      cachedDb.close();
      _dictDbCache.delete(id);
    }
    indexedDB.deleteDatabase(dictDbName(id));

    await refreshDictionaries();
  }

  async function updateDictionary(id: string, updates: Partial<Pick<DictionaryMeta, 'priority' | 'mode' | 'name'>>): Promise<void> {
    if (id === JMDICT_DICTIONARY_ID) {
      const settings = getJmDictSettings();
      if (updates.priority !== undefined) settings.priority = updates.priority;
      if (updates.mode !== undefined) settings.mode = updates.mode;
      saveJmDictSettings(settings);
      await refreshDictionaries();
      return;
    }

    const db = await getMetaDb();
    const tx = db.transaction('dictionaries', 'readwrite');
    const store = tx.objectStore('dictionaries');
    const existing = await idbRequest(store.get(id));
    if (!existing) return;
    Object.assign(existing, updates);
    store.put(existing);
    await idbTransaction(tx);
    await refreshDictionaries();
  }

  async function swapPriority(idA: string, idB: string): Promise<void> {
    const dictA = dictionaries.value.find((d) => d.id === idA);
    const dictB = dictionaries.value.find((d) => d.id === idB);
    if (!dictA || !dictB) return;

    const tmpPriority = dictA.priority;
    const newPriorityA = dictB.priority;
    const newPriorityB = tmpPriority;

    await updateDictionary(idA, { priority: newPriorityA });
    await updateDictionary(idB, { priority: newPriorityB });
  }

  async function reorderDictionaries(orderedIds: string[]): Promise<void> {
    const db = await getMetaDb();
    const tx = db.transaction('dictionaries', 'readwrite');
    const store = tx.objectStore('dictionaries');

    for (let i = 0; i < orderedIds.length; i++) {
      const id = orderedIds[i];
      if (id === JMDICT_DICTIONARY_ID) {
        const settings = getJmDictSettings();
        settings.priority = i;
        saveJmDictSettings(settings);
      } else {
        const existing = await idbRequest(store.get(id));
        if (existing) {
          existing.priority = i;
          store.put(existing);
        }
      }
    }

    await idbTransaction(tx);
    await refreshDictionaries();
  }

  async function lookupWord(
    word: string,
    reading?: string,
  ): Promise<{ entry: DictionaryEntry; dictionary: DictionaryMeta }[]> {
    const dicts = dictionaries.value.length > 0
      ? dictionaries.value
      : await loadDictionaries();

    const customDicts = dicts.filter((d) => d.id !== JMDICT_DICTIONARY_ID);
    if (customDicts.length === 0) return [];

    const results: { entry: DictionaryEntry; dictionary: DictionaryMeta }[] = [];

    for (const dict of customDicts) {
      const dictDb = await getDictDb(dict.id);
      const tx = dictDb.transaction('entries', 'readonly');
      const entryStore = tx.objectStore('entries');

      let entries: DictionaryEntry[];
      if (reading) {
        const wordReadingEntries = await idbRequest(
          entryStore.index('word-reading').getAll(IDBKeyRange.only([word, reading])),
        );
        if (wordReadingEntries.length > 0) {
          entries = wordReadingEntries;
        } else {
          entries = await idbRequest(entryStore.index('word').getAll(IDBKeyRange.only(word)));
        }
      } else {
        entries = await idbRequest(entryStore.index('word').getAll(IDBKeyRange.only(word)));
      }

      for (const entry of entries) {
        results.push({ entry, dictionary: dict });
      }
    }

    results.sort((a, b) => a.dictionary.priority - b.dictionary.priority);
    return results;
  }

  async function resolveDefinitions(
    word: string,
    reading: string | undefined,
    jmDictDefinitions: Definition[] | undefined,
  ): Promise<ResolvedDefinitionGroup[]> {
    const dicts = dictionaries.value.length > 0
      ? dictionaries.value
      : await loadDictionaries();

    const customResults = await lookupWord(word, reading);
    const customByDict = new Map<string, DictionaryEntry[]>();
    for (const r of customResults) {
      const existing = customByDict.get(r.dictionary.id) || [];
      existing.push(r.entry);
      customByDict.set(r.dictionary.id, existing);
    }

    const alwaysGroups: ResolvedDefinitionGroup[] = [];
    const fallbackGroups: ResolvedDefinitionGroup[] = [];

    for (const dict of dicts) {
      if (dict.mode === 'never') continue;

      const isJmDict = dict.id === JMDICT_DICTIONARY_ID;
      let hasMatch = false;
      let group: ResolvedDefinitionGroup | null = null;

      if (isJmDict) {
        hasMatch = !!jmDictDefinitions && jmDictDefinitions.length > 0;
        if (hasMatch) {
          group = {
            dictionaryId: dict.id,
            dictionaryName: dict.name,
            isJmDict: true,
            jmDictDefinitions: jmDictDefinitions!,
          };
        }
      } else {
        const entries = customByDict.get(dict.id);
        hasMatch = !!entries && entries.length > 0;
        if (hasMatch) {
          group = {
            dictionaryId: dict.id,
            dictionaryName: dict.name,
            isJmDict: false,
            customDefinitions: entries![0].definitions,
          };
        }
      }

      if (group) {
        if (dict.mode === 'always') alwaysGroups.push(group);
        else fallbackGroups.push(group);
      }
    }

    return alwaysGroups.length > 0 ? alwaysGroups : fallbackGroups;
  }

  const hasCustomDictionaries = computed(() => dictionaries.value.some((d) => d.id !== JMDICT_DICTIONARY_ID));

  return {
    dictionaries: readonly(dictionaries),
    hasCustomDictionaries,
    loadDictionaries,
    importDictionary,
    removeDictionary,
    updateDictionary,
    swapPriority,
    reorderDictionaries,
    lookupWord,
    resolveDefinitions,
    definitionsToHtml,
    structuredContentToHtml,
  };
}
