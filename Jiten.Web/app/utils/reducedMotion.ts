/** True when the system asks for reduced motion or the display profile forces it. */
export function prefersReducedMotion(): boolean {
  if (typeof window === 'undefined') return false;
  return document.documentElement.classList.contains('reduce-motion') || window.matchMedia('(prefers-reduced-motion: reduce)').matches;
}
