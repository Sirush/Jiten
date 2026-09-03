<script setup lang="ts">
  import { ref } from 'vue';

  const { $api } = useNuxtApp();
  const email = ref('');
  const isLoading = ref(false);
  const message = ref<string | null>(null);
  const error = ref<string | null>(null);

  const recaptchaResponse = ref();
  useRecaptchaProvider();

  async function submit() {
    isLoading.value = true;
    message.value = null;
    error.value = null;
    try {
      if (!recaptchaResponse.value) {
        message.value = 'Please complete the reCAPTCHA.';
      }
      await $api('/auth/forgot-password', { method: 'POST', body: { email: email.value, recaptchaResponse: recaptchaResponse.value } });
      message.value =
        'If your email address is registered and confirmed, you will receive a password reset link. If you created your account with google auth, you will need to connect through google.';
    } catch (err: unknown) {
    } finally {
      recaptchaResponse.value = null;
      isLoading.value = false;
    }
  }
</script>

<template>
  <Card class="auth-card">
    <template #title>Forgot Password</template>
    <template #content>
      <form class="flex flex-col gap-2" @submit.prevent="submit">
        <div class="field">
          <FloatLabel for="email">Email</FloatLabel>
          <InputText id="email" v-model.trim="email" type="email" required />
        </div>
        <RecaptchaCheckbox v-model="recaptchaResponse" />
        <Button type="submit" :disabled="isLoading">{{ isLoading ? 'Sending...' : 'Send Reset Link' }}</Button>
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
