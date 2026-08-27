export function useLegalIdentity() {
  const legal = useRuntimeConfig().public.legal;
  const configured = computed(() => !!(legal.publisherName && legal.publicationDirector && legal.siren && legal.siret && legal.address));
  return { legal, configured };
}
