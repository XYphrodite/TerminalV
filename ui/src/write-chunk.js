export const WRITE_CHUNK = 4000;

export function chunkText(data, chunkSize = WRITE_CHUNK) {
  if (data.length <= chunkSize) return [data];
  const chunks = [];
  for (let i = 0; i < data.length;) {
    let end = Math.min(i + chunkSize, data.length);
    if (end < data.length && data.charCodeAt(end - 1) >= 0xd800 && data.charCodeAt(end - 1) <= 0xdbff
      && data.charCodeAt(end) >= 0xdc00 && data.charCodeAt(end) <= 0xdfff) {
      end--;
    }
    chunks.push(data.slice(i, end));
    i = end;
  }
  return chunks;
}
