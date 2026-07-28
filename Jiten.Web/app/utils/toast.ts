import type { ToastServiceMethods } from 'primevue/toastservice';

export function showSuccessToast(toast: ToastServiceMethods, summary: string, detail?: string) {
  toast.add({ severity: 'success', summary, detail, life: 5000 });
}

export function showErrorToast(toast: ToastServiceMethods, summary: string, detail?: string) {
  toast.add({ severity: 'error', summary, detail, life: 5000 });
}

export function showWarnToast(toast: ToastServiceMethods, summary: string, detail?: string) {
  toast.add({ severity: 'warn', summary, detail, life: 5000 });
}

// Extracts a user-friendly message from a $fetch error response.
// The API returns either a plain string body or a ProblemDetails object with a `detail` field.
export function extractApiError(err: unknown, fallback: string): string {
  const data = (err as any)?.data;
  if (typeof data === 'string' && data.length > 0 && data.length < 400) return data;
  if (typeof data?.error === 'string' && data.error.length > 0) return data.error;
  if (typeof data?.detail === 'string' && data.detail.length > 0) return data.detail;
  // ValidationProblemDetails: flatten the errors dictionary into readable messages
  if (data?.errors && typeof data.errors === 'object') {
    const messages = (Object.values(data.errors) as string[][]).flat().filter(Boolean);
    if (messages.length > 0) return messages.join(' ');
  }
  if (typeof data?.title === 'string' && data.title.length > 0) return data.title;
  return fallback;
}
