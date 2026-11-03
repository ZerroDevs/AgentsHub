import { webviewBridge } from './webviewBridge';

/** One slice is a few hundred KB of JSON: small enough for a web message, large enough to stay quick. */
const CHUNK_BYTES = 512 * 1024;

function toBase64(bytes: Uint8Array): string {
  let binary = '';
  const step = 0x8000;

  for (let index = 0; index < bytes.length; index += step) {
    binary += String.fromCharCode(...bytes.subarray(index, index + step));
  }

  return btoa(binary);
}

/**
 * Writes one file to the host and answers its path.
 *
 * The previous route read the whole file into a data URL and shipped it inside the send request, so a
 * 60 MB PDF became an 80 MB JSON string in a single web message. Slicing keeps every message small,
 * and the engines only ever wanted a path anyway.
 */
export async function uploadAttachment(file: File): Promise<string> {
  const begin: any = await webviewBridge.invoke('attachment_begin', {
    name: file.name,
    type: file.type || 'application/octet-stream',
  });

  const uploadId = begin?.uploadId;
  if (!uploadId) throw new Error('the desktop app refused the upload');

  try {
    for (let offset = 0; offset < file.size; offset += CHUNK_BYTES) {
      const slice = file.slice(offset, Math.min(offset + CHUNK_BYTES, file.size));
      const bytes = new Uint8Array(await slice.arrayBuffer());

      await webviewBridge.invoke('attachment_chunk', { uploadId, data: toBase64(bytes) });
    }

    const done: any = await webviewBridge.invoke('attachment_end', { uploadId });
    if (!done?.path) throw new Error('the upload finished without a file');

    return done.path as string;
  } catch (error) {
    // A half-written file in the temp folder is nobody's attachment.
    void webviewBridge.invoke('attachment_abort', { uploadId }).catch(() => {});
    throw error;
  }
}
