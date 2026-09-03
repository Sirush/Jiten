<script setup lang="ts">
  import { useAuthStore } from '~/stores/authStore';
  import { USERNAME_MAX, validateUsername as validateUsernameValue } from '~/utils/usernameRules';

  const { $api } = useNuxtApp();
  const runtimeConfig = useRuntimeConfig();
  const recaptchaEnabled = !!runtimeConfig.public.recaptcha?.v2SiteKey;
  const googleSignInEnabled = !!runtimeConfig.public.googleSignInClientId;
  const GoogleSignInButtonComponent = googleSignInEnabled ? resolveComponent('GoogleSignInButton') : null;

  const authStore = useAuthStore();
  const router = useRouter();
  const route = useRoute();

  const redirectQuery = computed(() => {
    const redirect = safeRedirectPath(route.query.redirect);
    return redirect ? { redirect } : {};
  });

  const handleGoogleOnSuccess = async (response: { credential?: string }) => {
    try {
      const result = await authStore.loginWithGoogle(response.credential);
      if (result === 'requiresRegistration') {
        await router.push({ path: '/google-registration', query: redirectQuery.value });
      } else if (result === true) {
        await router.push(safeRedirectPath(route.query.redirect) ?? '/');
      } else {
        error.value = authStore.error || 'Google sign-in failed. Please try again.';
      }
    } catch {
      error.value = 'Google sign-in failed. Please try again.';
    }
  };

  const handleGoogleOnError = () => {
    error.value = 'Google sign-in failed. Please try again.';
  };

  const form = reactive({
    username: '',
    email: '',
    password: '',
    tosAccepted: false,
    receiveNewsletter: false,
  });

  const recaptchaResponse = ref();
  if (recaptchaEnabled) {
    useRecaptchaProvider();
  }

  const RecaptchaCheckboxComponent = recaptchaEnabled ? resolveComponent('RecaptchaCheckbox') : null;

  const isLoading = ref(false);
  const registered = ref(false);
  const registeredEmail = ref('');
  const error = ref<string | null>(null);
  const errorDetails = ref<string[]>([]);

  const usernameError = ref<string | null>(null);
  const passwordError = ref<string | null>(null);
  const emailError = ref<string | null>(null);
  const tosError = ref<string | null>(null);

  function validateTos() {
    if (!form.tosAccepted) {
      tosError.value = 'You need to accept the Terms of Service and Privacy Policy to create an account';
      return false;
    }
    tosError.value = null;
    return true;
  }

  function validateUsername() {
    usernameError.value = validateUsernameValue(form.username.trim());
    return usernameError.value === null;
  }

  function validatePassword() {
    const password = form.password;

    if (!password) {
      passwordError.value = 'Password is required';
      return false;
    }

    if (password.length < 10) {
      passwordError.value = 'Password must be at least 10 characters';
      return false;
    }

    if (password.length > 100) {
      passwordError.value = 'Password must be at most 100 characters';
      return false;
    }

    if (!/[a-z]/.test(password)) {
      passwordError.value = 'Password must contain at least one lowercase letter';
      return false;
    }

    if (!/[A-Z]/.test(password)) {
      passwordError.value = 'Password must contain at least one uppercase letter';
      return false;
    }

    if (!/\d/.test(password)) {
      passwordError.value = 'Password must contain at least one digit';
      return false;
    }

    passwordError.value = null;
    return true;
  }

  function validateEmail() {
    const email = form.email.trim();

    if (!email) {
      emailError.value = 'Email is required';
      return false;
    }

    if (email.length > 100) {
      emailError.value = 'Email must be at most 100 characters';
      return false;
    }

    emailError.value = null;
    return true;
  }

  async function handleRegister() {
    error.value = null;
    errorDetails.value = [];

    const isUsernameValid = validateUsername();
    const isPasswordValid = validatePassword();
    const isEmailValid = validateEmail();
    const isTosValid = validateTos();

    if (!isUsernameValid || !isPasswordValid || !isEmailValid || !isTosValid) {
      error.value = 'Please fix the validation errors before submitting';
      return;
    }

    isLoading.value = true;
    try {
      if (recaptchaEnabled && !recaptchaResponse.value) {
        throw new Error('Please complete the reCAPTCHA.');
      }
      await $api('/auth/register', { method: 'POST', body: { ...form, recaptchaResponse: recaptchaResponse.value || '' } });
      registeredEmail.value = form.email.trim();
      registered.value = true;
      trackEvent('signup_completed', { method: 'email' });
    } catch (err) {
      const data = (err as { response?: { _data?: { message?: string; errors?: string[] } } }).response?._data;
      const fallback = err instanceof Error && err.message ? err.message : 'An unexpected error occurred.';
      error.value = `Registration failed: ${data?.message || fallback}`;
      errorDetails.value = Array.isArray(data?.errors) ? data.errors.filter((e) => !error.value?.includes(e)) : [];
    } finally {
      isLoading.value = false;
    }
  }
</script>

