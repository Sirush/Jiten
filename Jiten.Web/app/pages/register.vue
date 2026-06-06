<script setup lang="ts">
  const { $api } = useNuxtApp();
  const runtimeConfig = useRuntimeConfig();
  const recaptchaEnabled = !!runtimeConfig.public.recaptcha?.v2SiteKey;

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
  const message = ref<string | null>(null);
  const error = ref<string | null>(null);

  const usernameError = ref<string | null>(null);
  const passwordError = ref<string | null>(null);
  const emailError = ref<string | null>(null);

  function validateUsername() {
    const username = form.username.trim();

    if (!username) {
      usernameError.value = 'Username is required';
      return false;
    }

    if (username.length < 3) {
      usernameError.value = 'Username must be at least 3 characters';
      return false;
    }

    if (username.length > 30) {
      usernameError.value = 'Username must be at most 30 characters';
      return false;
    }

    if (username.includes(' ')) {
      usernameError.value = 'Username cannot contain spaces';
      return false;
    }

    usernameError.value = null;
    return true;
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
    message.value = null;

    const isUsernameValid = validateUsername();
    const isPasswordValid = validatePassword();
    const isEmailValid = validateEmail();

    if (!isUsernameValid || !isPasswordValid || !isEmailValid) {
      error.value = 'Please fix the validation errors before submitting';
      return;
    }

    isLoading.value = true;
    try {
      if (recaptchaEnabled && !recaptchaResponse.value) {
        throw new Error('Please complete the reCAPTCHA.');
      }
      await $api('/auth/register', { method: 'POST', body: { ...form, recaptchaResponse: recaptchaResponse.value || '' } });
      message.value =
        "Registration successful. Please check your email to confirm your account. If you don't receive the email within a few minutes, please contact us on Discord or send an email to contact@jiten.moe from the email address you used to register for a manual confirmation.";
    } catch (err: any) {
      if (err.response && err.response._data) {
        const apiMessage = err.response._data.message || 'Registration failed.';
        error.value = `Registration failed: ${apiMessage}`;
      } else {
        error.value = (err as Error)?.message ? `Registration failed: ${(err as Error).message}` : 'Registration failed: An unexpected error occurred.';
      }
    } finally {
      isLoading.value = false;
    }
  }
</script>

<template>
  <Card class="max-w-120 mx-auto p-2">
    <template #title>Register</template>
    <template #content>
      <form @submit.prevent="handleRegister" class="flex flex-col gap-6 pt-4">
        <div class="w-full">
          <FloatLabel>
            <InputText id="username" v-model.trim="form.username" required maxlength="30" class="w-full" @blur="validateUsername" @focus="usernameError = null" />
            <label for="username">Username</label>
          </FloatLabel>
          <small v-if="usernameError" class="text-red-500">{{ usernameError }}</small>
        </div>
        <div class="w-full">
          <FloatLabel>
            <InputText id="email" v-model.trim="form.email" type="email" required class="w-full" @blur="validateEmail" @focus="emailError = null" />
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
              :feedback="true"
              :promptLabel="'At least 10 characters including upper, lower, digit'"
              :weakLabel="'Weak'"
              :mediumLabel="'Medium'"
              :strongLabel="'Strong'"
              :inputProps="{ autocomplete: 'new-password', minlength: 10 }"
              :inputClass="'w-full'"
              required
              @blur="validatePassword"
              @focus="passwordError = null"
            />
            <label for="password">Password</label>
          </FloatLabel>
          <small v-if="passwordError" class="text-red-500">{{ passwordError }}</small>
        </div>

        <div class="flex flex-col gap-4 pt-2">
          <div class="flex items-start gap-3">
            <Checkbox inputId="terms" v-model="form.tosAccepted" name="terms" binary required class="mt-1" />
            <label for="terms" class="text-sm text-gray-700 leading-relaxed cursor-pointer">
              I agree to the
              <NuxtLink to="/terms" target="_blank" class="text-blue-600 hover:text-blue-800 hover:underline font-medium"> Terms of Service </NuxtLink>
              and
              <NuxtLink to="/privacy" target="_blank" class="text-blue-600 hover:text-blue-800 hover:underline font-medium"> Privacy Policy </NuxtLink>
            </label>
          </div>

          <div class="flex items-start gap-3">
            <Checkbox inputId="newsletter" v-model="form.receiveNewsletter" name="newsletter" binary class="mt-1" />
            <label for="newsletter" class="text-sm text-gray-700 leading-relaxed cursor-pointer">
              I would like to receive occasional updates and newsletters via email
            </label>
          </div>
        </div>

        <component v-if="RecaptchaCheckboxComponent" :is="RecaptchaCheckboxComponent" v-model="recaptchaResponse" class="my-2" />
        <Button type="submit" :disabled="isLoading" class="w-full">{{ isLoading ? 'Registering...' : 'Register' }}</Button>
      </form>
      <p v-if="message" class="text-amber-400">{{ message }}</p>
      <p v-if="error" class="text-red-500">{{ error }}</p>
      <div class="links">
        <NuxtLink to="/login">Back to Login</NuxtLink>
      </div>
    </template>
  </Card>
</template>

<style scoped>
  /* Ensure PrimeVue Password component takes full width */
  :deep(.p-password) {
    width: 100%;
  }

  :deep(.p-password input) {
    width: 100%;
  }

  /* Ensure consistent input heights */
  :deep(.p-inputtext),
  :deep(.p-password input) {
    min-height: 3rem;
  }

  /* Better spacing for float labels */
  :deep(.p-float-label) {
    margin-bottom: 0;
  }

  /* Improve checkbox alignment */
  :deep(.p-checkbox) {
    flex-shrink: 0;
  }
</style>
