<script setup lang="ts">
  import { useAuthStore } from '~/stores/authStore';
  import { ONE_TIME_NOTICE_COPY, type ActiveNotice } from '~/utils/oneTimeNotices';

  const props = defineProps<{ surface: string }>();

  const { $api } = useNuxtApp();
  const auth = useAuthStore();

  const notices = ref<ActiveNotice[]>([]);

  const rendered = computed(() =>
    notices.value.flatMap((notice) => {
      const copy = ONE_TIME_NOTICE_COPY[notice.key];
      return copy ? [{ key: notice.key, ...copy(notice.values ?? {}) }] : [];
    })
  );

  onMounted(async () => {
    if (!auth.isAuthenticated) return;
    try {
      notices.value = (await $api<ActiveNotice[]>('user/notices', { query: { surface: props.surface } })) ?? [];
    } catch {
      notices.value = [];
    }
  });

  async function dismiss(key: string) {
    notices.value = notices.value.filter((n) => n.key !== key);
    try {
      await $api(`user/notices/${encodeURIComponent(key)}/dismiss`, { method: 'POST' });
    } catch {
      // Hidden for this visit either way; a failed save only means it shows again next time.
    }
  }
</script>

<template>
  <div v-if="rendered.length" class="flex flex-col gap-2">
    <Message v-for="notice in rendered" :key="notice.key" severity="info" closable @close="dismiss(notice.key)">
      <div class="flex flex-col gap-1 text-sm">
        <span class="font-semibold">{{ notice.title }}</span>
        <span>{{ notice.body }}</span>
        <NuxtLink v-if="notice.link" :to="notice.link.to" class="underline underline-offset-2 w-fit">{{ notice.link.label }}</NuxtLink>
      </div>
    </Message>
  </div>
</template>
