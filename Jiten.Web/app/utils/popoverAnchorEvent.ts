/** PrimeVue Popover.show reads event.currentTarget, which is null once an awaited load has let the click finish dispatching. */
export function popoverAnchorEvent(event: Event): Event {
  const currentTarget = event.currentTarget;
  return { currentTarget } as unknown as Event;
}
