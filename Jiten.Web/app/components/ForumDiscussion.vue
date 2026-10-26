<script setup lang="ts">
  import { useToast } from 'primevue/usetoast';
  import { ForumDiscussionStatus } from '~/types';
  import type { ForumDiscussion, ForumThread } from '~/types/types';
  import { apiErrorMessage } from '~/utils/apiErrorMessage';

  const props = defineProps<{
    subject: 'list' | 'deck';
    id: number;
  }>();

  /** Every load costs forum requests, so nothing loads until the reader nears the section. */
  const PRELOAD_MARGIN = '400px';

  /** Anchor for links to the discussion; handled here because the section renders after the router's own hash scroll. */
  const ANCHOR_ID = 'discussion';

  /** Longer threads open on their newest replies, with the earlier ones a click away. */
  const REPLIES_SHOWN = 3;

  /** The forum creates a topic in a background job that crawls the page, usually within seconds; each poll spends a forum lookup, so a slow crawl gets fewer of them. */
  const PENDING_POLL_DELAYS_MS = [2_000, 3_000, 4_000, 6_000, 8_000, 10_000, 10_000];

  const { $api } = useNuxtApp();
  const toast = useToast();
  const authStore = useAuthStore();
  const { authLink } = useGuestPrompt();
  const communityUrl = useRuntimeConfig().public.communityUrl as string;

  /** Client-only so the forum's crawler, which builds the topic from the SSR page, never picks up this section. */
  const mounted = ref(false);
  const root = ref<HTMLElement | null>(null);
  const discussion = ref<ForumDiscussion | null>(null);
  const expanded = ref(new Set<number>());
  const loadingOlder = ref(false);
  const starting = ref(false);
  const pendingGaveUp = ref(false);

  const path = computed(() => `forum/discussions/${props.subject}/${props.id}`);

  /** Development only: `?forumTopic=<id>` previews a real forum thread on a page that doesn't exist on the live site. */
  const route = useRoute();
  const previewTopic = import.meta.dev && route.query.forumTopic ? String(route.query.forumTopic) : undefined;
  const status = computed(() => discussion.value?.status);
  const threads = computed(() => discussion.value?.threads ?? []);
  const replyLabel = computed(() => {
    const count = discussion.value?.replyCount ?? 0;
    return `${count.toLocaleString()} ${count === 1 ? 'reply' : 'replies'}`;
  });

  let pollTimer: ReturnType<typeof setTimeout> | null = null;
  let pollIndex = 0;
  let started = false;

  /** Set by a jump made before the replies arrived; the section is short until then, so the jump lands wherever the page ends. */
  let realignAfterLoad = false;

  function show(result: ForumDiscussion) {
    discussion.value = result;
    if (result.status !== ForumDiscussionStatus.Pending) return;
    const delay = PENDING_POLL_DELAYS_MS[pollIndex++];
    if (delay != null) pollTimer = setTimeout(() => load(), delay);
    else pendingGaveUp.value = true;
  }

  async function load() {
    try {
      show(await $api<ForumDiscussion>(path.value, { query: { previewTopic } }));
    } catch {
      discussion.value = { status: ForumDiscussionStatus.Unavailable, topicUrl: null, replyCount: 0, threads: [], nextBefore: null };
    }
    if (realignAfterLoad) {
      realignAfterLoad = false;
      await nextTick();
      root.value?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    }
  }

  function start() {
    if (started) return;
    started = true;
    intersection?.disconnect();
    load();
  }

  function jump() {
    if (!discussion.value) realignAfterLoad = true;
    root.value?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    start();
  }

  defineExpose({ jump });

  async function startDiscussion() {
    if (!authStore.isAuthenticated) {
      await navigateTo(authLink('/login', `${route.fullPath.split('#')[0]}#${ANCHOR_ID}`));
      return;
    }
    starting.value = true;
    try {
      const result = await $api<ForumDiscussion>(`${path.value}/start`, { method: 'POST' });
      if (result.status === ForumDiscussionStatus.Unavailable) {
        toast.add({ severity: 'error', summary: "The discussion couldn't be started. Try again in a minute.", life: 5000 });
        return;
      }
      pendingGaveUp.value = false;
      pollIndex = 0;
      show(result);
    } catch (e) {
      toast.add({ severity: 'error', summary: apiErrorMessage(e, "The discussion couldn't be started"), life: 5000 });
    } finally {
      starting.value = false;
    }
  }

  async function loadOlder() {
    const current = discussion.value;
    if (current?.nextBefore == null) return;
    loadingOlder.value = true;
    try {
      const page = await $api<ForumDiscussion>(path.value, { query: { before: current.nextBefore, previewTopic } });
      discussion.value = { ...current, threads: [...current.threads, ...page.threads], nextBefore: page.nextBefore };
    } catch (e) {
      toast.add({ severity: 'error', summary: apiErrorMessage(e, 'Could not load more of the discussion'), life: 5000 });
    } finally {
      loadingOlder.value = false;
    }
  }

  /** Discourse marks spoilers with these classes and relies on its own script to reveal them. */
  function revealSpoiler(event: MouseEvent) {
    const spoiler = (event.target as HTMLElement | null)?.closest('.spoiler, .spoiled');
    if (!spoiler || spoiler.classList.contains('is-revealed')) return;
    event.preventDefault();
    spoiler.classList.add('is-revealed');
  }

  const hiddenReplies = (thread: ForumThread) => (expanded.value.has(thread.post.postId) ? 0 : Math.max(0, thread.replies.length - REPLIES_SHOWN));
  const shownReplies = (thread: ForumThread) => thread.replies.slice(hiddenReplies(thread));

  let intersection: IntersectionObserver | null = null;

  onMounted(async () => {
    mounted.value = true;
    await nextTick();
    if (!root.value) return;
    if (window.location.hash === `#${ANCHOR_ID}`) {
      jump();
      return;
    }
    intersection = new IntersectionObserver((entries) => entries.some((e) => e.isIntersecting) && start(), { rootMargin: PRELOAD_MARGIN });
    intersection.observe(root.value);
  });

  onBeforeUnmount(() => {
    intersection?.disconnect();
    if (pollTimer) clearTimeout(pollTimer);
  });
