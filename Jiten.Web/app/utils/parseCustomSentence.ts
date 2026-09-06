import { escapeHtml } from './sanitiseHtml';

export function parseCustomSentenceHtml(text: string): string {
  return escapeHtml(text).replace(/\*\*([^*]+)\*\*/g, '<span class="text-primary-500 dark:text-primary-500 font-bold">$1</span>');
}

export function hasMarkers(text: string): boolean {
  return /\*\*[^*]+\*\*/.test(text);
}

export function stripMarkers(text: string): string {
  return text.replace(/\*\*/g, '');
}
