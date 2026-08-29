import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { compileTemplate, parse as parseSfc } from 'vue/compiler-sfc';

// Prettier with htmlWhitespaceSensitivity:'ignore' once reformatted 219 templates and moved the
// rendered text of 43 of them ("packing ;crating", "CC BY-SA 4.0 ."), because Vue condenses a
// newline inside an inline element into a rendered space. The setting is 'css' now; this fails if
// a template ever paints a space in front of punctuation again.

const APP = fileURLToPath(new URL('../app', import.meta.url));

const BLOCK = new Set([
  'div',
  'p',
  'li',
  'ul',
  'ol',
  'h1',
  'h2',
  'h3',
  'h4',
  'h5',
  'h6',
  'section',
  'article',
  'td',
  'th',
  'tr',
  'table',
  'thead',
  'tbody',
  'header',
  'footer',
  'nav',
  'main',
  'aside',
  'form',
  'fieldset',
  'legend',
  'blockquote',
  'pre',
  'dl',
  'dt',
  'dd',
  'figure',
  'figcaption',
  'hr',
  'button',
  'select',
  'option',
  'textarea',
  'input',
  'br',
  'template',
]);

// Private-use code points, so they cannot collide with anything a template renders.
const BOUNDARY = '\ue000';
const EXPR = '\ue001';

// Only the handful of node fields this walk reads; the compiler's own types are not exported
// in a shape that survives the SFC/template boundary.
interface TemplateNode {
  type: number;
  tag?: string;
  content?: string | TemplateNode;
  children?: TemplateNode[];
  branches?: TemplateNode[];
  props?: { type: number; name: string; value?: { content?: string } }[];
}

function vueFiles(dir: string, out: string[] = []): string[] {
  for (const entry of readdirSync(dir)) {
    const p = join(dir, entry);
    if (statSync(p).isDirectory()) vueFiles(p, out);
    else if (entry.endsWith('.vue')) out.push(p);
  }
  return out;
}

// Flex and grid containers drop whitespace-only nodes between their items, so spacing there comes
// from gap/margin rather than the markup and no reformat can disturb it.
function laysOutItems(node: TemplateNode): boolean {
  const cls = node.props?.find((p) => p.type === 6 && p.name === 'class');
  return /(^|[\s:])(inline-)?(flex|grid)(\s|$)/.test(cls?.value?.content ?? '');
}

function walk(node: TemplateNode, out: string[]): void {
  if (node.type === 2) return void out.push(node.content as string);
  if (node.type === 5) return void out.push(EXPR);
  // <code> shows literal syntax, where a comma or a leading ? is data rather than punctuation.
  if (node.type === 1 && node.tag === 'code') return void out.push(EXPR);
  // The transform wraps runs of text in TEXT_CALL / COMPOUND_EXPRESSION nodes.
  if (node.type === 12) return void walk(node.content as TemplateNode, out);
  if (node.type === 8) {
    for (const c of node.children ?? []) if (typeof c === 'object') walk(c, out);
    return;
  }
  if (node.type === 1) {
    const items = laysOutItems(node);
    const block = items || BLOCK.has(node.tag) || BLOCK.has(node.tag.toLowerCase());
    if (block) out.push(BOUNDARY);
    for (const c of node.children ?? []) {
      if (items) out.push(BOUNDARY);
      walk(c, out);
      if (items) out.push(BOUNDARY);
    }
    if (block) out.push(BOUNDARY);
    return;
  }
  if (node.type === 9) return void node.branches?.forEach((b) => (b.children ?? []).forEach((c) => walk(c, out)));
  for (const c of node.children ?? []) walk(c, out);
}

/** The visible text of a template, one line per block, with HTML whitespace collapsing applied. */
function renderedText(src: string, filename: string): string | null {
  const { descriptor } = parseSfc(src, { filename });
  if (!descriptor.template) return null;
  const { ast } = compileTemplate({ source: descriptor.template.content, filename, id: 'ws', compilerOptions: { whitespace: 'condense' } });
  if (!ast) return null;
  const parts: string[] = [];
  walk(ast as unknown as TemplateNode, parts);
  return parts
    .join('')
    .replace(/[ \t\r\n]+/g, ' ')
    .replace(/ *\ue000 */g, BOUNDARY)
    .replace(/\ue000+/g, '\n');
}

describe('template whitespace', () => {
  it('never paints a space in front of punctuation', () => {
    const offenders: string[] = [];
    for (const file of vueFiles(APP)) {
      let text: string | null;
      try {
        text = renderedText(readFileSync(file, 'utf8'), file);
      } catch {
        continue;
      }
      if (!text) continue;
      for (const line of text.split('\n')) {
        // French typography puts a space before ; and :, so those only count when the mark also
        // has no space after it — the lopsided shape condensing produces. A space before a
        // closing . , ) or ] is wrong in both languages.
        if (/\S [.,)\]](\s|$)/.test(line) || /\S [;:]\S/.test(line)) {
          offenders.push(`${file.slice(APP.length + 1).replace(/\\/g, '/')} :: ${line.trim().slice(0, 100)}`);
        }
      }
    }
    expect(offenders).toEqual([]);
  });
});
