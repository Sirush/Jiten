import JSZip from 'jszip';
import { definitionsToHtml, definitionsToText, JMDICT_DICTIONARY_ID } from './useYomitanDictionary';

const FIELD_SEPARATOR = '\x1f';
const FIELD_INDEX_EXPRESSION = 0;
const FIELD_INDEX_READING = 2;
const FIELD_INDEX_DEFINITION = 5;

const CSV_COL_WORD = 0;
const CSV_COL_READING_KANA = 2;
const CSV_COL_DEFINITIONS = 6;

export interface ApkgProcessingProgress {
  phase: 'unzipping' | 'loading' | 'processing' | 'zipping';
  current: number;
  total: number;
}

export interface CsvProcessingProgress {
  phase: 'parsing' | 'processing' | 'building';
  current: number;
  total: number;
}

interface VisibleGroup {
  name: string;
  html: string;
  isJmDict: boolean;
}

interface VisibleTextGroup {
  name: string;
  text: string;
  isJmDict: boolean;
}

function parseCsv(text: string): string[][] {
  const rows: string[][] = [];
  let current = '';
  let fields: string[] = [];
  let inQuotes = false;

  for (let i = 0; i < text.length; i++) {
    const char = text[i];

    if (inQuotes) {
      if (char === '"') {
        if (i + 1 < text.length && text[i + 1] === '"') {
          current += '"';
          i++;
        } else {
          inQuotes = false;
        }
      } else {
        current += char;
      }
    } else {
      if (char === '"') {
        inQuotes = true;
      } else if (char === ',') {
        fields.push(current);
        current = '';
      } else if (char === '\r' && i + 1 < text.length && text[i + 1] === '\n') {
        fields.push(current);
        current = '';
        rows.push(fields);
        fields = [];
        i++;
      } else if (char === '\n' || char === '\r') {
        fields.push(current);
        current = '';
        rows.push(fields);
        fields = [];
      } else {
        current += char;
      }
    }
  }

  if (current || fields.length > 0) {
    fields.push(current);
    rows.push(fields);
  }

  return rows;
}

