<script setup lang="ts">
  import type { ForumPost } from '~/types/types';
  import { formatDateLong } from '~/utils/formatDateLong';
  import { formatRelativeTime } from '~/utils/relativeTime';

  const props = defineProps<{
    post: ForumPost;
    /** Replies inside a thread sit under their root post, so they take a smaller avatar. */
    compact?: boolean;
  }>();

  const communityUrl = useRuntimeConfig().public.communityUrl as string;
  const profileUrl = computed(() => `${communityUrl.replace(/\/$/, '')}/u/${encodeURIComponent(props.post.username)}`);
  const avatarSize = computed(() => (props.compact ? 32 : 40));
</script>

<template>
  <div class="flex gap-3">
    <img
      :src="post.avatarUrl"
      alt=""
      :width="avatarSize"
      :height="avatarSize"
      loading="lazy"
      class="shrink-0 rounded-full bg-surface-100 dark:bg-surface-800"
      :class="compact ? 'size-8' : 'size-10'"
    />
    <div class="flex min-w-0 flex-1 flex-col gap-1">
      <div class="flex flex-wrap items-baseline gap-x-2 text-sm">
        <a :href="profileUrl" target="_blank" rel="noopener" class="font-semibold text-surface-800! no-underline! dark:text-surface-100!">
          {{ post.username }}
        </a>
        <span v-if="post.replyToUsername" class="text-muted-color">replying to {{ post.replyToUsername }}</span>
        <Tooltip :content="formatDateLong(post.createdAt)">
          <a :href="post.url" target="_blank" rel="noopener" class="ml-auto text-xs text-muted-color! no-underline! hover:underline!">
            {{ formatRelativeTime(post.createdAt) }}
          </a>
        </Tooltip>
      </div>
      <div class="forum-post-body break-words" v-html="post.html" />
    </div>
  </div>
</template>

<style scoped>
  .forum-post-body {
    line-height: 1.6;
  }

  .forum-post-body :deep(p) {
    margin: 0 0 0.75em;
  }

  .forum-post-body > :deep(:last-child) {
    margin-bottom: 0;
  }

  .forum-post-body :deep(h1),
  .forum-post-body :deep(h2),
  .forum-post-body :deep(h3),
  .forum-post-body :deep(h4),
  .forum-post-body :deep(h5),
  .forum-post-body :deep(h6) {
    margin: 0.75em 0 0.5em;
    font-size: 1.05em;
    font-weight: 700;
  }

  .forum-post-body :deep(ul),
  .forum-post-body :deep(ol) {
    margin: 0 0 0.75em;
    padding-left: 1.5em;
  }

  .forum-post-body :deep(ul) {
    list-style: disc;
  }

  .forum-post-body :deep(ol) {
    list-style: decimal;
  }

  .forum-post-body :deep(img) {
    max-width: 100%;
    height: auto;
  }

  .forum-post-body :deep(img.emoji) {
    display: inline;
    width: 1.25em;
    height: 1.25em;
    vertical-align: text-bottom;
  }

  .forum-post-body :deep(a.mention) {
    font-weight: 600;
  }

  .forum-post-body :deep(blockquote),
  .forum-post-body :deep(aside.quote) {
    margin: 0 0 0.75em;
    padding: 0.25em 0 0.25em 0.75em;
    border-left: 3px solid var(--p-content-border-color);
    color: var(--p-text-muted-color);
  }

  .forum-post-body :deep(aside.quote blockquote) {
    margin: 0;
    padding: 0;
    border: 0;
  }

  .forum-post-body :deep(aside.quote .title) {
    display: flex;
    align-items: center;
    gap: 0.4em;
    font-size: 0.875em;
    font-weight: 600;
  }

  .forum-post-body :deep(aside.quote .avatar) {
    width: 20px;
    height: 20px;
    border-radius: 9999px;
  }

  .forum-post-body :deep(code) {
    padding: 0.1em 0.3em;
    border-radius: 3px;
    background: var(--p-content-hover-background);
    font-size: 0.9em;
  }

  .forum-post-body :deep(pre) {
    margin: 0 0 0.75em;
    padding: 0.75em;
    overflow-x: auto;
    border-radius: 4px;
    background: var(--p-content-hover-background);
  }

  .forum-post-body :deep(pre code) {
    padding: 0;
    background: none;
  }

  .forum-post-body :deep(aside.onebox) {
    margin: 0 0 0.75em;
    padding: 0.75em;
    border: 1px solid var(--p-content-border-color);
    border-radius: 4px;
  }

  .forum-post-body :deep(aside.onebox .site-icon) {
    display: inline;
    width: 16px;
    height: 16px;
    margin-right: 0.4em;
    vertical-align: middle;
  }

  .forum-post-body :deep(aside.onebox .thumbnail) {
    float: right;
    max-width: 96px;
    max-height: 96px;
    margin-left: 0.75em;
  }

  .forum-post-body :deep(aside.onebox h3) {
    margin: 0.4em 0;
  }

  .forum-post-body :deep(table) {
    margin: 0 0 0.75em;
    border-collapse: collapse;
  }

  .forum-post-body :deep(th),
  .forum-post-body :deep(td) {
    padding: 0.25em 0.5em;
    border: 1px solid var(--p-content-border-color);
  }

  .forum-post-body :deep(.spoiler:not(.is-revealed)),
  .forum-post-body :deep(.spoiled:not(.is-revealed)) {
    cursor: pointer;
    filter: blur(0.3em);
    user-select: none;
  }
</style>
