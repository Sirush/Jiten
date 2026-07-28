/**
 * Shared open-state for the header search modal (HeaderSearchDialog.vue, mounted once in
 * AppHeader). Separate from useGuidesSearch, which owns the guides-only Ctrl+K palette.
 */
export function useHeaderSearch() {
  const isOpen = useState('header-search-open', () => false);
  const open = () => {
    isOpen.value = true;
  };
  const close = () => {
    isOpen.value = false;
  };
  return { isOpen, open, close };
}
