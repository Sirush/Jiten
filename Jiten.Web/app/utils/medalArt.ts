const r2 = (n: number) => Math.round(n * 100) / 100;

type Point = readonly [number, number];

/** Seeded so server and client render the same flecks. */
export const LACQUER_FLECKS = (() => {
  let seed = 11;
  const rand = () => (seed = (seed * 16807) % 2147483647) / 2147483647;
  return Array.from({ length: 18 }, () => {
    const angle = rand() * Math.PI * 2;
    const distance = Math.sqrt(rand()) * 42;
    const size = 1.8 + rand() * 3;
    return {
      x: r2(50 + Math.cos(angle) * distance - size / 2),
      y: r2(50 + Math.sin(angle) * distance - size / 2),
      size: r2(size),
      rotate: r2(rand() * 90),
      opacity: r2(0.45 + rand() * 0.5),
    };
  });
})();

/** Strands tilted off the tangent so the ring reads as twisted straw. */
export const ROPE_STRANDS = Array.from({ length: 30 }, (_, i) => {
  const angle = (i / 30) * Math.PI * 2;
  return {
    cx: r2(50 + Math.cos(angle) * 42),
    cy: r2(50 + Math.sin(angle) * 42),
    rotate: r2((angle * 180) / Math.PI + 90 + 40),
  };
});

/** Ichibu-gin face art is drawn in a 40x60 box, matching the coin's upright proportions. */
export const ICHIBU_BOX = { w: 40, h: 60 };

export const ICHIBU_FLOWERS = (() => {
  const inset = 4;
  const step = 5.2;
  const { w, h } = ICHIBU_BOX;
  const flowers: { x: number; y: number }[] = [];
  const edge = (x0: number, y0: number, x1: number, y1: number) => {
    const length = Math.hypot(x1 - x0, y1 - y0);
    const count = Math.round(length / step);
    for (let i = 0; i < count; i++) flowers.push({ x: r2(x0 + ((x1 - x0) * i) / count), y: r2(y0 + ((y1 - y0) * i) / count) });
  };
  edge(inset, inset, w - inset, inset);
  edge(w - inset, inset, w - inset, h - inset);
  edge(w - inset, h - inset, inset, h - inset);
  edge(inset, h - inset, inset, inset);
  return flowers;
})();

export const SAKURA_PETALS = Array.from({ length: 5 }, (_, k) => {
  const angle = (k / 5) * Math.PI * 2 - Math.PI / 2;
  return { cx: r2(Math.cos(angle) * 1.05), cy: r2(Math.sin(angle) * 1.05) };
});

/** Shifts the bean left so its body, where the glyph sits, is centred in the medal slot like every other tier. */
const MAGATAMA_SHIFT_X = -6;

/** Magatama as a sampled polygon in a 64px box: Chrome drops clip-path path() on faces inside a preserve-3d coin. */
const magatamaClip = (mirrored: boolean) => {
  const start: Point = [10, 17];
  const curves: [Point, Point, Point][] = [
    [[11, 7], [17, 2], [25, 2]],
    [[40, 2], [54, 16], [54, 34]],
    [[54, 50], [43, 62], [31, 62]],
    [[22, 62], [15, 59], [15, 53]],
    [[15, 46], [21, 42], [22, 35]],
    [[22, 29], [14, 26], [11, 22]],
    [[10, 20], [10, 18], [10, 17]],
  ];
  const points: string[] = [];
  let from = start;
  for (const [c1, c2, to] of curves) {
    for (let step = 1; step <= 8; step++) {
      const t = step / 8;
      const u = 1 - t;
      const x = u * u * u * from[0] + 3 * u * u * t * c1[0] + 3 * u * t * t * c2[0] + t * t * t * to[0];
      const y = u * u * u * from[1] + 3 * u * u * t * c1[1] + 3 * u * t * t * c2[1] + t * t * t * to[1];
      const px = ((x + MAGATAMA_SHIFT_X) / 64) * 100;
      points.push(`${r2(mirrored ? 100 - px : px)}% ${r2((y / 64) * 100)}%`);
    }
    from = to;
  }
  return `polygon(${points.join(', ')})`;
};

export const MAGATAMA_CLIP = magatamaClip(false);

/** The back face is seen from behind, so its outline is mirrored to match the coin's silhouette. */
export const MAGATAMA_CLIP_BACK = magatamaClip(true);

/** Petals and gold leaf thrown out by the Shimenawa's rare ascension, offsets in px from the medal centre. */
export const ASCEND_PARTICLES = Array.from({ length: 12 }, (_, i) => {
  const angle = (i / 12) * Math.PI * 2 + (i % 2 ? 0.2 : -0.15);
  const distance = 34 + (i % 3) * 6;
  return {
    dx: r2(Math.cos(angle) * distance),
    dy: r2(Math.sin(angle) * distance - 6),
    rotate: ((i * 67) % 180) - 90,
    delay: 280 + (i % 4) * 70,
    petal: i % 3 !== 0,
  };
});
