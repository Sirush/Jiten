import JSZip from 'jszip';
import type { MediaRequestDto, MediaRequestUploadAdminDto } from '~/types/types';

const ignoredZipEntryRe = /(^|\/)(__MACOSX\/|\.DS_Store$|Thumbs\.db$|desktop\.ini$)/i;

export async function extractUploadFiles(blob: Blob): Promise<File[]> {
  const zip = await JSZip.loadAsync(await blob.arrayBuffer());
  const entries = Object.values(zip.files)
    .filter((entry) => !entry.dir && !ignoredZipEntryRe.test(entry.name))
    .sort((a, b) => a.name.localeCompare(b.name, undefined, { numeric: true }));

  const files: File[] = [];
  for (const entry of entries) {
    const name = entry.name.split('/').pop() || entry.name;
    files.push(new File([await entry.async('blob')], name, { lastModified: entry.date?.getTime() ?? Date.now() }));
  }
  return files;
}

export function useRequestUploads() {
  const { fetchComments, downloadUploadFile, reviewUpload } = useMediaRequests();

  const fulfillingRequest = ref<MediaRequestDto | null>(null);
  const requestUploads = ref<MediaRequestUploadAdminDto[]>([]);

  async function attachRequest(request: MediaRequestDto) {
    fulfillingRequest.value = request;
    requestUploads.value = [];
    const comments = await fetchComments(request.id);
    if (fulfillingRequest.value?.id !== request.id) return;
    requestUploads.value = comments
      .map((c) => c.upload as MediaRequestUploadAdminDto | undefined)
      .filter((u): u is MediaRequestUploadAdminDto => !!u && !u.fileDeleted);
  }

  function detachRequest() {
    fulfillingRequest.value = null;
    requestUploads.value = [];
  }

  /** Null when the download failed; throws when the archive cannot be read. */
  async function fetchUploadFiles(upload: MediaRequestUploadAdminDto): Promise<File[] | null> {
    if (!fulfillingRequest.value) return null;
    const blob = await downloadUploadFile(fulfillingRequest.value.id, upload.id);
    return blob ? extractUploadFiles(blob) : null;
  }

  async function markUploadReviewed(upload: MediaRequestUploadAdminDto) {
    if (!fulfillingRequest.value || upload.adminReviewed) return;
    if (await reviewUpload(fulfillingRequest.value.id, upload.id, true)) upload.adminReviewed = true;
  }

  return { fulfillingRequest, requestUploads, attachRequest, detachRequest, fetchUploadFiles, markUploadReviewed };
}
