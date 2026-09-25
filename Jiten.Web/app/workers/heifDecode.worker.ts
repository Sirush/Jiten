import createLibHeif from 'libheif-js/libheif-wasm/libheif-bundle.mjs';

export type HeifDecodeResult = { bitmap: ImageBitmap } | { error: string };

self.onmessage = async (event: MessageEvent<ArrayBuffer>) => {
  try {
    const images = new (createLibHeif().HeifDecoder)().decode(new Uint8Array(event.data));
    try {
      const image = images.find((i) => i.is_primary()) ?? images[0];
      if (!image) throw new Error('The file holds no image.');

      const width = image.get_width();
      const height = image.get_height();
      const canvas = new OffscreenCanvas(width, height);
      const ctx = canvas.getContext('2d');
      if (!ctx) throw new Error('Canvas is unavailable.');

      const pixels = ctx.createImageData(width, height);
      await new Promise<void>((resolve, reject) =>
        image.display(pixels, (result) => (result ? resolve() : reject(new Error('The image could not be decoded.'))))
      );
      ctx.putImageData(pixels, 0, 0);

      const bitmap = canvas.transferToImageBitmap();
      self.postMessage({ bitmap } satisfies HeifDecodeResult, { transfer: [bitmap] });
    } finally {
      for (const image of images) image.free();
    }
  } catch (err) {
    self.postMessage({ error: err instanceof Error ? err.message : String(err) } satisfies HeifDecodeResult);
  }
};
