import JSZip from 'jszip';

const imageExtensionRe = /\.(jpe?g|png|gif|svg|webp|bmp|tiff?)$/i;
const htmlExtensionRe = /\.(xhtml|html|htm)$/i;

export async function stripEpubImages(file: File): Promise<File> {
  try {
    const zip = await JSZip.loadAsync(await file.arrayBuffer());
    const deletedImages: string[] = [];

    for (const path of Object.keys(zip.files)) {
      if (!zip.files[path].dir && imageExtensionRe.test(path)) {
        zip.remove(path);
        deletedImages.push(path);
      }
    }

    if (deletedImages.length === 0) return file;

    const deletedBasenames = new Set(deletedImages.map((p) => p.split('/').pop()!));

    // Strip <img> tags from HTML/XHTML content files
    for (const [path, entry] of Object.entries(zip.files)) {
      if (entry.dir || !htmlExtensionRe.test(path)) continue;
      const text = await entry.async('text');
      const doc = new DOMParser().parseFromString(text, 'application/xhtml+xml');
      doc.querySelectorAll('img, image').forEach((el) => el.remove());
      // Remove <svg> wrappers that only contained an <image> child and are now empty
      doc.querySelectorAll('svg').forEach((svg) => {
        if (svg.children.length === 0) svg.remove();
      });
      zip.file(path, new XMLSerializer().serializeToString(doc));
    }

    // Clean OPF manifest — remove <item> entries referencing deleted images,
    // orphaned <meta name="cover"> and <reference> entries
    for (const [path, entry] of Object.entries(zip.files)) {
      if (entry.dir || !path.endsWith('.opf')) continue;
      const text = await entry.async('text');
      const doc = new DOMParser().parseFromString(text, 'application/xml');

      const removedIds = new Set<string>();
      doc.querySelectorAll('manifest > item').forEach((item) => {
        const href = item.getAttribute('href') || '';
        const basename = href.split('/').pop()!;
        if (deletedBasenames.has(basename)) {
          const id = item.getAttribute('id');
          if (id) removedIds.add(id);
          item.remove();
        }
      });

      // Remove <meta name="cover" content="..."> where content matches a removed item id
      doc.querySelectorAll('meta[name="cover"]').forEach((meta) => {
        if (removedIds.has(meta.getAttribute('content') || '')) meta.remove();
      });

      // Remove <reference> entries in <guide> pointing to deleted images
      doc.querySelectorAll('guide > reference').forEach((ref) => {
        const href = ref.getAttribute('href') || '';
        const basename = href.split('/').pop()!;
        if (deletedBasenames.has(basename)) ref.remove();
      });

      zip.file(path, new XMLSerializer().serializeToString(doc));
    }

    // Clean leftover src/href references in NCX and any remaining HTML files
    const refRe = /(src|href)\s*=\s*"([^"]*)"/g;
    for (const [path, entry] of Object.entries(zip.files)) {
      if (entry.dir) continue;
      if (!htmlExtensionRe.test(path) && !path.endsWith('.ncx')) continue;
      const text = await entry.async('text');
      const cleaned = text.replace(refRe, (match, attr, value) => {
        const basename = value.split('/').pop()!;
        return deletedBasenames.has(basename) ? `${attr}=""` : match;
      });
      if (cleaned !== text) zip.file(path, cleaned);
    }

    const blob = await zip.generateAsync({
      type: 'blob',
      mimeType: 'application/epub+zip',
      compression: 'DEFLATE',
      compressionOptions: { level: 6 },
    });
    return new File([blob], file.name, { type: 'application/epub+zip', lastModified: file.lastModified });
  } catch {
    return file;
  }
}
