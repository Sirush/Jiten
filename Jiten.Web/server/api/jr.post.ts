const MAX_BODY = 32 * 1024;

export default defineEventHandler(async (event) => {
  setResponseStatus(event, 204);
  const length = Number(getRequestHeader(event, 'content-length') ?? 0);
  if (length > MAX_BODY) return null;

  let body: string | undefined;
  try {
    body = await readRawBody(event, 'utf8');
  } catch {
    return null;
  }
  if (!body || body.length > MAX_BODY || body[0] !== '{') return null;

  relayToKami(event, body);
  return null;
});