<template>
  <Card class="max-w-120 mx-auto p-2">
    <template #title>{{ registered ? 'Check your inbox' : 'Create your account' }}</template>
    <template #content>
      <div v-if="registered" class="pt-2">
        <p>
          We sent a confirmation link to <b>{{ registeredEmail }}</b
          >. Open it to finish creating your account.
        </p>
        <p class="text-sm text-gray-600 dark:text-gray-400 mt-3">
          Nothing after a few minutes? Resend it from the <NuxtLink :to="{ path: '/login', query: redirectQuery }">login page</NuxtLink> under "Didn't get your
          confirmation email?", or email <a href="mailto:contact@jiten.moe">contact@jiten.moe</a> from that address for a manual confirmation.
        </p>
      </div>
      <template v-else>
        <p class="text-sm text-gray-600 dark:text-gray-400 pt-1">See your coverage, track your vocabulary, build filtered decks, and study across devices.</p>
        <form @submit.prevent="handleRegister" class="flex flex-col gap-6 pt-4">
          <div class="w-full">
            <FloatLabel>
              <InputText
                id="username"
                v-model.trim="form.username"
                required
                :maxlength="USERNAME_MAX"
                autocomplete="username"
                class="w-full"
                @blur="validateUsername"
                @focus="usernameError = null"
              />
              <label for="username">Username</label>
            </FloatLabel>
            <small v-if="usernameError" class="text-red-500">{{ usernameError }}</small>
          </div>
          <div class="w-full">
            <FloatLabel>
              <InputText
                id="email"
                v-model.trim="form.email"
                type="email"
                required
                autocomplete="email"
                class="w-full"
                @blur="validateEmail"
                @focus="emailError = null"
              />
              <label for="email">Email</label>
            </FloatLabel>
            <small v-if="emailError" class="text-red-500">{{ emailError }}</small>
          </div>
          <div class="w-full">
            <FloatLabel>
              <Password
                id="password"
                v-model="form.password"
                toggleMask
                :feedback="false"
                :inputProps="{ autocomplete: 'new-password', minlength: 10 }"
                :inputClass="'w-full'"
                required
                @blur="validatePassword"
                @focus="passwordError = null"
              />
              <label for="password">Password</label>
            </FloatLabel>
            <PasswordStrengthMeter :value="form.password" :error="passwordError" />
          </div>

          <div class="flex flex-col gap-4 pt-2">
            <div class="flex flex-col gap-1">
              <div class="flex items-start gap-3">
                <Checkbox
                  inputId="terms"
                  v-model="form.tosAccepted"
                  name="terms"
                  binary
                  class="mt-1"
                  :invalid="!!tosError"
                  :aria-describedby="tosError ? 'terms-error' : undefined"
                  @update:model-value="tosError = null"
                />
                <label for="terms" class="text-sm text-gray-700 dark:text-gray-300 leading-relaxed cursor-pointer">
                  I agree to the
                  <NuxtLink to="/terms" target="_blank" class="text-blue-600 hover:text-blue-800 hover:underline font-medium"> Terms of Service </NuxtLink>
                  and
                  <NuxtLink to="/privacy" target="_blank" class="text-blue-600 hover:text-blue-800 hover:underline font-medium"> Privacy Policy </NuxtLink>
                </label>
              </div>
              <small v-if="tosError" id="terms-error" class="text-red-500">{{ tosError }}</small>
            </div>

            <div class="flex items-start gap-3">
              <Checkbox inputId="newsletter" v-model="form.receiveNewsletter" name="newsletter" binary class="mt-1" />
              <label for="newsletter" class="text-sm text-gray-700 dark:text-gray-300 leading-relaxed cursor-pointer">
                I would like to receive occasional updates and newsletters via email
              </label>
            </div>
          </div>

          <component v-if="RecaptchaCheckboxComponent" :is="RecaptchaCheckboxComponent" v-model="recaptchaResponse" class="my-2" />
          <div class="flex flex-col gap-2">
            <Button type="submit" :disabled="isLoading" class="w-full">{{ isLoading ? 'Creating account...' : 'Create account' }}</Button>
            <small class="text-gray-600 dark:text-gray-400 text-center">We'll email you a confirmation link.</small>
          </div>
        </form>
        <div v-if="GoogleSignInButtonComponent" class="flex justify-center mt-4">
          <component :is="GoogleSignInButtonComponent" @success="handleGoogleOnSuccess" @error="handleGoogleOnError" />
        </div>
        <p v-if="error" class="text-red-500">{{ error }}</p>
        <ul v-if="errorDetails.length" class="text-red-500 text-sm list-disc pl-5">
          <li v-for="detail in errorDetails" :key="detail">{{ detail }}</li>
        </ul>
        <div class="links">
          <NuxtLink :to="{ path: '/login', query: redirectQuery }">Back to Login</NuxtLink>
        </div>
      </template>
    </template>
  </Card>
</template>

<style scoped>
  :deep(.p-password) {
    width: 100%;
  }

  :deep(.p-password input) {
    width: 100%;
  }

  :deep(.p-inputtext),
  :deep(.p-password input) {
    min-height: 3rem;
  }

  :deep(.p-float-label) {
    margin-bottom: 0;
  }

  :deep(.p-checkbox) {
    flex-shrink: 0;
  }
</style>
