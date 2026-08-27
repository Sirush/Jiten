import type {
  MediaRequestDto,
  MediaRequestCommentDto,
  DuplicateCheckResultDto,
  PaginatedResponse,
  RequestActivityLogDto,
  RequestUserSummaryDto,
  MediaRequestUploadAdminDto,
} from '~/types/types';
import type { MediaType, RequestAction, RequestKind, RequestStatus } from '~/types';

export interface RequestFacets {
  mediaTypes: Record<string, number>;
  mediaTypeTotal: number;
  statuses: Record<string, number>;
  statusTotal: number;
  kinds: Record<string, number>;
  kindTotal: number;
  attachmentsYes: number;
  attachmentsNo: number;
  attachmentTotal: number;
}

export interface BoostBalance {
  used: number;
  limit: number;
  remaining: number;
  resetAt: string;
}

export interface BoostResult {
  boostCount: number;
  balance: BoostBalance;
}

export interface MediaRequestQuota {
  activeCount: number;
  limit: number;
  plusLimit: number;
  isPlus: boolean;
}

export function useMediaRequests() {
  const { $api } = useNuxtApp();

  const requests = ref<MediaRequestDto[]>([]);
  const totalCount = ref(0);
  const isLoading = ref(false);
  const error = ref<Error | null>(null);
  let fetchVersion = 0;

  const fetchRequests = async (
    params: {
      mediaType?: MediaType;
      status?: RequestStatus;
      kind?: RequestKind;
      sort?: string;
      offset?: number;
      limit?: number;
      mine?: boolean;
      contributed?: boolean;
      voted?: boolean;
      excludeOwn?: boolean;
      search?: string;
      attachments?: string;
    } = {}
  ) => {
    const version = ++fetchVersion;
    const showLoading = requests.value.length === 0;
    if (showLoading) isLoading.value = true;
    error.value = null;
    try {
      const result = await $api<PaginatedResponse<MediaRequestDto[]>>('requests', {
        query: {
          mediaType: params.mediaType,
          status: params.status,
          kind: params.kind,
          sort: params.sort ?? 'votes',
          offset: params.offset ?? 0,
          limit: params.limit ?? 20,
          mine: params.mine || undefined,
          contributed: params.contributed || undefined,
          voted: params.voted || undefined,
          excludeOwn: params.excludeOwn || undefined,
          search: params.search || undefined,
          attachments: params.attachments || undefined,
        },
      });
      if (version !== fetchVersion) return;
      requests.value = result?.data ?? [];
      totalCount.value = result?.totalItems ?? 0;
    } catch (e) {
      if (version !== fetchVersion) return;
      error.value = e as Error;
      requests.value = [];
      totalCount.value = 0;
    } finally {
      if (version === fetchVersion) isLoading.value = false;
    }
  };

  const fetchRequest = async (id: number): Promise<MediaRequestDto | null> => {
    error.value = null;
    try {
      return await $api<MediaRequestDto>(`requests/${id}`);
    } catch (e) {
      error.value = e as Error;
      return null;
    }
  };

  const createRequest = async (data: {
    title: string;
    mediaType: MediaType;
    kind?: RequestKind;
    targetDeckId?: number;
    externalUrl?: string;
    description?: string;
  }): Promise<{ id: number } | null> => {
    error.value = null;
    try {
      return await $api<{ id: number }>('requests', {
        method: 'POST',
        body: data,
      });
    } catch (e) {
      error.value = e as Error;
      return null;
    }
  };

  const deleteRequest = async (id: number): Promise<boolean> => {
    error.value = null;
    try {
      await $api(`requests/${id}`, { method: 'DELETE' });
      return true;
    } catch (e) {
      error.value = e as Error;
      return false;
    }
  };

  const toggleUpvote = async (id: number): Promise<{ upvoted: boolean; upvoteCount: number; subscribed: boolean } | null> => {
    error.value = null;
    try {
      return await $api<{ upvoted: boolean; upvoteCount: number; subscribed: boolean }>(`requests/${id}/upvote`, {
        method: 'POST',
      });
    } catch (e) {
      error.value = e as Error;
      return null;
    }
  };

  const boostRequest = async (id: number): Promise<BoostResult | null> => {
    error.value = null;
    try {
      return await $api<BoostResult>(`requests/${id}/boost`, { method: 'POST' });
    } catch (e) {
      error.value = e as Error;
      return null;
    }
  };

  const fetchBoostBalance = async (): Promise<BoostBalance | null> => {
    error.value = null;
    try {
      return await $api<BoostBalance>('requests/boost-balance');
    } catch (e) {
      error.value = e as Error;
      return null;
    }
  };

  const subscribe = async (id: number): Promise<boolean> => {
    error.value = null;
    try {
      await $api(`requests/${id}/subscribe`, { method: 'POST' });
      return true;
    } catch (e) {
      error.value = e as Error;
      return false;
    }
  };

  const unsubscribe = async (id: number): Promise<boolean> => {
    error.value = null;
    try {
      await $api(`requests/${id}/subscribe`, { method: 'DELETE' });
      return true;
    } catch (e) {
      error.value = e as Error;
      return false;
    }
  };

  const fetchComments = async (id: number): Promise<MediaRequestCommentDto[]> => {
    error.value = null;
    try {
      return (await $api<MediaRequestCommentDto[]>(`requests/${id}/comments`)) ?? [];
    } catch (e) {
      error.value = e as Error;
      return [];
    }
  };

  const addComment = async (id: number, text?: string, files?: File[]): Promise<boolean> => {
    error.value = null;
    try {
      const formData = new FormData();
      if (text) formData.append('text', text);
      if (files) {
        for (const file of files) {
          formData.append('files', file);
        }
      }
      await $api(`requests/${id}/comments`, {
        method: 'POST',
        body: formData,
      });
      return true;
    } catch (e) {
      error.value = e as Error;
      return false;
    }
  };

  const editComment = async (requestId: number, commentId: number, text: string): Promise<boolean> => {
    error.value = null;
    try {
      await $api(`requests/${requestId}/comments/${commentId}`, {
        method: 'PUT',
        body: { text },
      });
      return true;
    } catch (e) {
      error.value = e as Error;
      return false;
    }
  };

  const addAdminComment = async (requestId: number, commentId: number, text: string): Promise<boolean> => {
    error.value = null;
    try {
      await $api(`requests/${requestId}/comments/${commentId}/admin-comment`, {
        method: 'POST',
        body: { text },
      });
      return true;
    } catch (e) {
      error.value = e as Error;
      return false;
    }
  };

  const editRequestDescription = async (id: number, description?: string, externalUrl?: string, targetDeckId?: number): Promise<boolean> => {
    error.value = null;
    try {
      await $api(`requests/${id}/edit-description`, {
        method: 'PUT',
        body: { description, externalUrl, targetDeckId },
      });
      return true;
    } catch (e) {
      error.value = e as Error;
      return false;
    }
  };

  const deleteUpload = async (requestId: number, uploadId: number): Promise<boolean> => {
    error.value = null;
    try {
      await $api(`requests/${requestId}/uploads/${uploadId}`, { method: 'DELETE' });
      return true;
    } catch (e) {
      error.value = e as Error;
      return false;
    }
  };

  const reviewUpload = async (requestId: number, uploadId: number, adminReviewed: boolean, adminNote?: string): Promise<boolean> => {
    error.value = null;
    try {
      await $api(`requests/${requestId}/uploads/${uploadId}/review`, {
        method: 'PUT',
        body: { adminReviewed, adminNote },
      });
      return true;
    } catch (e) {
      error.value = e as Error;
      return false;
    }
  };

  const getUploadDownloadUrl = async (requestId: number, uploadId: number): Promise<string | null> => {
    error.value = null;
    try {
      const result = await $api<{ url: string }>(`requests/${requestId}/uploads/${uploadId}/download`);
      return result?.url ?? null;
    } catch (e) {
      error.value = e as Error;
      return null;
    }
  };

  const checkDuplicates = async (title: string, targetDeckId?: number): Promise<DuplicateCheckResultDto | null> => {
    error.value = null;
    try {
      return await $api<DuplicateCheckResultDto>('requests/duplicate-check', {
        query: { title, targetDeckId },
      });
    } catch (e) {
      error.value = e as Error;
      return null;
    }
  };

  const editRequest = async (
    id: number,
    data: {
      title: string;
      mediaType: MediaType;
      kind?: RequestKind;
      targetDeckId?: number;
      externalUrl?: string;
      description?: string;
    }
  ): Promise<boolean> => {
    error.value = null;
    try {
      await $api(`requests/${id}/edit`, {
        method: 'PUT',
        body: data,
      });
      return true;
    } catch (e) {
      error.value = e as Error;
      return false;
    }
  };

  const updateStatus = async (
    id: number,
    data: {
      status: RequestStatus;
      adminNote?: string;
      fulfilledDeckId?: number;
    }
  ): Promise<boolean> => {
    error.value = null;
    try {
      await $api(`requests/${id}/status`, {
        method: 'PUT',
        body: data,
      });
      return true;
    } catch (e) {
      error.value = e as Error;
      return false;
    }
  };

  const fetchActivityLog = async (id: number): Promise<RequestActivityLogDto[]> => {
    error.value = null;
    try {
      return (await $api<RequestActivityLogDto[]>(`requests/${id}/activity-log`)) ?? [];
    } catch (e) {
      error.value = e as Error;
      return [];
    }
  };

  const fetchGlobalActivityLog = async (
    params: {
      userId?: string;
      action?: RequestAction;
      offset?: number;
      limit?: number;
    } = {}
  ): Promise<PaginatedResponse<RequestActivityLogDto[]> | null> => {
    error.value = null;
    try {
      return await $api<PaginatedResponse<RequestActivityLogDto[]>>('admin/request-activity', {
        query: params,
      });
    } catch (e) {
      error.value = e as Error;
      return null;
    }
  };

  const fetchUserSummary = async (userId: string): Promise<RequestUserSummaryDto | null> => {
    error.value = null;
    try {
      return await $api<RequestUserSummaryDto>(`admin/request-user-summary/${userId}`);
    } catch (e) {
      error.value = e as Error;
      return null;
    }
  };

  const fetchFacets = async (
    params: {
      mediaType?: MediaType;
      status?: RequestStatus;
      kind?: RequestKind;
      mine?: boolean;
      contributed?: boolean;
      voted?: boolean;
      excludeOwn?: boolean;
      search?: string;
      attachments?: string;
    } = {}
  ): Promise<RequestFacets | null> => {
    try {
      return await $api<RequestFacets>('requests/facets', {
        query: {
          mediaType: params.mediaType,
          status: params.status,
          kind: params.kind,
          mine: params.mine || undefined,
          contributed: params.contributed || undefined,
          voted: params.voted || undefined,
          excludeOwn: params.excludeOwn || undefined,
          search: params.search || undefined,
          attachments: params.attachments || undefined,
        },
      });
    } catch {
      return null;
    }
  };

  const fetchMyQuota = async (): Promise<MediaRequestQuota> => {
    const fallback: MediaRequestQuota = { activeCount: 0, limit: 20, plusLimit: 30, isPlus: false };
    try {
      return (await $api<MediaRequestQuota>('requests/my-quota')) ?? fallback;
    } catch {
      return fallback;
    }
  };

  return {
    requests,
    totalCount,
    isLoading,
    error,
    fetchRequests,
    fetchRequest,
    createRequest,
    deleteRequest,
    toggleUpvote,
    boostRequest,
    fetchBoostBalance,
    subscribe,
    unsubscribe,
    fetchComments,
    addComment,
    editComment,
    addAdminComment,
    editRequestDescription,
    deleteUpload,
    reviewUpload,
    getUploadDownloadUrl,
    checkDuplicates,
    editRequest,
    updateStatus,
    fetchActivityLog,
    fetchGlobalActivityLog,
    fetchUserSummary,
    fetchMyQuota,
    fetchFacets,
  };
}
