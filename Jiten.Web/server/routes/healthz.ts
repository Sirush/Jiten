// Liveness probe for the Nuxt SSR server itself. Must NOT touch the API. A healthy
// web container should report healthy even while the API is mid-deploy, otherwise the
// orchestrator's rolling switchover stalls on a transient API blip.
export default defineEventHandler((event) => {
  setResponseHeader(event, 'Cache-Control', 'no-store');
  return 'healthy';
});
