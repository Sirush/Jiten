import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { compileTemplate, parse } from 'vue/compiler-sfc';

// Vue condenses a newline inside an inline element into a rendered space, so formatting
// VocabularyDefinitions' meaning list across several lines puts the space on the wrong side
// of the separator ("packing ;crating" instead of "packing; crating"). Prettier did exactly
// that once (67db6b9b) and the damage reached every word page and every SRS card, so assert
// against the compiled render function rather than the source layout.
function renderCode(componentPath: string): string {
  const filename = fileURLToPath(new URL(componentPath, import.meta.url));
  const { descriptor } = parse(readFileSync(filename, 'utf8'), { filename });
  return compileTemplate({ source: descriptor.template!.content, filename, id: 'test' }).code;
}

describe('dictionary meaning separator whitespace', () => {
  const code = renderCode('../app/components/VocabularyDefinitions.vue');

  it('puts the space after the semicolon, not before it', () => {
    expect(code).toContain('"; "');
    expect(code).not.toMatch(/,\s*";"\)/);
  });

  it('does not pad the meaning text with a trailing space', () => {
    expect(code).not.toMatch(/_toDisplayString\(seg\.text\)\s*\+\s*"/);
  });

  it('keeps cross-reference sense numbers tight against the headword', () => {
    expect(code).not.toMatch(/_toDisplayString\(_ctx\.xrefBaseText\(x\)\)\s*\+\s*"/);
  });
});
