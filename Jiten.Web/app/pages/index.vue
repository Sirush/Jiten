<script setup lang="ts">
  import { useAuthStore } from '~/stores/authStore';

  const auth = useAuthStore();
</script>

<template>
  <HomeGuest v-if="!auth.isAuthenticated" />
  <HomeMember v-else />
</template>
