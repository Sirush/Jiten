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
  const resendOpen = ref(false);
  const resendEmail = ref('');

  const emailNotConfirmed = computed(() => !!authStore.error && authStore.error.toLowerCase().includes('email not confirmed'));
  const resendEmailValid = computed(() => /.+@.+\..+/.test(resendEmail.value.trim()));

  // The resend form carries its own address: the login field above accepts a username, which is not
  // something the confirmation endpoint can resolve.
  function openResend() {
    if (!resendEmail.value && credentials.usernameOrEmail.includes('@')) resendEmail.value = credentials.usernameOrEmail.trim();
    resendOpen.value = true;
  }

  watch(emailNotConfirmed, (isUnconfirmed) => {
    if (isUnconfirmed) openResend();
  });

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
        body: { email: resendEmail.value.trim(), recaptchaResponse: recaptchaResponse.value || '' },
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
    return safeRedirectPath(route.query.redirect);
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
        const redirect = getSafeRedirect();
        await router.push({ path: '/google-registration', query: redirect ? { redirect } : {} });
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
          <NuxtLink :to="{ path: '/register', query: getSafeRedirect() ? { redirect: getSafeRedirect() } : {} }">Create an account</NuxtLink>
          <span> · </span>
          <NuxtLink to="/forgot-password">Forgot password?</NuxtLink>
        </div>
      </form>

      <p v-if="authStore.error" class="error-message">{{ authStore.error }}</p>

      <div class="resend-block">
        <Button v-if="!resendOpen" type="button" link class="resend-toggle" @click="openResend">Didn't get your confirmation email?</Button>
        <div v-else class="flex flex-col gap-2">
          <p class="resend-hint">Enter the address you registered with and we'll send a new confirmation link.</p>
          <InputText v-model="resendEmail" type="email" autocomplete="email" placeholder="you@example.com" aria-label="Email address" />
          <component v-if="RecaptchaCheckboxComponent" :is="RecaptchaCheckboxComponent" v-model="recaptchaResponse" class="my-2" />
          <div class="flex">
            <Button type="button" severity="secondary" :disabled="resendLoading || !resendEmailValid" @click="resendConfirmation">
              {{ resendLoading ? 'Sending...' : 'Resend confirmation email' }}
            </Button>
          </div>
          <template v-if="resendMessage">
            <p class="info-message text-green-700 dark:text-green-400">{{ resendMessage }}</p>
            <p class="resend-hint text-gray-600 dark:text-gray-400">
              Nothing after a few minutes? The address on your account may have a typo, which looks identical to success here. Email
              <a href="mailto:contact@jiten.moe">contact@jiten.moe</a> from the address you meant to use and ask for manual confirmation.
            </p>
          </template>
        </div>
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
    border-radius: var(--radius-lg);
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

  .resend-block div {
    margin-bottom: 0;
  }

  .resend-toggle {
    padding: 0;
    font-size: 0.875rem;
  }

  .resend-hint {
    font-size: 0.875rem;
    margin-bottom: 0;
  }

  .info-message {
    margin: 0;
  }
</style>