</script>

<template>
  <section v-if="mounted" :id="ANCHOR_ID" ref="root" aria-labelledby="forum-discussion-heading" class="flex scroll-mt-4 flex-col gap-2">
    <div class="flex flex-wrap items-center justify-between gap-x-4 gap-y-2 px-1">
      <h2 id="forum-discussion-heading" class="m-0 flex items-baseline gap-2 text-lg font-bold">
        Discussion
        <span v-if="status === ForumDiscussionStatus.Ready" class="text-sm font-normal text-muted-color">{{ replyLabel }}</span>
      </h2>
      <Button
        v-if="discussion?.topicUrl"
        as="a"
        :href="discussion.topicUrl"
        target="_blank"
        rel="noopener"
        label="Reply on the forum"
        icon="pi pi-comment"
        size="small"
      />
    </div>

    <div class="rounded-lg border border-surface-200 bg-surface-0 dark:border-surface-700 dark:bg-surface-900">
      <ol v-if="threads.length" class="m-0 list-none divide-y divide-surface-200 p-0 dark:divide-surface-700" @click="revealSpoiler">
        <li v-for="thread in threads" :key="thread.post.postId" class="flex flex-col gap-3 px-4 py-3">
          <ForumDiscussionPost :post="thread.post" />
          <div v-if="thread.replies.length" class="ml-5 flex flex-col gap-3 border-l-2 border-surface-200 pl-4 dark:border-surface-700">
            <button
              v-if="hiddenReplies(thread)"
              type="button"
              class="cursor-pointer self-start border-0 bg-transparent p-0 text-sm font-semibold text-primary-600 hover:underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary-500 dark:text-primary-300"
              @click="expanded.add(thread.post.postId)"
            >
              Show {{ hiddenReplies(thread) }} earlier {{ hiddenReplies(thread) === 1 ? 'reply' : 'replies' }}
            </button>
            <ForumDiscussionPost v-for="reply in shownReplies(thread)" :key="reply.postId" :post="reply" compact />
          </div>
        </li>
      </ol>

      <div v-if="discussion?.nextBefore != null" class="border-t border-surface-200 p-2 text-center dark:border-surface-700">
        <Button label="Show more of the discussion" icon="pi pi-chevron-down" text size="small" :loading="loadingOlder" @click="loadOlder" />
      </div>

      <p v-if="status === ForumDiscussionStatus.Ready && !threads.length" class="m-0 px-4 py-8 text-center text-sm text-muted-color">
        No replies yet. Be the first to share your thoughts on the forum!
      </p>
      <div v-else-if="status === ForumDiscussionStatus.NotStarted" class="flex flex-col items-center gap-3 px-4 py-8 text-center">
        <p class="m-0 text-sm text-muted-color">No one has started a discussion about this yet.</p>
        <Button label="Start the discussion" icon="pi pi-comments" size="small" :loading="starting" @click="startDiscussion" />
      </div>
      <p v-else-if="status === ForumDiscussionStatus.Pending && pendingGaveUp" class="m-0 px-4 py-8 text-center text-sm text-muted-color">
        The forum is taking longer than usual to set up this discussion. Please check back in a minute.
      </p>
      <p v-else-if="status === ForumDiscussionStatus.Pending" class="m-0 px-4 py-8 text-center text-sm text-muted-color">Setting up the discussion…</p>
      <p v-else-if="status === ForumDiscussionStatus.Unavailable" class="m-0 px-4 py-8 text-center text-sm text-muted-color">
        The discussion didn't load. Please try again later.
        <a :href="communityUrl" target="_blank" rel="noopener">Open the forum</a>
      </p>
      <p v-else-if="status == null" class="m-0 px-4 py-8 text-center text-sm text-muted-color">Loading the discussion…</p>
    </div>
  </section>
</template>
