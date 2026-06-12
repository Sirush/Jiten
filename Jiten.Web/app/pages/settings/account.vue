<script setup lang="ts">
  import { useToast } from 'primevue/usetoast';
  import { useConfirm } from 'primevue/useconfirm';
  import { useAuthStore } from '~/stores/authStore';
  import type { AccountInfo, TokenResponse } from '~/types/types';

  definePageMeta({
    middleware: ['auth'],
  });

  useHead({ title: 'Account Settings' });

  const { $api } = useNuxtApp();
  const toast = useToast();
  const confirm = useConfirm();
  const auth = useAuthStore();

  const account = ref<AccountInfo | null>(null);

  const fetchAccount = async () => {
    account.value = await $api<AccountInfo>('account');
  };

  await fetchAccount();

  const signInMethods = computed(() => {
    if (!account.value) return [] as string[];
    const methods: string[] = [];
    if (account.value.hasPassword) methods.push('Password');
    else methods.push('Google');
    return methods;
  });

  const formatDate = (dateString: string) => new Date(dateString).toLocaleDateString(undefined, { year: 'numeric', month: 'long', day: 'numeric' });

  const idCopied = ref(false);
  const copyUserId = async () => {
    if (!account.value) return;
    try {
      await navigator.clipboard.writeText(account.value.userId);
      idCopied.value = true;
      toast.add({ severity: 'success', summary: 'Copied', detail: 'User ID copied to clipboard', life: 2000 });
      setTimeout(() => (idCopied.value = false), 2000);
    } catch {
      toast.add({ severity: 'error', summary: 'Error', detail: 'Failed to copy to clipboard', life: 3000 });
    }
  };

  const errorMessage = (err: unknown, fallback: string) => {
    const e = err as { data?: { message?: string }; response?: { _data?: { message?: string } } };
    return e?.data?.message || e?.response?._data?.message || (err instanceof Error ? err.message : fallback);
  };

  // --- Password validation (mirrors register.vue) ---
  function validatePasswordValue(password: string): string | null {
    if (!password) return 'Password is required';
    if (password.length < 10) return 'Password must be at least 10 characters';
    if (password.length > 100) return 'Password must be at most 100 characters';
    if (!/[a-z]/.test(password)) return 'Password must contain at least one lowercase letter';
    if (!/[A-Z]/.test(password)) return 'Password must contain at least one uppercase letter';
    if (!/\d/.test(password)) return 'Password must contain at least one digit';
    return null;
  }

  // --- Change email ---
  const emailForm = reactive({ newEmail: '', currentPassword: '' });
  const emailLoading = ref(false);
  const emailError = ref<string | null>(null);
  const emailSent = ref(false);

  const submitChangeEmail = async () => {
    if (!account.value) return;
    emailError.value = null;
    emailSent.value = false;
    const newEmail = emailForm.newEmail.trim();
    if (!newEmail) {
      emailError.value = 'Email is required';
      return;
    }
    if (account.value.hasPassword && !emailForm.currentPassword) {
      emailError.value = 'Current password is required';
      return;
    }
    emailLoading.value = true;
    try {
      await $api('account/change-email', {
        method: 'POST',
        body: { newEmail, currentPassword: account.value.hasPassword ? emailForm.currentPassword : undefined },
      });
      emailSent.value = true;
      emailForm.newEmail = '';
      emailForm.currentPassword = '';
      toast.add({ severity: 'success', summary: 'Confirmation sent', detail: 'Check your new email address to confirm the change.', life: 6000 });
    } catch (err) {
      emailError.value = errorMessage(err, 'Failed to change email.');
    } finally {
      emailLoading.value = false;
    }
  };

  // --- Change / set password ---
  const pwForm = reactive({ currentPassword: '', newPassword: '', confirmPassword: '' });
  const pwLoading = ref(false);
  const pwError = ref<string | null>(null);

  const submitChangePassword = async () => {
    pwError.value = null;
    const validation = validatePasswordValue(pwForm.newPassword);
    if (validation) {
      pwError.value = validation;
      return;
    }
    if (pwForm.newPassword !== pwForm.confirmPassword) {
      pwError.value = 'Passwords do not match';
      return;
    }
    if (!pwForm.currentPassword) {
      pwError.value = 'Current password is required';
      return;
    }
    pwLoading.value = true;
    try {
      const tokens = await $api<TokenResponse>('account/change-password', {
        method: 'POST',
        body: { currentPassword: pwForm.currentPassword, newPassword: pwForm.newPassword },
      });
      auth.setTokens(tokens.accessToken, tokens.refreshToken);
      pwForm.currentPassword = '';
      pwForm.newPassword = '';
      pwForm.confirmPassword = '';
      toast.add({ severity: 'success', summary: 'Password changed', detail: 'Your password has been updated.', life: 5000 });
    } catch (err) {
      pwError.value = errorMessage(err, 'Failed to change password.');
    } finally {
      pwLoading.value = false;
    }
  };

  const submitSetPassword = async () => {
    pwError.value = null;
    const validation = validatePasswordValue(pwForm.newPassword);
    if (validation) {
      pwError.value = validation;
      return;
    }
    if (pwForm.newPassword !== pwForm.confirmPassword) {
      pwError.value = 'Passwords do not match';
      return;
    }
    pwLoading.value = true;
    try {
      await $api('account/set-password', { method: 'POST', body: { newPassword: pwForm.newPassword } });
      pwForm.newPassword = '';
      pwForm.confirmPassword = '';
      await fetchAccount();
      toast.add({ severity: 'success', summary: 'Password set', detail: 'You can now log in with your email and password.', life: 5000 });
    } catch (err) {
      pwError.value = errorMessage(err, 'Failed to set password.');
    } finally {
      pwLoading.value = false;
    }
  };

  // --- Email preferences ---
  const newsletter = ref(false);
  watch(
    account,
    (a) => {
      if (a) newsletter.value = a.receivesNewsletter;
    },
    { immediate: true }
  );
  const newsletterLoading = ref(false);

  const onNewsletterChange = async (value: boolean) => {
    newsletterLoading.value = true;
    try {
      const result = await $api<{ receivesNewsletter: boolean }>('account/preferences', {
        method: 'PATCH',
        body: { receivesNewsletter: value },
      });
      newsletter.value = result.receivesNewsletter;
      if (account.value) account.value.receivesNewsletter = result.receivesNewsletter;
      toast.add({ severity: 'success', summary: 'Preferences updated', life: 2500 });
    } catch (err) {
      newsletter.value = !value;
      toast.add({ severity: 'error', summary: 'Error', detail: errorMessage(err, 'Failed to update preferences.'), life: 4000 });
    } finally {
      newsletterLoading.value = false;
    }
  };

  // --- Security: log out everywhere ---
  const logoutLoading = ref(false);
  const logoutOtherDevices = async () => {
    logoutLoading.value = true;
    try {
      await $api('/auth/revoke-token', { method: 'POST', body: { keepCurrent: true } });
      toast.add({ severity: 'success', summary: 'Done', detail: 'All other sessions have been signed out.', life: 4000 });
    } catch (err) {
      toast.add({ severity: 'error', summary: 'Error', detail: errorMessage(err, 'Failed to sign out other devices.'), life: 4000 });
    } finally {
      logoutLoading.value = false;
    }
  };

  const confirmLogoutOthers = () => {
    confirm.require({
      message: 'This will sign out every other device and browser. Your current session stays active.',
      header: 'Log out other devices',
      icon: 'pi pi-exclamation-triangle',
      rejectProps: { label: 'Cancel', severity: 'secondary', outlined: true },
      acceptProps: { label: 'Log out others', severity: 'danger' },
      accept: () => logoutOtherDevices(),
    });
  };
