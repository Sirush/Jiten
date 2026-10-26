export function replyCountLabel(count: number): string {
  return `${count.toLocaleString()} ${count === 1 ? 'reply' : 'replies'}`;
}

export function discussionReplyLabel(count: number): string {
  return `${replyCountLabel(count)} in the discussion`;
}
