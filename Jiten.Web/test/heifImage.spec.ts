import { describe, expect, it } from 'vitest';
import { isHeifImage } from '../app/utils/heifImage';

function ftyp(major: string, ...compatible: string[]): Uint8Array {
  const brands = [major, '\0\0\0\0', ...compatible];
  const box = new Uint8Array(8 + brands.length * 4 + 32);
  box[3] = 8 + brands.length * 4;
  box.set(new TextEncoder().encode('ftyp'), 4);
  brands.forEach((brand, i) => box.set(new TextEncoder().encode(brand), 8 + i * 4));
  return box;
}

describe('isHeifImage', () => {
  it('matches iPhone HEIC', () => {
    expect(isHeifImage(ftyp('heic', 'mif1', 'heic'))).toBe(true);
  });

  it('matches AVIF stills and sequences', () => {
    expect(isHeifImage(ftyp('avif', 'avif', 'mif1', 'miaf'))).toBe(true);
    expect(isHeifImage(ftyp('avis', 'avis', 'msf1'))).toBe(true);
  });

  it('matches a HEIF brand listed only among the compatible brands', () => {
    expect(isHeifImage(ftyp('mif1', 'mif1', 'heic'))).toBe(true);
  });

  it('ignores the minor version slot', () => {
    const bytes = ftyp('M4A ', 'isom');
    bytes.set(new TextEncoder().encode('heic'), 12);
    expect(isHeifImage(bytes)).toBe(false);
  });

  it('leaves m4a audio alone', () => {
    expect(isHeifImage(ftyp('M4A ', 'M4A ', 'mp42', 'isom'))).toBe(false);
  });

  it('leaves non-ISO-BMFF files alone', () => {
    expect(isHeifImage(new Uint8Array([0xff, 0xd8, 0xff, 0xe0, 0, 0x10, 0x4a, 0x46, 0x49, 0x46, 0, 1]))).toBe(false);
    expect(isHeifImage(new Uint8Array([0, 0, 0, 0x18]))).toBe(false);
  });
});
