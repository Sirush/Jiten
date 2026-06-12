<script setup lang="ts">
  import { useAuthStore } from '~/stores/authStore';

  const route = useRoute();
  const { $api } = useNuxtApp();
  const auth = useAuthStore();

  const status = ref<'pending' | 'success' | 'error'>('pending');
  const message = ref<string>('Confirming your email change...');

  onMounted(async () => {
    const userId = route.query.userId as string | undefined;
    const email = route.query.email as string | undefined;
    const code = route.query.code as string | undefined;
    if (!userId || !email || !code) {
      status.value = 'error';
      message.value = 'Invalid confirmation link.';
      return;
    }
    try {
      await $api('/account/confirm-email-change', {
        method: 'POST',
        body: { userId, newEmail: email, code },
      });
      status.value = 'success';
      message.value = 'Your email has been changed. Please log in again with your new email address.';
      auth.clearAuthData();
    } catch {
      status.value = 'error';
      message.value = 'Email change confirmation failed. The link may have expired or already been used.';
    }
  });
</script>

<template>
  <Card class="auth-card">
    <template #title>Email Change Confirmation</template>
    <template #content>
      <p :class="{ success: status === 'success', error: status === 'error' }">{{ message }}</p>
      <div class="links">
        <NuxtLink to="/login">Go to Login</NuxtLink>
      </div>
    </template>
  </Card>
</template>

<style scoped>
  .auth-card {
    max-width: 420px;
    margin: 40px auto;
    padding: 20px;
  }
  .error {
    color: #c0392b;
  }
  .success {
    color: #27ae60;
  }
  .links {
    margin-top: 12px;
  }
</style>
