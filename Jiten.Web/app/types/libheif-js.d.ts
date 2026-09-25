declare module 'libheif-js/libheif-wasm/libheif-bundle.mjs' {
  interface HeifImage {
    get_width(): number;
    get_height(): number;
    is_primary(): boolean;
    display(target: ImageData, callback: (result: ImageData | null) => void): void;
    free(): void;
  }

  interface LibHeif {
    HeifDecoder: new () => { decode(data: Uint8Array): HeifImage[] };
  }

  const createLibHeif: () => LibHeif;
  export default createLibHeif;
}
