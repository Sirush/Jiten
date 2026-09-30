import { defineStore } from 'pinia';
import { useJitenStore } from '~/stores/jitenStore';
import { useDisplayStyleStore } from '~/stores/displayStyleStore';
import {
  DEFAULT_DISPLAY_VALUES,
  DISPLAY_SECTION_KEYS,
  MAX_DISPLAY_PROFILES,
  localSettingsWorthKeeping,
  newDisplayProfileId,
  pickActiveProfile,
  sanitiseDisplayValues,
  trimProfileName,
  type DisplayProfile,
  type DisplaySectionId,
  type DisplayValues,
} from '~/utils/displayProfile';
import { isDefaultMediaCardStatColumns, sanitiseMediaCardStatColumns, type MediaCardStatColumns } from '~/utils/mediaCardStats';
import { copySectionLayout } from '~/utils/mediaCardSections';
import { onSettingChangedInOtherTab, readJarCookie, registerSettingResync } from '~/utils/settingsTabSync';

const ENDPOINT = 'user/settings/display-profiles';
const SAVE_DEBOUNCE_MS = 800;
const REFRESH_AFTER_MS = 60_000;

interface ProfileDto {
  id: string;
  name: string;
  values: Record<string, unknown>;
  statColumns: string[][] | null;
  updatedAt: number;
}

function fromDto(dto: ProfileDto): DisplayProfile {
  return { id: dto.id, name: dto.name, values: sanitiseDisplayValues(dto.values), statColumns: sanitiseMediaCardStatColumns(dto.statColumns), updatedAt: dto.updatedAt };
}

export interface LocalSettingsOffer {
  values: DisplayValues;
  statColumns: MediaCardStatColumns | null;
}

