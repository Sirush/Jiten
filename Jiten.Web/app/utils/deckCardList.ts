export type CoverLoading = 'priority' | 'eager' | 'lazy';

const PRIORITY_COVER_COUNT = 3;
const EAGER_COVER_COUNT = 8;

/** A lazy cover inside a `content-visibility: auto` card starts loading about 2 s after a scroll jump, so the first tall screen of cards stays eager. */
export function deckCardCoverLoading(index: number): CoverLoading {
  if (index < PRIORITY_COVER_COUNT) return 'priority';
  return index < EAGER_COVER_COUNT ? 'eager' : 'lazy';
}

/** Placeholder heights are the typical rendered card height at each breakpoint; a wrong estimate resizes the page as cards scroll in. */
const DEFERRED_RENDER_CLASS =
  '[content-visibility:auto] [contain-intrinsic-size:auto_57rem] sm:[contain-intrinsic-size:auto_52rem] md:[contain-intrinsic-size:auto_35rem] lg:[contain-intrinsic-size:auto_24rem] p-1 -m-1';

export function deckCardRenderClass(index: number): string {
  return index >= PRIORITY_COVER_COUNT ? DEFERRED_RENDER_CLASS : '';
}
