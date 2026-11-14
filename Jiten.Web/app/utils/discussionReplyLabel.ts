import { formatNumber } from '~/utils/formatNumber';
export function replyCountLabel(count: number): string {
  return `${formatNumber(count)} ${count === 1 ? 'reply' : 'replies'}`;
}

export function discussionReplyLabel(count: number): string {
  return `${replyCountLabel(count)} in the discussion`;
}
