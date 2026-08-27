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

  if (auth.user?.userName) {
    await navigateTo(`/profile/${auth.user.userName}`, { replace: true });
  }
</script>

<template>
  <div class="flex justify-center items-center min-h-[50vh]">
    <ProgressSpinner />
  </div>
</template>
