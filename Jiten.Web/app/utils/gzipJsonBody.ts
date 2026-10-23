export type JsonRequestBody = {
  body: string | Blob;
  headers: Record<string, string>;
};

export async function gzipJsonBody(payload: unknown): Promise<JsonRequestBody> {
  const json = JSON.stringify(payload);
  if (typeof CompressionStream === 'undefined') {
    return { body: json, headers: { 'Content-Type': 'application/json' } };
  }

  const compressed = new Blob([json]).stream().pipeThrough(new CompressionStream('gzip'));
  const body = await new Response(compressed).blob();
  return { body, headers: { 'Content-Type': 'application/json', 'Content-Encoding': 'gzip' } };
}