function csvEscape(value: string): string {
  return '"' + value.replace(/"/g, '""') + '"';
}

function rowToCsv(fields: string[]): string {
  return fields.map(csvEscape).join(',');
}

function stripLinks(html: string): string {
  return html.replace(/<a\b[^>]*>/gi, '').replace(/<\/a>/gi, '');
}

function stripDataAttributes(html: string): string {
  return html.replace(/\s+data-[a-z-]+="[^"]*"/gi, '');
}

export function useClientApkg() {
  const { dictionaries, loadDictionaries, lookupWord } = useYomitanDictionary();

  async function processApkg(apkgBlob: Blob, onProgress?: (progress: ApkgProcessingProgress) => void): Promise<Blob> {
    onProgress?.({ phase: 'unzipping', current: 0, total: 1 });

    const zip = await JSZip.loadAsync(apkgBlob);

    const collectionFile = zip.file('collection.anki21') || zip.file('collection.anki21b') || zip.file('collection.anki2');

    if (!collectionFile) {
      throw new Error('Invalid APKG: no collection database found');
    }

    onProgress?.({ phase: 'loading', current: 0, total: 1 });

    const initSqlJs = (await import('sql.js')).default;
    const SQL = await initSqlJs({
      locateFile: (file: string) => `/${file}`,
    });

    const dbBuffer = await collectionFile.async('uint8array');
    const db = new SQL.Database(dbBuffer);

    onProgress?.({ phase: 'processing', current: 0, total: 1 });

    if (dictionaries.value.length === 0) {
      await loadDictionaries();
    }

    const notes = db.exec('SELECT id, flds FROM notes');
    if (!notes.length || !notes[0].values.length) {
      db.close();
      return apkgBlob;
    }

    const rows = notes[0].values;
    onProgress?.({ phase: 'processing', current: 0, total: rows.length });

    const stmt = db.prepare('UPDATE notes SET flds = ? WHERE id = ?');
    const mod = Math.floor(Date.now() / 1000);
    const allDicts = dictionaries.value;

    for (let i = 0; i < rows.length; i++) {
      const noteId = rows[i][0] as number;
      const flds = rows[i][1] as string;
      const fields = flds.split(FIELD_SEPARATOR);

      const expression = fields[FIELD_INDEX_EXPRESSION] || '';
      const reading = fields[FIELD_INDEX_READING] || '';
      const originalDefinition = fields[FIELD_INDEX_DEFINITION] || '';

      const customResults = await lookupWord(expression, reading);

      const alwaysGroups: VisibleGroup[] = [];
      const fallbackGroups: VisibleGroup[] = [];

      for (const dict of allDicts) {
        if (dict.mode === 'never') continue;

        if (dict.id === JMDICT_DICTIONARY_ID) {
          if (originalDefinition) {
            const entry: VisibleGroup = { name: dict.name, html: originalDefinition, isJmDict: true };
            if (dict.mode === 'always') alwaysGroups.push(entry);
            else fallbackGroups.push(entry);
          }
        } else {
          const dictEntries = customResults.filter((r) => r.dictionary.id === dict.id);
          if (dictEntries.length > 0) {
            const html = definitionsToHtml(dictEntries[0].entry.definitions);
            if (html) {
              const entry: VisibleGroup = { name: dict.name, html, isJmDict: false };
              if (dict.mode === 'always') alwaysGroups.push(entry);
              else fallbackGroups.push(entry);
            }
          }
        }
      }

      const visibleGroups = alwaysGroups.length > 0 ? alwaysGroups : fallbackGroups;
      if (visibleGroups.length === 0) continue;

      // Only JMDict visible — field 5 already has it, nothing to change
      if (visibleGroups.length === 1 && visibleGroups[0].isJmDict) continue;

      let combinedHtml: string;
      if (visibleGroups.length === 1) {
        combinedHtml = visibleGroups[0].html;
      } else {
        combinedHtml = visibleGroups.map((g) => `<div class="dict-group"><div class="dict-name"><b>${g.name}</b></div>${g.html}</div>`).join('');
      }

      fields[FIELD_INDEX_DEFINITION] = stripDataAttributes(stripLinks(combinedHtml));
      stmt.run([fields.join(FIELD_SEPARATOR), noteId]);

      if (i % 50 === 0) {
        onProgress?.({ phase: 'processing', current: i + 1, total: rows.length });
      }
    }

    stmt.free();
    db.run(`UPDATE notes SET mod = ${mod}`);

    onProgress?.({ phase: 'zipping', current: 0, total: 1 });

    const modifiedDb = db.export();
    db.close();

    zip.file(collectionFile.name, modifiedDb);

    const result = await zip.generateAsync({ type: 'blob' });
    return result;
  }

  async function processCsv(csvBlob: Blob, onProgress?: (progress: CsvProcessingProgress) => void): Promise<Blob> {
    onProgress?.({ phase: 'parsing', current: 0, total: 1 });

    const text = await csvBlob.text();
    const rows = parseCsv(text);

    if (rows.length < 2) return csvBlob;

    if (dictionaries.value.length === 0) {
      await loadDictionaries();
    }

    const allDicts = dictionaries.value;
    const dataRows = rows.length - 1;
    onProgress?.({ phase: 'processing', current: 0, total: dataRows });

    for (let i = 1; i < rows.length; i++) {
      const row = rows[i];
      if (row.length <= CSV_COL_DEFINITIONS) continue;

      const word = row[CSV_COL_WORD];
      const reading = row[CSV_COL_READING_KANA];
      const originalDefinition = row[CSV_COL_DEFINITIONS];

      const customResults = await lookupWord(word, reading);

      const alwaysGroups: VisibleTextGroup[] = [];
      const fallbackGroups: VisibleTextGroup[] = [];

      for (const dict of allDicts) {
        if (dict.mode === 'never') continue;

        if (dict.id === JMDICT_DICTIONARY_ID) {
          if (originalDefinition) {
            const entry: VisibleTextGroup = { name: dict.name, text: originalDefinition, isJmDict: true };
            if (dict.mode === 'always') alwaysGroups.push(entry);
            else fallbackGroups.push(entry);
          }
        } else {
          const dictEntries = customResults.filter((r) => r.dictionary.id === dict.id);
          if (dictEntries.length > 0) {
            const defText = definitionsToText(dictEntries[0].entry.definitions);
            if (defText) {
              const entry: VisibleTextGroup = { name: dict.name, text: defText, isJmDict: false };
              if (dict.mode === 'always') alwaysGroups.push(entry);
              else fallbackGroups.push(entry);
            }
          }
        }
      }

      const visibleGroups = alwaysGroups.length > 0 ? alwaysGroups : fallbackGroups;
      if (visibleGroups.length === 0) continue;
      if (visibleGroups.length === 1 && visibleGroups[0].isJmDict) continue;

      let combined: string;
      if (visibleGroups.length === 1) {
        combined = visibleGroups[0].text;
      } else {
        combined = visibleGroups.map((g) => `[${g.name}]\n${g.text}`).join('\n\n');
      }

      row[CSV_COL_DEFINITIONS] = stripDataAttributes(stripLinks(combined));

      if (i % 50 === 0) {
        onProgress?.({ phase: 'processing', current: i, total: dataRows });
      }
    }

    onProgress?.({ phase: 'building', current: 0, total: 1 });

    const output = rows.map(rowToCsv).join('\r\n');
    return new Blob([output], { type: 'text/csv;charset=utf-8' });
  }

  return { processApkg, processCsv };
}
