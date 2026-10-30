<script setup lang="ts">
  import { useAuthStore } from '~/stores/authStore';

  const auth = useAuthStore();

  definePageMeta({
    middleware: ['auth'],
  });

  if (!auth.user) {
    try {
      await auth.fetchCurrentUser();
    } catch {}
  }

  const handle = ownProfileHandle(auth.user);
  if (handle) {
    await navigateTo(profilePath(handle), { replace: true });
  }
</script>

<template>
  <div class="flex justify-center items-center min-h-[50vh]">
    <ProgressSpinner />
  </div>
</template>
