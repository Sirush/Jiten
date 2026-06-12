<script setup lang="ts">
  import { ref, reactive, onMounted } from 'vue';
  import { useAuthStore } from '~/stores/authStore';
  import type { LoginRequest } from '~/types/types';

  const runtimeConfig = useRuntimeConfig();
  const googleSignInEnabled = !!runtimeConfig.public.googleSignInClientId;
  const recaptchaEnabled = !!runtimeConfig.public.recaptcha?.v2SiteKey;

  const GoogleSignInButtonComponent = googleSignInEnabled ? resolveComponent('GoogleSignInButton') : null;
  const RecaptchaCheckboxComponent = recaptchaEnabled ? resolveComponent('RecaptchaCheckbox') : null;

  const authStore = useAuthStore();
  const router = useRouter();
  const route = useRoute();
  const { $api } = useNuxtApp();

  if (recaptchaEnabled) {
    useRecaptchaProvider();
  }

  const recaptchaResponse = ref();
  const resendLoading = ref(false);
  const resendMessage = ref<string | null>(null);

  const emailNotConfirmed = computed(() => !!authStore.error && authStore.error.toLowerCase().includes('email not confirmed'));
  const resendEmailValid = computed(() => credentials.usernameOrEmail.includes('@'));

  async function resendConfirmation() {
    resendMessage.value = null;
    if (recaptchaEnabled && !recaptchaResponse.value) {
      resendMessage.value = 'Please complete the reCAPTCHA.';
      return;
    }
    resendLoading.value = true;
    try {
      const result = await $api<{ message: string }>('/account/resend-confirmation', {
        method: 'POST',
        body: { email: credentials.usernameOrEmail, recaptchaResponse: recaptchaResponse.value || '' },
      });
      resendMessage.value = result?.message || 'If your email address is registered and not yet confirmed, a new confirmation link has been sent.';
    } catch {
      resendMessage.value = 'If your email address is registered and not yet confirmed, a new confirmation link has been sent.';
    } finally {
      resendLoading.value = false;
    }
  }

  const credentials = reactive<LoginRequest>({
    usernameOrEmail: '',
    password: '',
  });

  onMounted(() => {
    if (authStore.isAuthenticated) {
      router.push(getSafeRedirect() ?? '/');
    }
  });

  function getSafeRedirect(): string | null {
    const redirect = Array.isArray(route.query.redirect) ? route.query.redirect[0] : route.query.redirect;
    return redirect && redirect.startsWith('/') && !redirect.startsWith('//') ? redirect : null;
  }

  async function handleLoginSubmit() {
    const success = await authStore.login(credentials);
    if (success && authStore.isAuthenticated) {
      await router.push(getSafeRedirect() ?? '/');
    }
  }

  const handleGoogleOnSuccess = async (response: { credential?: string }) => {
    const { credential } = response;

    try {
      const result = await authStore.loginWithGoogle(credential);

      if (result === 'requiresRegistration') {
        await router.push({ path: '/google-registration' });
      } else if (result === true) {
        await router.push(getSafeRedirect() ?? '/');
      } else {
        console.error('Login failed:', authStore.error);
      }
    } catch (error) {
      console.error('Unexpected error during Google login:', error);
    }
  };

  const handleGoogleOnError = () => {
    console.error('Google login failed. Please try again.');
  };
</script>

<template>
  <Card v-if="authStore" class="login-container">
    <template #title>Login</template>
    <template #content>
      <form @submit.prevent="handleLoginSubmit">
        <div>
          <FloatLabel for="usernameOrEmail">Username or Email:</FloatLabel>
          <InputText id="usernameOrEmail" v-model="credentials.usernameOrEmail" type="text" required />
        </div>
        <div>
          <FloatLabel for="password">Password:</FloatLabel>
          <InputText id="password" v-model="credentials.password" type="password" required />
        </div>
        <div class="flex flex-col items-center">
          <div>
            <Button type="submit" :disabled="authStore.isLoading">
              {{ authStore.isLoading ? 'Logging in...' : 'Login' }}
            </Button>
          </div>
          <div v-if="GoogleSignInButtonComponent">
            <component :is="GoogleSignInButtonComponent" @success="handleGoogleOnSuccess" @error="handleGoogleOnError" />
          </div>
        </div>
        <div>
          <NuxtLink to="/register">Create an account</NuxtLink>
          <span> · </span>
          <NuxtLink to="/forgot-password">Forgot password?</NuxtLink>
        </div>
      </form>

      <p v-if="authStore.error" class="error-message">{{ authStore.error }}</p>

      <div v-if="emailNotConfirmed" class="resend-block">
        <p v-if="resendEmailValid" class="resend-hint">Didn't get the confirmation email? Resend it to {{ credentials.usernameOrEmail }}.</p>
        <p v-else class="resend-hint">Didn't get the confirmation email? Enter your email address above to resend it.</p>
        <component v-if="RecaptchaCheckboxComponent" :is="RecaptchaCheckboxComponent" v-model="recaptchaResponse" class="my-2" />
        <Button type="button" severity="secondary" :disabled="resendLoading || !resendEmailValid" @click="resendConfirmation">
          {{ resendLoading ? 'Sending...' : 'Resend confirmation email' }}
        </Button>
        <p v-if="resendMessage" class="info-message">{{ resendMessage }}</p>
      </div>
    </template>
  </Card>
</template>

<style scoped>
  .login-container {
    max-width: 400px;
    margin: 50px auto;
    padding: 20px;
    border: 1px solid #ccc;
    border-radius: 8px;
  }

  .login-container div {
    margin-bottom: 15px;
  }

  .login-container label {
    display: block;
    margin-bottom: 5px;
  }

  .login-container input {
    width: 100%;
    padding: 8px;
    box-sizing: border-box;
  }

  .error-message {
    color: red;
    margin-top: 10px;
  }

  .resend-block {
    margin-top: 12px;
  }

  .resend-hint {
    font-size: 0.875rem;
    margin-bottom: 8px;
  }

  .info-message {
    color: #27ae60;
    margin-top: 8px;
  }
</style>
