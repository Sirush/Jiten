<script setup lang="ts">
  import { type Deck, LinkType, MediaType, DeckStatus } from '~/types';
  import Card from 'primevue/card';
  import TieredMenu from 'primevue/tieredmenu';
  import Popover from 'primevue/popover';
  import { getChildrenCountText, getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { ordinal } from '~/utils/ordinal';
  import { getLinkLabel } from '~/utils/linkTypeMapper';
  import { getDeckStatusText, isInProgressStatus } from '~/utils/deckStatusMapper';
  import { useJitenStore } from '~/stores/jitenStore';
  import { formatDateAsYyyyMmDd } from '~/utils/formatDateAsYyyyMmDd';
  import { useAuthStore } from '~/stores/authStore';
  import { useConfirm } from 'primevue/useconfirm';
  import { useToast } from 'primevue/usetoast';
  import { apiErrorMessage } from '~/utils/apiErrorMessage';
  import { deckHasStatData, isDefaultMediaCardStatColumns, mediaCardStatColumns, type MediaCardStatId } from '~/utils/mediaCardStats';
  import { DEFAULT_MEDIA_CARD_SECTION_LAYOUT, type MediaCardSectionId } from '~/utils/mediaCardSections';
  import { getDifficultyName } from '~/utils/difficultyColours';
  import { entryProgressLabel, listEntryFacts, mediaWords } from '~/utils/mediaListEntry';

  const props = defineProps<{
    deck: Deck;
    isCompact?: boolean;
    hideControl?: boolean;
    hideDetailButton?: boolean;
    titleTag?: string;
    // Set by list views for below-the-fold cards so their covers don't compete
    // with the LCP image. Defaults to eager (single-card pages).
    lazyCover?: boolean;
    // Guest homepage demo: shows the coverage bars without an authenticated user; hides rating and download to keep the card short.
    demoCoverage?: boolean;
    /** Opens the study dialog once mounted, for a visitor coming back from sign-up through the guest Study button. */
    openStudy?: boolean;
    readOnlyList?: boolean;
  }>();

  const emit = defineEmits<{
    'update:deck': [deck: Deck];
    'status-menu': [open: boolean];
  }>();

  const showDownloadDialog = ref(false);
  const showStudyDeckDialog = ref(false);
  const showIssueDialog = ref(false);
  const isDescriptionExpanded = ref(false);
  const showIgnoreOverlay = ref(false);
  const historyDeck = useHistoryDialogDeck();
  const menu = ref();
  const statusPopover = ref();
  const statusPillTooltip = ref<{ hide: () => void }>();
  const difficultyRef = ref<{ tooltip: string }>();
  // A string ref inside the stat v-for would collect an array; the function form keeps the single instance.
  const setDifficultyRef = (el: unknown) => {
    difficultyRef.value = (el as { tooltip: string } | null) ?? undefined;
  };

  const popularityTooltip = computed(() => {
    const deck = props.deck;
    if (!deck.popularityRank) return '';
    const lines = [`${ordinal(deck.popularityRank)} most popular ${getMediaTypeText(deck.mediaType).toLowerCase()} on Jiten`];
    if (deck.popularityGlobalRank) lines[0] += `, #${deck.popularityGlobalRank} across all media`;
    if (deck.popularityCounts) {
      const c = deck.popularityCounts;
      lines.push(`${c.inLists.toLocaleString()} in lists · ${c.favourites.toLocaleString()} favourites · ${c.studyDecks.toLocaleString()} study decks`);
    }
    if (deck.isTrending) lines.push('Trending: has a spike of activity recently');
    return lines.join('\n');
  });

  const store = useJitenStore();
  const authStore = useAuthStore();
  const localiseTitle = useLocaliseTitle();
  const confirm = useConfirm();
  const toast = useToast();
  const smartDeck = useSmartDeck();
  const { isPlus: isPlusUser } = useJitenPlus();
  const smartSourceState = computed(() => smartDeck.sourceState(props.deck.deckId));

  const smartMenuVisible = computed(() => isPlusUser.value && !props.deck.parentDeckId && smartDeck.exists.value);

  async function applySmartAction(action: 'pin' | 'unpin' | 'include' | 'exclude' | 'clear') {
    try {
      await smartDeck.applySourceAction(props.deck.deckId, action);
      const done: Record<string, string> = {
        pin: 'Pinned to the top of your Smart Deck',
        unpin: 'Unpinned',
        include: 'Included in your Smart Deck',
        exclude: 'Excluded from your Smart Deck',
        clear: 'Smart Deck override removed',
      };
      toast.add({ severity: 'success', summary: done[action], life: 2500 });
    } catch (e) {
      toast.add({ severity: 'warn', summary: apiErrorMessage(e, 'Could not update the Smart Deck'), life: 3000 });
    }
  }

  const displayAdminFunctions = computed(() => store.displayAdminFunctions);
  // The community-adjusted difficulty, the one the card shows.
  const deckDifficulty = computed(() => props.deck.difficultyRaw ?? props.deck.difficulty);
  const readingSpeed = computed(() => store.readingSpeedFor(deckDifficulty.value));
  const readingSpeedLabel = computed(() =>
    store.readingSpeedByDifficulty && deckDifficulty.value >= 0
      ? `your reading speed for ${getDifficultyName(deckDifficulty.value)} titles`
      : 'your reading speed'
  );
  const readingDuration = computed(() => Math.round(props.deck.characterCount / readingSpeed.value));
  const speechSpeed = computed(() => props.deck.speechSpeed ?? 0);

  const hasLandscapeCover = computed(() => props.deck.mediaType === MediaType.YouTube && !!props.deck.parentDeckId);
  const isVideo = computed(() => props.deck.mediaType === MediaType.YouTube && !!props.deck.parentDeckId);
  const youtubeUrl = computed(() => props.deck.links?.find((l) => l.linkType === LinkType.YouTube)?.url ?? null);
  const runtimeLabel = computed(() => {
    const seconds = props.deck.parentDeckId ? props.deck.runtimeSeconds : props.deck.medianChildRuntimeSeconds;
    return seconds ? formatRuntime(seconds) : '';
  });
  const coverSrc = computed(() => (props.deck.coverName == 'nocover.jpg' ? '/img/nocover.jpg' : props.deck.coverName));

  const hasChildren = computed(() => props.deck.childrenDeckCount > 0);
  const childrenLabel = computed(() => getChildrenCountText(props.deck.mediaType));
  const showChildrenLink = computed(() => hasChildren.value && !props.hideDetailButton);

  // Descriptive text used when sharing the deck to social platforms / the native share sheet.
  const shareTitle = computed(() => `${localiseTitle(props.deck)} — Japanese vocabulary list, stats & Anki deck · Jiten`);

  // Title variants not already shown as the heading, deduped. `ja` marks the original (Japanese)
  // title so it can be wrapped in lang="ja". Aliases are surfaced via JSON-LD alternateName, not here.
  const alternateTitles = computed<{ text: string; ja: boolean }[]>(() => {
    const d = props.deck;
    const shown = localiseTitle(d);
    const list: { text: string; ja: boolean }[] = [];
    const push = (text: string | undefined | null, ja: boolean) => {
      if (text && text !== shown && !list.some((e) => e.text === text)) list.push({ text, ja });
    };
    push(d.originalTitle, true);
    push(d.romajiTitle, false);
    push(d.englishTitle, false);
    return list;
  });

  const formattedSpeechDuration = computed(() => {
    if (props.deck.speechDuration <= 0) return '';
    const totalSeconds = Math.floor(props.deck.speechDuration / 1000);
    if (totalSeconds < 60) return `${totalSeconds}s`;
    const totalMinutes = Math.floor(totalSeconds / 60);
    const hours = Math.floor(totalMinutes / 60);
    const minutes = totalMinutes % 60;
    if (hours === 0) return `${minutes}min`;
    if (minutes === 0) return `${hours}h`;
    return `${hours}h ${minutes}min`;
  });

  // Menu is mounted lazily on first open — lists render many cards and most
  // menus are never opened.
  const menuActivated = ref(false);

  const toggleMenu = async (event: Event) => {
    menuActivated.value = true;
    if (isPlusUser.value && !props.deck.parentDeckId) smartDeck.fetchStatus();
    await nextTick();
    menu.value?.toggle(event);
  };

  const {
    toggleFavourite,
    toggleIgnore: _toggleIgnore,
    cancelIgnore: _cancelIgnore,
  } = useDeckPreference(
    () => props.deck,
    (updated) => emit('update:deck', updated)
  );

  const toggleIgnore = async () => {
    const newState = await _toggleIgnore();
    if (newState !== null) {
      showIgnoreOverlay.value = newState;
    }
  };

  const cancelIgnore = async () => {
    await _cancelIgnore();
    showIgnoreOverlay.value = false;
  };

  const { openRatingDialog } = useCompletionFollowUp();

  onMediaListChange((change) => {
    if (props.readOnlyList) return;
    const updated = applyMediaListChange(props.deck, change);
    if (updated !== props.deck) emit('update:deck', updated);
  });

  const { $api } = useNuxtApp();

  const { isRefreshing: isRefreshingCoverage, refresh: refreshDeckCoverage } = useDeckCoverageRefresh(
    () => props.deck,
    (updated) => emit('update:deck', updated)
  );

  const queueFullCoverageRefresh = async () => {
    try {
      await $api('user/coverage/refresh', { method: 'POST' });
      showSuccessToast(toast, 'Coverage computation started', 'Your numbers will appear within a minute or two.');
    } catch (error) {
      console.error('Failed to queue full coverage refresh:', error);
      showErrorToast(toast, 'Error', 'Could not start the coverage computation, please try again.');
    }
  };

  const handleRefreshCoverage = async () => {
    const result = await refreshDeckCoverage();
    switch (result) {
      case 'refreshed':
        showSuccessToast(toast, 'Coverage refreshed');
        break;
      case 'not_eligible':
        showWarnToast(toast, 'Not enough tracked words', 'Coverage needs a few words in your vocabulary first. Review or import some words, then try again.');
        break;
      case 'no_baseline':
        confirm.require({
          message: 'Your coverage has never been computed, so there is nothing to refresh yet. Compute it for your whole account now?',
          header: 'Compute coverage',
          icon: 'pi pi-chart-pie',
          acceptLabel: 'Compute coverage',
          rejectLabel: 'Not now',
          rejectProps: { severity: 'secondary' },
          accept: () => queueFullCoverageRefresh(),
        });
        break;
      case 'rate_limited':
        showWarnToast(toast, 'Too many refreshes', 'Please wait a few seconds and try again.');
        break;
      case 'error':
        showErrorToast(toast, 'Error refreshing coverage', 'There was an error refreshing coverage, please try again.');
        break;
    }
  };

  const menuItems = computed(() => [
    {
      label: props.deck.isIgnored ? 'Unignore' : 'Ignore',
      icon: props.deck.isIgnored ? 'pi pi-eye' : 'pi pi-eye-slash',
      command: toggleIgnore,
    },
    {
      label: 'Rate difficulty',
      icon: 'pi pi-gauge',
      visible: !props.readOnlyList && (props.deck.status === DeckStatus.Completed || !!props.deck.listEntry?.completedCount) && !props.deck.parentDeckId,
      command: () => openRatingDialog(props.deck.deckId, localiseTitle(props.deck)),
    },
    {
      label: mediaWords(props.deck.mediaType).historyTitle,
      icon: 'pi pi-history',
      visible: !props.readOnlyList && canOpenHistory.value,
      command: () => openHistory(),
    },
    {
      label: 'Refresh coverage',
      icon: 'pi pi-refresh',
      disabled: isRefreshingCoverage.value,
      command: () => handleRefreshCoverage(),
    },
    {
      label: 'Edit',
      icon: 'pi pi-pencil',
      visible: !props.isCompact && authStore.isAdmin && displayAdminFunctions.value,
      route: `/dashboard/media/${props.deck.deckId}`,
    },
    {
      label: 'Report an issue',
      icon: 'pi pi-exclamation-triangle',
      visible: !props.isCompact,
      command: () => {
        showIssueDialog.value = true;
      },
    },
    {
      separator: true,
      visible: smartMenuVisible.value,
    },
    {
      label: smartSourceState.value === 'pinned' ? 'Unpin from Smart Deck' : 'Pin in Smart Deck',
      icon: 'pi pi-bookmark',
      visible: smartMenuVisible.value,
      command: () => applySmartAction(smartSourceState.value === 'pinned' ? 'unpin' : 'pin'),
    },
    {
      label: smartSourceState.value === 'excluded' ? 'Stop excluding from Smart Deck' : 'Exclude from Smart Deck',
      icon: 'pi pi-ban',
      visible: smartMenuVisible.value,
      command: () => applySmartAction(smartSourceState.value === 'excluded' ? 'clear' : 'exclude'),
    },
    {
      label: 'Include in Smart Deck',
      icon: 'pi pi-plus-circle',
      visible: smartMenuVisible.value && !props.readOnlyList && smartSourceState.value === 'none' && props.deck.status !== DeckStatus.Ongoing,
      command: () => applySmartAction('include'),
    },
  ]);

  const statusOptions = [DeckStatus.None, DeckStatus.Planning, DeckStatus.Ongoing, DeckStatus.Paused, DeckStatus.Completed, DeckStatus.Dropped];

  const currentStatus = computed(() => props.deck.status ?? DeckStatus.None);

  const statusPopoverOpen = ref(false);
  const { menu: statusMenu, rememberTrigger, restoreFocus, focusList } = useMenuFocus();
  const toggleStatusPopover = (event: Event, hideTooltip: () => void) => {
    hideTooltip();
    rememberTrigger(event);
    statusPopover.value?.toggle(event);
  };

  const onStatusPopoverShow = () => {
    statusPillTooltip.value?.hide();
    statusPopoverOpen.value = true;
    emit('status-menu', true);
    void focusList();
  };

  const onStatusPopoverHide = () => {
    restoreFocus();
    statusFlow.reset();
    statusPopoverOpen.value = false;
    emit('status-menu', false);
  };

  const statusFlow = useStatusFlow(() => props.deck, { close: () => statusPopover.value?.hide(), isOpen: () => statusPopoverOpen.value });
  watch(statusFlow.step, (step) => {
    if (step === 'list' && statusPopoverOpen.value) void focusList();
  });

  const progressLabel = computed(() => entryProgressLabel(props.deck.listEntry, props.deck.characterCount));
  const showLogProgress = computed(() => isInProgressStatus(currentStatus.value));

  const listEntryLine = computed(() =>
    props.deck.listEntry && currentStatus.value !== DeckStatus.None ? listEntryFacts(props.deck.listEntry, props.deck.mediaType, props.deck.characterCount) : []
  );

  const canOpenHistory = computed(() => currentStatus.value !== DeckStatus.None || !!props.deck.listEntry);

  const openHistory = () => {
    statusPopover.value?.hide();
    historyDeck.value = props.deck;
  };

  const statusTooltip = computed(() => {
    if (currentStatus.value === DeckStatus.None) return 'Set status';
    const fact = listEntryLine.value[0];
    if (!props.isCompact) return fact ?? 'Set status';
    const status = getDeckStatusText(currentStatus.value);
    return fact ? `${status}, ${fact.charAt(0).toLowerCase()}${fact.slice(1)}` : status;
  });

  const statusButtonLabel = computed(() => {
    if (currentStatus.value === DeckStatus.None) return 'Set status';
    const status = `Status: ${getDeckStatusText(currentStatus.value)}`;
    return isInProgressStatus(currentStatus.value) && progressLabel.value ? `${status}, ${progressLabel.value}` : status;
  });

  const statusColor = computed(() => {
    if (!props.deck.status || props.deck.status === DeckStatus.None) return '';

    switch (props.deck.status) {
      case DeckStatus.Planning:
        return 'text-gray-500 dark:text-gray-400';
      case DeckStatus.Ongoing:
        return 'text-yellow-500';
      case DeckStatus.Paused:
        return 'text-sky-600 dark:text-sky-400';
      case DeckStatus.Completed:
        return 'text-green-500';
      case DeckStatus.Dropped:
        return 'text-red-500';
      default:
        return '';
    }
  });

  const sortedLinks = computed(() => {
    if (!props.deck.links || props.deck.links.length === 0) return [];

    return [...props.deck.links].sort((a, b) => {
      const textA = getLinkLabel(a);
      const textB = getLinkLabel(b);
      return textA.localeCompare(textB);
    });
  });

  const canEditInline = computed(() => !props.isCompact && authStore.isAdmin && displayAdminFunctions.value);
  const isEditing = ref(false);

  const onMetadataSaved = (result: import('~/types/types').DeckMetadataPatchResult) => {
    emit('update:deck', { ...props.deck, ...result });
    isEditing.value = false;
  };

  const titleBoxRef = ref<HTMLElement | null>(null);
  const isTitleClipped = ref(false);
  let titleResizeObserver: ResizeObserver | undefined;

  const measureTitleClip = () => {
    const el = titleBoxRef.value;
    if (el) isTitleClipped.value = el.scrollHeight > el.clientHeight + 1;
  };

  const guestStudyPopoverActive = ref(false);
  const guestStudyPopover = ref<{ show: (event: Event, target?: HTMLElement) => void } | null>(null);

  async function onGuestStudy(event: MouseEvent) {
    const target = event.currentTarget as HTMLElement;
    trackEvent('guest_locked_control_clicked', { control: 'study' });
    guestStudyPopoverActive.value = true;
    await nextTick();
    guestStudyPopover.value?.show({ currentTarget: target } as unknown as Event, target);
  }

  onMounted(() => {
    if (props.openStudy && authStore.isAuthenticated) showStudyDeckDialog.value = true;
  });

  onMounted(() => {
    if (!props.isCompact) return;
    measureTitleClip();
    if (titleBoxRef.value && typeof ResizeObserver !== 'undefined') {
      titleResizeObserver = new ResizeObserver(measureTitleClip);
      titleResizeObserver.observe(titleBoxRef.value);
    }
    document.fonts?.ready.then(measureTitleClip);
  });

  onBeforeUnmount(() => titleResizeObserver?.disconnect());

  watch(
    () => localiseTitle(props.deck),
    () => nextTick(measureTitleClip)
  );

  const formatOnce = (count: number) => `${count.toLocaleString()} once`;

  const hiddenInDemo = new Set<MediaCardStatId>(['dialogue', 'readingDuration', 'externalRating']);

  const statVisible = (id: MediaCardStatId): boolean => {
    if (!deckHasStatData(props.deck, id)) return false;
    if (props.demoCoverage && hiddenInDemo.has(id)) return false;
    return id !== 'externalRating' || !store.hideExternalRating;
  };

  // The stat editor covers the full card only; compact cards keep the built-in layout.
  const profileColumns = computed(() => (props.isCompact ? null : store.mediaCardStatColumns));
  const hasCustomStats = computed(() => !isDefaultMediaCardStatColumns(profileColumns.value));
  const statColumns = computed(() => mediaCardStatColumns(profileColumns.value, statVisible));

  const sectionShown = (section: MediaCardSectionId): boolean => {
    const deck = props.deck;
    switch (section) {
      case 'description':
        return !!deck.description && !store.hideDescriptions;
      case 'genres':
        return !store.hideGenres && !!deck.genres?.length;
      case 'tags':
        return !store.hideTags && !!deck.tags?.length;
      case 'relations':
        return !store.hideRelations && (!!deck.relationships?.length || deck.franchiseId != null);
    }
  };

  // Compact cards have no section editor of their own, so they keep the built-in order.
  const cardSections = computed(() => {
    const { top, bottom } = props.isCompact ? DEFAULT_MEDIA_CARD_SECTION_LAYOUT : store.mediaCardSectionLayout;
    return { top: top.filter(sectionShown), bottom: bottom.filter(sectionShown) };
  });

  const showCoverageStrip = computed(
    () => (authStore.isAuthenticated || props.demoCoverage) && !store.hideCoverageBorders && (props.deck.coverage != 0 || props.deck.uniqueCoverage != 0)
  );
</script>

<template>
  <div class="relative" :class="isCompact ? 'w-80 compact-card' : ''">
    <div
      v-if="showIgnoreOverlay"
      class="absolute inset-0 z-50 flex items-center justify-center backdrop-blur-lg bg-black/50 rounded-lg ignore-overlay"
      @click.stop
    >
      <div class="bg-white dark:bg-gray-800 rounded-lg p-6 max-w-md mx-4 shadow-xl">
        <p class="text-center text-gray-800 dark:text-gray-200 mb-4">This media will be ignored and no longer appear in search results.</p>
        <div class="text-center">
          <button
            type="button"
            class="text-primary-500 hover:text-primary-700 dark:hover:text-primary-400 font-semibold underline-offset-2 hover:underline cursor-pointer"
            @click="cancelIgnore"
          >
            Cancel
          </button>
        </div>
      </div>
    </div>

    <div class="relative" :class="isCompact ? 'h-full' : ''">
      <Card :class="isCompact ? 'h-full' : ''" :pt="{ body: { style: 'padding: 0.75rem 1rem; gap: 0.25rem' } }">
        <template #title>
          <div ref="titleBoxRef" class="overflow-hidden" :class="isCompact ? 'relative leading-snug h-[2.75em]' : ''">
            <div
              class="flex flex-row items-center gap-1 h-6 shrink-0"
              :class="isCompact ? 'float-right ml-2' : 'justify-end mb-1 md:float-right md:ml-2 md:mb-0'"
            >
              <div v-if="authStore.isAuthenticated && deck.isIgnored" class="flex items-center pr-1.5">
                <i class="pi pi-eye-slash text-gray-800 dark:text-gray-300 text-lg" />
              </div>
              <Tooltip v-if="authStore.isAuthenticated && !readOnlyList" :content="deck.isFavourite ? 'Remove from favourites' : 'Add to favourites'">
                <button
                  type="button"
                  class="p-1.5 rounded hover:bg-gray-200 dark:hover:bg-gray-700 transition-colors cursor-pointer"
                  :aria-pressed="!!deck.isFavourite"
                  :aria-label="deck.isFavourite ? 'Remove from favourites' : 'Add to favourites'"
                  @click="toggleFavourite"
                >
                  <i :class="deck.isFavourite ? 'pi pi-star-fill text-yellow-500' : 'pi pi-star utility-icon'" />
                </button>
              </Tooltip>
              <Tooltip
                v-if="authStore.isAuthenticated && !readOnlyList"
                ref="statusPillTooltip"
                v-slot="{ hide }"
                :content="statusPopoverOpen ? '' : statusTooltip"
              >
                <button
                  type="button"
                  class="flex items-center gap-1 p-1.5 rounded hover:bg-gray-200 dark:hover:bg-gray-700 transition-colors cursor-pointer"
                  aria-haspopup="true"
                  :aria-expanded="statusPopoverOpen"
                  :aria-label="statusButtonLabel"
                  @click="toggleStatusPopover($event, hide)"
                >
                  <i :class="['pi', currentStatus === DeckStatus.None ? 'pi-flag utility-icon' : `pi-flag-fill ${statusColor}`]" />
                  <span v-if="!isCompact && currentStatus !== DeckStatus.None" :class="['text-sm font-bold leading-none', statusColor]">
                    {{ getDeckStatusText(currentStatus) }}
                  </span>
                  <span
                    v-if="isInProgressStatus(currentStatus) && progressLabel"
                    class="text-xs font-semibold leading-none tabular-nums text-gray-600 dark:text-gray-300"
                  >
                    {{ progressLabel }}
                  </span>
                </button>
              </Tooltip>
              <Tooltip v-if="canEditInline" :content="isEditing ? 'Stop editing' : 'Edit metadata inline'">
                <button
                  type="button"
                  class="p-1.5 rounded hover:bg-gray-200 dark:hover:bg-gray-700 transition-colors cursor-pointer"
                  :aria-pressed="isEditing"
                  @click="isEditing = !isEditing"
                >
                  <i class="pi pi-pencil utility-icon" />
                </button>
              </Tooltip>
              <ShareButton v-if="!isCompact" :path="`/decks/media/${deck.deckId}/detail`" :title="shareTitle" />
              <Tooltip content="View stats">
                <router-link
                  :to="`/decks/media/${deck.deckId}/stats`"
                  class="inline-block p-1.5 rounded hover:bg-gray-200 dark:hover:bg-gray-700 transition-colors cursor-pointer"
                >
                  <i class="pi pi-chart-bar utility-icon" />
                </router-link>
              </Tooltip>
              <Tooltip v-if="authStore.isAuthenticated" content="More options">
                <button type="button" class="p-1.5 rounded hover:bg-gray-200 dark:hover:bg-gray-700 transition-colors cursor-pointer" @click="toggleMenu">
                  <i class="pi pi-ellipsis-v utility-icon" />
                </button>
              </Tooltip>
            </div>
            <Tooltip v-if="isCompact" :content="localiseTitle(deck)">
              <component :is="titleTag || 'span'" class="break-words" v-bind="japaneseTextAttrs(localiseTitle(deck))">{{ localiseTitle(deck) }}</component>
            </Tooltip>
            <component :is="titleTag || 'span'" v-else class="break-words" v-bind="japaneseTextAttrs(localiseTitle(deck))">{{ localiseTitle(deck) }}</component>
            <span v-if="isTitleClipped" aria-hidden="true" class="title-clip-ellipsis pointer-events-none absolute bottom-0 right-0 pl-6">…</span>
          </div>
        </template>
        <template v-if="!isCompact" #subtitle>
          <span class="flex items-baseline gap-1 min-w-0 text-xs pl-0.5">
            <span class="font-semibold whitespace-nowrap text-gray-800 dark:text-gray-100">{{ getMediaTypeText(deck.mediaType) }}</span>
            <template v-if="deck.popularityRank">
              <span class="text-gray-400 dark:text-gray-400">·</span>
              <Tooltip :content="popularityTooltip">
                <span class="whitespace-nowrap cursor-help">
                  <span class="font-bold tabular-nums text-gray-800 dark:text-gray-100">#{{ deck.popularityRank }}</span>
                  <span class="text-gray-600 dark:text-gray-400"> most popular</span>
                </span>
              </Tooltip>
            </template>
            <span
              v-if="deck.isTrending"
              class="inline-flex items-center gap-1 rounded px-1.5 leading-4 text-[11px] font-semibold bg-purple-100 dark:bg-purple-900/50 text-purple-700 dark:text-purple-200"
            >
              <i class="pi pi-arrow-up-right text-[9px]" />Trending
            </span>
            <template v-if="alternateTitles.length && !store.hideAlternativeTitles">
              <span class="text-gray-400 dark:text-gray-400">·</span>
              <!-- Full text stays in the DOM (truncate only clips visually) so it remains crawlable. -->
              <span
                data-section="alternativeTitles"
                class="min-w-0 flex-1 truncate md:overflow-visible md:whitespace-normal md:break-words text-gray-600 dark:text-gray-400"
              >
                <template v-for="(t, i) in alternateTitles" :key="i">
                  <span v-if="i > 0" class="mx-1 text-gray-400 dark:text-gray-400">·</span>
                  <span :lang="t.ja ? 'ja' : undefined" :class="{ 'ja-general': t.ja }">{{ t.text }}</span>
                </template>
              </span>
            </template>
          </span>
        </template>
        <template #content>
          <div class="flex-gap-6" :class="isCompact ? 'h-full flex flex-col' : ''">
            <div class="flex-1 max-w-full overflow-hidden" :class="isCompact ? 'flex flex-col' : ''">
              <div class="flex flex-col md:flex-row md:items-stretch gap-x-4 gap-y-2 w-full" :class="isCompact ? 'flex-1' : ''">
                <div v-if="!isCompact" class="@container text-left text-sm md:shrink-0" :class="hasLandscapeCover ? 'md:w-60' : 'md:w-34'">
                  <div class="flex items-start gap-4 @max-[17rem]:flex-col @max-[17rem]:items-stretch md:block">
                    <div class="shrink-0">
                      <img
                        :src="coverSrc"
                        :alt="localiseTitle(deck)"
                        class="object-cover"
                        :class="hasLandscapeCover ? 'w-60 min-w-60 aspect-video rounded-md' : 'h-48 w-34 min-w-34'"
                        :fetchpriority="lazyCover ? undefined : 'high'"
                        :loading="lazyCover ? 'lazy' : 'eager'"
                        decoding="async"
                        :width="hasLandscapeCover ? 240 : 136"
                        :height="hasLandscapeCover ? 135 : 192"
                      />
                      <Tooltip content="Release date">
                        <div class="mt-2 flex items-center md:justify-center tabular-nums text-gray-600 dark:text-gray-400">
                          {{ formatDateAsYyyyMmDd(new Date(deck.releaseDate)).replace(/-/g, '/') }}
                        </div>
                      </Tooltip>
                    </div>
                    <DeckCoverageBars
                      v-if="(authStore.isAuthenticated || demoCoverage) && (deck.coverage != 0 || deck.uniqueCoverage != 0)"
                      :deck="deck"
                      class="flex-1 min-w-0 @max-[17rem]:flex-none md:mt-3"
                    />
                  </div>
                </div>
                <div class="@container min-w-0 flex-1 flex flex-col">
                  <NuxtLink
                    v-if="isCompact && hasLandscapeCover && deck.coverName != 'nocover.jpg'"
                    :to="`/decks/media/${deck.deckId}/${isVideo ? 'watch' : 'detail'}`"
                    class="block mb-2 -mx-1 rounded-md overflow-hidden bg-gray-200 dark:bg-gray-800"
                    :aria-label="isVideo ? `Watch ${localiseTitle(deck)}` : localiseTitle(deck)"
                  >
                    <img :src="coverSrc" alt="" class="w-full aspect-video object-cover" loading="lazy" decoding="async" width="320" height="180" />
                  </NuxtLink>
                  <div
                    class="grid grid-cols-1 gap-x-3 @xl:gap-x-8 @3xl:gap-x-12 gap-y-1 max-w-[51rem] text-sm"
                    :class="isCompact ? '' : '@xs:grid-cols-2 @3xl:grid-cols-3'"
                  >
                    <div v-for="(column, columnIndex) in statColumns" :key="columnIndex" class="min-w-0 @max-3xl:contents">
                      <template v-for="statId in column" :key="statId">
                        <div v-if="statId === 'speechDuration'" :data-stat="statId" class="flex justify-between gap-2 stat-row">
                          <Tooltip :content="'Total duration of speech, excluding silence.\nCharacter count: ' + deck.characterCount.toLocaleString()">
                            <span class="text-gray-600 dark:text-gray-400 font-normal whitespace-nowrap">
                              <span class="@xl:hidden">Speech time</span><span class="hidden @xl:inline">Speech duration</span>
                            </span>
                          </Tooltip>
                          <span class="tabular-nums font-bold text-gray-900 dark:text-gray-50 whitespace-nowrap">{{ formattedSpeechDuration }}</span>
                        </div>
                        <div v-else-if="statId === 'characters'" :data-stat="statId" class="flex justify-between gap-2 stat-row">
                          <span class="text-gray-600 dark:text-gray-400 font-normal whitespace-nowrap">
                            <span class="@xl:hidden">Characters</span><span class="hidden @xl:inline">Character count</span>
                          </span>
                          <span class="tabular-nums font-bold text-gray-900 dark:text-gray-50 whitespace-nowrap">{{
                            deck.characterCount.toLocaleString()
                          }}</span>
                        </div>
                        <div v-else-if="statId === 'wordCount'" :data-stat="statId" class="flex justify-between gap-2 stat-row">
                          <span class="text-gray-600 dark:text-gray-400 font-normal whitespace-nowrap">Word count</span>
                          <span class="tabular-nums font-bold text-gray-900 dark:text-gray-50 whitespace-nowrap">{{ deck.wordCount.toLocaleString() }}</span>
                        </div>
                        <div v-else-if="statId === 'uniqueWords'" :data-stat="statId" class="flex justify-between gap-2 stat-row">
                          <Tooltip :content="'Words appearing exactly once: ' + deck.uniqueWordUsedOnceCount.toLocaleString()">
                            <span class="text-gray-600 dark:text-gray-400 font-normal whitespace-nowrap">
                              Unique words
                              <span class="hidden @xl:inline text-gray-600 dark:text-gray-400 text-xs tabular-nums"
                                >· {{ formatOnce(deck.uniqueWordUsedOnceCount) }}</span
                              >
                            </span>
                          </Tooltip>
                          <span class="tabular-nums font-bold text-gray-900 dark:text-gray-50 whitespace-nowrap">{{
                            deck.uniqueWordCount.toLocaleString()
                          }}</span>
                        </div>
                        <div v-else-if="statId === 'uniqueKanji'" :data-stat="statId" class="flex justify-between gap-2 stat-row">
                          <Tooltip :content="'Kanji appearing exactly once: ' + deck.uniqueKanjiUsedOnceCount.toLocaleString()">
                            <span class="text-gray-600 dark:text-gray-400 font-normal whitespace-nowrap">
                              Unique kanji
                              <span class="hidden @xl:inline text-gray-600 dark:text-gray-400 text-xs tabular-nums"
                                >· {{ formatOnce(deck.uniqueKanjiUsedOnceCount) }}</span
                              >
                            </span>
                          </Tooltip>
                          <span class="tabular-nums font-bold text-gray-900 dark:text-gray-50 whitespace-nowrap">{{
                            deck.uniqueKanjiCount.toLocaleString()
                          }}</span>
                        </div>
                        <div v-else-if="statId === 'averageSentenceLength'" :data-stat="statId" class="flex justify-between gap-2 stat-row">
                          <span class="text-gray-600 dark:text-gray-400 font-normal whitespace-nowrap"
                            ><span class="@xl:hidden">Avg. sentence</span><span class="hidden @xl:inline">Average sentence length</span></span
                          >
                          <span class="tabular-nums font-bold text-gray-900 dark:text-gray-50 whitespace-nowrap">{{
                            deck.averageSentenceLength.toFixed(1)
                          }}</span>
                        </div>
                        <div v-else-if="statId === 'speechSpeed'" :data-stat="statId" class="flex justify-between gap-2 stat-row">
                          <Tooltip content="Average speed of speech in mora per minute.">
                            <span class="text-gray-600 dark:text-gray-400 font-normal whitespace-nowrap">Speech speed</span>
                          </Tooltip>
                          <span class="tabular-nums font-bold text-gray-900 dark:text-gray-50 whitespace-nowrap">{{ speechSpeed.toFixed(0) }}</span>
                        </div>
                        <div
                          v-else-if="statId === 'difficulty'"
                          :data-stat="statId"
                          class="stat-row cursor-help @max-3xl:col-span-full"
                          :class="{ '@max-3xl:order-first': !hasCustomStats }"
                        >
                          <Tooltip :content="difficultyRef?.tooltip ?? ''" block>
                            <div class="flex justify-between gap-x-2">
                              <span class="text-gray-600 dark:text-gray-400 font-normal shrink-0">
                                Difficulty
                                <i class="pi pi-info-circle text-primary-400 text-xs ml-0.5" />
                              </span>
                              <DifficultyDisplay
                                :ref="setDifficultyRef"
                                :difficulty="deck.difficulty"
                                :difficulty-raw="deck.difficultyRaw"
                                :difficulty-algorithmic="deck.difficultyAlgorithmic"
                                :user-adjustment="deck.userAdjustment"
                                :vote-count="deck.distinctVoterCount || 0"
                                :adjustment-confidence="deck.adjustmentConfidence || 0"
                              />
                            </div>
                          </Tooltip>
                        </div>
                        <div v-else-if="statId === 'dialogue'" :data-stat="statId" class="flex justify-between gap-2 stat-row">
                          <span class="text-gray-600 dark:text-gray-400 font-normal whitespace-nowrap">Dialogue</span>
                          <span class="tabular-nums font-bold text-gray-900 dark:text-gray-50 whitespace-nowrap"
                            >{{ deck.dialoguePercentage.toFixed(1) }}%</span
                          >
                        </div>
                        <template v-else-if="statId === 'children'">
                          <router-link
                            v-if="showChildrenLink"
                            :data-stat="statId"
                            :to="`/decks/media/${deck.deckId}/detail`"
                            class="flex justify-between gap-2 stat-row group cursor-pointer no-underline"
                          >
                            <span class="text-primary-600 dark:text-primary-400 font-normal whitespace-nowrap underline-offset-2 group-hover:underline">{{
                              childrenLabel
                            }}</span>
                            <span class="tabular-nums font-semibold whitespace-nowrap text-primary-600 dark:text-primary-400">
                              {{ deck.childrenDeckCount.toLocaleString() }}
                              <i class="pi pi-arrow-right text-xs ml-0.5 transition-transform group-hover:translate-x-0.5" />
                            </span>
                          </router-link>
                          <div v-else :data-stat="statId" class="flex justify-between gap-2 stat-row">
                            <span class="text-gray-600 dark:text-gray-400 font-normal whitespace-nowrap">{{ childrenLabel }}</span>
                            <span class="tabular-nums font-bold text-gray-900 dark:text-gray-50 whitespace-nowrap">{{
                              deck.childrenDeckCount.toLocaleString()
                            }}</span>
                          </div>
                        </template>
                        <div v-else-if="statId === 'runtime'" :data-stat="statId" class="flex justify-between gap-2 stat-row">
                          <Tooltip :content="deck.parentDeckId ? 'Length of the video.' : 'Median length of a video on this channel.'">
                            <span class="text-gray-600 dark:text-gray-400 font-normal whitespace-nowrap">
                              <span v-if="deck.parentDeckId">Length</span
                              ><span v-else><span class="@xl:hidden">Avg. length</span><span class="hidden @xl:inline">Average video length</span></span>
                            </span>
                          </Tooltip>
                          <span class="tabular-nums font-bold text-gray-900 dark:text-gray-50 whitespace-nowrap">{{ runtimeLabel }}</span>
                        </div>
                        <div v-else-if="statId === 'readingDuration'" :data-stat="statId" class="flex justify-between gap-2 stat-row">
                          <Tooltip
                            :content="
                              'Based on ' +
                              readingSpeedLabel +
                              ':\n ' +
                              '<strong>' +
                              readingSpeed.toLocaleString() +
                              '</strong>' +
                              ' characters per hour.\n<i>You can change it in the display settings.</i>'
                            "
                          >
                            <span class="text-gray-600 dark:text-gray-400 font-normal whitespace-nowrap">
                              Duration
                              <i class="pi pi-info-circle cursor-pointer text-primary-500" />
                            </span>
                          </Tooltip>

                          <span class="tabular-nums font-bold text-gray-900 dark:text-gray-50 whitespace-nowrap"
                            >{{ readingDuration > 0 ? readingDuration : '<1' }} h</span
                          >
                        </div>
                        <div v-else-if="statId === 'externalRating'" :data-stat="statId" class="flex justify-between gap-2 stat-row">
                          <Tooltip content="Score based on user ratings from 3rd party websites, such as AniList, TMDB, VNDB or IGDB.">
                            <span class="text-gray-600 dark:text-gray-400 font-normal whitespace-nowrap">
                              <span class="@xl:hidden">Rating</span><span class="hidden @xl:inline">External Rating</span>
                            </span>
                          </Tooltip>
                          <span class="tabular-nums font-bold text-gray-900 dark:text-gray-50 whitespace-nowrap">{{ deck.externalRating }} %</span>
                        </div>
                        <div v-else-if="statId === 'appears'" :data-stat="statId" class="flex justify-between gap-2 stat-row">
                          <span class="text-gray-600 dark:text-gray-400 font-normal whitespace-nowrap">
                            <span class="@xl:hidden">Appears</span><span class="hidden @xl:inline">Appears (times)</span>
                          </span>
                          <span class="tabular-nums font-bold whitespace-nowrap">{{ deck.selectedWordOccurrences.toLocaleString() }}</span>
                        </div>
                      </template>
                    </div>
                  </div>

                  <div class="mt-3 space-y-2">
                    <MediaDeckCardSection
                      v-for="section in cardSections.top"
                      :key="section"
                      v-model:description-expanded="isDescriptionExpanded"
                      :section="section"
                      :deck="deck"
                    />
                  </div>

                  <ExampleSentenceEntry v-if="deck.exampleSentence != undefined" :example-sentence="deck.exampleSentence" />

                  <div class="mt-auto">
                    <LazyDeckInlineEditor v-if="isEditing" :deck="deck" @saved="onMetadataSaved" @close="isEditing = false" />

                    <div v-else-if="cardSections.bottom.length" class="pt-5 space-y-2 max-w-[51rem]">
                      <MediaDeckCardSection
                        v-for="section in cardSections.bottom"
                        :key="section"
                        v-model:description-expanded="isDescriptionExpanded"
                        :section="section"
                        :deck="deck"
                      />
                    </div>
                  </div>
                  <DeckCoverageBars
                    v-if="isCompact && (authStore.isAuthenticated || demoCoverage) && (deck.coverage != 0 || deck.uniqueCoverage != 0)"
                    :deck="deck"
                    class="mt-3"
                  />
                  <div
                    v-if="!hideControl || (!isCompact && sortedLinks.length)"
                    :class="isCompact ? 'pt-4' : 'pt-4 flex flex-col @xl:flex-row @xl:items-end @xl:justify-between gap-x-6 gap-y-3'"
                  >
                    <div v-if="!hideControl" class="gap-2" :class="[isCompact ? 'flex flex-row justify-center' : 'grid grid-cols-2 @xl:flex @xl:flex-row']">
                      <Tooltip v-if="!hideDetailButton" content="Details">
                        <Button
                          as="router-link"
                          :to="`/decks/media/${deck.deckId}/detail`"
                          :label="isCompact ? undefined : 'Details'"
                          icon="pi pi-eye"
                          size="small"
                          class="text-center"
                        />
                      </Tooltip>
                      <Tooltip v-if="isVideo" content="Watch with the transcript">
                        <Button
                          as="router-link"
                          :to="`/decks/media/${deck.deckId}/watch`"
                          :label="isCompact ? undefined : 'Watch'"
                          icon="pi pi-youtube"
                          size="small"
                          class="text-center"
                        />
                      </Tooltip>
                      <Tooltip content="Vocabulary">
                        <Button
                          as="router-link"
                          :to="`/decks/media/${deck.deckId}/vocabulary`"
                          :label="isCompact ? undefined : 'Vocabulary'"
                          icon="pi pi-book"
                          size="small"
                          class="text-center"
                        />
                      </Tooltip>
                      <Tooltip v-if="authStore.isAuthenticated" content="Study with SRS">
                        <Button
                          :label="isCompact ? undefined : 'Study'"
                          icon="pi pi-play"
                          size="small"
                          class="text-center"
                          @click="showStudyDeckDialog = true"
                        />
                      </Tooltip>
                      <Tooltip v-else-if="!demoCoverage" :content="isCompact ? 'Study with SRS (free account)' : ''">
                        <Button
                          :label="isCompact ? undefined : 'Study'"
                          :aria-label="isCompact ? 'Study with SRS (free account)' : undefined"
                          icon="pi pi-play"
                          size="small"
                          class="text-center"
                          @click="onGuestStudy"
                        />
                      </Tooltip>
                      <Tooltip v-if="!demoCoverage" content="Download / Learn">
                        <!-- Label shortens rather than wrapping: a two-line label makes this button taller than its row. -->
                        <Button :icon="isCompact ? 'pi pi-download' : undefined" size="small" class="text-center" @click="showDownloadDialog = true">
                          <template v-if="!isCompact">
                            <i class="pi pi-download" />
                            <span class="whitespace-nowrap">
                              <span class="@xl:hidden">Download</span><span class="hidden @xl:inline">Download / Learn</span>
                            </span>
                          </template>
                        </Button>
                      </Tooltip>
                    </div>

                    <div v-if="!isCompact && sortedLinks.length" class="flex flex-wrap items-center gap-x-3 gap-y-1 @xl:justify-end">
                      <span class="text-xs font-semibold text-gray-600 dark:text-gray-400 uppercase tracking-wider shrink-0">Sources</span>
                      <a v-for="link in sortedLinks" :key="link.url" :href="link.url" target="_blank" class="text-sm">{{ getLinkLabel(link) }}</a>
                    </div>
                  </div>
                </div>
              </div>
            </div>
          </div>
        </template>
      </Card>

      <CoverageStrip
        v-if="showCoverageStrip"
        data-section="coverage"
        :coverage="deck.coverage"
        :young-coverage="deck.youngCoverage"
        with-tooltip
        class="absolute inset-x-0 bottom-0 z-10 rounded-b-[var(--p-card-border-radius)]"
      />
    </div>

    <LazyMediaDeckDownloadDialog v-if="showDownloadDialog" :deck="deck" :visible="showDownloadDialog" @update:visible="showDownloadDialog = $event" />
    <GuestAccountPopover
      v-if="guestStudyPopoverActive"
      ref="guestStudyPopover"
      message="Study the vocabulary in this title with Jiten, an user-friendly SRS experience with extensive customisation, powered by the modern FSRS-7 algorithm."
      prompt="study_button"
      :redirect="`/decks/media/${deck.deckId}/detail?study=1`"
    />
    <LazySrsAddDeckDialog v-if="showStudyDeckDialog" :visible="showStudyDeckDialog" :preselected-deck="deck" @update:visible="showStudyDeckDialog = $event" />
    <LazyReportIssueDialog v-if="showIssueDialog" :visible="showIssueDialog" :deck="deck" @update:visible="showIssueDialog = $event" />

    <TieredMenu v-if="authStore.isAuthenticated && menuActivated" ref="menu" :model="menuItems" popup>
      <template #item="{ item, props: itemProps }">
        <NuxtLink v-if="item.route" v-slot="{ href, navigate }" :to="item.route" custom>
          <a :href="href" v-bind="itemProps.action" @click="navigate">
            <span :class="item.icon" />
            <span class="ml-2">{{ item.label }}</span>
          </a>
        </NuxtLink>
        <a v-else v-bind="itemProps.action">
          <span :class="item.icon" />
          <span class="ml-2">{{ item.label }}</span>
        </a>
      </template>
    </TieredMenu>
    <Popover v-if="authStore.isAuthenticated" ref="statusPopover" :pt="{ content: { class: 'p-1' } }" @show="onStatusPopoverShow" @hide="onStatusPopoverHide">
      <div ref="statusMenu">
        <StatusFlowSteps :deck="deck" :flow="statusFlow" @open-history="openHistory">
          <div class="flex flex-col min-w-36">
            <template v-if="canOpenHistory">
              <div v-if="listEntryLine.length" class="flex flex-col gap-0.5 px-3 pt-1.5 pb-1 text-xs tabular-nums text-gray-600 dark:text-gray-300">
                <span v-for="fact in listEntryLine" :key="fact">{{ fact }}</span>
              </div>
              <button
                type="button"
                class="flex items-center justify-between gap-3 px-3 py-1.5 rounded text-left text-sm hover:bg-gray-100 dark:hover:bg-gray-700 focus-visible:bg-gray-100 dark:focus-visible:bg-gray-700 transition-colors cursor-pointer"
                :class="showLogProgress ? 'font-semibold text-primary-700 dark:text-primary-300' : ''"
                @click="showLogProgress ? statusFlow.goTo('progress') : openHistory()"
              >
                <span class="flex items-center gap-2">
                  <i :class="['pi text-xs', showLogProgress ? 'pi-pen-to-square' : 'pi-history']" aria-hidden="true" />
                  {{ showLogProgress ? 'Log progress' : mediaWords(deck.mediaType).historyTitle }}
                </span>
                <i class="pi pi-chevron-right text-[10px]" aria-hidden="true" />
              </button>
              <div class="my-1 border-t border-gray-200 dark:border-gray-700" role="separator" />
            </template>
            <div class="flex flex-col" role="menu" aria-label="Set status">
              <button
                v-for="option in statusOptions"
                :key="option"
                type="button"
                role="menuitemradio"
                :aria-checked="option === currentStatus"
                class="flex items-center justify-between gap-3 px-3 py-1.5 rounded text-left text-sm hover:bg-gray-100 dark:hover:bg-gray-700 transition-colors cursor-pointer disabled:opacity-50 disabled:cursor-default"
                :class="option === currentStatus ? 'font-bold' : ''"
                :disabled="statusFlow.busy.value"
                @click="statusFlow.choose(option)"
              >
                <span>{{ getDeckStatusText(option) }}</span>
                <i v-if="option === currentStatus" class="pi pi-check text-xs" />
              </button>
            </div>
          </div>
        </StatusFlowSteps>
      </div>
    </Popover>
    <LoadingOverlay :visible="isRefreshingCoverage" message="Refreshing coverage…" />
  </div>
</template>

<style scoped>
  /* Ensure text wraps properly on small screens */
  .flex-1 {
    min-width: 0;
  }

  /* Add additional responsive behavior for small screens */
  @media (max-width: 640px) {
    .flex-1 > div > div {
      width: 100%;
    }
  }

  .title-clip-ellipsis {
    background: linear-gradient(to right, transparent, var(--p-card-background, var(--p-content-background)) 1.5rem);
  }

  .compact-card :deep(.p-card-body) {
    height: 100%;
  }

  .compact-card :deep(.p-card-content) {
    flex: 1 1 auto;
    min-height: 0;
  }

  .stat-row {
    padding: 0.2rem;
    margin-inline: -0.2rem;
    border-radius: var(--radius-sm);
    transition: background-color 0.2s;
  }

  .stat-row > span:last-child {
    flex-shrink: 0;
  }

  .utility-icon {
    color: var(--p-surface-400);
    transition: color 0.15s;
  }

  :is(button, a):hover > .utility-icon,
  :is(button, a):focus-visible > .utility-icon {
    color: var(--p-primary-color);
  }

  .stat-row:hover {
    background-color: rgba(183, 135, 243, 0.21);
  }

  :deep(.dark) .stat-row:hover {
    background-color: rgba(255, 255, 255, 0.05);
  }

  .ignore-overlay {
    animation: fadeIn 0.3s ease-in-out;
  }

  @keyframes fadeIn {
    from {
      opacity: 0;
    }
    to {
      opacity: 1;
    }
  }
</style>
