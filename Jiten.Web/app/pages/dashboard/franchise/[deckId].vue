<script setup lang="ts">
  import { ref, computed, watch, onMounted, onBeforeUnmount } from 'vue';
  import Button from 'primevue/button';
  import InputText from 'primevue/inputtext';
  import Select from 'primevue/select';
  import Message from 'primevue/message';
  import ProgressSpinner from 'primevue/progressspinner';
  import BuilderBoard from '~/components/dashboard/franchise/BuilderBoard.vue';
  import BuilderCommandBar from '~/components/dashboard/franchise/BuilderCommandBar.vue';
  import BuilderLinkPicker from '~/components/dashboard/franchise/BuilderLinkPicker.vue';
  import BuilderFloating from '~/components/dashboard/franchise/BuilderFloating.vue';
  import BuilderSeriesDialog from '~/components/dashboard/franchise/BuilderSeriesDialog.vue';
  import BuilderSettingsPanel from '~/components/dashboard/franchise/BuilderSettingsPanel.vue';
  import { SeriesKind, type SeriesRef } from '~/types/series';
  import type { Franchise, FranchiseEdge } from '~/types/types';
  import { getEdgeFlow, type LinkTone } from '~/utils/relationshipRoles';
  import { compareFranchiseRelease } from '~/utils/franchiseLayout';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { apiErrorMessage } from '~/utils/apiErrorMessage';
  import {
    activeEdges,
    activeMembers,
    applyLink,
    buildSaveRequest,
    builderChecks,
    builderSeriesFromFranchise,
    builderStateFromFranchise,
    byRelease,
    cardRemoveAction,
    chainSequels,
    checkLink,
    choiceForEdge,
    choiceToEdge,
    cutOffBy,
    discardPending,
    dropLink,
    layoutBoard,
    leaveGroup,
    lineInSeries,
    lineOf,
    linkSentence,
    linkSentenceText,
    linkToneClass,
    orderSeries,
    otherSeriesOfLine,
    pendingChanges,
    putLinesInSeries,
    revertLink,
    revertMembership,
    toBuilderDeck,
    type BuilderDeck,
    type BuilderSeries,
    type BuilderState,
    type SeriesDialogRequest,
  } from '~/utils/franchiseBuilder';

  definePageMeta({ middleware: ['auth-admin'] });

  type Point = { x: number; y: number };

  const route = useRoute();
  const anchorDeckId = Number(route.params.deckId);
  const { $api } = useNuxtApp();
  const localiseTitle = useLocaliseTitle();
  const confirm = useConfirm();

  const loading = ref(true);
  const loadError = ref('');
  const decks = ref<Record<number, BuilderDeck>>({});
  const series = ref<BuilderSeries[]>([]);
  const state = ref<BuilderState>({ board: [], edges: [], members: [] });
  const history = useUndoHistory(state);
  const { canUndo, canRedo, commit } = history;

  const title = (id: number) => (decks.value[id] ? localiseTitle(decks.value[id]!) : `#${id}`);
  const year = (id: number) => decks.value[id]?.year ?? null;
  const deck = (id: number) => decks.value[id];
  const seriesById = computed(() => new Map(series.value.map((s) => [s.seriesId, s])));
  const seriesName = (id: number) => seriesById.value.get(id)?.name ?? `Series #${id}`;
  const onBoard = (id: number) => state.value.board.includes(id);
  const releaseOrder = (ids: number[]) => byRelease(ids, { year, title });
  const compareRelease = (a: number, b: number) =>
    compareFranchiseRelease({ deckId: a, releaseDate: deck(a)?.releaseDate ?? '' }, { deckId: b, releaseDate: deck(b)?.releaseDate ?? '' });

  function addDecks(list: BuilderDeck[]) {
    const next = { ...decks.value };
    for (const d of list) next[d.deckId] ??= d;
    decks.value = next;
  }

  function applyFranchise(f: Franchise, keepBoard: number[] = []) {
    const next = { ...decks.value };
    for (const n of f.nodes) next[n.deckId] = toBuilderDeck(n);
    decks.value = next;
    const fromServer = builderSeriesFromFranchise(f);
    const serverIds = new Set(fromServer.map((s) => s.seriesId));
    const localOnly = series.value.filter((s) => !serverIds.has(s.seriesId)).map((s) => ({ ...s, outsideCount: undefined }));
    series.value = [...fromServer, ...localOnly];
    const fresh = builderStateFromFranchise(f);
    fresh.board = [...new Set([...fresh.board, ...keepBoard.filter((id) => next[id])])];
    history.reset(fresh);
  }

  const anchorTitle = computed(() => (decks.value[anchorDeckId] ? title(anchorDeckId) : `Deck #${anchorDeckId}`));
  useHead({ title: computed(() => `${anchorTitle.value} - Franchise builder - Jiten`) });

  async function load() {
    loading.value = true;
    loadError.value = '';
    try {
      applyFranchise(await $api<Franchise>(`admin/franchise-builder/${anchorDeckId}`));
    } catch (e) {
      loadError.value = apiErrorMessage(e, 'The franchise could not be loaded.');
    } finally {
      loading.value = false;
    }
  }

  function undo() {
    if (!history.undo()) return;
    closeFloating();
    flash('Undone');
  }

  function redo() {
    if (!history.redo()) return;
    closeFloating();
    flash('Redone');
  }

  const changes = computed(() => pendingChanges(state.value));
  const check = (edge: FranchiseEdge, ignoreId: number | null) => checkLink(state.value, edge, title, ignoreId);

  const toastMsg = ref<{ text: string; undo: boolean } | null>(null);
  let toastTimer: ReturnType<typeof setTimeout> | null = null;
  function flash(text: string, withUndo = false) {
    toastMsg.value = { text, undo: withUndo };
    if (toastTimer) clearTimeout(toastTimer);
    toastTimer = setTimeout(() => (toastMsg.value = null), 5000);
  }

  const boardWidth = ref(960);
  const boardRef = ref<InstanceType<typeof BuilderBoard> | null>(null);
  const layout = computed(() =>
    layoutBoard({
      board: state.value.board,
      edges: activeEdges(state.value),
      members: activeMembers(state.value),
      series: series.value,
      year,
      title,
      compareRelease,
      width: boardWidth.value,
    })
  );

  const seriesOnly = computed(() => series.value.filter((s) => s.kind === SeriesKind.Series));
  const settingsOnly = computed(() => series.value.filter((s) => s.kind === SeriesKind.Setting));
  const seriesOrder = computed(() => orderSeries(series.value, () => null));
  const seriesOptions = computed(() => seriesOrder.value.map((s) => ({ value: s.seriesId, label: s.name })));

  const stats = computed(() => {
    const links = activeEdges(state.value).length;
    const inSeries = layout.value.sections.reduce((s, sec) => s + sec.deckCount, 0);
    const unlinked = layout.value.bands.find((b) => b.kind === 'unlinked')?.ids.length ?? 0;
    const n = state.value.board.length;
    return [
      `${n} ${n === 1 ? 'deck' : 'decks'} on the board`,
      `${links} story ${links === 1 ? 'link' : 'links'}`,
      `${inSeries} in a series`,
      ...(unlinked ? [`${unlinked} not linked`] : []),
    ].join(' · ');
  });

  const selected = ref<number[]>([]);
  const linkFrom = ref<number | null>(null);

  const { drag, beginDrag, stopDrag } = useBuilderDrag({
    onStart: closeFloating,
    onMove: (clientX) => boardRef.value?.autoScroll(clientX),
    onDrop: (deckId, target, at) => {
      if (target.kind === 'deck') openPicker(deckId, target.id, at);
      else if (target.kind === 'series') addToSeries(target.id, deckId);
      else if (target.kind === 'setting') addToSetting(target.id, deckId);
      else addToBoard([deckId]);
    },
    onClick: (deckId, press) => {
      if (press.handle && linkFrom.value == null) startLink(deckId);
      else cardClick(deckId, press.additive);
    },
  });

  const hint = computed(() => {
    const d = drag.value;
    if (d?.moved) {
      const t = d.target;
      if (t?.kind === 'deck') return `Release to choose how ${title(d.deckId)} relates to ${title(t.id)}.`;
      if (t?.kind === 'series') return seriesDropHint(t.id, d.deckId);
      if (t?.kind === 'setting') return `Release to add ${title(d.deckId)} to the ${seriesName(t.id)} setting.`;
      if (t?.kind === 'board') return `Release to put ${title(d.deckId)} on the board.`;
      return `Drop ${title(d.deckId)} on the deck it relates to, or on a series rail.`;
    }
    if (linkFrom.value != null) return `Linking ${title(linkFrom.value)}: pick the deck it relates to, or press Esc.`;
    return 'Drag a deck onto the one it relates to, or onto a series rail to move it there. Arrows point forward: the original on the left, what follows on the right.';
  });

  function cardClick(id: number, additive: boolean) {
    if (linkFrom.value != null) {
      const from = linkFrom.value;
      linkFrom.value = null;
      if (from !== id) openPicker(from, id, boardRef.value?.cardAnchor(id) ?? { x: 200, y: 200 });
      return;
    }
    if (additive) selected.value = selected.value.includes(id) ? selected.value.filter((x) => x !== id) : [...selected.value, id];
    else if (selected.value.length === 1 && selected.value[0] === id) selected.value = [];
    else selected.value = [id];
  }

  function startLink(id: number) {
    linkFrom.value = id;
    selected.value = [];
  }

  function onCardKey(id: number, ev: KeyboardEvent) {
    if (ev.key === 'Enter') {
      ev.preventDefault();
      if (linkFrom.value != null) cardClick(id, false);
      else startLink(id);
    } else if (ev.key === ' ') {
      ev.preventDefault();
      cardClick(id, true);
    } else if (ev.key === 'Delete' || ev.key === 'Backspace') {
      ev.preventDefault();
      const action = cardRemoveAction(state.value, layout.value, id);
      if (action?.kind === 'leave') takeOutOfSeries(action.seriesId, id);
      else removeFromBoard([id]);
    }
  }

  function clearSelection() {
    selected.value = [];
    linkFrom.value = null;
  }

  function addToBoard(ids: number[]) {
    const fresh = ids.filter((id) => !onBoard(id));
    if (!fresh.length) return;
    commit((s) => s.board.push(...fresh));
    flash(fresh.length === 1 ? `Added ${title(fresh[0]!)} to the board` : `Added ${fresh.length} decks to the board`, true);
  }

  const removable = (id: number) => cardRemoveAction(state.value, layout.value, id)?.kind === 'remove';

  function removeFromBoard(ids: number[]) {
    const blocked = ids.filter((id) => !removable(id));
    if (blocked.length) {
      flash(`${blocked.map(title).join(', ')} still ${blocked.length === 1 ? 'has' : 'have'} links or memberships. Remove those first.`);
      return;
    }
    commit((s) => (s.board = s.board.filter((id) => !ids.includes(id))));
    selected.value = selected.value.filter((id) => !ids.includes(id));
    flash(`Took ${ids.length === 1 ? title(ids[0]!) : `${ids.length} decks`} off the board`, true);
  }

  function seriesDropHint(seriesId: number, id: number) {
    const via = lineInSeries(state.value, seriesId, id);
    if (via.length) return `${title(id)} is already in ${seriesName(seriesId)}${via[0] !== id ? ` through ${via.map(title).join(', ')}` : ''}.`;
    const n = lineOf(state.value, id).length - 1;
    const linked = n > 0 ? ` and the ${n} ${n === 1 ? 'deck' : 'decks'} linked to it` : '';
    const from = otherSeriesOfLine(state.value, series.value, seriesId, id);
    if (from.length) return `Release to move ${title(id)}${linked} from ${from.map(seriesName).join(', ')} to ${seriesName(seriesId)}.`;
    return `Release to add ${title(id)}${linked} to ${seriesName(seriesId)}.`;
  }

  /** Returns the decks that joined; nothing is committed when all were already in. */
  function putInSeries(seriesId: number, ids: number[]): number[] {
    let joined: number[] = [];
    commit((s) => {
      joined = putLinesInSeries(s, series.value, ids, seriesId, releaseOrder);
      return joined.length > 0;
    });
    return joined;
  }

  function addToSeries(seriesId: number, id: number) {
    const from = otherSeriesOfLine(state.value, series.value, seriesId, id);
    if (!putInSeries(seriesId, [id]).length) {
      flash(`${title(id)} is already in ${seriesName(seriesId)}`);
      return;
    }
    flash(from.length ? `Moved ${title(id)} to ${seriesName(seriesId)}` : `Added ${title(id)} to ${seriesName(seriesId)}`, true);
  }

  function addToSetting(seriesId: number, id: number) {
    if (!putInSeries(seriesId, [id]).length) {
      flash(`${title(id)} is already in the ${seriesName(seriesId)} setting`);
      return;
    }
    flash(`Added ${title(id)} to the ${seriesName(seriesId)} setting`, true);
  }

  function selectionToSeries(seriesId: number) {
    const joined = putInSeries(seriesId, selected.value);
    if (!joined.length) {
      flash(`Already in ${seriesName(seriesId)}`);
      return;
    }
    selected.value = [];
    flash(`Put ${joined.length} ${joined.length === 1 ? 'line' : 'lines'} in ${seriesName(seriesId)}`, true);
  }

  function takeOutOfSeries(seriesId: number, id: number) {
    const via = lineInSeries(state.value, seriesId, id);
    if (!via.length) return;
    const isLine = lineOf(state.value, id).length > 1;
    commit((s) => via.forEach((v) => leaveGroup(s, seriesId, v)));
    flash(`Took ${isLine ? `the decks linked to ${title(via[0]!)}` : title(id)} out of ${seriesName(seriesId)}`, true);
  }

  function chainSelection() {
    let result: ReturnType<typeof chainSequels> = { made: 0, skipped: [] };
    const ordered = releaseOrder(selected.value);
    commit((s) => {
      result = chainSequels(s, ordered);
    });
    selected.value = [];
    const { made, skipped } = result;
    const skippedText = skipped.map(([a, b]) => `${title(a)} and ${title(b)}`).join(', ');
    flash(`Made ${made} ${made === 1 ? 'link' : 'links'}${skipped.length ? `; skipped ${skippedText}` : ''}`, true);
  }

  const pickedSeries = ref<number | null>(null);
  const selectionSeries = computed({
    get: () => (seriesOptions.value.some((o) => o.value === pickedSeries.value) ? pickedSeries.value : (seriesOptions.value[0]?.value ?? null)),
    set: (v: number | null) => (pickedSeries.value = v),
  });

  const selectionOrdered = computed(() => releaseOrder(selected.value));
  const selectionRemovable = computed(() => selected.value.every(removable));

  const picker = ref<{ subject: number; object: number; editId: number | null; key: string; anchor: Point; returnTo: number } | null>(null);

  function openPicker(subject: number, object: number, anchor: Point, editId: number | null = null, key?: string) {
    closeFloating();
    selected.value = [];
    picker.value = { subject, object, editId, anchor, returnTo: object, key: key ?? ((year(subject) ?? 0) >= (year(object) ?? 0) ? 'sequel' : 'prequel') };
  }

  function closePicker() {
    const back = picker.value?.returnTo;
    picker.value = null;
    if (back != null) boardRef.value?.focusCard(back);
  }

  function onPickerCommit(edge: FranchiseEdge, replaces: { id: number } | null) {
    const editId = picker.value?.editId ?? null;
    commit((s) => {
      if (editId != null && (!replaces || replaces.id !== editId)) dropLink(s, editId);
      applyLink(s, edge, replaces ? s.edges.find((e) => e.id === replaces.id) : null);
    });
    flash(`${editId != null ? 'Updated' : 'Linked'}: ${linkSentenceText(edge, title)}`, true);
    closePicker();
  }

  const edgePop = ref<{ id: number; anchor: Point } | null>(null);
  const popEdge = computed(() => (edgePop.value ? state.value.edges.find((e) => e.id === edgePop.value!.id) : undefined));
  const popCut = computed(() => (edgePop.value ? cutOffBy(state.value, series.value, edgePop.value.id) : []));

  function edgeChangeType() {
    const e = popEdge.value;
    const anchor = edgePop.value?.anchor;
    if (!e || !anchor) return;
    const r = choiceForEdge(e);
    if (!r) return;
    openPicker(r.subject, r.object, anchor, e.id, r.choice.key);
  }

  function swapEdge(id: number) {
    const e = state.value.edges.find((x) => x.id === id);
    const r = e && choiceForEdge(e);
    if (!e || !r) return;
    const next = choiceToEdge(r.choice, r.object, r.subject);
    const v = check(next, e.id);
    if (v.error) {
      flash(v.error);
      return;
    }
    commit((s) => {
      dropLink(s, id);
      applyLink(s, next, v.replaces ? s.edges.find((x) => x.id === v.replaces!.id) : null);
    });
    flash(`Swapped: ${linkSentenceText(next, title)}`, true);
  }

  function removeEdge(id: number) {
    const e = state.value.edges.find((x) => x.id === id);
    if (!e) return;
    commit((s) => dropLink(s, id));
    flash(`Removed: ${linkSentenceText(e, title)}`, true);
    edgePop.value = null;
  }

  const railPop = ref<{ seriesId: number; index: number; anchor: Point; confirmDeck: number | null } | null>(null);
  const railNode = computed(() => {
    const p = railPop.value;
    if (!p) return null;
    return layout.value.sections.find((s) => s.seriesId === p.seriesId)?.nodes[p.index] ?? null;
  });

  function leaveSeries(seriesId: number, ids: number[]) {
    commit((s) => ids.forEach((id) => leaveGroup(s, seriesId, id)));
    flash(`Took ${ids.length === 1 ? title(ids[0]!) : `${ids.length} decks`} out of ${seriesName(seriesId)}`, true);
    railPop.value = null;
  }

  const memberStatus = (seriesId: number, deckId: number) => state.value.members.find((m) => m.seriesId === seriesId && m.deckId === deckId)?.status;

  function openEdgePop(id: number, anchor: Point) {
    closeFloating();
    edgePop.value = { id, anchor };
  }

  function openRailPop(seriesId: number, index: number, anchor: Point) {
    closeFloating();
    railPop.value = { seriesId, index, anchor, confirmDeck: null };
  }

  function closeFloating() {
    if (picker.value) picker.value = null;
    edgePop.value = null;
    railPop.value = null;
  }

  const seriesDialog = ref<SeriesDialogRequest | null>(null);

  function openCreateSeries(kind: SeriesKind) {
    seriesDialog.value = { mode: 'create', kind, seriesId: null, name: '' };
  }

  function openRename(seriesId: number) {
    const s = seriesById.value.get(seriesId);
    if (!s) return;
    seriesDialog.value = { mode: 'rename', kind: s.kind, seriesId, name: s.name };
  }

  function addExistingSeries(entry: SeriesRef) {
    if (seriesById.value.has(entry.seriesId)) return;
    series.value = [...series.value, { seriesId: entry.seriesId, name: entry.name, kind: entry.kind }];
  }

  function onSeriesPicked(entry: SeriesRef) {
    addExistingSeries(entry);
    flash(`${entry.name} is on the board. Drop decks on it to add them.`);
    seriesDialog.value = null;
  }

  function onSeriesCreated(created: SeriesRef) {
    addExistingSeries(created);
    flash(`Created ${created.name}`);
    seriesDialog.value = null;
  }

  function onSeriesRenamed(updated: SeriesRef) {
    const id = seriesDialog.value?.seriesId;
    series.value = series.value.map((s) => (s.seriesId === id ? { ...s, name: updated.name } : s));
    flash(`Renamed to ${updated.name}`);
    seriesDialog.value = null;
  }

  const searchQuery = ref('');
  const { suggestions: deckSuggestions, isLoading: searching, fetchSuggestions: searchDecks } = useMediaSuggestions({ limit: 20 });
  watch(searchQuery, (q) => searchDecks(q.trim()));
  watch(deckSuggestions, (list) => addDecks(list.map(toBuilderDeck)));
  const searchResults = computed(() => deckSuggestions.value.map((s) => s.deckId));
  const notOnBoard = computed(() => searchResults.value.filter((id) => !onBoard(id)));

  function runLinks(edges: FranchiseEdge[]) {
    commit((s) => {
      for (const e of edges) {
        const v = checkLink(s, e, title);
        if (!v.error) applyLink(s, e, v.replaces);
      }
    });
    flash(edges.length > 1 ? `Made ${edges.length} changes` : `Linked: ${linkSentenceText(edges[0]!, title)}`, true);
  }

  const firstSeries = computed(() => seriesOrder.value[0] ?? null);

  const checks = computed(() =>
    builderChecks(state.value, series.value, year).map((c) => {
      switch (c.kind) {
        case 'outside': {
          const names = c.lines.map((l) => `${title(releaseOrder(l)[0]!)} (${l.length})`).join(', ');
          const root = firstSeries.value;
          return {
            tone: 'warn',
            tag: 'Outside',
            text: `${c.lines.length} story ${c.lines.length === 1 ? "line isn't" : "lines aren't"} in a series: ${names}. Jiten will show ${c.lines.length === 1 ? 'it' : 'each'} as a separate franchise.`,
            action: root
              ? {
                  label: `Add ${c.lines.length === 1 ? 'it' : `the ${c.lines.length} lines`} to ${root.name}`,
                  run: () => {
                    putInSeries(root.seriesId, c.lines.flat());
                    flash(`Added ${c.lines.length} ${c.lines.length === 1 ? 'line' : 'lines'} to ${root.name}`, true);
                  },
                }
              : null,
          };
        }
        case 'unlinked':
          return {
            tone: 'warn',
            tag: 'Unlinked',
            text: `${c.deckIds.map(title).join(', ')} ${c.deckIds.length === 1 ? "isn't" : "aren't"} linked to anything. Link spin-offs and sequels to their line; drop main entries on a series rail.`,
            action: null,
          };
        case 'branch':
          return {
            tone: 'info',
            tag: 'Branch',
            text: `${title(c.deckId)} has ${c.sequels.length} sequels: ${c.sequels.map(title).join(', ')}. That's fine if the story branches. Otherwise one may be a side story.`,
            action: null,
          };
        case 'order': {
          const flow = getEdgeFlow(c.edge);
          return {
            tone: 'info',
            tag: 'Order',
            text: `${title(flow.from)} (${year(flow.from)}) is set as a prequel to ${title(flow.to)} (${year(flow.to)}) but came out later. Right for a prequel; swap it if not.`,
            action: { label: 'Swap direction', run: () => swapEdge(c.edge.id) },
          };
        }
        case 'ok':
          return { tone: 'ok', tag: 'Good', text: `All ${c.deckCount} decks on the board form one franchise.`, action: null };
      }
    })
  );

  const checkTagClass: Record<string, string> = {
    warn: 'bg-amber-100 text-amber-900 dark:bg-amber-950 dark:text-amber-300',
    info: 'bg-gray-100 text-gray-600 dark:bg-gray-800 dark:text-gray-300',
    ok: 'bg-green-100 text-green-800 dark:bg-green-950 dark:text-green-300',
  };

  function revertChange(change: (typeof changes.value)[number]) {
    commit((s) => {
      if (change.kind === 'link') revertLink(s, change.edge.id);
      else revertMembership(s, change.member.seriesId, change.member.deckId);
    });
  }

  function discardAll() {
    commit((s) => discardPending(s));
    flash('Discarded all unsaved changes', true);
  }

  function memberChangeText(seriesId: number, deckId: number, joining: boolean) {
    const s = seriesById.value.get(seriesId);
    const group = s?.kind === SeriesKind.Setting ? `the ${seriesName(seriesId)} setting` : seriesName(seriesId);
    return `${title(deckId)} ${joining ? 'joins' : 'leaves'} ${group}`;
  }

  const saving = ref(false);
  const saveError = ref('');

  async function save() {
    const n = changes.value.length;
    if (!n || saving.value) return;
    saving.value = true;
    saveError.value = '';
    try {
      const saved = await $api<Franchise>('admin/franchise-builder/save', {
        method: 'POST',
        body: buildSaveRequest(anchorDeckId, state.value),
      });
      applyFranchise(saved, state.value.board);
      closeFloating();
      flash(`Saved ${n} ${n === 1 ? 'change' : 'changes'}`);
    } catch (e) {
      saveError.value = apiErrorMessage(e, 'Saving failed. Nothing was changed.');
    } finally {
      saving.value = false;
    }
  }

  onBeforeRouteLeave(() => {
    if (!changes.value.length) return true;
    return new Promise<boolean>((resolve) => {
      let settled = false;
      const done = (v: boolean) => {
        if (settled) return;
        settled = true;
        resolve(v);
      };
      confirm.require({
        header: 'Leave without saving?',
        message: `${changes.value.length} unsaved ${changes.value.length === 1 ? 'change' : 'changes'} will be lost.`,
        acceptLabel: 'Leave',
        rejectLabel: 'Stay',
        acceptClass: 'p-button-danger',
        accept: () => done(true),
        reject: () => done(false),
        onHide: () => done(false),
      });
    });
  });

  function onKeydown(ev: KeyboardEvent) {
    const typing = (ev.target as Element | null)?.matches?.('input, textarea, [contenteditable="true"]') ?? false;
    if ((ev.ctrlKey || ev.metaKey) && ev.key.toLowerCase() === 'z' && !typing) {
      ev.preventDefault();
      if (ev.shiftKey) redo();
      else undo();
      return;
    }
    if ((ev.ctrlKey || ev.metaKey) && ev.key.toLowerCase() === 'y' && !typing) {
      ev.preventDefault();
      redo();
      return;
    }
    if (ev.key === '/' && !typing) {
      ev.preventDefault();
      document.getElementById('fb-cmd')?.focus();
      return;
    }
    if (ev.key === 'Escape' && !typing) {
      if (drag.value) {
        stopDrag();
        return;
      }
      closeFloating();
      clearSelection();
    }
  }

  onMounted(() => {
    load();
    window.addEventListener('keydown', onKeydown);
  });
  onBeforeUnmount(() => {
    window.removeEventListener('keydown', onKeydown);
    if (toastTimer) clearTimeout(toastTimer);
  });

  const legend: { tone: LinkTone; label: string; directed: boolean }[] = [
    { tone: 'sequel', label: 'sequel', directed: true },
    { tone: 'side', label: 'side story', directed: true },
    { tone: 'spin', label: 'spin-off', directed: true },
    { tone: 'fan', label: 'fandisc', directed: true },
    { tone: 'adapt', label: 'adaptation', directed: true },
    { tone: 'alt', label: 'no direction', directed: false },
  ];

  const panelClass = 'rounded-md border border-gray-200 bg-white dark:border-gray-800 dark:bg-gray-900';
