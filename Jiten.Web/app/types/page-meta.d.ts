declare module '#app' {
  interface PageMeta {
    /** Opts the page into the wide layout shell (max-w-7xl instead of max-w-6xl). */
    wide?: boolean;
    /** The page lays out its own width, so a section can span the whole window. */
    fullBleed?: boolean;
    /** Hides the site header and footer; the page renders its own minimal chrome. */
    bare?: boolean;
  }
}

export {};
