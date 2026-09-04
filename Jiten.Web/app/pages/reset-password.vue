<script setup lang="ts">
  const route = useRoute();
  const { $api } = useNuxtApp();

  const email = ref<string>((route.query.email as string) || '');
  const token = ref<string>((route.query.code as string) || (route.query.token as string) || '');
  const newPassword = ref('');
  const isLoading = ref(false);
  const message = ref<string | null>(null);
  const error = ref<string | null>(null);

  async function submit() {
    isLoading.value = true;
    message.value = null;
    error.value = null;
    try {
      await $api('/auth/reset-password', {
        method: 'POST',
        body: { email: email.value, token: token.value, newPassword: newPassword.value },
      });
      message.value = 'Password has been reset successfully. You may now log in.';
    } catch (err: unknown) {
      error.value = 'Password reset failed.';
    } finally {
      isLoading.value = false;
    }
  }
</script>

<template>
  <Card class="auth-card">
    <template #title>Reset Password</template>
    <template #content>
      <form @submit.prevent="submit">
        <div class="field">
          <FloatLabel for="email">Email</FloatLabel>
          <InputText id="email" v-model.trim="email" type="email" required />
        </div>
        <div class="field">
          <FloatLabel for="token">Token</FloatLabel>
          <InputText id="token" v-model.trim="token" required />
        </div>
        <div class="field">
          <FloatLabel for="password">New Password</FloatLabel>
          <Password
            id="password"
            v-model="newPassword"
            toggle-mask
            :feedback="false"
            :input-props="{ autocomplete: 'new-password', minlength: 10 }"
            required
          />
          <PasswordStrengthMeter :value="newPassword" />
        </div>
        <Button type="submit" :disabled="isLoading">{{ isLoading ? 'Resetting...' : 'Reset Password' }}</Button>
      </form>
      <p v-if="message" class="info">{{ message }}</p>
      <p v-if="error" class="error">{{ error }}</p>
      <div class="links">
        <NuxtLink to="/login">Back to Login</NuxtLink>
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
  .field {
    margin-bottom: 16px;
  }
  .error {
    color: #c0392b;
    margin-top: 8px;
  }
  .info {
    color: #2c3e50;
    margin-top: 8px;
  }
  .links {
    margin-top: 12px;
  }
</style>
