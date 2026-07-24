// Canvas helpers shared by the "Export as image" features (difficulty ranking, immersion plans).

/** Cached bitmap loader for one export surface; failures resolve to null so a missing cover never aborts the export. */
export function createBitmapLoader(): (url: string) => Promise<ImageBitmap | null> {
  const cache = new Map<string, ImageBitmap | null>();
  // Fetch via CORS + decode through a blob so the export canvas is never tainted.
  return async (url: string) => {
    if (cache.has(url)) return cache.get(url)!;
    let bmp: ImageBitmap | null = null;
    try {
      const resp = await fetch(url, { mode: 'cors' });
      bmp = await createImageBitmap(await resp.blob());
    } catch {
      bmp = null;
    }
    cache.set(url, bmp);
    return bmp;
  };
}

/** Draw bmp into (x,y,w,h) with object-fit: cover, clipped to a rounded rect. */
export function drawCoverImage(
  ctx: CanvasRenderingContext2D,
  bmp: ImageBitmap | null,
  x: number,
  y: number,
  w: number,
  h: number,
  fallback: string,
  radius = 4,
) {
  ctx.save();
  ctx.beginPath();
  ctx.roundRect(x, y, w, h, radius);
  ctx.clip();
  if (bmp) {
    const scale = Math.max(w / bmp.width, h / bmp.height);
    const sw = w / scale;
    const sh = h / scale;
    ctx.drawImage(bmp, (bmp.width - sw) / 2, (bmp.height - sh) / 2, sw, sh, x, y, w, h);
  } else {
    ctx.fillStyle = fallback;
    ctx.fillRect(x, y, w, h);
  }
  ctx.restore();
}

/** Truncate text with an ellipsis so it fits maxW in the current canvas font. */
export function fitCanvasText(ctx: CanvasRenderingContext2D, text: string, maxW: number): string {
  if (ctx.measureText(text).width <= maxW) return text;
  let lo = 0;
  let hi = text.length;
  while (lo < hi) {
    const mid = (lo + hi + 1) >> 1;
    if (ctx.measureText(text.slice(0, mid) + '…').width <= maxW) lo = mid;
    else hi = mid - 1;
  }
  return text.slice(0, lo) + '…';
}

/** Hand the finished canvas to the user: share sheet on mobile (desktops also expose Web Share but can't save from it), download elsewhere. */
export async function saveCanvasPng(canvas: HTMLCanvasElement, fileName: string, shareTitle: string) {
  const blob = await new Promise<Blob | null>((res) => canvas.toBlob(res, 'image/png'));
  if (!blob) throw new Error('Export produced an empty image');
  const file = new File([blob], fileName, { type: 'image/png' });

  const ua = navigator.userAgent;
  const isMobile =
    (navigator as Navigator & { userAgentData?: { mobile?: boolean } }).userAgentData?.mobile ??
    (/Android|iPhone|iPod/i.test(ua) || (/iPad|Macintosh/i.test(ua) && navigator.maxTouchPoints > 1));

  if (isMobile && navigator.canShare?.({ files: [file] })) {
    try {
      await navigator.share({ files: [file], title: shareTitle });
      return;
    } catch (err) {
      if ((err as Error)?.name === 'AbortError') return; // user dismissed the sheet
      // any other failure falls through to the download path
    }
  }

  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.download = fileName;
  link.href = url;
  link.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
