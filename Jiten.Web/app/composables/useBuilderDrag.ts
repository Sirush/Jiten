import { dropTargetOf, type BuilderDropTarget } from '~/utils/franchiseBuilder';

type Point = { x: number; y: number };

export interface BuilderDragState {
  source: 'card' | 'result';
  deckId: number;
  startX: number;
  startY: number;
  x: number;
  y: number;
  moved: boolean;
  /** Pressed on the card's link handle. */
  handle: boolean;
  additive: boolean;
  target: BuilderDropTarget | null;
}

const DRAG_THRESHOLD_PX = 6;

/** Pointer drag of a board card or a search result; a card press that never moves counts as a click. */
export function useBuilderDrag(opts: {
  onStart: () => void;
  onMove: (clientX: number) => void;
  onDrop: (deckId: number, target: BuilderDropTarget, at: Point) => void;
  onClick: (deckId: number, press: { handle: boolean; additive: boolean }) => void;
}) {
  const drag = ref<BuilderDragState | null>(null);

  function beginDrag(source: BuilderDragState['source'], deckId: number, ev: PointerEvent) {
    if (ev.button !== 0) return;
    opts.onStart();
    drag.value = {
      source,
      deckId,
      startX: ev.clientX,
      startY: ev.clientY,
      x: ev.clientX,
      y: ev.clientY,
      moved: false,
      handle: !!(ev.target as Element).closest('[data-link-handle]'),
      additive: ev.shiftKey || ev.ctrlKey || ev.metaKey,
      target: null,
    };
    window.addEventListener('pointermove', onMove);
    window.addEventListener('pointerup', onEnd);
    window.addEventListener('pointercancel', stopDrag);
  }

  function onMove(ev: PointerEvent) {
    const d = drag.value;
    if (!d) return;
    if (!d.moved) {
      if (Math.hypot(ev.clientX - d.startX, ev.clientY - d.startY) < DRAG_THRESHOLD_PX) return;
      d.moved = true;
    }
    ev.preventDefault();
    d.x = ev.clientX;
    d.y = ev.clientY;
    let t = dropTargetOf(document.elementFromPoint(ev.clientX, ev.clientY));
    if (t?.kind === 'deck' && t.id === d.deckId) t = null;
    if (t?.kind === 'board' && d.source === 'card') t = null;
    d.target = t;
    opts.onMove(ev.clientX);
  }

  function stopDrag() {
    window.removeEventListener('pointermove', onMove);
    window.removeEventListener('pointerup', onEnd);
    window.removeEventListener('pointercancel', stopDrag);
    drag.value = null;
  }

  function onEnd(ev: PointerEvent) {
    const d = drag.value;
    stopDrag();
    if (!d) return;
    if (d.moved) {
      if (d.target) opts.onDrop(d.deckId, d.target, { x: ev.clientX, y: ev.clientY });
      return;
    }
    if (d.source === 'card') opts.onClick(d.deckId, { handle: d.handle, additive: d.additive });
  }

  onBeforeUnmount(stopDrag);

  return { drag, beginDrag, stopDrag };
}