</script>

<template>
  <div class="container mx-auto px-4 pb-8 pt-4">
    <header class="mb-4 flex flex-wrap items-end justify-between gap-3 border-b border-gray-200 pb-3 dark:border-gray-800">
      <div class="flex min-w-0 items-start gap-2">
        <Button icon="pi pi-arrow-left" text aria-label="Back to the deck editor" @click="navigateTo(`/dashboard/media/${anchorDeckId}`)" />
        <div class="min-w-0">
          <div class="text-xs text-gray-500 dark:text-gray-400">
            <NuxtLink to="/dashboard" class="hover:underline">Dashboard</NuxtLink> ›
            <NuxtLink to="/dashboard/series" class="hover:underline">Series</NuxtLink> › Franchise builder
          </div>
          <h1 class="m-0 text-2xl font-bold" v-bind="japaneseTextAttrs(anchorTitle)">{{ anchorTitle }}</h1>
          <p v-if="!loading && !loadError" class="m-0 mt-0.5 text-[13px] tabular-nums text-gray-500 dark:text-gray-400">{{ stats }}</p>
        </div>
      </div>
      <div class="flex flex-wrap gap-2">
        <Tooltip content="Undo (Ctrl+Z)">
          <Button severity="secondary" outlined :disabled="!canUndo" @click="undo">
            <Icon name="material-symbols:undo-rounded" />
            Undo
          </Button>
        </Tooltip>
        <Tooltip content="Redo (Ctrl+Shift+Z)">
          <Button severity="secondary" outlined :disabled="!canRedo" @click="redo">
            <Icon name="material-symbols:redo-rounded" />
            Redo
          </Button>
        </Tooltip>
        <Button
          :label="changes.length ? `Save ${changes.length} ${changes.length === 1 ? 'change' : 'changes'}` : 'Saved'"
          :disabled="!changes.length"
          :loading="saving"
          @click="save"
        />
      </div>
    </header>

    <div v-if="loading" class="flex h-64 items-center justify-center gap-3 text-gray-500 dark:text-gray-400">
      <ProgressSpinner style="width: 32px; height: 32px" />
      Loading the franchise…
    </div>

    <Message v-else-if="loadError" severity="error" :closable="false">
      <div class="flex flex-wrap items-center gap-3">
        <span>{{ loadError }}</span>
        <Button label="Try again" size="small" severity="secondary" @click="load" />
      </div>
    </Message>

    <div v-else class="grid items-start gap-4 lg:grid-cols-[290px_minmax(0,1fr)]">
      <aside class="order-2 flex min-w-0 flex-col gap-4 lg:order-1 lg:sticky lg:top-4">
        <section :class="panelClass" class="flex flex-col gap-2.5 p-3.5" aria-labelledby="fb-add-h">
          <h2 id="fb-add-h" class="m-0 text-sm font-bold">Add decks</h2>
          <label for="fb-search" class="sr-only">Search decks</label>
          <InputText id="fb-search" v-model="searchQuery" type="search" placeholder="Title in English or Japanese" autocomplete="off" class="w-full" />
          <div class="flex items-center justify-between gap-2 text-xs text-gray-500 dark:text-gray-400">
            <span>{{
              searching ? 'Searching…' : searchQuery.trim().length >= 2 ? `${searchResults.length} ${searchResults.length === 1 ? 'deck' : 'decks'}` : ''
            }}</span>
            <button
              v-if="notOnBoard.length >= 2"
              type="button"
              class="text-xs font-medium text-primary-700 hover:underline dark:text-primary-300"
              @click="addToBoard(notOnBoard)"
            >
              Add all {{ notOnBoard.length }}
            </button>
          </div>
          <ul class="m-0 flex max-h-72 list-none flex-col gap-0.5 overflow-y-auto p-0 lg:max-h-[40vh]">
            <li
              v-for="id in searchResults"
              :key="id"
              class="grid grid-cols-[auto_auto_minmax(0,1fr)_auto] items-center gap-2 rounded px-1.5 py-1.5 hover:bg-gray-100 dark:hover:bg-gray-800"
            >
              <Tooltip :content="onBoard(id) ? 'Already on the board' : 'Drag onto a deck or a series rail'">
                <span
                  class="fb-handle grid h-7 w-5 place-items-center text-gray-400"
                  :class="onBoard(id) ? 'cursor-default opacity-40' : 'cursor-grab'"
                  aria-hidden="true"
                  @pointerdown="!onBoard(id) && beginDrag('result', id, $event)"
                >
                  <Icon name="material-symbols:drag-indicator" />
                </span>
              </Tooltip>
              <img :src="coverUrl(deck(id)!.coverName)" alt="" class="h-12 w-8 rounded-xs object-cover" loading="lazy" draggable="false" />
              <div class="min-w-0">
                <div class="break-words text-[13px] font-medium leading-tight" v-bind="japaneseTextAttrs(title(id))">{{ title(id) }}</div>
                <div class="break-words text-[11.5px] text-gray-500 dark:text-gray-400">
                  {{ getMediaTypeText(deck(id)!.mediaType) }} · #{{ id
                  }}<template v-if="deck(id)!.originalTitle !== title(id)">
                    · <span lang="ja">{{ deck(id)!.originalTitle }}</span></template
                  ><template v-else-if="deck(id)!.romajiTitle && deck(id)!.romajiTitle !== title(id)"> · {{ deck(id)!.romajiTitle }}</template>
                </div>
              </div>
              <span v-if="onBoard(id)" class="text-[11.5px] text-gray-500 dark:text-gray-400">On board</span>
              <Button v-else label="Add" size="small" severity="secondary" outlined :aria-label="`Add ${title(id)} to the board`" @click="addToBoard([id])" />
            </li>
            <li v-if="searchQuery.trim().length >= 2 && !searching && !searchResults.length" class="px-2 py-2 text-xs text-gray-500 dark:text-gray-400">
              No deck matches. Try the Japanese title or fewer words.
            </li>
          </ul>
          <p class="m-0 border-t border-gray-200 pt-2.5 text-xs text-gray-500 dark:border-gray-800 dark:text-gray-400">
            Drag a result onto a deck on the board to add it and link it in one move.
          </p>
        </section>

        <BuilderSettingsPanel
          :settings="settingsOnly"
          :state="state"
          :title="title"
          :order="releaseOrder"
          :drop-target="drag?.target ?? null"
          @create="openCreateSeries(SeriesKind.Setting)"
          @rename="openRename"
          @pick="addExistingSeries"
          @add="addToSetting"
          @leave="(seriesId, deckId) => commit((s) => leaveGroup(s, seriesId, deckId))"
        />
      </aside>

      <main class="order-1 flex min-w-0 flex-col gap-4 lg:order-2">
        <section :class="panelClass" class="min-w-0" aria-label="Franchise board">
          <div class="flex flex-wrap items-center justify-between gap-x-5 gap-y-2 border-b border-gray-200 px-3.5 py-2.5 dark:border-gray-800">
            <p class="m-0 min-w-0 flex-[1_1_320px] text-[13px] text-gray-600 dark:text-gray-400" aria-live="polite">{{ hint }}</p>
            <div class="flex flex-wrap items-center gap-3">
              <ul class="m-0 flex list-none flex-wrap gap-x-3 gap-y-1 p-0 text-xs text-gray-500 dark:text-gray-400" aria-label="Link colours">
                <li v-for="l in legend" :key="l.tone" class="flex items-center gap-1.5">
                  <svg viewBox="0 0 26 8" class="h-2 w-[26px]" :class="linkToneClass[l.tone]" aria-hidden="true">
                    <template v-if="l.directed">
                      <path d="M1 4h18" stroke="currentColor" stroke-width="2" fill="none" />
                      <path d="M18 0l7 4-7 4z" fill="currentColor" />
                    </template>
                    <path v-else d="M1 4h24" stroke="currentColor" stroke-width="2" stroke-dasharray="6 4" fill="none" />
                  </svg>
                  {{ l.label }}
                </li>
              </ul>
              <Button size="small" severity="secondary" outlined @click="openCreateSeries(SeriesKind.Series)">
                <Icon name="material-symbols:add-rounded" />
                New series
              </Button>
            </div>
          </div>

          <div
            v-if="selected.length"
            class="flex flex-wrap items-center gap-2 border-b border-gray-200 bg-primary-50 px-3.5 py-2 text-[13px] dark:border-gray-800 dark:bg-primary-950/40"
          >
            <span class="font-medium">{{ selected.length }} selected</span>
            <Button v-if="selected.length >= 2" label="Chain as sequels" size="small" severity="secondary" outlined @click="chainSelection" />
            <Button v-else label="Link to…" size="small" severity="secondary" outlined @click="startLink(selected[0]!)" />
            <template v-if="seriesOptions.length">
              <Select
                v-if="seriesOptions.length > 1"
                v-model="selectionSeries"
                :options="seriesOptions"
                option-label="label"
                option-value="value"
                size="small"
                aria-label="Series to put the selection in"
              />
              <Button
                :label="seriesOptions.length > 1 ? 'Put in series' : `Put in ${seriesOptions[0]!.label}`"
                size="small"
                severity="secondary"
                outlined
                :disabled="selectionSeries == null"
                @click="selectionSeries != null && selectionToSeries(selectionSeries)"
              />
            </template>
            <Button v-if="selectionRemovable" label="Take off the board" size="small" severity="secondary" text @click="removeFromBoard(selected)" />
            <Button label="Clear" size="small" severity="secondary" text @click="clearSelection" />
            <span class="min-w-0 text-xs text-gray-600 [overflow-wrap:anywhere] dark:text-gray-400">
              <template v-if="selected.length >= 2">{{ selectionOrdered.map(title).join(' → ') }} (release order)</template>
              <template v-else>Shift-click more decks to link several at once.</template>
            </span>
          </div>

          <BuilderBoard
            ref="boardRef"
            :layout="layout"
            :state="state"
            :series-name="seriesName"
            :deck="deck"
            :title="title"
            :selected="selected"
            :link-from="linkFrom"
            :drop-target="drag?.moved ? drag.target : null"
            :drag-source="drag?.moved && drag.source === 'card' ? drag.deckId : null"
            :ghost="drag?.moved ? { x: drag.x, y: drag.y } : null"
            :focus-edge-id="edgePop?.id ?? null"
            @card-down="(id, ev) => beginDrag('card', id, ev)"
            @card-key="onCardKey"
            @remove="(id) => removeFromBoard([id])"
            @leave="takeOutOfSeries"
            @edge="openEdgePop"
            @rail-node="openRailPop"
            @rename="openRename"
            @background="clearSelection"
            @resize="(w) => (boardWidth = w)"
          />
          <div v-if="!state.board.length" class="px-4 py-6 text-center text-sm text-gray-500 dark:text-gray-400">
            <b class="block text-gray-900 dark:text-gray-100">The board is empty</b>
            Search for a deck on the left and add it, or drag it here.
          </div>
          <div
            v-if="!seriesOnly.length && state.board.length"
            class="border-t border-gray-200 px-3.5 py-2 text-[13px] text-gray-600 dark:border-gray-800 dark:text-gray-400"
          >
            This franchise has no series yet.
            <button type="button" class="font-medium text-primary-700 hover:underline dark:text-primary-300" @click="openCreateSeries(SeriesKind.Series)">
              Create one
            </button>
            to group its story lines.
          </div>

          <BuilderCommandBar :decks="decks" :board="state.board" :title="title" :check="check" @found="addDecks" @run="runLinks" @notice="flash" />
        </section>

        <Message v-if="saveError" severity="error" closable @close="saveError = ''"> <span class="font-medium">Not saved.</span> {{ saveError }} </Message>

        <div class="grid gap-4 xl:grid-cols-[minmax(0,1.3fr)_minmax(0,1fr)]">
          <section :class="panelClass" class="min-w-0 p-3.5" aria-labelledby="fb-chg-h">
            <div class="mb-2 flex items-center justify-between gap-2">
              <h2 id="fb-chg-h" class="m-0 text-sm font-bold">Unsaved changes</h2>
              <Button v-if="changes.length" label="Discard all" size="small" severity="secondary" text @click="discardAll" />
            </div>
            <ul class="m-0 flex list-none flex-col p-0">
              <li v-if="!changes.length" class="py-2 text-[13px] text-gray-500 dark:text-gray-400">
                No unsaved changes. Links and memberships you change on the board are listed here until you save.
              </li>
              <li
                v-for="c in changes"
                :key="c.kind === 'link' ? `l${c.edge.id}` : `m${c.member.seriesId}-${c.member.deckId}`"
                class="grid grid-cols-[auto_minmax(0,1fr)_auto] items-start gap-2.5 border-t border-gray-200 py-2 text-[13px] first:border-t-0 dark:border-gray-800"
              >
                <template v-if="c.kind === 'link'">
                  <span
                    class="w-4 text-center font-bold"
                    :class="c.edge.status === 'new' ? 'text-green-700 dark:text-green-400' : 'text-red-700 dark:text-red-400'"
                    :aria-label="c.edge.status === 'new' ? 'Added' : 'Removed'"
                  >
                    {{ c.edge.status === 'new' ? '+' : '−' }}
                  </span>
                  <span :class="c.edge.status === 'removed' ? 'text-gray-500 line-through dark:text-gray-400' : ''">
                    <b class="font-medium">{{ title(linkSentence(c.edge).first) }}</b> {{ linkSentence(c.edge).verb }}
                    <b class="font-medium">{{ title(linkSentence(c.edge).second) }}</b>
                  </span>
                </template>
                <template v-else>
                  <span
                    class="w-4 text-center font-bold"
                    :class="c.member.status === 'new' ? 'text-green-700 dark:text-green-400' : 'text-red-700 dark:text-red-400'"
                    :aria-label="c.member.status === 'new' ? 'Added' : 'Removed'"
                  >
                    {{ c.member.status === 'new' ? '+' : '−' }}
                  </span>
                  <span>{{ memberChangeText(c.member.seriesId, c.member.deckId, c.member.status === 'new') }}</span>
                </template>
                <Button label="Revert" size="small" severity="secondary" text @click="revertChange(c)" />
              </li>
            </ul>
          </section>

          <section :class="panelClass" class="min-w-0 p-3.5" aria-labelledby="fb-chk-h">
            <h2 id="fb-chk-h" class="m-0 mb-2 text-sm font-bold">Checks</h2>
            <ul class="m-0 flex list-none flex-col p-0">
              <li v-if="!checks.length" class="py-2 text-[13px] text-gray-500 dark:text-gray-400">Nothing to check yet.</li>
              <li
                v-for="(c, i) in checks"
                :key="i"
                class="grid grid-cols-[auto_minmax(0,1fr)] items-start gap-2.5 border-t border-gray-200 py-2 text-[13px] first:border-t-0 dark:border-gray-800"
              >
                <span class="mt-px whitespace-nowrap rounded-sm px-1.5 py-0.5 text-[10.5px] font-bold" :class="checkTagClass[c.tone]">{{ c.tag }}</span>
                <div class="flex flex-col items-start gap-1.5">
                  <span>{{ c.text }}</span>
                  <Button v-if="c.action" :label="c.action.label" size="small" severity="secondary" outlined @click="c.action.run()" />
                </div>
              </li>
            </ul>
          </section>
        </div>
      </main>
    </div>

    <BuilderLinkPicker
      v-if="picker"
      :key="`${picker.subject}-${picker.object}-${picker.editId}`"
      :subject="picker.subject"
      :object="picker.object"
      :edit-id="picker.editId"
      :initial-key="picker.key"
      :anchor="picker.anchor"
      :check="check"
      :name="title"
      :year="year"
      @commit="onPickerCommit"
      @cancel="closePicker"
    />

    <BuilderFloating v-if="edgePop && popEdge" :anchor="edgePop.anchor" label="Link details" @close="edgePop = null">
      <p class="m-0">
        <b class="font-medium">{{ title(linkSentence(popEdge).first) }}</b> {{ linkSentence(popEdge).verb }}
        <b class="font-medium">{{ title(linkSentence(popEdge).second) }}</b
        >.
      </p>
      <p class="m-0 text-xs text-gray-500 dark:text-gray-400">
        {{ popEdge.status === 'new' ? 'Not saved yet.' : popEdge.status === 'removed' ? 'Will be removed when you save.' : 'Saved.' }}
      </p>
      <p v-if="popCut.length" class="m-0 text-[12.5px] text-amber-800 dark:text-amber-300">
        Removing it cuts {{ popCut.length === 1 ? '' : `${popCut.length} decks ` }}off from the franchise: {{ popCut.slice(0, 5).map(title).join(', ')
        }}{{ popCut.length > 5 ? '…' : '' }}.
      </p>
      <div class="flex flex-wrap gap-1.5">
        <template v-if="popEdge.status === 'removed'">
          <Button
            label="Keep this link"
            size="small"
            severity="secondary"
            outlined
            @click="
              commit((s) => revertLink(s, popEdge!.id));
              edgePop = null;
            "
          />
        </template>
        <template v-else>
          <Button label="Change type" size="small" severity="secondary" outlined @click="edgeChangeType" />
          <Button
            v-if="getEdgeFlow(popEdge).directed"
            label="Swap direction"
            size="small"
            severity="secondary"
            outlined
            @click="
              swapEdge(popEdge!.id);
              edgePop = null;
            "
          />
          <Button label="Remove link" size="small" severity="danger" outlined @click="removeEdge(popEdge!.id)" />
        </template>
      </div>
    </BuilderFloating>

    <BuilderFloating v-if="railPop && railNode" :anchor="railPop.anchor" :label="`${seriesName(railPop.seriesId)} options`" @close="railPop = null">
      <template v-if="railNode.kind === 'empty'">
        <p class="m-0">{{ seriesName(railPop.seriesId) }} has no decks on the board yet.</p>
        <p class="m-0 text-xs text-gray-500 dark:text-gray-400">Drag a deck onto the rail, or select decks and use Add to series.</p>
      </template>
      <template v-else-if="railNode.kind === 'line'">
        <p class="m-0">
          The <b class="font-medium">{{ title(railNode.via[0]!) }}</b> line ({{ railNode.ids.length }} decks) is in {{ seriesName(railPop.seriesId) }} through
          <b class="font-medium">{{ railNode.via.map(title).join(', ') }}</b
          >.
        </p>
        <p class="m-0 text-xs text-gray-500 dark:text-gray-400">Its other decks follow through their story links.</p>
        <div v-if="railPop.confirmDeck != null" class="flex flex-col gap-1.5">
          <span class="text-[12.5px] text-amber-800 dark:text-amber-300">{{ railNode.ids.length }} decks will leave {{ seriesName(railPop.seriesId) }}.</span>
          <div class="flex gap-1.5">
            <Button label="Remove" size="small" severity="danger" outlined @click="leaveSeries(railPop.seriesId, railNode.via)" />
            <Button label="Keep" size="small" severity="secondary" text @click="railPop.confirmDeck = null" />
          </div>
        </div>
        <div v-else>
          <Button label="Take the line out of the series" size="small" severity="secondary" outlined @click="railPop.confirmDeck = railNode.via[0]!" />
        </div>
      </template>
      <template v-else>
        <p class="m-0">Standalone entries in {{ seriesName(railPop.seriesId) }}.</p>
        <ul class="m-0 flex max-h-60 list-none flex-col gap-1 overflow-y-auto p-0">
          <li
            v-for="id in railNode.ids"
            :key="id"
            class="flex flex-wrap items-center justify-between gap-1.5 border-t border-gray-200 py-1 first:border-t-0 dark:border-gray-800"
          >
            <span class="min-w-0">
              <span v-bind="japaneseTextAttrs(title(id))">{{ title(id) }}</span>
              <small class="text-gray-500 dark:text-gray-400">
                {{ year(id) ?? '' }}{{ memberStatus(railPop.seriesId, id) === 'new' ? ' · not saved yet' : '' }}
              </small>
            </span>
            <div v-if="railPop.confirmDeck === id" class="flex w-full flex-col gap-1.5">
              <span class="text-[12.5px] text-amber-800 dark:text-amber-300">{{ title(id) }} will leave {{ seriesName(railPop.seriesId) }}.</span>
              <div class="flex gap-1.5">
                <Button label="Remove" size="small" severity="danger" outlined @click="leaveSeries(railPop.seriesId, [id])" />
                <Button label="Keep" size="small" severity="secondary" text @click="railPop.confirmDeck = null" />
              </div>
            </div>
            <Button v-else label="Remove" size="small" severity="secondary" text @click="railPop.confirmDeck = id" />
          </li>
        </ul>
      </template>
    </BuilderFloating>

    <Teleport to="body">
      <div
        v-if="drag?.moved && drag.source === 'result'"
        class="pointer-events-none fixed z-[1200] max-w-60 truncate rounded border border-primary-500 bg-white px-2 py-1 text-xs font-medium shadow-md dark:bg-gray-900"
        :style="{ left: `${drag.x + 12}px`, top: `${drag.y + 12}px` }"
        aria-hidden="true"
      >
        {{ title(drag.deckId) }}
      </div>
      <div
        v-if="toastMsg"
        role="status"
        class="fixed bottom-[calc(18px+env(safe-area-inset-bottom,0px))] left-1/2 z-[1200] flex max-w-[calc(100vw-32px)] -translate-x-1/2 items-center gap-3 rounded-md bg-gray-900 py-2 pl-3.5 pr-2.5 text-[13px] text-white shadow-lg dark:bg-gray-100 dark:text-gray-900"
      >
        <span>{{ toastMsg.text }}</span>
        <button
          v-if="toastMsg.undo"
          type="button"
          class="flex-none rounded-sm border border-current px-2 py-0.5 text-xs"
          @click="
            undo();
            toastMsg = null;
          "
        >
          Undo
        </button>
      </div>
    </Teleport>

    <BuilderSeriesDialog :request="seriesDialog" @close="seriesDialog = null" @picked="onSeriesPicked" @created="onSeriesCreated" @renamed="onSeriesRenamed" />
  </div>
</template>

<style scoped>
  .fb-handle {
    touch-action: none;
  }
</style>