</script>

<template>
  <div class="container mx-auto p-2 md:p-4 max-w-3xl">
    <div class="flex flex-wrap items-center justify-between gap-2 mb-4 min-h-[2.5rem]">
      <h1 class="text-2xl font-bold">Account Settings</h1>
    </div>

    <div v-if="account" class="flex flex-col gap-4">
      <!-- Account overview -->
      <Card>
        <template #title>
          <h3 class="text-lg font-semibold">Account</h3>
        </template>
        <template #content>
          <dl class="grid grid-cols-1 sm:grid-cols-[10rem_1fr] gap-x-4 gap-y-3 text-sm">
            <dt class="font-semibold text-muted-color">Username</dt>
            <dd class="break-all">{{ account.userName }}</dd>

            <dt class="font-semibold text-muted-color">User ID</dt>
            <dd class="flex items-center gap-2 flex-wrap">
              <code class="bg-surface-100 dark:bg-surface-800 px-2 py-1 rounded break-all">{{ account.userId }}</code>
              <Button
                :icon="idCopied ? 'pi pi-check' : 'pi pi-copy'"
                :severity="idCopied ? 'success' : 'secondary'"
                text
                size="small"
                class="!p-1 !h-6 !w-6"
                aria-label="Copy user ID"
                @click="copyUserId"
              />
            </dd>

            <dt class="font-semibold text-muted-color">Member since</dt>
            <dd>{{ formatDate(account.createdAt) }}</dd>

            <dt class="font-semibold text-muted-color">Email</dt>
            <dd class="flex items-center gap-2 flex-wrap">
              <span class="break-all">{{ account.email }}</span>
              <Tag v-if="account.emailConfirmed" value="Verified" severity="success" />
              <Tag v-else value="Unverified" severity="warn" />
            </dd>

            <dt class="font-semibold text-muted-color">Sign-in method</dt>
            <dd class="flex items-center gap-2 flex-wrap">
              <Tag v-for="m in signInMethods" :key="m" :value="m" severity="info" />
            </dd>

            <template v-if="account.rateLimitTier && account.rateLimitTier !== 'Default'">
              <dt class="font-semibold text-muted-color">Rate limit tier</dt>
              <dd><Tag :value="account.rateLimitTier" severity="secondary" /></dd>
            </template>

            <template v-if="account.roles?.includes('Administrator')">
              <dt class="font-semibold text-muted-color">Roles</dt>
              <dd class="flex items-center gap-2 flex-wrap">
                <Tag v-for="r in account.roles" :key="r" :value="r" severity="secondary" />
              </dd>
            </template>
          </dl>
        </template>
      </Card>

      <!-- Email -->
      <Card>
        <template #title>
          <h3 class="text-lg font-semibold">Email</h3>
        </template>
        <template #content>
          <Message v-if="!account.hasPassword" severity="info" :closable="false" class="mb-4"> Set a password first to change your email. </Message>
          <Message v-if="emailSent" severity="info" :closable="false" class="mb-4">
            A confirmation link has been sent to your new email address. Your email changes once confirmed.
          </Message>

          <form class="flex flex-col gap-6 pt-4" @submit.prevent="submitChangeEmail">
            <div class="w-full">
              <FloatLabel>
                <InputText
                  id="newEmail"
                  v-model.trim="emailForm.newEmail"
                  type="email"
                  autocomplete="email"
                  :disabled="!account.hasPassword || emailLoading"
                  class="w-full"
                />
                <label for="newEmail">New email</label>
              </FloatLabel>
            </div>
            <div v-if="account.hasPassword" class="w-full">
              <FloatLabel>
                <Password
                  id="emailCurrentPassword"
                  v-model="emailForm.currentPassword"
                  :feedback="false"
                  toggleMask
                  :disabled="emailLoading"
                  :inputProps="{ autocomplete: 'current-password' }"
                  :inputClass="'w-full'"
                />
                <label for="emailCurrentPassword">Current password</label>
              </FloatLabel>
            </div>
            <small v-if="emailError" class="text-red-500">{{ emailError }}</small>
            <div>
              <Button
                type="submit"
                label="Change email"
                icon="pi pi-envelope"
                :loading="emailLoading"
                :disabled="!account.hasPassword || emailLoading"
                class="w-full md:w-auto"
              />
            </div>
          </form>
        </template>
      </Card>

      <!-- Password -->
      <Card>
        <template #title>
          <h3 class="text-lg font-semibold">{{ account.hasPassword ? 'Password' : 'Set a password' }}</h3>
        </template>
        <template #content>
          <form v-if="account.hasPassword" class="flex flex-col gap-6 pt-4" @submit.prevent="submitChangePassword">
            <div class="w-full">
              <FloatLabel>
                <Password
                  id="currentPassword"
                  v-model="pwForm.currentPassword"
                  :feedback="false"
                  toggleMask
                  :disabled="pwLoading"
                  :inputProps="{ autocomplete: 'current-password' }"
                  :inputClass="'w-full'"
                />
                <label for="currentPassword">Current password</label>
              </FloatLabel>
            </div>
            <div class="w-full">
              <FloatLabel>
                <Password
                  id="newPassword"
                  v-model="pwForm.newPassword"
                  toggleMask
                  :feedback="true"
                  :promptLabel="'At least 10 characters including upper, lower, digit'"
                  :weakLabel="'Weak'"
                  :mediumLabel="'Medium'"
                  :strongLabel="'Strong'"
                  :disabled="pwLoading"
                  :inputProps="{ autocomplete: 'new-password', minlength: 10 }"
                  :inputClass="'w-full'"
                />
                <label for="newPassword">New password</label>
              </FloatLabel>
            </div>
            <div class="w-full">
              <FloatLabel>
                <Password
                  id="confirmPassword"
                  v-model="pwForm.confirmPassword"
                  :feedback="false"
                  toggleMask
                  :disabled="pwLoading"
                  :inputProps="{ autocomplete: 'new-password' }"
                  :inputClass="'w-full'"
                />
                <label for="confirmPassword">Confirm new password</label>
              </FloatLabel>
            </div>
            <small v-if="pwError" class="text-red-500">{{ pwError }}</small>
            <div>
              <Button type="submit" label="Change password" icon="pi pi-lock" :loading="pwLoading" class="w-full md:w-auto" />
            </div>
          </form>

          <form v-else class="flex flex-col gap-6 pt-4" @submit.prevent="submitSetPassword">
            <p class="text-gray-600 dark:text-gray-300">Add a password so you can sign in with your email. Your Google sign-in keeps working as before.</p>
            <div class="w-full">
              <FloatLabel>
                <Password
                  id="setNewPassword"
                  v-model="pwForm.newPassword"
                  toggleMask
                  :feedback="true"
                  :promptLabel="'At least 10 characters including upper, lower, digit'"
                  :weakLabel="'Weak'"
                  :mediumLabel="'Medium'"
                  :strongLabel="'Strong'"
                  :disabled="pwLoading"
                  :inputProps="{ autocomplete: 'new-password', minlength: 10 }"
                  :inputClass="'w-full'"
                />
                <label for="setNewPassword">New password</label>
              </FloatLabel>
            </div>
            <div class="w-full">
              <FloatLabel>
                <Password
                  id="setConfirmPassword"
                  v-model="pwForm.confirmPassword"
                  :feedback="false"
                  toggleMask
                  :disabled="pwLoading"
                  :inputProps="{ autocomplete: 'new-password' }"
                  :inputClass="'w-full'"
                />
                <label for="setConfirmPassword">Confirm new password</label>
              </FloatLabel>
            </div>
            <small v-if="pwError" class="text-red-500">{{ pwError }}</small>
            <div>
              <Button type="submit" label="Set password" icon="pi pi-lock" :loading="pwLoading" class="w-full md:w-auto" />
            </div>
          </form>
        </template>
      </Card>

      <!-- Email preferences -->
      <Card>
        <template #title>
          <h3 class="text-lg font-semibold">Email preferences</h3>
        </template>
        <template #content>
          <div class="flex items-center justify-between gap-4">
            <label for="newsletter" class="text-sm cursor-pointer">
              <span class="font-semibold">Newsletter</span>
              <span class="block text-gray-600 dark:text-gray-300">Receive occasional updates and newsletters via email.</span>
            </label>
            <ToggleSwitch input-id="newsletter" v-model="newsletter" :disabled="newsletterLoading" @update:model-value="onNewsletterChange" />
          </div>
        </template>
      </Card>

      <!-- Security -->
      <Card>
        <template #title>
          <h3 class="text-lg font-semibold">Security</h3>
        </template>
        <template #content>
          <p class="text-gray-600 dark:text-gray-300 mb-3">
            Sign out everywhere you're logged in except this device. Useful if you used a shared or lost device.
          </p>
          <Button
            label="Log out of all other devices"
            icon="pi pi-sign-out"
            severity="danger"
            outlined
            :loading="logoutLoading"
            class="w-full md:w-auto"
            @click="confirmLogoutOthers"
          />
        </template>
      </Card>
    </div>
  </div>
</template>

<style scoped>
  :deep(.p-password),
  :deep(.p-password input) {
    width: 100%;
  }

  :deep(.p-inputtext),
  :deep(.p-password input) {
    min-height: 3rem;
  }
</style>
