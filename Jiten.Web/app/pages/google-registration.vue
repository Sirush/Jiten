<script setup lang="ts">
import { ref, computed, watch, onMounted } from 'vue';
import { useAuthStore } from '~/stores/authStore';
import type { CompleteGoogleRegistrationRequest } from '~/types/types';

const authStore = useAuthStore();
const router = useRouter();
const route = useRoute();

// Prefer data from authStore (set during loginWithGoogle), fallback to route query params
const tempToken = ref((authStore.googleRegistrationData?.tempToken as string) || (route.query.tempToken as string) || '');
const email = ref((authStore.googleRegistrationData?.email as string) || (route.query.email as string) || '');
const name = ref((authStore.googleRegistrationData?.name as string) || (route.query.name as string) || '');
const picture = ref((authStore.googleRegistrationData?.picture as string) || (route.query.picture as string) || '');

// Form data
const USERNAME_MIN = 3;
const USERNAME_MAX = 30;
const username = ref('');
const acceptedTerms = ref(false);
const acceptedEmailConsent = ref(false);

// Keep only characters Identity allows, then cap to the server limit
function sanitizeUsername(value: string): string {
  return value.replace(/[^a-zA-Z0-9_-]/g, '').slice(0, USERNAME_MAX);
}

// UI state
const isCheckingUsername = ref(false);
const usernameError = ref('');
const step = ref(1); // 1: username, 2: terms and consent

onMounted(() => {
  // Redirect if missing required data
  if (!tempToken.value || !email.value) {
    router.push('/login');
    return;
  }

  // Generate suggested username from email or name, stripping punctuation and capping length
  const raw = email.value.split('@')[0] || name.value.toLowerCase().replace(/\s+/g, '');
  username.value = sanitizeUsername(raw);
  checkUsername();
});

// Debounced username checking
let usernameCheckTimeout: NodeJS.Timeout;
watch(username, (newUsername) => {
  clearTimeout(usernameCheckTimeout);
  if (newUsername.length > USERNAME_MAX) {
    usernameError.value = `Username must be at most ${USERNAME_MAX} characters`;
  } else if (newUsername.length >= USERNAME_MIN) {
    usernameCheckTimeout = setTimeout(checkUsername, 500);
  } else {
    usernameError.value = newUsername.length > 0 ? `Username must be at least ${USERNAME_MIN} characters` : '';
  }
});

async function checkUsername() {
  if (username.value.length < USERNAME_MIN) return;

  isCheckingUsername.value = true;
  usernameError.value = '';

  try {
  } catch (error) {
    console.error('Error checking username:', error);
    usernameError.value = 'Error checking username availability';
  } finally {
    isCheckingUsername.value = false;
  }
}

const canProceedToStep2 = computed(() => {
  return username.value.length >= USERNAME_MIN &&
    username.value.length <= USERNAME_MAX &&
    !usernameError.value;
});

const canComplete = computed(() => {
  return canProceedToStep2.value &&
    acceptedTerms.value;
});

function nextStep() {
  if (canProceedToStep2.value) {
    step.value = 2;
  }
}

function previousStep() {
  step.value = 1;
}

async function completeRegistration() {
  if (!canComplete.value) return;

  const registrationData: CompleteGoogleRegistrationRequest = {
    tempToken: tempToken.value,
    username: username.value,
    tosAccepted: acceptedTerms.value,
    receiveNewsletter: acceptedEmailConsent.value,
  };

  const success = await authStore.completeGoogleRegistration(registrationData);

  if (success) {
    router.push('/');
  }
}
</script>

<template>
  <Card class="max-w-[500px] mx-auto my-12 p-8 border border-gray-300 dark:border-gray-700 rounded-lg">
    <template #title>Complete Your Registration</template>
    <template #content>
      <!-- User Info Display -->
      <div class="flex items-center mb-8 p-5 bg-gray-100 dark:bg-gray-800 rounded-lg">
        <img v-if="picture" :src="picture" :alt="name" class="w-[60px] h-[60px] rounded-full mr-4" />
        <div>
          <h3 class="m-0 mb-1 text-gray-800 dark:text-gray-200">Welcome, {{ name }}!</h3>
          <p class="m-0 text-gray-600 dark:text-gray-400 text-sm">{{ email }}</p>
        </div>
      </div>

      <!-- Step 1: Username Selection -->
      <div v-if="step === 1" class="mb-5">
        <h4 class="mb-5 text-gray-800 dark:text-gray-200">Choose a Username</h4>
        <div class="mb-4">
          <FloatLabel for="username">Username:</FloatLabel>
          <InputText
            id="username"
            v-model="username"
            type="text"
            :maxlength="USERNAME_MAX"
            :class="[
              'w-full p-3 rounded-md border-2 transition-colors',
              usernameError ? 'border-red-600' : 'border-gray-300 dark:border-gray-700'
            ]"
            placeholder="Enter your username"
          />
          <div class="mt-2 text-sm">
            <div v-if="usernameError" class="text-red-600">
              <i class="pi pi-times-circle"></i> {{ usernameError }}
            </div>
          </div>
        </div>

        <div class="flex justify-between mt-6">
          <Button
            @click="nextStep"
            :disabled="!canProceedToStep2"
            class="ml-auto min-w-[120px]"
          >
            Next
          </Button>
        </div>
      </div>

      <!-- Step 2: Terms and Consent -->
      <div v-if="step === 2" class="mb-5">
        <h4 class="mb-5 text-gray-800 dark:text-gray-200">Terms and Preferences</h4>

        <div class="mb-6">
          <div class="flex items-start mb-4 gap-2">
            <Checkbox
              id="terms"
              v-model="acceptedTerms"
              :binary="true"
            />
            <label for="terms" class="leading-snug cursor-pointer">
              I have read and agree to the
              <NuxtLink to="/terms" target="_blank" class="text-blue-600 underline">Terms of Service</NuxtLink>
              and
              <NuxtLink to="/privacy" target="_blank" class="text-blue-600 underline">Privacy Policy</NuxtLink>
              <span class="text-red-600 ml-0.5">*</span>
            </label>
          </div>

          <div class="flex items-start mb-4 gap-2">
            <Checkbox
              id="email-consent"
              v-model="acceptedEmailConsent"
              :binary="true"
            />
            <label for="email-consent" class="leading-snug cursor-pointer">
              I would like to receive occasional updates and newsletters via email
            </label>
          </div>
        </div>

        <div class="flex justify-between mt-6">
          <Button @click="previousStep" class="min-w-[120px]" outlined>
            Back
          </Button>
          <Button
            @click="completeRegistration"
            :disabled="!canComplete || authStore.isLoading"
            class="ml-auto min-w-[120px]"
          >
            {{ authStore.isLoading ? 'Creating Account...' : 'Complete Registration' }}
          </Button>
        </div>
      </div>

      <p v-if="authStore.error" class="text-red-600">{{ authStore.error }}</p>
    </template>
  </Card>
</template>

<style scoped>
</style>
