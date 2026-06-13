export function applySSRProxyHeaders(headers: Headers): void {
  if (!import.meta.server) return;

  const proxyHeaders = useRequestHeaders(['x-forwarded-for', 'cf-connecting-ip', 'user-agent']);

  // Pin the real visitor IP as the leftmost X-Forwarded-For so the API rate-limits SSR
  // calls per visitor instead of collapsing them onto the SSR server's IP. Only effective
  // if the API's reverse proxy (Traefik) trusts this host's XFF; the bypass key below is
  // the unconditional safety net.
  const clientIp = proxyHeaders['cf-connecting-ip'] || proxyHeaders['x-forwarded-for'];
  if (clientIp) {
    headers.set('X-Forwarded-For', clientIp);
  }
  if (proxyHeaders['cf-connecting-ip']) {
    headers.set('CF-Connecting-IP', proxyHeaders['cf-connecting-ip']);
  }
  if (proxyHeaders['user-agent']) {
    headers.set('User-Agent', proxyHeaders['user-agent']);
  }

  // First-party SSR bypass: lets the API skip the per-IP anonymous rate limit for server
  // rendering (including nuxt-og-image's internal page re-render, which carries no visitor IP).
  const ssrBypassKey = useRuntimeConfig().ssrBypassKey as string | undefined;
  if (ssrBypassKey) {
    headers.set('X-Internal-Ssr-Key', ssrBypassKey);
  }
}
