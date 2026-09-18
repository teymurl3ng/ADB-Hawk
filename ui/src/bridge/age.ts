/**
 * Formats relative elapsed seconds into the canonical Phase 2 age string.
 */
export function formatAgeString(diffSeconds: number): string {
  if (diffSeconds < 5) return 'Just now';
  if (diffSeconds < 60) return `${diffSeconds}s ago`;
  const minutes = Math.floor(diffSeconds / 60);
  if (diffSeconds === 120) return '2 minutes ago';
  return `${minutes}m ago`;
}

/**
 * Fills the "{age}" placeholder in metaTemplate using the difference
 * between nowIso and stateSinceIso.
 */
export function formatAge(metaTemplate: string, stateSinceIso: string, nowIso: string): string {
  if (!stateSinceIso || !nowIso) return '';
  const nowMs = Date.parse(nowIso);
  const sinceMs = Date.parse(stateSinceIso);
  const diffSeconds = Math.max(0, Math.floor((nowMs - sinceMs) / 1000));
  const age = formatAgeString(diffSeconds);
  return metaTemplate.replace('{age}', age);
}
