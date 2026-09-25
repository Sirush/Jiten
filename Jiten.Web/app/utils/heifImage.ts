import type { HeifDecodeResult } from '~/workers/heifDecode.worker';

// Sequence brands (heim/heis/avis) count too: the server refuses every HEIF-family file.
const HEIF_BRANDS = new Set(['heic', 'heix', 'heim', 'heis', 'hevc', 'hevx', 'heif', 'mif1', 'msf1', 'avif', 'avis']);

/** Enough of the file head to cover the ftyp box, which is where the brands live. */
export const HEIF_SNIFF_BYTES = 512;

/** Matches CardMediaImageProcessor.MaxLongEdge; the server would shrink anything larger anyway. */
const MAX_LONG_EDGE = 1600;
const JPEG_QUALITY = 0.92;

function ascii(bytes: Uint8Array, offset: number): string {
  return String.fromCharCode(bytes[offset]!, bytes[offset + 1]!, bytes[offset + 2]!, bytes[offset + 3]!);
}

/** Mirrors CardMediaSniffer.IsHeifImage, which rejects these files server-side. */
export function isHeifImage(head: Uint8Array): boolean {
  if (head.length < 12 || ascii(head, 4) !== 'ftyp') return false;

  // ftyp box: [size:4]['ftyp':4][major_brand:4][minor_version:4][compatible_brands:4*n]
  const boxSize = ((head[0]! << 24) | (head[1]! << 16) | (head[2]! << 8) | head[3]!) >>> 0;
  const end = boxSize > 8 && boxSize <= 4096 ? Math.min(boxSize, head.length) : head.length;
  for (let offset = 8; offset + 4 <= end; offset += 4) {
    if (offset === 12) continue;
    if (HEIF_BRANDS.has(ascii(head, offset))) return true;
  }
  return false;
}

export async function isHeifBlob(blob: Blob): Promise<boolean> {
  return isHeifImage(new Uint8Array(await blob.slice(0, HEIF_SNIFF_BYTES).arrayBuffer()));
}

export interface ConvertedImage {
  blob: Blob;
  extension: 'jpg' | 'png';
}

/**
 * Re-encodes a HEIC/AVIF image as JPEG (PNG when it has transparency), capped at the server's long edge.
 * Uses the browser's own decoder when it has one; libheif in a worker covers the rest (HEIC outside Safari).
 */
export async function convertHeifImage(blob: Blob): Promise<ConvertedImage> {
  const source = (await decodeNatively(blob)) ?? (await decodeWithLibheif(blob));
  try {
    return await encode(source);
  } finally {
    source.close();
  }
}

async function decodeNatively(blob: Blob): Promise<ImageBitmap | null> {
  try {
    return await createImageBitmap(blob);
  } catch {
    // Some engines decode a format only via <img>; img.decode() would never settle in a hidden tab.
    const url = URL.createObjectURL(blob);
    try {
      const img = new Image();
      await new Promise<void>((resolve, reject) => {
        img.onload = () => resolve();
        img.onerror = () => reject(new Error('Not decodable natively.'));
        img.src = url;
      });
      return await createImageBitmap(img);
    } catch {
      return null;
    } finally {
      URL.revokeObjectURL(url);
    }
  }
}

async function decodeWithLibheif(blob: Blob): Promise<ImageBitmap> {
  const buffer = await blob.arrayBuffer();
  const worker = new Worker(new URL('../workers/heifDecode.worker.ts', import.meta.url), { type: 'module' });
  try {
    return await new Promise<ImageBitmap>((resolve, reject) => {
      worker.onmessage = (event: MessageEvent<HeifDecodeResult>) => {
        if ('bitmap' in event.data) resolve(event.data.bitmap);
        else reject(new Error(event.data.error));
      };
      worker.onerror = () => reject(new Error('The image decoder failed to load.'));
      worker.postMessage(buffer, [buffer]);
    });
  } finally {
    worker.terminate();
  }
}

async function encode(source: ImageBitmap): Promise<ConvertedImage> {
  const scale = Math.min(1, MAX_LONG_EDGE / Math.max(source.width, source.height));
  const width = Math.max(1, Math.round(source.width * scale));
  const height = Math.max(1, Math.round(source.height * scale));

  const canvas = document.createElement('canvas');
  canvas.width = width;
  canvas.height = height;
  const ctx = canvas.getContext('2d');
  if (!ctx) throw new Error('Canvas is unavailable.');
  ctx.imageSmoothingQuality = 'high';
  ctx.drawImage(source, 0, 0, width, height);

  const transparent = hasTransparency(ctx.getImageData(0, 0, width, height).data);
  const out = await new Promise<Blob | null>((resolve) => canvas.toBlob(resolve, transparent ? 'image/png' : 'image/jpeg', JPEG_QUALITY));
  if (!out) throw new Error('The converted image could not be encoded.');
  return { blob: out, extension: transparent ? 'png' : 'jpg' };
}

function hasTransparency(rgba: Uint8ClampedArray): boolean {
  for (let i = 3; i < rgba.length; i += 4) {
    if (rgba[i]! < 255) return true;
  }
  return false;
}
