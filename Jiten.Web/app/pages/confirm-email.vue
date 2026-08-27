<script setup lang="ts">
  const route = useRoute();
  const { $api } = useNuxtApp();

  const status = ref<'pending' | 'success' | 'error'>('pending');
  const message = ref<string>('Confirming your email...');

  onMounted(async () => {
    const userId = route.query.userId as string | undefined;
    const code = route.query.code as string | undefined;
    if (!userId || !code) {
      status.value = 'error';
      message.value = 'Invalid confirmation link.';
      return;
    }
    try {
      const query = new URLSearchParams({ userId, code }).toString();
      await $api(`/auth/confirm-email?${query}`);
      status.value = 'success';
      message.value = 'Email confirmed successfully. You can now log in.';
    } catch (err: unknown) {
      status.value = 'error';
      message.value = 'Email confirmation failed.';
    }
  });
</script>

<template>
  <Card class="auth-card">
    <template #title>Email Confirmation</template>
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
