import type { H3Event } from 'h3';

const RELAY_TIMEOUT_MS = 3000;

function kamiHeaders(config: ReturnType<typeof useRuntimeConfig>) {
  return {
    'content-type': 'application/json',
    ...(config.kamiKey ? { authorization: `Bearer ${config.kamiKey}` } : {}),
  };
}

export function relayToKami(event: H3Event, body: string): void {
  const config = useRuntimeConfig();
  if (!config.kamiUrl) return;

  const headers = getRequestHeaders(event);
  const forwarded = String(headers['x-forwarded-for'] ?? '')
    .split(',')[0]
    ?.trim();
  const ip = String(headers['cf-connecting-ip'] ?? '') || forwarded || event.node.req.socket?.remoteAddress || '';

  const send = $fetch(`${config.kamiUrl.replace(/\/$/, '')}/ingest/web`, {
    method: 'POST',
    body,
    timeout: RELAY_TIMEOUT_MS,
    retry: 0,
    headers: {
      ...kamiHeaders(config),
      'x-client-ip': ip,
      'x-client-ua': String(headers['user-agent'] ?? '').slice(0, 512),
      'x-client-country': String(headers['cf-ipcountry'] ?? ''),
    },
  }).catch(() => {});

  event.waitUntil(send);
}

/// Server-originated report (no request context); errors are swallowed for the same reason.
export function sendToKami(path: string, body: Record<string, unknown>): void {
  const config = useRuntimeConfig();
  if (!config.kamiUrl) return;
  $fetch(`${config.kamiUrl.replace(/\/$/, '')}${path}`, {
    method: 'POST',
    body,
    timeout: RELAY_TIMEOUT_MS,
    retry: 0,
    headers: kamiHeaders(config),
  }).catch(() => {});
}