export const useDisplayProfileStore = defineStore('displayProfile', () => {
  const jiten = useJitenStore();
  const displayStyle = useDisplayStyleStore();
  const { $api } = useNuxtApp();

  // Per device: two browsers on one account can sit on different profiles.
  const activeIdCookie = useCookie<string | null>('jiten-display-profile', { maxAge: 60 * 60 * 24 * 365, path: '/', watch: true });

  const profiles = ref<DisplayProfile[]>([]);
  const status = ref<'idle' | 'loading' | 'ready' | 'error'>('idle');
  const saveState = ref<'saved' | 'saving' | 'error'>('saved');
  const localOffer = ref<LocalSettingsOffer | null>(null);
  // Session-scoped, so the offer made at sign-in survives a reload but not a closed tab.
  const OFFER_KEY = 'jiten-display-local-offer';
  const saveOffer = (offer: LocalSettingsOffer | null) => {
    localOffer.value = offer;
    try {
      if (offer) sessionStorage.setItem(OFFER_KEY, JSON.stringify(offer));
      else sessionStorage.removeItem(OFFER_KEY);
    } catch {
      // Storage can be unavailable in private modes; the offer then lasts until the next reload.
    }
  };
  const restoreOffer = () => {
    try {
      const stored = JSON.parse(sessionStorage.getItem(OFFER_KEY) ?? 'null');
      if (stored?.values) localOffer.value = { values: stored.values, statColumns: sanitiseMediaCardStatColumns(stored.statColumns) };
    } catch {
      localOffer.value = null;
    }
  };

  const activeId = computed(() => activeIdCookie.value ?? null);
  const activeProfile = computed(() => profiles.value.find((p) => p.id === activeId.value) ?? null);
  const canCreate = computed(() => profiles.value.length < MAX_DISPLAY_PROFILES);

  let lastSyncedKey = '';
  let lastFetchAt = 0;
  let saveTimer: ReturnType<typeof setTimeout> | null = null;
  let stopWatch: (() => void) | null = null;

  function readValues(): DisplayValues {
    return {
      titleLanguage: jiten.titleLanguage,
      themeMode: jiten.themeMode,
      headwordFurigana: jiten.headwordFurigana,
      sentenceFurigana: jiten.sentenceFurigana,
      headwordSize: jiten.headwordSize,
      sentenceSize: jiten.sentenceSize,
      japaneseFont: jiten.japaneseFont,
      japaneseCustomFont: jiten.japaneseCustomFont,
      japaneseFontWordsOnly: jiten.japaneseFontWordsOnly,
      japaneseFontDictionaries: jiten.japaneseFontDictionaries,
      furiganaSize: jiten.furiganaSize,
      furiganaOnHover: jiten.furiganaOnHover,
      pitchAccentDisplay: jiten.pitchAccentDisplay,
      pitchAccentColours: jiten.pitchAccentColours,
      colourWordsByState: jiten.colourWordsByState,
      stateColours: { ...jiten.resolvedStateColours },
      reducedMotion: jiten.reducedMotion,
      readingSpeed: jiten.readingSpeed,
      readingSpeedByDifficulty: jiten.readingSpeedByDifficulty,
      readingSpeeds: [...jiten.readingSpeeds],
      displayAllNsfw: jiten.displayAllNsfw,
      hideVocabularyDefinitions: jiten.hideVocabularyDefinitions,
      hideCoverageBorders: jiten.hideCoverageBorders,
      hideGenres: jiten.hideGenres,
      hideTags: jiten.hideTags,
      hideRelations: jiten.hideRelations,
      hideDescriptions: jiten.hideDescriptions,
      hideExternalRating: jiten.hideExternalRating,
      hideAlternativeTitles: jiten.hideAlternativeTitles,
      mediaCardSectionLayout: copySectionLayout(jiten.mediaCardSectionLayout),
      quickMasterVocabulary: jiten.quickMasterVocabulary,
      ttsVoice: jiten.ttsVoice,
      difficultyDisplayStyle: jiten.difficultyDisplayStyle,
      difficultyValueDisplayStyle: jiten.difficultyValueDisplayStyle,
      difficultyPalette: jiten.difficultyPalette,
      kanjiScale: jiten.kanjiScale,
      listView: displayStyle.displayStyle,
    };
  }

  function readStatColumns(): MediaCardStatColumns | null {
    return jiten.mediaCardStatColumns ? jiten.mediaCardStatColumns.map((column) => [...column]) : null;
  }

  /** Keys a profile lacks fall back to the defaults, so every device on the same profile renders the same. */
  function applyValues(values: Partial<DisplayValues>, statColumns: MediaCardStatColumns | null) {
    const v = { ...DEFAULT_DISPLAY_VALUES, ...values };
    jiten.titleLanguage = v.titleLanguage;
    jiten.themeMode = v.themeMode;
    jiten.headwordFurigana = v.headwordFurigana;
    jiten.sentenceFurigana = v.sentenceFurigana;
    jiten.headwordSize = v.headwordSize;
    jiten.sentenceSize = v.sentenceSize;
    jiten.japaneseFont = v.japaneseFont;
    jiten.japaneseCustomFont = v.japaneseCustomFont;
    jiten.japaneseFontWordsOnly = v.japaneseFontWordsOnly;
    jiten.japaneseFontDictionaries = v.japaneseFontDictionaries;
    jiten.furiganaSize = v.furiganaSize;
    jiten.furiganaOnHover = v.furiganaOnHover;
    jiten.pitchAccentDisplay = v.pitchAccentDisplay;
    jiten.pitchAccentColours = v.pitchAccentColours;
    jiten.colourWordsByState = v.colourWordsByState;
    jiten.stateColours = { ...v.stateColours };
    jiten.reducedMotion = v.reducedMotion;
    jiten.readingSpeed = v.readingSpeed;
    jiten.readingSpeedByDifficulty = v.readingSpeedByDifficulty;
    jiten.readingSpeeds = [...v.readingSpeeds];
    jiten.displayAllNsfw = v.displayAllNsfw;
    jiten.hideVocabularyDefinitions = v.hideVocabularyDefinitions;
    jiten.hideCoverageBorders = v.hideCoverageBorders;
    jiten.hideGenres = v.hideGenres;
    jiten.hideTags = v.hideTags;
    jiten.hideRelations = v.hideRelations;
    jiten.hideDescriptions = v.hideDescriptions;
    jiten.hideExternalRating = v.hideExternalRating;
    jiten.hideAlternativeTitles = v.hideAlternativeTitles;
    jiten.mediaCardSectionLayout = copySectionLayout(v.mediaCardSectionLayout);
    jiten.quickMasterVocabulary = v.quickMasterVocabulary;
    jiten.ttsVoice = v.ttsVoice;
    jiten.difficultyDisplayStyle = v.difficultyDisplayStyle;
    jiten.difficultyValueDisplayStyle = v.difficultyValueDisplayStyle;
    jiten.difficultyPalette = v.difficultyPalette;
    jiten.kanjiScale = v.kanjiScale;
    displayStyle.displayStyle = v.listView;
    jiten.mediaCardStatColumns = statColumns ? statColumns.map((column) => [...column]) : null;
  }

  const snapshotKey = () => JSON.stringify([readValues(), readStatColumns()]);

  function applyProfile(profile: DisplayProfile) {
    applyValues(profile.values, profile.statColumns);
    lastSyncedKey = snapshotKey();
  }

  async function putProfile(id: string, name: string, values: Partial<DisplayValues>, statColumns: MediaCardStatColumns | null): Promise<DisplayProfile> {
    const saved = fromDto(await $api<ProfileDto>(`${ENDPOINT}/${id}`, { method: 'PUT', body: { name, values, statColumns } }));
    const index = profiles.value.findIndex((p) => p.id === id);
    if (index < 0) profiles.value = [...profiles.value, saved];
    else profiles.value = profiles.value.map((p) => (p.id === id ? saved : p));
    return saved;
  }

  async function saveActiveNow() {
    const profile = activeProfile.value;
    if (!profile) return;
    const key = snapshotKey();
    if (key === lastSyncedKey) return;

    saveState.value = 'saving';
    try {
      await putProfile(profile.id, profile.name, readValues(), readStatColumns());
      lastSyncedKey = key;
      saveState.value = 'saved';
    } catch (error) {
      console.error('Failed to save display profile', error);
      saveState.value = 'error';
    }
  }

  function scheduleSave() {
    if (saveTimer) clearTimeout(saveTimer);
    saveTimer = setTimeout(() => {
      saveTimer = null;
      saveActiveNow();
    }, SAVE_DEBOUNCE_MS);
  }

  function startWatching() {
    stopWatch?.();
    stopWatch = watch(
      snapshotKey,
      (key) => {
        if (key !== lastSyncedKey) scheduleSave();
      },
      { flush: 'post' }
    );
  }

  async function init() {
    if (status.value === 'loading') return;
    status.value = 'loading';
    // A browser with no active-profile cookie has never synced, so its local settings may be worth keeping.
    const firstSyncOnThisBrowser = !activeIdCookie.value;

    try {
      const response = await $api<{ profiles: ProfileDto[] }>(ENDPOINT);
      profiles.value = response.profiles.map(fromDto);
      lastFetchAt = Date.now();

      if (profiles.value.length === 0) {
        const created = await putProfile(newDisplayProfileId(), 'Default', readValues(), readStatColumns());
        activeIdCookie.value = created.id;
        lastSyncedKey = snapshotKey();
      } else {
        const active = pickActiveProfile(profiles.value, activeIdCookie.value)!;
        activeIdCookie.value = active.id;
        const local = { values: readValues(), statColumns: readStatColumns() };
        if (firstSyncOnThisBrowser && localSettingsWorthKeeping(local, active)) saveOffer(local);
        else if (!firstSyncOnThisBrowser) restoreOffer();
        applyProfile(active);
      }

      status.value = 'ready';
      startWatching();
    } catch (error) {
      console.error('Failed to load display profiles', error);
      status.value = 'error';
    }
  }

  /** Picks up edits made on another device, unless this one has an edit still waiting to save. */
  async function refresh(force = false) {
    if (status.value !== 'ready' || saveTimer || (!force && Date.now() - lastFetchAt < REFRESH_AFTER_MS)) return;
    try {
      const response = await $api<{ profiles: ProfileDto[] }>(ENDPOINT);
      lastFetchAt = Date.now();
      const previous = activeProfile.value;
      profiles.value = response.profiles.map(fromDto);
      const active = pickActiveProfile(profiles.value, activeIdCookie.value);
      if (!active) return;
      if (active.id !== previous?.id || active.updatedAt !== previous?.updatedAt) {
        activeIdCookie.value = active.id;
        applyProfile(active);
      }
    } catch (error) {
      console.error('Failed to refresh display profiles', error);
    }
  }

  if (import.meta.client) {
    // The tab that made the change saves it; saving it again here would only repeat the same PUT.
    onSettingChangedInOtherTab(() => {
      if (status.value === 'ready' && !saveTimer) lastSyncedKey = snapshotKey();
    });
    registerSettingResync(() => {
      const jar = readJarCookie('jiten-display-profile');
      if (typeof jar === 'string' && jar !== activeIdCookie.value) activeIdCookie.value = jar;
    });
    // Another tab switched to a profile created after this tab last fetched the list.
    watch(activeId, (id) => {
      if (id && status.value === 'ready' && !profiles.value.some((p) => p.id === id)) refresh(true);
    });
  }

  function reset() {
    stopWatch?.();
    stopWatch = null;
    if (saveTimer) clearTimeout(saveTimer);
    saveTimer = null;
    profiles.value = [];
    saveOffer(null);
    status.value = 'idle';
    saveState.value = 'saved';
    activeIdCookie.value = null;
  }

  async function switchTo(id: string) {
    const target = profiles.value.find((p) => p.id === id);
    if (!target || target.id === activeId.value) return;
    if (saveTimer) {
      clearTimeout(saveTimer);
      saveTimer = null;
      await saveActiveNow();
    }
    activeIdCookie.value = target.id;
    applyProfile(target);
  }

  async function create(name: string, values: Partial<DisplayValues>, statColumns: MediaCardStatColumns | null, activate = true) {
    const created = await putProfile(newDisplayProfileId(), trimProfileName(name) || 'Profile', values, statColumns);
    if (activate) await switchTo(created.id);
    return created;
  }

  async function rename(id: string, name: string) {
    const profile = profiles.value.find((p) => p.id === id);
    const trimmed = trimProfileName(name);
    if (!profile || !trimmed || trimmed === profile.name) return;
    const isActive = id === activeId.value;
    await putProfile(id, trimmed, isActive ? readValues() : profile.values, isActive ? readStatColumns() : profile.statColumns);
    if (isActive) lastSyncedKey = snapshotKey();
  }

  async function remove(id: string) {
    await $api(`${ENDPOINT}/${id}`, { method: 'DELETE' });
    profiles.value = profiles.value.filter((p) => p.id !== id);
    if (id === activeId.value && profiles.value[0]) {
      activeIdCookie.value = profiles.value[0].id;
      applyProfile(profiles.value[0]);
    }
  }

  async function acceptLocalOffer(name: string) {
    const offer = localOffer.value;
    if (!offer) return;
    await create(name, offer.values, offer.statColumns);
    saveOffer(null);
  }

  function dismissLocalOffer() {
    saveOffer(null);
  }

  function resetToDefaults() {
    applyValues(DEFAULT_DISPLAY_VALUES, null);
  }

  // The media card stat columns live outside DisplayValues, so they reset with the section that edits them.
  function isSectionDefault(section: DisplaySectionId): boolean {
    const values = readValues();
    const keysDefault = DISPLAY_SECTION_KEYS[section].every((key) => JSON.stringify(values[key]) === JSON.stringify(DEFAULT_DISPLAY_VALUES[key]));
    return keysDefault && (section !== 'mediaCards' || isDefaultMediaCardStatColumns(jiten.mediaCardStatColumns));
  }

  function resetSection(section: DisplaySectionId) {
    const values: Record<string, unknown> = { ...readValues() };
    for (const key of DISPLAY_SECTION_KEYS[section]) values[key] = structuredClone(DEFAULT_DISPLAY_VALUES[key]);
    applyValues(values as Partial<DisplayValues>, section === 'mediaCards' ? null : readStatColumns());
  }

  return {
    profiles,
    status,
    saveState,
    localOffer,
    activeId,
    activeProfile,
    canCreate,
    readValues,
    readStatColumns,
    applyValues,
    init,
    refresh,
    reset,
    switchTo,
    create,
    rename,
    remove,
    acceptLocalOffer,
    dismissLocalOffer,
    resetToDefaults,
    isSectionDefault,
    resetSection,
    saveActiveNow,
  };
});
