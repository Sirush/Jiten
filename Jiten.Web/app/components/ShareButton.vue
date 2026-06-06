<script setup lang="ts">
  import Popover from 'primevue/popover';
  import { useToast } from 'primevue/usetoast';

  const props = defineProps<{
    // Path (e.g. /decks/media/123/detail) or absolute URL of the thing being shared.
    path: string;
    title: string;
  }>();

  const op = ref();
  const toast = useToast();
  const canNativeShare = ref(false);

  onMounted(() => {
    canNativeShare.value = typeof navigator !== 'undefined' && typeof navigator.share === 'function';
  });

  const absoluteUrl = () => {
    if (props.path.startsWith('http')) return props.path;
    const origin = typeof window !== 'undefined' ? window.location.origin : 'https://jiten.moe';
    return origin + props.path;
  };

  const targets = computed(() => {
    const u = encodeURIComponent(absoluteUrl());
    const t = encodeURIComponent(props.title);
    return [
      { name: 'X', icon: 'mdi:twitter', href: `https://twitter.com/intent/tweet?url=${u}&text=${t}` },
      { name: 'Reddit', icon: 'mdi:reddit', href: `https://www.reddit.com/submit?url=${u}&title=${t}` },
      { name: 'Bluesky', icon: 'simple-icons:bluesky', href: `https://bsky.app/intent/compose?text=${t}%20${u}` },
    ];
  });

  const toggle = (e: Event) => op.value?.toggle(e);

  const nativeShare = () => {
    navigator.share({ title: props.title, url: absoluteUrl() }).catch(() => {});
    op.value?.hide();
  };

  const copyLink = async () => {
    try {
      await navigator.clipboard.writeText(absoluteUrl());
      toast.add({ severity: 'success', summary: 'Link copied', life: 2000 });
    } catch {
      // Clipboard unavailable (e.g. insecure context) — silently ignore.
    }
    op.value?.hide();
  };
</script>

<template>
  <span>
    <button
      type="button"
      aria-label="Share"
      class="p-1.5 rounded hover:bg-gray-200 dark:hover:bg-gray-700 transition-colors cursor-pointer"
      @click="toggle"
    >
      <i class="pi pi-share-alt text-primary-500" />
    </button>
    <Popover ref="op">
      <div class="flex items-center gap-1">
        <a
          v-for="target in targets"
          :key="target.name"
          :href="target.href"
          target="_blank"
          rel="noopener"
          :title="`Share on ${target.name}`"
          :aria-label="`Share on ${target.name}`"
          class="p-2 rounded hover:bg-gray-100 dark:hover:bg-gray-700 transition-colors"
          @click="op?.hide()"
        >
          <Icon :name="target.icon" size="1.3em" />
        </a>
        <button
          type="button"
          title="Copy link"
          aria-label="Copy link"
          class="p-2 rounded hover:bg-gray-100 dark:hover:bg-gray-700 transition-colors cursor-pointer"
          @click="copyLink"
        >
          <Icon name="mdi:link-variant" size="1.3em" />
        </button>
        <button
          v-if="canNativeShare"
          type="button"
          title="More…"
          aria-label="More sharing options"
          class="p-2 rounded hover:bg-gray-100 dark:hover:bg-gray-700 transition-colors cursor-pointer"
          @click="nativeShare"
        >
          <Icon name="mdi:share-variant" size="1.3em" />
        </button>
      </div>
    </Popover>
  </span>
</template>
