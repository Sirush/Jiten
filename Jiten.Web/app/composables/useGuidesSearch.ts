// Shared open-state for the global guides command palette (GuidesSearch.vue, mounted once
// in app.vue). The header trigger and the /guides index search bar both call open().
export function useGuidesSearch() {
  const isOpen = useState('guides-search-open', () => false);
  const open = () => {
    isOpen.value = true;
  };
  const close = () => {
    isOpen.value = false;
  };
  return { isOpen, open, close };
}
